/**
 * R2 keys of an HLS share, and the one cleanup every delete path runs.
 *
 * An HLS share stores one object per segment, vods/<id>/<seq>.m4s (its inits
 * live in D1). Every key is built from the id and a seq, and every seq a PUT
 * ever claimed is at most vod_hls.max_seq (the claim raises it before the
 * body is read), so a cleanup deletes keys 0..max_seq by name, at most 1000
 * per R2 call. It never lists the prefix: a list is a Class A operation, and
 * it would race the PUTs still in flight anyway.
 *
 * Order, the same in discardRow, the owner delete, the hourly purge, the
 * cron settle and account deletion: the caller claims the row (status
 * 'purging', so no new segment PUT can claim), then removeHlsShare deletes the
 * keys, then in one batch writes a prefix tombstone and deletes every D1 row.
 * The tombstone stands for keys 0..max_seq+8 and is not purged before
 * HLS_TOMBSTONE_DELAY_SECONDS, so a PUT that was already past its claim and
 * whose commit never ran (the isolate died) has its object deleted once more.
 * A failed R2 delete keeps the claimed row, counted in the storage budget,
 * and the hourly purge retries it.
 *
 * No imports from route modules, so upload-cleanup, clip-purge, clips and
 * web-auth all use this without an import cycle.
 */

import { Env } from "./types";
import { HLS_TOMBSTONE_DELAY_SECONDS, HLS_TOMBSTONE_EXTRA_KEYS } from "./share-limits";

/** R2 delete() takes at most 1000 keys per call. */
export const R2_DELETE_MAX_KEYS = 1000;

/** The r2_key an HLS share's clips row (and its prefix tombstone) carries. */
export function hlsPrefix(id: string): string {
  return `vods/${id}/`;
}

export function hlsSegmentKey(id: string, seq: number): string {
  return `vods/${id}/${seq}.m4s`;
}

/** The id inside an HLS prefix 'vods/<id>/', or null for any other key. */
export function idOfHlsPrefix(r2Key: string): string | null {
  const m = /^vods\/([0-9A-Za-z]{7})\/$/.exec(r2Key);
  return m ? m[1] : null;
}

/** Every key an HLS share can hold: seq 0..max_seq. */
export function r2KeysFor(row: { id: string; max_seq: number }): string[] {
  return segmentKeys(row.id, 0, Math.max(0, Math.floor(Number(row.max_seq) + 1) || 0));
}

/** Keys of seq from..from+count-1. */
export function segmentKeys(id: string, from: number, count: number): string[] {
  const keys: string[] = [];
  for (let seq = from; seq < from + count; seq++) keys.push(hlsSegmentKey(id, seq));
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

/** A share's highest claimed seq (-1 before any claim), or -1 when it has no vod_hls row. */
export async function maxSeqOf(env: Env, id: string): Promise<number> {
  const row = await env.DB.prepare("SELECT max_seq FROM vod_hls WHERE clip_id = ?1").bind(id).first<{ max_seq: number }>();
  return row ? Number(row.max_seq) : -1;
}

/** key_count of the prefix tombstone a cleanup leaves for a share whose highest claim is maxSeq. */
export function tombstoneKeyCount(maxSeq: number): number {
  return maxSeq + 1 + HLS_TOMBSTONE_EXTRA_KEYS;
}

/**
 * The tail of every HLS delete path, after the caller claimed the row (status
 * 'purging'): delete keys 0..max_seq, then in ONE batch write the prefix
 * tombstone and delete every D1 row of the share. The tombstone counts the
 * pending rows' bytes (what a PUT in flight may still store). A share no PUT
 * ever claimed (max_seq -1) has no object and gets no tombstone. Returns false
 * when R2 failed: the claimed row stays counted and the purge retries.
 */
export async function removeHlsShare(env: Env, id: string, now: number, knownMaxSeq?: number): Promise<boolean> {
  const maxSeq = knownMaxSeq ?? (await maxSeqOf(env, id));
  if (maxSeq >= 0 && !(await deleteKeys(env, r2KeysFor({ id, max_seq: maxSeq })))) return false;
  const statements: D1PreparedStatement[] = [];
  if (maxSeq >= 0) {
    statements.push(
      env.DB
        .prepare(
          `INSERT INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at, key_count, not_before)
           SELECT ?1, NULL, (SELECT COALESCE(SUM(bytes), 0) FROM vod_hls_segments WHERE clip_id = ?2 AND committed = 0), 'vod', ?3, ?4, ?5
           WHERE EXISTS (SELECT 1 FROM clips WHERE id = ?2 AND format = 'hls' AND status = 'purging')
           ON CONFLICT (r2_key) DO UPDATE SET size_bytes = MAX(size_bytes, excluded.size_bytes),
             key_count = MAX(COALESCE(key_count, 0), excluded.key_count), not_before = MAX(COALESCE(not_before, 0), excluded.not_before)`,
        )
        .bind(hlsPrefix(id), id, now, tombstoneKeyCount(maxSeq), now + HLS_TOMBSTONE_DELAY_SECONDS),
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
