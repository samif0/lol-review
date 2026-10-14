/**
 * Storage budget and full-game (VOD) sharing limits: the single source of truth.
 *
 * The two budgets cover EVERY stored object in the CLIPS bucket: clips and
 * VODs, in every state (uploading reservations, ready, purging rows) plus the
 * storage_tombstones rows. Paid storage later only means raising
 * TOTAL_BUDGET_BYTES / VOD_BUDGET_BYTES in wrangler.toml [vars].
 */

import { Env } from "./types";

export const DEFAULT_TOTAL_BUDGET_BYTES = 9_000_000_000;
export const DEFAULT_VOD_BUDGET_BYTES = 7_000_000_000;

/** A share link lives 3 days, counted from upload INIT (matches the bucket's 3-day lifecycle rule). */
export const VOD_TTL_SECONDS = 259_200;
export const VOD_MAX_BYTES = 2_147_483_648; // 2 GiB
export const VOD_MAX_SECONDS = 7200;
export const VOD_PART_SIZE_BYTES = 16_777_216; // 16 MiB
export const VOD_MAX_LIVE_PER_USER = 3;
export const VOD_MAX_PENDING_PER_USER = 1;
export const VOD_MAX_STARTS_PER_DAY = 6; // per UTC day
export const VOD_DAILY_PLAY_CAP = 200; // per VOD per UTC day
export const VOD_META_MAX_BODY_BYTES = 65_536;
export const VOD_MARKERS_MAX_BYTES = 49_152; // serialized markers array
export const VOD_NOTES_MAX_BYTES = 14_336; // serialized notes array
export const VOD_MAX_MARKERS = 1000;
export const VOD_MAX_NOTES = 200;
export const VOD_LABEL_MAX_CHARS = 40;
export const VOD_NOTE_MAX_CHARS = 500;

/** Part PUT attempts per multipart row (every kind): 2 x part_count + 4. */
export const PART_PUT_FACTOR = 2;
export const PART_PUT_SLACK = 4;

/**
 * Part PUTs one account may make per UTC day, every multipart upload and kind
 * together (upload_part_usage). It survives row deletes, so deleting a pending
 * upload and starting another can't reset it. 2000 x 16 MiB is about 33 GB a
 * day, far past anything the storage budget lets one account keep.
 */
export const PART_PUTS_PER_DAY = 2000;

/**
 * Clip bytes (ready and pending reservations, unexpired) one account that is
 * NOT in vod_sharers may hold: a third of the default total, so one account
 * can't fill the shared pool with reservations or uploads. The older per-user
 * clip quota (12 GiB) still applies on top. USER_CLIP_BUDGET_BYTES overrides it.
 */
export const DEFAULT_USER_CLIP_BUDGET_BYTES = 3_000_000_000;

/**
 * A pending upload of any account with no part activity for this long is
 * discarded by the hourly cron, and by any upload start that would otherwise
 * be refused for storage. Twice the owner's own idle auto-abort, so a client
 * riding out a short network drop is never swept.
 */
export const IDLE_SWEEP_SECONDS = 1800;
/** At most this many idle uploads are discarded by one upload start. */
export const IDLE_SWEEP_ON_START = 10;

/**
 * A VOD's upload window: long enough to send the declared size at
 * VOD_UPLOAD_MIN_BPS (16 MiB parts were chosen for that floor) plus an hour,
 * never under the clip window (6 h) and never over 12 h. The share TTL still
 * counts from init, so the link always ends inside the 3-day lifecycle rule.
 */
export const VOD_UPLOAD_MIN_BPS = 450_000;
export const VOD_UPLOAD_WINDOW_MIN_SECONDS = 21_600;
export const VOD_UPLOAD_WINDOW_MAX_SECONDS = 43_200;

export function vodUploadWindowSeconds(sizeBytes: number): number {
  const transfer = Math.ceil((Math.max(0, sizeBytes) * 8) / VOD_UPLOAD_MIN_BPS) + 3600;
  return Math.min(VOD_UPLOAD_WINDOW_MAX_SECONDS, Math.max(VOD_UPLOAD_WINDOW_MIN_SECONDS, transfer));
}

/** At most this many grouped release times in the status pool. */
export const RELEASES_MAX = 48;

const BUDGET_PATTERN = /^\d{1,13}$/;

/** Digits only (1 to 13 of them, no spaces or signs) and above zero, else the default. */
function parseBudget(raw: string | undefined, fallback: number): number {
  if (typeof raw !== "string" || !BUDGET_PATTERN.test(raw)) return fallback;
  const n = Number(raw);
  return n > 0 ? n : fallback;
}

function switchOn(raw: string | undefined): boolean {
  if (raw === undefined || raw === null) return true;
  const v = String(raw).trim().toLowerCase();
  return v !== "0" && v !== "off";
}

/** Bytes every stored object together may use. */
export function totalBudgetBytes(env: Env): number {
  return parseBudget(env.TOTAL_BUDGET_BYTES, DEFAULT_TOTAL_BUDGET_BYTES);
}

/** Bytes VOD objects may use; never more than the total budget. */
export function vodBudgetBytes(env: Env): number {
  return Math.min(parseBudget(env.VOD_BUDGET_BYTES, DEFAULT_VOD_BUDGET_BYTES), totalBudgetBytes(env));
}

/** Clip bytes one account outside vod_sharers may hold (see DEFAULT_USER_CLIP_BUDGET_BYTES). */
export function userClipBudgetBytes(env: Env): number {
  return parseBudget(env.USER_CLIP_BUDGET_BYTES, DEFAULT_USER_CLIP_BUDGET_BYTES);
}

/** Kill switch for new VOD share starts (init). Parts and complete are not blocked. */
export function vodSharingOn(env: Env): boolean {
  return switchOn(env.VOD_SHARING);
}

/** Kill switch for VOD playback (/vod-file). */
export function vodServingOn(env: Env): boolean {
  return switchOn(env.VOD_SERVING);
}

/** Public watch link for a VOD share: ${WATCH_BASE}/v/<id>. */
export function vodWatchUrl(env: Env, id: string): string {
  const base = (env.WATCH_BASE || "https://revu.lol").replace(/\/+$/, "");
  return `${base}/v/${id}`;
}

// ── UTC day helpers (daily starts and the play cap) ─────────────────────────

/** 'YYYY-MM-DD' (UTC) for a unix-seconds time. */
export function utcDayOf(nowSeconds: number): string {
  return new Date(nowSeconds * 1000).toISOString().slice(0, 10);
}

/** Unix seconds of the next UTC midnight after `nowSeconds`. */
export function nextUtcMidnight(nowSeconds: number): number {
  return (Math.floor(nowSeconds / 86400) + 1) * 86400;
}
