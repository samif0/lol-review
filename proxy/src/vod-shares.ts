/**
 * Full-game (VOD) shares, desktop side (Bearer session):
 *
 *   GET  /vod-shares/status           allowlist, limits, usage, storage pool, own shares
 *   POST /vod-shares/uploads          reserve a VOD row + R2 multipart upload
 *   PUT  /vod-shares/:id/parts/:n     one raw 16 MiB part (shared clip part handler)
 *   POST /vod-shares/:id/complete     assemble, sniff, finalize (shared clip complete)
 *   PUT  /vod-shares/:id/markers      markers document v1 (vod-markers.ts)
 *
 * With format=hls the init reserves an HLS share instead (segments that play
 * while they upload; vod-hls-upload.ts holds its routes), after the same
 * checks in the same order.
 *
 * VOD rows live in `clips` with kind = 'vod' and R2 key vods/<id>.mp4, so the
 * hourly purge, owner delete (DELETE /clips/:id), account delete and the
 * storage budget treat them like every other stored object. Only allowlisted
 * accounts (vod_sharers, filled by hand at deploy time) can start one. None of
 * these routes spend the Riot budget.
 */

import { Env } from "./types";
import { jsonResponse, mediaTypeOf, parseStrictInt, readSmallBody } from "./http";
import { UPLOAD_IDLE_ABORT_SECONDS, uniqueSlug } from "./clips";
import { completeUpload, discardRow, handleUploadPart } from "./clip-uploads";
import { vodInitLimitOrDeny, vodStatusLimitOrDeny } from "./user-limits";
import { fitsAt, isVodSharer, poolSnapshot, storageUsage, vodFits } from "./storage-budget";
import { sweepIdleUploads } from "./upload-cleanup";
import { normalizeVodMeta, vodTitle } from "./vod-markers";
import { reserveHlsShare } from "./vod-hls-upload";
import { settleHlsShare } from "./vod-hls-end";
import {
  HLS_INIT_MAX_BYTES,
  HLS_MAX_RUNS,
  HLS_PAUSE_MAX_SECONDS,
  HLS_PLAYABLE_MS,
  HLS_SEGMENT_MAX_BYTES,
  HLS_SEGMENT_SECONDS,
  HLS_TARGET_DURATION,
  IDLE_SWEEP_ON_START,
  nextUtcMidnight,
  utcDayOf,
  VOD_DAILY_PLAY_CAP,
  VOD_MARKERS_MAX_BYTES,
  VOD_MAX_BYTES,
  VOD_MAX_LIVE_PER_USER,
  VOD_MAX_PENDING_PER_USER,
  VOD_MAX_SECONDS,
  VOD_MAX_STARTS_PER_DAY,
  VOD_META_MAX_BODY_BYTES,
  VOD_NOTES_MAX_BYTES,
  VOD_PART_SIZE_BYTES,
  VOD_TTL_SECONDS,
  vodHlsOn,
  vodServingLive,
  vodSharingOn,
  vodUploadWindowSeconds,
  vodWatchUrl,
} from "./share-limits";

const QUALITIES = new Set(["1080p", "720p"]);
/**
 * Desktops from 4.3 send the share copy's frame rate; it is stored with the quality
 * ("1080p60", "720p30") so the watch page can show it. Older desktops send none and keep the
 * bare resolution.
 */
const FRAME_RATES = new Set(["30", "60"]);

function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

function refuse(status: number, error: string, message?: string, extra: Record<string, unknown> = {}, headers: Record<string, string> = {}): Response {
  const body: Record<string, unknown> = { error };
  if (message !== undefined) body.message = message;
  return jsonResponse({ ...body, ...extra }, status, headers);
}

function notFound(): Response {
  return refuse(404, "not_found");
}

// ── shared reads ────────────────────────────────────────────────────────────

interface VodCounts {
  pending: number;
  live: number;
}

/**
 * pending = the caller's non-idle uploading VOD rows; live = ready VOD rows plus
 * non-idle uploading ones. Idle = no part activity for UPLOAD_IDLE_ABORT_SECONDS.
 * A paused HLS share (its sharer is in a game) is never idle: it resumes after
 * the game, so it keeps its pending slot until then.
 */
