/**
 * Expired-clip purge, run hourly from the Worker's scheduled handler.
 *
 * Covers both finished clips (status 'ready', past their 3-day TTL) and pending
 * multipart uploads (status 'uploading', past their upload window), plus rows a
 * discard claimed (status 'purging') but did not finish. Each batch:
 *   1. selects at most PURGE_BATCH_ROWS expired rows;
 *   2. claims them BY ID with a guarded UPDATE (status 'purging', only while
 *      still expired) that RETURNs the rows it claimed. A pending row that a
 *      concurrent complete finalized between the select and the claim has a new
 *      expiry, so it is skipped and its object is left alone; a claimed row can
 *      no longer be finalized (complete requires 'uploading');
 *   3. aborts any multipart upload still attached to a claimed row. An upload
 *      that is already completed or gone is fine; any other abort failure
 *      KEEPS that row (its parts may still be in R2) for the next run, unless
 *      the row is older than ABORT_GIVE_UP_SECONDS (R2's own 7-day rule has
 *      aborted the upload by then);
 *   4. deletes ALL the claimed R2 keys in one call (R2 takes at most 1000);
 *   5. deletes the transcripts and VOD share meta, then the rows, BY ID in
 *      chunks small enough for D1's 100-bound-parameter limit.
 *
 * Both kinds (clips and full-game VOD shares) go through the same purge. An
 * HLS share (format 'hls', many segment objects) goes through removeHlsShare
 * (hls-objects.ts) instead of steps 3 to 5, inside a per-run budget of R2
 * calls; the rest stay claimed and expired for the next run. An HLS share
 * still uploading is never selected here: the cron settles it first (it may
 * be watchable, so it is ended at its edge rather than deleted).
 *
 * Deleting by the selected ids (not "WHERE expires_at <= now") is deliberate: the
 * old unbounded DELETE removed rows past the LIMIT whose R2 objects were never
 * deleted, orphaning them in the bucket forever. Claimed rows stay expired, so
 * if the R2 delete fails the next hourly run selects and retries them.
 */

import { Env } from "./types";
import { abortUpload } from "./upload-cleanup";
import { deleteCalls, deleteKeys, idOfHlsPrefix, maxSeqOf, removeHlsShare, segmentKeys } from "./hls-objects";
import { HLS_PURGE_R2_CALLS_PER_RUN } from "./share-limits";

export const PURGE_BATCH_ROWS = 500;

/** R2 calls one cron run may still spend on HLS keys; shared by settle, purge and tombstones. */
export interface HlsBudget {
  calls: number;
}

export function hlsRunBudget(): HlsBudget {
  return { calls: HLS_PURGE_R2_CALLS_PER_RUN };
}

/** Take `calls` from the budget; false (nothing taken) when it doesn't hold them. */
export function spend(budget: HlsBudget, calls: number): boolean {
  if (calls > budget.calls) return false;
  budget.calls -= calls;
  return true;
}
/**
 * A row or tombstone whose abort keeps failing is dropped once it is this old:
 * the bucket's default rule aborts incomplete multipart uploads after 7 days,
 * so nothing it held can be left by then.
 */
export const ABORT_GIVE_UP_SECONDS = 8 * 86400;
export const PURGE_MAX_BATCHES = 4;
// D1 allows at most 100 bound parameters per statement.
export const PURGE_ID_CHUNK = 90;

interface ExpiredRow {
  id: string;
  r2_key: string;
  upload_id: string | null;
  created_at?: number;
  format?: string | null;
}

/** False when the row's upload could not be aborted and is young enough to retry. */
async function abortedOrGivenUp(env: Env, r2Key: string, uploadId: string | null, createdAt: number | undefined, now: number): Promise<boolean> {
  if (await abortUpload(env, r2Key, uploadId)) return true;
  return typeof createdAt === "number" && createdAt <= now - ABORT_GIVE_UP_SECONDS;
}

function placeholders(count: number): string {
  return Array.from({ length: count }, (_, i) => `?${i + 1}`).join(", ");
}

