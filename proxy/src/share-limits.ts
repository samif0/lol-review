/**
 * Storage budget and full-game (VOD) sharing limits: the single source of truth.
 *
 * The two budgets cover EVERY stored object in the CLIPS bucket: clips and
 * VODs, in every state (uploading reservations, ready, purging rows) plus the
 * storage_tombstones rows. Paid storage later only means raising
 * TOTAL_BUDGET_BYTES / VOD_BUDGET_BYTES in wrangler.toml [vars].
 */

import { Env } from "./types";
import { opsFlagOn } from "./ops-flags";
import { playSecretOk } from "./play-token";

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
/**
 * At most this many idle uploads are discarded by one upload start. Each
 * discard costs 4 D1 queries, so with the caller's own idle discards and the
 * post-insert recount a start stays well inside the 50 queries per invocation
 * that Workers Free allows (about 40 at worst).
 */
export const IDLE_SWEEP_ON_START = 5;
/**
 * At most this many idle uploads are discarded by one hourly cron run. Each
 * discard costs about 6 D1 and R2 calls, and the run is the last step of the
 * cron, so a backlog can only use what the purge and the pruning left of the
 * per-invocation query budget on Workers Free; later runs clear the rest.
 */
export const IDLE_SWEEP_PER_RUN = 5;

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

// ── HLS shares: watch a full game while it uploads ──────────────────────────

/** One GOP is one fragment is one segment (the desktop's -g). */
export const HLS_SEGMENT_SECONDS = 2;
/** #EXT-X-TARGETDURATION, fixed: EXTINF rounds to at most 6 (RFC 8216 4.3.3.1). */
export const HLS_TARGET_DURATION = 6;
/** Longest EXTINF a segment may declare; anything longer is an encoder fault. */
export const HLS_MAX_SEGMENT_MS = 6499;
/** One segment body (measured: at most 2.0 MB per 2 s at 1080p60). */
export const HLS_SEGMENT_MAX_BYTES = 16_777_216;
/** One run's init segment, ftyp + moov (measured 1,358 to 1,408 bytes). */
export const HLS_INIT_MAX_BYTES = 16_384;
/** Playable once this much media is committed contiguously from 0 (with run 0's init). */
export const HLS_PLAYABLE_MS = 8000;
/** Runs per share (k = 0..7): run 0 plus resumes at the edge. */
export const HLS_MAX_RUNS = 8;
/** A paused share is ended (or discarded) by the cron after this long. */
export const HLS_PAUSE_MAX_SECONDS = 21_600;
/** Attempts per share: segment and run PUTs < 2 x part_count + HLS_PUT_SLACK. */
export const HLS_PUT_SLACK = 16;
/** Stored bytes (hls_bytes) may pass the reservation by one segment at most. */
export const HLS_RESERVATION_SLACK_BYTES = HLS_SEGMENT_MAX_BYTES;
/** A reservation that runs out grows by max(this, 2%), never past VOD_MAX_BYTES. */
export const HLS_GROW_MIN_BYTES = 67_108_864;
/** Paused reasons a desktop may send. */
export const HLS_PAUSE_REASONS = new Set(["game"]);
/** A cleanup deletes once more this long after it ran, once in-flight PUTs have settled. */
export const HLS_TOMBSTONE_DELAY_SECONDS = 600;
/** ...covering this many seqs past the highest claimed one. */
export const HLS_TOMBSTONE_EXTRA_KEYS = 8;
/** HLS shares the hourly cron settles at most (ends at the edge or discards) per run. */
export const HLS_SETTLE_PER_RUN = 10;
/**
 * R2 delete calls (1000 keys each) one hourly run may spend on HLS keys, purge
 * and tombstones together; a 2-hour game costs 4. The rest waits for the next
 * run. Sized for Workers Paid, which HLS needs anyway.
 */
export const HLS_PURGE_R2_CALLS_PER_RUN = 40;

/**
 * Segment slots for a declared duration of `durationS` seconds:
 * ceil(d/2) + 2 + max(10, ceil(0.01 x d)). Slack for a short tail and a
 * resume or two; slots come from the probed duration, never from bytes.
 */
export function hlsSlots(durationS: number): number {
  return Math.ceil(durationS / HLS_SEGMENT_SECONDS) + 2 + Math.max(10, Math.ceil(0.01 * durationS));
}

/** Most PUT attempts (segments and runs) one HLS share may make. */
export function hlsAttemptCap(partCount: number): number {
  return PART_PUT_FACTOR * partCount + HLS_PUT_SLACK;
}

/**
 * Does VOD_HLS let `userId` start HLS shares? "1" (or "on") is everyone,
 * "users:<id>,<id>" is those accounts only; anything else, unset included,
 * is nobody. Fails closed.
 */
export function vodHlsAllows(raw: string | undefined, userId: number): boolean {
  const v = String(raw ?? "0").trim().toLowerCase();
  if (v === "1" || v === "on") return true;
  if (!v.startsWith("users:")) return false;
  return v.slice("users:".length).split(",").map((s) => s.trim()).filter((s) => s.length > 0).includes(String(userId));
}

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

/**
 * VOD playback right now: the VOD_SERVING var AND the `vod_serving` ops flag
 * (ops-flags.ts), so an operator can stop every public VOD route within 30 s
 * without a deploy.
 */
export async function vodServingLive(env: Env): Promise<boolean> {
  return vodServingOn(env) && (await opsFlagOn(env, "vod_serving"));
}

/**
 * May `userId` start an HLS share now? VOD_HLS must allow the account, the play
 * token secret must be set (no secret, no way to play) and the `vod_hls` ops
 * flag must not be "0". Gates new starts only.
 */
export async function vodHlsOn(env: Env, userId: number): Promise<boolean> {
  return vodHlsAllows(env.VOD_HLS, userId) && playSecretOk(env) && (await opsFlagOn(env, "vod_hls"));
}

/** Public watch link for a VOD share: ${WATCH_BASE}/v/<id>. */
export function vodWatchUrl(env: Env, id: string): string {
  const base = (env.WATCH_BASE || "https://revu.lol").replace(/\/+$/, "");
  return `${base}/v/${id}`;
}

/**
 * True when the request came to the host of PUBLIC_BASE (clips.revu.lol, or
 * localhost:8787 in local dev). The VOD media routes answer only there: never
 * on workers.dev, where the desktop's API lives and which no page should embed.
 */
export function onPublicHost(request: Request, env: Env): boolean {
  let host: string;
  try {
    host = new URL(env.PUBLIC_BASE || "https://clips.revu.lol").host;
  } catch {
    return false;
  }
  return new URL(request.url).host === host;
}

/**
 * Watchable: run 0's init is stored and HLS_PLAYABLE_MS of media is committed
 * contiguously from 0, or the share ended with at least one segment.
 */
export function hlsPlayable(s: { runs: number; media_ms: number; ended: number | boolean; segments: number }): boolean {
  return (Number(s.runs) >= 1 && Number(s.media_ms) >= HLS_PLAYABLE_MS) || (!!s.ended && Number(s.segments) >= 1);
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
