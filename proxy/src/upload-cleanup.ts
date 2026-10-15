/**
 * Cleanup shared by every path that drops a multipart upload or an object:
 * discards, owner delete, the hourly purge, account delete and the single-PUT
 * rollbacks.
 *
 * The rule: bytes stay counted in the storage budget until R2 has really let
 * go of them. An abort that fails for any reason other than "this upload no
 * longer exists" keeps the row (an expired 'purging' row) or a tombstone, so
 * the hourly purge retries the abort and the delete. A row is dropped only
 * after both worked.
 */

import { Env } from "./types";
import { IDLE_SWEEP_SECONDS } from "./share-limits";
import { removeHlsShare } from "./hls-objects";

/** True when R2 says the upload is already gone (completed, aborted or unknown). */
export function isGoneUploadError(err: unknown): boolean {
  const message = err instanceof Error ? err.message : String(err);
  return /NoSuchUpload|\(10024\)|does not exist/i.test(message);
}

/**
 * Abort a multipart upload. True when it is aborted now or was already gone;
 * false when R2 failed and the caller must keep the bytes counted and retry.
 */
export async function abortUpload(env: Env, r2Key: string, uploadId: string | null | undefined): Promise<boolean> {
  if (!uploadId) return true;
  try {
    await env.CLIPS.resumeMultipartUpload(r2Key, uploadId).abort();
    return true;
  } catch (err) {
    if (isGoneUploadError(err)) return true;
    console.error(JSON.stringify({ scope: "r2.abort", key: r2Key, error: (err as Error)?.message ?? String(err) }));
    return false;
  }
}

/**
 * Record an object whose delete failed when no clips row can hold it any more.
 * It stays counted in the storage budget and the hourly purge retries it.
 * Best effort: a D1 failure here is logged, not thrown.
 */
export async function tombstoneObject(
  env: Env,
  r2Key: string,
  uploadId: string | null,
  sizeBytes: number,
  kind: string,
): Promise<void> {
  try {
    await env.DB
      .prepare(
        "INSERT OR IGNORE INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at) VALUES (?1, ?2, ?3, ?4, ?5)",
      )
      .bind(r2Key, uploadId, sizeBytes, kind, Math.floor(Date.now() / 1000))
      .run();
  } catch (err) {
    console.error(JSON.stringify({ scope: "r2.tombstone", key: r2Key, error: (err as Error)?.message ?? String(err) }));
  }
}

/**
 * Remove a still-pending row everywhere: abort its upload, delete its key,
 * transcript, VOD meta, row. The row is first claimed with a guarded UPDATE
 * (status 'uploading' → 'purging', expired as of now), so a row that a
 * concurrent complete just finalized to 'ready' is never touched. A claimed
 * row can't be finalized (complete requires 'uploading'), is hidden from every
 * route (expired), and is retried by the hourly purge when the abort or the
 * delete fails here. An HLS share (no multipart upload) goes through
 * removeHlsShare: its segment keys, a prefix tombstone, its rows.
 */
export async function discardRow(env: Env, id: string): Promise<void> {
  const now = Math.floor(Date.now() / 1000);
  const claimed = await env.DB
    .prepare(
      "UPDATE clips SET status = 'purging', expires_at = ?2 WHERE id = ?1 AND status = 'uploading' RETURNING r2_key, upload_id, format",
    )
    .bind(id, now)
    .first<{ r2_key: string; upload_id: string | null; format?: string | null }>();
  if (!claimed) return;
  if (claimed.format === "hls") {
    await removeHlsShare(env, id, now);
    return;
  }
  // A failed abort leaves uploaded parts in R2: keep the claimed row counted.
  if (!(await abortUpload(env, claimed.r2_key, claimed.upload_id))) return;
  try {
    await env.CLIPS.delete(claimed.r2_key);
  } catch {
    // Keep the claimed (expired) row so the hourly purge retries the delete
    // instead of orphaning the object.
    return;
  }
  await env.DB.prepare("DELETE FROM clip_transcripts WHERE clip_id = ?1").bind(id).run();
  await env.DB.prepare("DELETE FROM vod_share_meta WHERE clip_id = ?1").bind(id).run();
  await env.DB.prepare("DELETE FROM clips WHERE id = ?1").bind(id).run();
}

/**
 * Discard pending uploads of ANY account with no part activity for
 * IDLE_SWEEP_SECONDS, at most `limit` of them. Their reservations stop
 * holding the shared storage pool. Returns how many were selected. HLS shares
 * are left to the cron settle (vod-hls-upload.ts), which ends a watchable one
 * at its edge instead of deleting it, and never touches a paused one.
 */
export async function sweepIdleUploads(env: Env, now: number, limit: number): Promise<number> {
  const idle = await env.DB
    .prepare(
      `SELECT id FROM clips WHERE format IS NULL AND status = 'uploading' AND COALESCE(last_activity_at, created_at) <= ?1 LIMIT ${Math.max(1, Math.floor(limit))}`,
    )
    .bind(now - IDLE_SWEEP_SECONDS)
    .all<{ id: string }>();
  const rows = idle.results ?? [];
  for (const row of rows) await discardRow(env, row.id);
  return rows.length;
}