export async function purgeExpiredClips(env: Env, hls: HlsBudget = hlsRunBudget()): Promise<number> {
  const now = Math.floor(Date.now() / 1000);
  let purged = 0;

  for (let batch = 0; batch < PURGE_MAX_BATCHES; batch++) {
    const res = await env.DB
      .prepare(`SELECT id, r2_key, upload_id FROM clips WHERE expires_at <= ?1 AND (format IS NULL OR status <> 'uploading') LIMIT ${PURGE_BATCH_ROWS}`)
      .bind(now)
      .all<ExpiredRow>();
    const selected = res.results ?? [];
    if (selected.length === 0) break;

    const claimedRows: ExpiredRow[] = [];
    for (let i = 0; i < selected.length; i += PURGE_ID_CHUNK) {
      const ids = selected.slice(i, i + PURGE_ID_CHUNK).map((r) => r.id);
      const claimed = await env.DB
        .prepare(
          `UPDATE clips SET status = 'purging' WHERE id IN (${placeholders(ids.length)}) AND expires_at <= ?${ids.length + 1} RETURNING id, r2_key, upload_id, created_at, format`,
        )
        .bind(...ids, now)
        .all<ExpiredRow>();
      claimedRows.push(...(claimed.results ?? []));
    }

    // HLS shares: their own cleanup (segment keys, tombstone, rows), while the
    // run's budget of R2 calls lasts. The others stay claimed and expired.
    for (const row of claimedRows.filter((r) => r.format === "hls")) {
      if (hls.calls <= 0) break;
      const maxSeq = await maxSeqOf(env, row.id);
      if (!spend(hls, deleteCalls(maxSeq + 1))) continue;
      if (await removeHlsShare(env, row.id, now, maxSeq)) purged++;
    }
    const rows = claimedRows.filter((r) => r.format !== "hls");

    // Already completed, aborted or unknown to R2 counts as aborted; the key
    // delete below removes any object a completed upload left behind. A row
    // whose abort really failed stays claimed and expired for the next run.
    const done: ExpiredRow[] = [];
    for (const row of rows) {
      if (!row.upload_id || (await abortedOrGivenUp(env, row.r2_key, row.upload_id, row.created_at, now))) done.push(row);
    }

    try {
      if (rows.length > 0) await env.CLIPS.delete(rows.map((r) => r.r2_key));
    } catch (err) {
      // Keep the rows so the next hourly run retries; deleting them now would
      // orphan the objects. Stop this run rather than spin on the same batch.
      console.error(JSON.stringify({ scope: "cron.purge.r2", error: (err as Error).message }));
      break;
    }

    for (let i = 0; i < done.length; i += PURGE_ID_CHUNK) {
      const ids = done.slice(i, i + PURGE_ID_CHUNK).map((r) => r.id);
      const marks = placeholders(ids.length);
      await env.DB.prepare(`DELETE FROM clip_transcripts WHERE clip_id IN (${marks})`).bind(...ids).run();
      await env.DB.prepare(`DELETE FROM vod_share_meta WHERE clip_id IN (${marks})`).bind(...ids).run();
      await env.DB.prepare(`DELETE FROM clips WHERE id IN (${marks})`).bind(...ids).run();
    }

    purged += done.length;
    if (selected.length < PURGE_BATCH_ROWS) break;
  }

  return purged;
}

// ── storage tombstones ──────────────────────────────────────────────────────

export const TOMBSTONE_BATCH_ROWS = 500;

interface TombstoneRow {
  r2_key: string;
  upload_id: string | null;
  created_at: number;
  key_count?: number | null;
}

/**
 * Retry the R2 deletes (and aborts) that an account delete, a discard or a
 * rollback could not finish. Tombstone rows stay counted in the storage budget
 * until their objects are really gone: on an R2 failure they are kept for the
 * next hourly run, and so is one whose upload abort really failed.
 *
 * A prefix tombstone (an HLS share, key_count set) deletes its segment keys
 * 0..key_count-1 by name, once its not_before has passed, inside the run's
 * budget of R2 calls; one that doesn't fit waits for the next run.
 */
export async function purgeTombstones(env: Env, hls: HlsBudget = hlsRunBudget()): Promise<number> {
  const now = Math.floor(Date.now() / 1000);
  const res = await env.DB
    .prepare(`SELECT r2_key, upload_id, created_at, key_count FROM storage_tombstones WHERE not_before IS NULL OR not_before <= ?1 LIMIT ${TOMBSTONE_BATCH_ROWS}`)
    .bind(now)
    .all<TombstoneRow>();
  const all = res.results ?? [];
  if (all.length === 0) return 0;

  const done: TombstoneRow[] = [];
  for (const row of all.filter((r) => r.key_count !== null && r.key_count !== undefined)) {
    const id = idOfHlsPrefix(row.r2_key);
    const count = Math.max(0, Number(row.key_count) || 0);
    if (!id) {
      done.push(row); // not a key this code ever writes: nothing to delete
      continue;
    }
    if (!spend(hls, deleteCalls(count))) continue;
    if (await deleteKeys(env, segmentKeys(id, 0, count))) done.push(row);
  }

  const rows = all.filter((r) => r.key_count === null || r.key_count === undefined);
  for (const row of rows) {
    if (!row.upload_id || (await abortedOrGivenUp(env, row.r2_key, row.upload_id, row.created_at, now))) done.push(row);
  }

  try {
    if (rows.length > 0) await env.CLIPS.delete(rows.map((r) => r.r2_key));
  } catch (err) {
    console.error(JSON.stringify({ scope: "cron.tombstones.r2", error: (err as Error).message }));
    // Single-object tombstones stay for the next run; prefix ones already done can go.
    done.splice(0, done.length, ...done.filter((r) => r.key_count !== null && r.key_count !== undefined));
  }

  for (let i = 0; i < done.length; i += PURGE_ID_CHUNK) {
    const keys = done.slice(i, i + PURGE_ID_CHUNK).map((r) => r.r2_key);
    await env.DB.prepare(`DELETE FROM storage_tombstones WHERE r2_key IN (${placeholders(keys.length)})`).bind(...keys).run();
  }
  return done.length;
}
