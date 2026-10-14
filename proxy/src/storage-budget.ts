/**
 * Global storage budget over every object in the CLIPS bucket.
 *
 * Usage is ONE statement over every clips row (both kinds, every status) plus
 * the storage_tombstones rows. There is deliberately no expiry or status
 * filter: an expired-but-unpurged row, an 'uploading' reservation and a
 * 'purging' row all still hold (or may hold) R2 bytes until the hourly purge
 * removes them.
 *
 * Each upload path checks the budget before it reserves, then re-checks after
 * its row is committed (the same pattern as the per-user recount): D1 can't
 * hold a transaction across the R2 call, so the loser of a race rolls itself
 * back. The single-PUT clip path cannot reserve before streaming (its row is
 * inserted after the put), so in-flight single PUTs can push usage over the
 * budget by at most their own bytes (each <= 100 MiB) until the post-insert
 * recount rolls them back. That overshoot is accepted.
 */

import { Env } from "./types";
import { jsonResponse } from "./http";
import { RELEASES_MAX, totalBudgetBytes, userClipBudgetBytes, vodBudgetBytes } from "./share-limits";

export interface StorageUsage {
  used: number;
  vod_used: number;
}

export interface PoolRelease {
  at: number;
  bytes: number;
  vod_bytes: number;
}

export interface StoragePool {
  total_budget_bytes: number;
  vod_budget_bytes: number;
  used_bytes: number;
  vod_used_bytes: number;
  free_bytes: number;
  releases: PoolRelease[];
}

// The derived table is aliased so this text never contains the per-user
// quota's "COALESCE(SUM(size_bytes)" fragment.
export const STORAGE_USAGE_SQL =
  "SELECT COALESCE(SUM(b.size_bytes), 0) AS used, COALESCE(SUM(CASE WHEN b.kind = 'vod' THEN b.size_bytes ELSE 0 END), 0) AS vod_used FROM (SELECT size_bytes, kind FROM clips UNION ALL SELECT size_bytes, kind FROM storage_tombstones) AS b";

const RELEASE_ROWS_SQL = "SELECT kind, size_bytes, status, expires_at FROM clips ORDER BY expires_at ASC LIMIT 1000";
const RELEASE_TOMBS_SQL = "SELECT kind, size_bytes FROM storage_tombstones LIMIT 1000";

export const STORAGE_FULL_MESSAGE = "Revu's sharing space is full right now. Try again in a few hours.";

/** 403 refusal for clip uploads; 4.0.1 desktops show `message` for quota_exceeded. */
export function storageFull(): Response {
  return jsonResponse({ error: "quota_exceeded", reason: "storage_full", message: STORAGE_FULL_MESSAGE }, 403);
}

export async function storageUsage(db: D1Database): Promise<StorageUsage> {
  const row = await db.prepare(STORAGE_USAGE_SQL).first<{ used: number | null; vod_used: number | null }>();
  return { used: Number(row?.used ?? 0), vod_used: Number(row?.vod_used ?? 0) };
}

/** A clip of `bytes` fits when used + bytes <= TOTAL. Pass 0 for the post-insert recount. */
export function clipFits(env: Env, usage: StorageUsage, bytes: number): boolean {
  return usage.used + bytes <= totalBudgetBytes(env);
}

/** True when the account is allowlisted for full-game sharing (vod_sharers). */
export async function isVodSharer(db: D1Database, userId: number): Promise<boolean> {
  const row = await db.prepare("SELECT 1 AS ok FROM vod_sharers WHERE user_id = ?1 LIMIT 1").bind(userId).first<{ ok: number }>();
  return !!row;
}

/**
 * True when `clipBytes` (the account's unexpired clip bytes, reservations
 * included, after this upload) is over its share of the pool. Accounts in
 * vod_sharers are trusted and only get the per-user clip quota; everyone else
 * gets USER_CLIP_BUDGET_BYTES, so no single account can fill the pool. The
 * allowlist is read only when the bytes are actually over the share.
 */
export async function overAccountShare(env: Env, userId: number, clipBytes: number): Promise<boolean> {
  if (clipBytes <= userClipBudgetBytes(env)) return false;
  return !(await isVodSharer(env.DB, userId));
}

/** A VOD of `bytes` fits when it fits the total AND the VOD subset. */
export function vodFits(env: Env, usage: StorageUsage, bytes: number): boolean {
  return usage.used + bytes <= totalBudgetBytes(env) && usage.vod_used + bytes <= vodBudgetBytes(env);
}

/** The hourly cron run that will purge something expiring at `t`. */
export function nextHour(t: number): number {
  return (Math.floor(t / 3600) + 1) * 3600;
}

/** When stored bytes are expected to be released, grouped by purge hour, ascending. */
export async function readReleases(db: D1Database, now: number): Promise<PoolRelease[]> {
  const rows = await db
    .prepare(RELEASE_ROWS_SQL)
    .all<{ kind: string; size_bytes: number; status: string; expires_at: number }>();
  const tombs = await db.prepare(RELEASE_TOMBS_SQL).all<{ kind: string; size_bytes: number }>();

  const grouped = new Map<number, PoolRelease>();
  const add = (at: number, kind: string, size: number) => {
    const g = grouped.get(at) ?? { at, bytes: 0, vod_bytes: 0 };
    const bytes = Number(size) || 0;
    g.bytes += bytes;
    if (kind === "vod") g.vod_bytes += bytes;
    grouped.set(at, g);
  };
  for (const r of rows.results ?? []) {
    const at = r.status === "purging" ? nextHour(now) : nextHour(Math.max(r.expires_at, now));
    add(at, r.kind, r.size_bytes);
  }
  for (const t of tombs.results ?? []) add(nextHour(now), t.kind, t.size_bytes);

  return [...grouped.values()].sort((a, b) => a.at - b.at).slice(0, RELEASES_MAX);
}

/** What a VOD can use right now: max(0, min(free total, free VOD)). */
export function freeBytesOf(env: Env, usage: StorageUsage): number {
  const freeTotal = totalBudgetBytes(env) - usage.used;
  const freeVod = vodBudgetBytes(env) - usage.vod_used;
  return Math.max(0, Math.min(freeTotal, freeVod));
}

export async function poolSnapshot(env: Env, now: number): Promise<StoragePool> {
  const usage = await storageUsage(env.DB);
  const releases = await readReleases(env.DB, now);
  return {
    total_budget_bytes: totalBudgetBytes(env),
    vod_budget_bytes: vodBudgetBytes(env),
    used_bytes: usage.used,
    vod_used_bytes: usage.vod_used,
    free_bytes: freeBytesOf(env, usage),
    releases,
  };
}

/**
 * Earliest time a VOD of `size` bytes fits: `now` when it fits already, else
 * the first release that frees enough, else null. Mirrored in the sidecar
 * (VodSharePool.FitsAt); keep the two identical.
 */
export function fitsAt(pool: StoragePool, size: number, now: number): number | null {
  let freeTotal = pool.total_budget_bytes - pool.used_bytes;
  let freeVod = pool.vod_budget_bytes - pool.vod_used_bytes;
  if (Math.min(freeTotal, freeVod) >= size) return now;
  for (const r of [...pool.releases].sort((a, b) => a.at - b.at)) {
    freeTotal += r.bytes;
    freeVod += r.vod_bytes;
    if (Math.min(freeTotal, freeVod) >= size) return r.at;
  }
  return null;
}
