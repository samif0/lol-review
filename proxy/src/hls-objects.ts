/**
 * R2 keys of an HLS share, and the one cleanup every delete path runs.
 *
 * An HLS share stores one object per segment and run, vods/<id>/<k>/<seq>.m4s
 * (its inits live in D1). The run is part of the key so a late write from a
 * run the desktop already replaced (an upload that outlived its request, then a
 * resume at the edge over the same seq) lands on a key nobody serves instead
 * of over the committed segment. A seq is served from the run that covers it,
 * the highest k whose from_seq is at or below it; that never changes once the
 * seq is committed (a new run may only start past every committed seq).
 *
 * Every key is built from the id, a run and a seq. Run k's PUTs only claim seqs
 * from from_seq(k) up, and every claimed seq is at most vod_hls.max_seq (the
 * claim raises it before the body is read), so a cleanup deletes, for each
 * run, keys from_seq(k)..max_seq by name, at most 1000 per R2 call. It never
 * lists a prefix: a list is a Class A operation, and it would race the PUTs
 * still in flight anyway.
 *
 * Order, the same in discardRow, the owner delete, the hourly purge, the
 * cron settle and account deletion: the caller claims the row (status
 * 'purging', so no new segment PUT can claim), then removeHlsShare deletes the
 * keys, then in one batch writes a prefix tombstone per run (vods/<id>/<k>/,
 * keys 0..max_seq+8, not purged before HLS_TOMBSTONE_DELAY_SECONDS) and
 * deletes every D1 row, so a PUT that was already past its claim and whose
 * commit never ran (the isolate died) has its object deleted once more. A
 * failed R2 delete keeps the claimed row, counted in the storage budget, and
 * the hourly purge retries it.
 *
 * No imports from route modules, so upload-cleanup, clip-purge, clips and
 * web-auth all use this without an import cycle.
 */

import { Env } from "./types";
import { HLS_TOMBSTONE_DELAY_SECONDS, HLS_TOMBSTONE_EXTRA_KEYS } from "./share-limits";

/** R2 delete() takes at most 1000 keys per call. */
export const R2_DELETE_MAX_KEYS = 1000;

/** A run's start: run k serves seq from_seq(k) up to the next run's start. */
export interface HlsRunStart {
  k: number;
  from: number;
}

/** The r2_key an HLS share's clips row carries (every key of the share is under it). */
export function hlsPrefix(id: string): string {
  return `vods/${id}/`;
}

/** The prefix of run k's objects, and of its tombstone. */
export function hlsRunPrefix(id: string, run: number): string {
  return `vods/${id}/${run}/`;
}

export function hlsSegmentKey(id: string, run: number, seq: number): string {
  return `vods/${id}/${run}/${seq}.m4s`;
}

/** The id and run inside a run prefix 'vods/<id>/<k>/', or null for any other key. */
export function parseHlsRunPrefix(r2Key: string): { id: string; run: number } | null {
  const m = /^vods\/([0-9A-Za-z]{7})\/(\d)\/$/.exec(r2Key);
  return m ? { id: m[1], run: Number(m[2]) } : null;
}

/** The run that serves `seq`: the highest k whose start is at or below it (runs sorted by k). */
export function runFor(runs: HlsRunStart[], seq: number): number {
  let k = 0;
  for (const run of runs) if (run.from <= seq) k = run.k;
  return k;
}

/** Keys of run `run`, seq from..from+count-1. */
export function segmentKeys(id: string, run: number, from: number, count: number): string[] {
  const keys: string[] = [];
  for (let seq = from; seq < from + count; seq++) keys.push(hlsSegmentKey(id, run, seq));
  return keys;
}

/** Every key a share can hold at or after `fromSeq`: for each run, max(fromSeq, from_seq(k))..max_seq. */
export function r2KeysFor(row: { id: string; max_seq: number; runs: HlsRunStart[] }, fromSeq = 0): string[] {
  const keys: string[] = [];
  for (const run of row.runs) {
    const from = Math.max(fromSeq, run.from);
    keys.push(...segmentKeys(row.id, run.k, from, Math.max(0, Number(row.max_seq) - from + 1)));
  }
  return keys;
}

/** R2 delete calls `count` keys cost. */
export function deleteCalls(count: number): number {
  return Math.ceil(Math.max(0, Number(count) || 0) / R2_DELETE_MAX_KEYS);
}

/**
 * Delete `keys` in calls of at most 1000. True when every call worked; false
 * (logged) when R2 failed, and the caller keeps the bytes counted.
 */
export async function deleteKeys(env: Env, keys: string[]): Promise<boolean> {
  try {
    for (let i = 0; i < keys.length; i += R2_DELETE_MAX_KEYS) await env.CLIPS.delete(keys.slice(i, i + R2_DELETE_MAX_KEYS));
    return true;
  } catch (err) {
    console.error(JSON.stringify({ scope: "r2.hls_delete", keys: keys.length, error: (err as Error)?.message ?? String(err) }));
    return false;
  }
}