async function vodCounts(db: D1Database, userId: number, now: number): Promise<VodCounts> {
  const active = "(c.status = 'uploading' AND (COALESCE(c.last_activity_at, c.created_at) > ?3 OR h.paused_at IS NOT NULL))";
  const row = await db
    .prepare(
      `SELECT COALESCE(SUM(CASE WHEN ${active} THEN 1 ELSE 0 END), 0) AS pending, COALESCE(SUM(CASE WHEN c.status = 'ready' OR ${active} THEN 1 ELSE 0 END), 0) AS live FROM clips c LEFT JOIN vod_hls h ON h.clip_id = c.id WHERE c.user_id = ?1 AND c.kind = 'vod' AND c.expires_at > ?2`,
    )
    .bind(userId, now, now - UPLOAD_IDLE_ABORT_SECONDS)
    .first<{ pending: number; live: number }>();
  return { pending: Number(row?.pending ?? 0), live: Number(row?.live ?? 0) };
}

async function startsToday(db: D1Database, userId: number, day: string): Promise<number> {
  const row = await db
    .prepare("SELECT starts FROM vod_share_usage WHERE user_id = ?1 AND day = ?2")
    .bind(userId, day)
    .first<{ starts: number }>();
  return Number(row?.starts ?? 0);
}

interface OwnShareRow {
  id: string;
  status: string;
  quality: string | null;
  size_bytes: number;
  created_at: number;
  expires_at: number;
  format: string | null;
  segments: number | null;
  paused_at: number | null;
}

/** The caller's live shares; HLS ones also carry their committed segments and whether they are paused. */
async function ownShares(db: D1Database, userId: number, now: number) {
  const res = await db
    .prepare(
      "SELECT c.id, c.status, c.quality, c.size_bytes, c.created_at, c.expires_at, c.format, h.segments, h.paused_at FROM clips c LEFT JOIN vod_hls h ON h.clip_id = c.id WHERE c.user_id = ?1 AND c.kind = 'vod' AND c.expires_at > ?2 AND c.status IN ('ready', 'uploading') ORDER BY c.created_at DESC LIMIT 20",
    )
    .bind(userId, now)
    .all<OwnShareRow>();
  return (res.results ?? []).map((r) => ({
    id: r.id,
    status: r.status,
    quality: r.quality ?? null,
    size_bytes: r.size_bytes,
    created_at: r.created_at,
    expires_at: r.expires_at,
    format: r.format === "hls" ? "hls" : "mp4",
    ...(r.format === "hls" ? { segments: Number(r.segments ?? 0), paused: r.paused_at !== null && r.paused_at !== undefined } : {}),
  }));
}

// ── GET /vod-shares/status ──────────────────────────────────────────────────

export async function handleVodShareStatus(env: Env, userId: number): Promise<Response> {
  const limited = vodStatusLimitOrDeny(userId);
  if (limited) return limited;

  const now = nowSeconds();
  const sharing = vodSharingOn(env);
  const serving = await vodServingLive(env);
  const shares = await ownShares(env.DB, userId, now);

  if (!(await isVodSharer(env.DB, userId))) {
    return jsonResponse({ allowed: false, sharing, serving, now, shares }, 200);
  }

  const counts = await vodCounts(env.DB, userId, now);
  const day = utcDayOf(now);
  const pool = await poolSnapshot(env, now);
  return jsonResponse(
    {
      allowed: true,
      sharing,
      serving,
      now,
      limits: {
        max_bytes: VOD_MAX_BYTES,
        max_seconds: VOD_MAX_SECONDS,
        ttl_seconds: VOD_TTL_SECONDS,
        max_live: VOD_MAX_LIVE_PER_USER,
        max_pending: VOD_MAX_PENDING_PER_USER,
        max_starts_per_day: VOD_MAX_STARTS_PER_DAY,
        part_size: VOD_PART_SIZE_BYTES,
        markers_max_bytes: VOD_MARKERS_MAX_BYTES,
        notes_max_bytes: VOD_NOTES_MAX_BYTES,
        daily_play_cap: VOD_DAILY_PLAY_CAP,
      },
      // A desktop sends format=hls only when this says enabled (an older proxy has no hls at all).
      hls: {
        enabled: await vodHlsOn(env, userId),
        segment_seconds: HLS_SEGMENT_SECONDS,
        target_duration: HLS_TARGET_DURATION,
        max_segment_bytes: HLS_SEGMENT_MAX_BYTES,
        init_max_bytes: HLS_INIT_MAX_BYTES,
        playable_ms: HLS_PLAYABLE_MS,
        max_runs: HLS_MAX_RUNS,
      },
      usage: {
        live: counts.live,
        pending: counts.pending,
        starts_today: await startsToday(env.DB, userId, day),
        day_resets_at: nextUtcMidnight(now),
      },
      pool,
      shares,
    },
    200,
  );
}

