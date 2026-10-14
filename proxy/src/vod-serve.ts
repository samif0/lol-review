/**
 * Public serving for full-game (VOD) shares:
 *
 *   GET      /vod-meta/:id   JSON for the watch page (revu.lol/v/<id>)
 *   GET|HEAD /vod-file/:id   the video bytes, single range / conditional / HEAD
 *
 * The desktop shows the link as soon as the upload starts, so /vod-meta also
 * answers for a VOD that is still uploading: status "uploading" with its
 * progress, and the watch page polls it until the share is ready. /vod-file
 * stays 404 until then (an R2 multipart object doesn't exist before complete).
 *
 * Unlisted: every response carries X-Robots-Tag noindex, and nothing here ever
 * returns the owner, upload id, R2 key or raw counters.
 *
 * Play cap: each VOD may be STARTED at most VOD_DAILY_PLAY_CAP times per UTC
 * day. A request counts as a play when it is a GET whose Range is absent,
 * ignored (parseRange null: the whole file is served) or starts at byte 0. Once
 * the cap is reached every request for that VOD (seeks included) is a 429 until
 * the next UTC midnight. Known residual risk: a client that never asks for
 * byte 0 (for example `Range: bytes=1-`) is never counted; it is refused only
 * once the day's cap has been reached by counted plays.
 */

import { Env } from "./types";
import { jsonResponse } from "./http";
import { ClipRow, SLUG_REGEX, UPLOAD_IDLE_ABORT_SECONDS } from "./clips";
import { parseRange, serveR2Object } from "./r2-serve";
import { nextUtcMidnight, utcDayOf, VOD_DAILY_PLAY_CAP, VOD_TTL_SECONDS, vodServingOn } from "./share-limits";

const NOINDEX = "noindex, nofollow";
const PLAY_LIMIT_MESSAGE = "This game has been watched a lot today. Try again tomorrow.";
const SERVING_OFF_MESSAGE = "Playback is paused right now. Try again later.";

function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

function notFound(): Response {
  return jsonResponse({ error: "not_found" }, 404, { "X-Robots-Tag": NOINDEX });
}

/** A ready, unexpired VOD share by slug, or null. Never matches a clip. */
export async function findVod(db: D1Database, id: string): Promise<ClipRow | null> {
  const row = await db
    .prepare("SELECT * FROM clips WHERE id = ?1 AND kind = 'vod' AND status = 'ready' AND expires_at > ?2 LIMIT 1")
    .bind(id, nowSeconds())
    .first<ClipRow>();
  return row ?? null;
}

/**
 * A VOD share the watch page can show by slug: ready and unexpired, or still
 * uploading inside its upload window (an uploading row's expires_at is the end
 * of that window). Never matches a clip, or a row being discarded ('purging').
 */
export async function findShownVod(db: D1Database, id: string): Promise<ClipRow | null> {
  const row = await db
    .prepare("SELECT * FROM clips WHERE id = ?1 AND kind = 'vod' AND status IN ('ready', 'uploading') AND expires_at > ?2 LIMIT 1")
    .bind(id, nowSeconds())
    .first<ClipRow>();
  return row ?? null;
}

function playLimited(row: ClipRow, today: string): boolean {
  return row.play_day === today && (row.play_count ?? 0) >= VOD_DAILY_PLAY_CAP;
}

/**
 * /vod-meta for a VOD that is still uploading. progress.parts counts the part
 * PUTs this proxy accepted (part_puts, claimed as each part starts) capped at
 * part_count: it can run a few parts ahead while parts are in flight or after
 * a retry, so the page treats it as an estimate and never as finished.
 * expires_at is when the share will end (its TTL runs from init). stalled: no
 * part activity for UPLOAD_IDLE_ABORT_SECONDS, when the proxy stops counting
 * the upload as pending; such an upload rarely resumes, and the idle sweep
 * discards it later (then this route answers 404). No markers yet: they are
 * attached after complete.
 */
