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
 *   3. aborts any multipart upload still attached to a claimed row (best effort:
 *      it may already be completed or gone);
 *   4. deletes ALL the claimed R2 keys in one call (R2 takes at most 1000);
 *   5. deletes the transcripts, then the rows, BY ID in chunks small enough for
 *      D1's 100-bound-parameter limit.
 *
 * Deleting by the selected ids (not "WHERE expires_at <= now") is deliberate: the
 * old unbounded DELETE removed rows past the LIMIT whose R2 objects were never
 * deleted, orphaning them in the bucket forever. Claimed rows stay expired, so
 * if the R2 delete fails the next hourly run selects and retries them.
 */

import { Env } from "./types";

export const PURGE_BATCH_ROWS = 500;
export const PURGE_MAX_BATCHES = 4;
// D1 allows at most 100 bound parameters per statement.
export const PURGE_ID_CHUNK = 90;

interface ExpiredRow {
  id: string;
  r2_key: string;
  upload_id: string | null;
}

function placeholders(count: number): string {
  return Array.from({ length: count }, (_, i) => `?${i + 1}`).join(", ");
}

export async function purgeExpiredClips(env: Env): Promise<number> {
  const now = Math.floor(Date.now() / 1000);
  let purged = 0;

  for (let batch = 0; batch < PURGE_MAX_BATCHES; batch++) {
    const res = await env.DB
      .prepare(`SELECT id, r2_key, upload_id FROM clips WHERE expires_at <= ?1 LIMIT ${PURGE_BATCH_ROWS}`)
      .bind(now)
      .all<ExpiredRow>();
    const selected = res.results ?? [];
    if (selected.length === 0) break;

    const rows: ExpiredRow[] = [];
    for (let i = 0; i < selected.length; i += PURGE_ID_CHUNK) {
      const ids = selected.slice(i, i + PURGE_ID_CHUNK).map((r) => r.id);
      const claimed = await env.DB
        .prepare(
          `UPDATE clips SET status = 'purging' WHERE id IN (${placeholders(ids.length)}) AND expires_at <= ?${ids.length + 1} RETURNING id, r2_key, upload_id`,
        )
        .bind(...ids, now)
        .all<ExpiredRow>();
      rows.push(...(claimed.results ?? []));
    }

    for (const row of rows) {
      if (!row.upload_id) continue;
      try {
        await env.CLIPS.resumeMultipartUpload(row.r2_key, row.upload_id).abort();
      } catch {
        // Already completed, already aborted, or unknown to R2. The key delete
        // below removes any object a completed upload left behind.
      }
    }

    try {
      if (rows.length > 0) await env.CLIPS.delete(rows.map((r) => r.r2_key));
    } catch (err) {
      // Keep the rows so the next hourly run retries; deleting them now would
      // orphan the objects. Stop this run rather than spin on the same batch.
      console.error(JSON.stringify({ scope: "cron.purge.r2", error: (err as Error).message }));
      break;
    }

    for (let i = 0; i < rows.length; i += PURGE_ID_CHUNK) {
      const ids = rows.slice(i, i + PURGE_ID_CHUNK).map((r) => r.id);
      const marks = placeholders(ids.length);
      await env.DB.prepare(`DELETE FROM clip_transcripts WHERE clip_id IN (${marks})`).bind(...ids).run();
      await env.DB.prepare(`DELETE FROM clips WHERE id IN (${marks})`).bind(...ids).run();
    }

    purged += rows.length;
    if (selected.length < PURGE_BATCH_ROWS) break;
  }

  return purged;
}