/** What a cleanup needs: the highest claimed seq (-1 before any claim) and the runs. One round trip. */
export async function keyStateOf(env: Env, id: string): Promise<{ max_seq: number; runs: HlsRunStart[] }> {
  const [state, runs] = await env.DB.batch([
    env.DB.prepare("SELECT max_seq FROM vod_hls WHERE clip_id = ?1").bind(id),
    env.DB.prepare("SELECT k, from_seq FROM vod_hls_runs WHERE clip_id = ?1 ORDER BY k").bind(id),
  ]);
  const row = ((state as D1Result<{ max_seq: number }>).results ?? [])[0];
  return {
    max_seq: row ? Number(row.max_seq) : -1,
    runs: ((runs as D1Result<{ k: number; from_seq: number }>).results ?? []).map((r) => ({ k: Number(r.k), from: Number(r.from_seq) })),
  };
}

/** key_count of the run prefix tombstones a cleanup leaves for a share whose highest claim is maxSeq. */
export function tombstoneKeyCount(maxSeq: number): number {
  return maxSeq + 1 + HLS_TOMBSTONE_EXTRA_KEYS;
}

/**
 * The tail of every HLS delete path, after the caller claimed the row (status
 * 'purging'): delete every key, then in ONE batch write a delayed prefix
 * tombstone per run and delete every D1 row of the share. The latest run's
 * tombstone counts the pending rows' bytes (what a PUT in flight may still
 * store). A share no PUT ever claimed (max_seq -1) has no object and gets no
 * tombstone. Returns false when R2 failed: the claimed row stays counted and
 * the purge retries.
 */
export async function removeHlsShare(
  env: Env,
  id: string,
  now: number,
  known?: { max_seq: number; runs: HlsRunStart[] },
): Promise<boolean> {
  const state = known ?? (await keyStateOf(env, id));
  if (state.max_seq >= 0 && !(await deleteKeys(env, r2KeysFor({ id, ...state })))) return false;
  const statements: D1PreparedStatement[] = [];
  if (state.max_seq >= 0) {
    statements.push(
      env.DB
        .prepare(
          `INSERT INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at, key_count, not_before)
           SELECT 'vods/' || ?1 || '/' || r.k || '/', NULL,
             CASE WHEN r.k = (SELECT MAX(k) FROM vod_hls_runs WHERE clip_id = ?1)
               THEN (SELECT COALESCE(SUM(bytes), 0) FROM vod_hls_segments WHERE clip_id = ?1 AND committed = 0) ELSE 0 END,
             'vod', ?2, ?3, ?4
           FROM vod_hls_runs r WHERE r.clip_id = ?1 AND ${CLAIMED}
           ON CONFLICT (r2_key) DO UPDATE SET size_bytes = MAX(size_bytes, excluded.size_bytes),
             key_count = MAX(COALESCE(key_count, 0), excluded.key_count), not_before = MAX(COALESCE(not_before, 0), excluded.not_before)`,
        )
        .bind(id, now, tombstoneKeyCount(state.max_seq), now + HLS_TOMBSTONE_DELAY_SECONDS),
    );
  }
  // Only a claimed row goes; the clips row last, so every guard still sees it.
  for (const table of ["vod_hls_segments", "vod_hls_runs", "vod_hls", "vod_share_meta", "clip_transcripts"]) {
    statements.push(env.DB.prepare(`DELETE FROM ${table} WHERE clip_id = ?1 AND ${CLAIMED}`).bind(id));
  }
  statements.push(env.DB.prepare("DELETE FROM clips WHERE id = ?1 AND format = 'hls' AND status = 'purging'").bind(id));
  await env.DB.batch(statements);
  return true;
}

const CLAIMED = "EXISTS (SELECT 1 FROM clips WHERE id = ?1 AND format = 'hls' AND status = 'purging')";

/**
 * Tombstones for single objects that a PUT in flight may still store after a
 * cleanup (an /end dropping the seqs past its edge): counted in the storage
 * budget at once and deleted by the hourly purge after `notBefore`, whether or
 * not the PUT's own commit removes its object first.
 */
export async function delayedTombstones(env: Env, objects: { key: string; bytes: number }[], now: number, notBefore: number): Promise<void> {
  if (objects.length === 0) return;
  try {
    await env.DB.batch(objects.map((o) =>
      env.DB
        .prepare(
          `INSERT INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at, key_count, not_before) VALUES (?1, NULL, ?2, 'vod', ?3, NULL, ?4)
           ON CONFLICT (r2_key) DO UPDATE SET size_bytes = MAX(size_bytes, excluded.size_bytes), not_before = MAX(COALESCE(not_before, 0), excluded.not_before)`,
        )
        .bind(o.key, o.bytes, now, notBefore)));
  } catch (err) {
    console.error(JSON.stringify({ scope: "r2.hls_tombstones", keys: objects.length, error: (err as Error)?.message ?? String(err) }));
  }
}