// ── POST /vod-shares/uploads ────────────────────────────────────────────────

const PENDING_MESSAGE = "Another game is still uploading. Try again when it finishes.";
const LIVE_MESSAGE = `You already share ${VOD_MAX_LIVE_PER_USER} games. Stop sharing one to share another.`;
const POOL_MESSAGE = "Sharing space is full right now.";

function tooManyPending(): Response {
  return refuse(429, "too_many_pending_uploads", PENDING_MESSAGE);
}

function liveLimit(): Response {
  return refuse(409, "live_limit", LIVE_MESSAGE);
}

async function poolFull(env: Env, size: number, now: number): Promise<Response> {
  const pool = await poolSnapshot(env, now);
  return refuse(409, "pool_full", POOL_MESSAGE, { retry_at: fitsAt(pool, size, now), free_bytes: pool.free_bytes });
}

export async function handleVodUploadInit(request: Request, env: Env, userId: number): Promise<Response> {
  // 2. kill switch, 3. allowlist, 4. per-user init limiter.
  if (!vodSharingOn(env)) return refuse(503, "vod_sharing_disabled", "Full-game sharing is paused right now.");
  if (!(await isVodSharer(env.DB, userId))) {
    return refuse(403, "not_allowed", "Full-game sharing isn't available for this account.");
  }
  const limited = vodInitLimitOrDeny(userId);
  if (limited) return limited;

  // 5 to 9. Request shape.
  if (mediaTypeOf(request) !== "video/mp4") {
    return refuse(415, "unsupported_media_type", "a shared game must be video/mp4");
  }
  const url = new URL(request.url);
  const size = parseStrictInt(url.searchParams.get("size"));
  if (size === null || size < 1) return refuse(400, "bad_request", "size must be a positive integer");
  if (size > VOD_MAX_BYTES) return refuse(413, "payload_too_large", "a shared game can be at most 2 GiB");
  const duration = parseStrictInt(url.searchParams.get("duration"));
  if (duration === null || duration < 1 || duration > VOD_MAX_SECONDS) {
    return refuse(400, "bad_request", `duration must be 1 to ${VOD_MAX_SECONDS} seconds`);
  }
  const resolution = url.searchParams.get("quality") ?? "";
  if (!QUALITIES.has(resolution)) return refuse(400, "bad_request", "quality must be 1080p or 720p");
  const fps = url.searchParams.get("fps");
  if (fps !== null && !FRAME_RATES.has(fps)) return refuse(400, "bad_request", "fps must be 30 or 60");
  const quality = fps === null ? resolution : `${resolution}${fps}`;
  // format=hls: checked with the request shape, before anything is discarded
  // or spent, so a desktop that gets 409 hls_unavailable can share the MP4 way.
  const format = url.searchParams.get("format") ?? "mp4";
  if (format !== "mp4" && format !== "hls") return refuse(400, "bad_request", "format must be mp4 or hls");
  if (format === "hls" && !(await vodHlsOn(env, userId))) {
    return refuse(409, "hls_unavailable", "Watching while it uploads is off right now. The game will be shared once it's uploaded.");
  }

  const now = nowSeconds();

  // 10. Discard the caller's idle VOD uploads (an app killed mid-upload must
  // not hold the one pending slot for the whole upload window). An idle HLS
  // share is settled instead: ended at its edge when people can watch it,
  // discarded otherwise. A paused one is not idle; one paused for longer than
  // the pause limit is settled too.
  const idle = await env.DB
    .prepare(
      "SELECT c.id, c.format FROM clips c LEFT JOIN vod_hls h ON h.clip_id = c.id WHERE c.user_id = ?1 AND c.status = 'uploading' AND c.kind = 'vod' AND ((h.paused_at IS NULL AND COALESCE(c.last_activity_at, c.created_at) <= ?2) OR h.paused_at <= ?3)",
    )
    .bind(userId, now - UPLOAD_IDLE_ABORT_SECONDS, now - HLS_PAUSE_MAX_SECONDS)
    .all<{ id: string; format: string | null }>();
  for (const row of idle.results ?? []) {
    if (row.format === "hls") await settleHlsShare(env, row.id, now, UPLOAD_IDLE_ABORT_SECONDS);
    else await discardRow(env, row.id);
  }

  // 11, 12. Per-account limits.
  const counts = await vodCounts(env.DB, userId, now);
  if (counts.pending >= VOD_MAX_PENDING_PER_USER) return tooManyPending();
  if (counts.live >= VOD_MAX_LIVE_PER_USER) return liveLimit();

  // 13. Storage budget (total and the VOD subset). Idle reservations of any
  // account are released first when they are in the way.
  if (!vodFits(env, await storageUsage(env.DB), size)) {
    if ((await sweepIdleUploads(env, now, IDLE_SWEEP_ON_START)) === 0
      || !vodFits(env, await storageUsage(env.DB), size)) return poolFull(env, size, now);
  }

  // 14. One daily start, atomically. Not refunded if a later step fails.
  const day = utcDayOf(now);
  const started = await env.DB
    .prepare(
      "INSERT INTO vod_share_usage (user_id, day, starts) VALUES (?1, ?2, 1) ON CONFLICT (user_id, day) DO UPDATE SET starts = starts + 1 WHERE starts < ?3 RETURNING starts",
    )
    .bind(userId, day, VOD_MAX_STARTS_PER_DAY)
    .first<{ starts: number }>();
  if (!started) {
    const resetsAt = nextUtcMidnight(now);
    return refuse(
      429,
      "daily_limit",
      `You've started ${VOD_MAX_STARTS_PER_DAY} shares today. Try again tomorrow.`,
      { retry_at: resetsAt },
      { "Retry-After": String(Math.max(1, resetsAt - now)) },
    );
  }

  // 15. Reserve: R2 multipart upload + the uploading row (declared size). The
  // upload window fits the declared size at the slowest supported upstream.
  // An HLS share is one D1 batch and no R2 call (reserveHlsShare).
  const id = await uniqueSlug(env.DB);
  let created: Record<string, unknown>;
  if (format === "hls") {
    created = await reserveHlsShare(env, { id, userId, size, duration, quality, now });
  } else {
    const r2Key = `vods/${id}.mp4`;
    const mpu = await env.CLIPS.createMultipartUpload(r2Key, { httpMetadata: { contentType: "video/mp4" } });
    const partCount = Math.ceil(size / VOD_PART_SIZE_BYTES);
    const uploadExpiresAt = now + vodUploadWindowSeconds(size);
    try {
      await env.DB
        .prepare(
          `INSERT INTO clips
            (id, user_id, r2_key, content_type, size_bytes, duration_s, created_at, expires_at, view_count, status,
             upload_id, part_size, part_count, last_activity_at, narrated, has_transcript, kind, quality, part_puts)
           VALUES (?1, ?2, ?3, 'video/mp4', ?4, ?5, ?6, ?7, 0, 'uploading', ?8, ?9, ?10, ?6, 0, 0, 'vod', ?11, 0)`,
        )
        .bind(id, userId, r2Key, size, duration, now, uploadExpiresAt, mpu.uploadId, VOD_PART_SIZE_BYTES, partCount, quality)
        .run();
    } catch (err) {
      try { await mpu.abort(); } catch { /* best effort */ }
      throw err; // dispatch catch → 502 clip_error
    }
    created = {
      id,
      part_size: VOD_PART_SIZE_BYTES,
      part_count: partCount,
      expires_at: uploadExpiresAt,
      share_expires_at: now + VOD_TTL_SECONDS,
      url: vodWatchUrl(env, id),
    };
  }

  // 16. Recount after the row is committed; the loser of a race rolls back.
  const after = await vodCounts(env.DB, userId, now);
  if (after.pending > VOD_MAX_PENDING_PER_USER) {
    await discardRow(env, id);
    return tooManyPending();
  }
  if (after.live > VOD_MAX_LIVE_PER_USER) {
    await discardRow(env, id);
    return liveLimit();
  }
  if (!vodFits(env, await storageUsage(env.DB), 0)) {
    await discardRow(env, id);
    return poolFull(env, size, now);
  }

  return jsonResponse(created, 201);
}