function uploadingMeta(env: Env, row: ClipRow, now: number): Response {
  const partCount = Math.max(1, Number(row.part_count) || 1);
  const parts = Math.min(Math.max(0, Number(row.part_puts) || 0), partCount);
  return jsonResponse(
    {
      id: row.id,
      kind: "vod",
      status: "uploading",
      quality: row.quality ?? null,
      duration_s: row.duration_s,
      created_at: row.created_at,
      expires_at: row.created_at + VOD_TTL_SECONDS,
      serving: vodServingOn(env),
      progress: { parts, part_count: partCount },
      stalled: now - (row.last_activity_at ?? row.created_at) >= UPLOAD_IDLE_ABORT_SECONDS,
    },
    200,
    { "Cache-Control": "no-store", "X-Robots-Tag": NOINDEX },
  );
}

/** GET /vod-meta/:id */
export async function handleVodMeta(env: Env, id: string): Promise<Response> {
  if (!SLUG_REGEX.test(id)) return notFound();
  const row = await findShownVod(env.DB, id);
  if (!row) return notFound();
  if (row.status === "uploading") return uploadingMeta(env, row, nowSeconds());

  const stored = await env.DB
    .prepare("SELECT meta_json FROM vod_share_meta WHERE clip_id = ?1")
    .bind(id)
    .first<{ meta_json: string }>();
  let meta: unknown = null;
  if (stored) {
    try {
      meta = JSON.parse(stored.meta_json);
    } catch {
      meta = null;
    }
  }

  const now = nowSeconds();
  return jsonResponse(
    {
      id: row.id,
      kind: "vod",
      content_type: row.content_type,
      quality: row.quality ?? null,
      duration_s: row.duration_s,
      size_bytes: row.size_bytes,
      created_at: row.created_at,
      expires_at: row.expires_at,
      serving: vodServingOn(env),
      play_limited: playLimited(row, utcDayOf(now)),
      meta,
    },
    200,
    { "Cache-Control": "no-store", "X-Robots-Tag": NOINDEX },
  );
}

/** GET|HEAD /vod-file/:id */
export async function handleVodFile(request: Request, env: Env, id: string): Promise<Response> {
  if (!SLUG_REGEX.test(id)) return notFound();
  const row = await findVod(env.DB, id);
  if (!row) return notFound();

  if (!vodServingOn(env)) {
    return jsonResponse({ error: "vod_serving_disabled", message: SERVING_OFF_MESSAGE }, 503, { "X-Robots-Tag": NOINDEX });
  }

  const now = nowSeconds();
  const today = utcDayOf(now);
  const range = parseRange(request.headers.get("Range"), row.size_bytes);
  const counts = request.method === "GET" && (range === null || (range !== "unsatisfiable" && range.offset === 0));

  let overCap: boolean;
  if (counts) {
    const counted = await env.DB
      .prepare(
        "UPDATE clips SET play_count = CASE WHEN play_day = ?2 THEN play_count + 1 ELSE 1 END, play_day = ?2 WHERE id = ?1 AND kind = 'vod' AND (play_day IS NOT ?2 OR play_count < ?3) RETURNING play_count",
      )
      .bind(id, today, VOD_DAILY_PLAY_CAP)
      .first<{ play_count: number }>();
    overCap = !counted;
  } else {
    overCap = playLimited(row, today);
  }
  if (overCap) {
    return jsonResponse({ error: "play_limit", message: PLAY_LIMIT_MESSAGE }, 429, {
      "Retry-After": String(Math.max(1, nextUtcMidnight(now) - now)),
      "X-Robots-Tag": NOINDEX,
    });
  }

  const headers = new Headers();
  headers.set("Content-Type", "video/mp4");
  headers.set("Accept-Ranges", "bytes");
  // Private: a share can be stopped early, so no shared cache may keep it.
  headers.set("Cache-Control", "private, max-age=3600");
  headers.set("X-Content-Type-Options", "nosniff");
  headers.set("Content-Security-Policy", "sandbox");
  headers.set("X-Robots-Tag", NOINDEX);
  headers.set("Content-Disposition", `inline; filename="revu-game-${row.id}.mp4"`);

  return serveR2Object(request, env.CLIPS, row.r2_key, row.size_bytes, headers);
}