// ── PUT /vod-shares/:id/parts/:n and POST /vod-shares/:id/complete ──────────

export function handleVodUploadPart(request: Request, env: Env, userId: number, id: string, n: string): Promise<Response> {
  return handleUploadPart(request, env, userId, id, n, "vod");
}

export function handleVodUploadComplete(request: Request, env: Env, userId: number, id: string): Promise<Response> {
  return completeUpload(request, env, userId, id, "vod");
}

// ── PUT /vod-shares/:id/markers ─────────────────────────────────────────────

/** A VOD row that can take markers: ready, or still uploading (never 'purging'). */
const MARKABLE = "kind = 'vod' AND status IN ('ready', 'uploading')";

/**
 * The desktop sends the markers right after init, so the watch page can name
 * the game while the upload runs: a VOD still uploading inside its upload
 * window (its expires_at) takes them as well as a ready one. The write checks
 * the row again in the same statement, so a row discarded or deleted in the
 * meantime never gets a meta row back (that is a 404, like a missing row).
 */
export async function handleVodMarkers(request: Request, env: Env, userId: number, id: string): Promise<Response> {
  const text = await readSmallBody(request, VOD_META_MAX_BODY_BYTES);
  if (text === null) return refuse(413, "payload_too_large", "markers body exceeds 64 KB");

  const now = nowSeconds();
  const row = await env.DB
    .prepare(`SELECT id FROM clips WHERE id = ?1 AND user_id = ?2 AND ${MARKABLE} AND expires_at > ?3 LIMIT 1`)
    .bind(id, userId, now)
    .first<{ id: string }>();
  if (!row) return notFound();

  const result = normalizeVodMeta(text);
  if (!result.ok) return refuse(result.status, result.error, result.message);
  const { doc, hasNotes } = result;

  const [stored] = await env.DB.batch([
    env.DB
      .prepare(
        `INSERT OR REPLACE INTO vod_share_meta (clip_id, meta_json, has_notes, created_at) SELECT id, ?2, ?3, ?4 FROM clips WHERE id = ?1 AND user_id = ?5 AND ${MARKABLE} AND expires_at > ?4`,
      )
      .bind(id, JSON.stringify(doc), hasNotes ? 1 : 0, now, userId),
    env.DB
      .prepare(`UPDATE clips SET champion = ?1, title = ?2 WHERE id = ?3 AND user_id = ?4 AND ${MARKABLE} AND expires_at > ?5`)
      .bind(doc.game.champion ?? null, vodTitle(doc.game), id, userId, now),
  ]);
  if (stored.meta.changes !== 1) return notFound();

  return jsonResponse({ ok: true, markers: doc.markers.length, notes: doc.notes?.length ?? 0 }, 200);
}

// ── cron ────────────────────────────────────────────────────────────────────

/** Drop vod_share_usage rows older than 7 days (UTC). Returns the cutoff day. */
export async function pruneVodShareUsage(env: Env): Promise<string> {
  const cutoff = utcDayOf(nowSeconds() - 7 * 86400);
  await env.DB.prepare("DELETE FROM vod_share_usage WHERE day < ?1").bind(cutoff).run();
  return cutoff;
}

/** Drop upload_part_usage rows (every kind) older than 7 days (UTC). Returns the cutoff day. */
export async function pruneUploadPartUsage(env: Env): Promise<string> {
  const cutoff = utcDayOf(nowSeconds() - 7 * 86400);
  await env.DB.prepare("DELETE FROM upload_part_usage WHERE day < ?1").bind(cutoff).run();
  return cutoff;
}
