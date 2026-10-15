/**
 * Multipart clip uploads for long clips (3.14.0).
 *
 *   POST /clips/uploads            reserve a pending row + R2 multipart upload
 *   PUT  /clips/:id/parts/:n       one raw part, straight to R2
 *   POST /clips/:id/complete       assemble, sniff, finalize to 'ready'
 *
 * CPU budget (free plan, ~10 ms per request): part bodies are handed to R2 as
 * `request.body` with the request's Content-Length, so the bulk bytes never pass
 * through JS. No TransformStream, FixedLengthStream, pipeTo or byte loops here.
 *
 * The client never sends or receives an R2 upload id or key. Every
 * resumeMultipartUpload call uses the D1 values from a row the caller owns.
 *
 * Pending rows (status 'uploading') are invisible to every public route because
 * findClip filters on status = 'ready'.
 *
 * Full-game (VOD) shares (vod-shares.ts) reuse the part and complete handlers
 * here with kind 'vod'; every row lookup is scoped to its kind so a clip route
 * can never touch a VOD row and the reverse.
 */

import { Env } from "./types";
import { jsonResponse, mediaTypeOf, parseStrictInt, readSmallBody } from "./http";
import {
  ALLOWED_CONTENT_TYPES,
  CLIP_TTL_SECONDS,
  ClipRow,
  MAX_ACTIVE_CLIPS_PER_USER,
  MAX_ACTIVE_CLIP_BYTES_PER_USER,
  MAX_MULTIPART_CLIP_BYTES,
  MAX_PENDING_UPLOADS_PER_USER,
  PART_SIZE_BYTES,
  QUOTA_EXCEEDED_MESSAGE,
  SERVER_MAX_CLIP_SECONDS,
  UPLOAD_IDLE_ABORT_SECONDS,
  UPLOAD_WINDOW_SECONDS,
  activeClipUsage,
  clampText,
  clipPublicUrl,
  hasVideoMagicBytes,
  uniqueSlug,
} from "./clips";
import { uploadInitLimitOrDeny } from "./user-limits";
import { clipFits, overAccountShare, storageFull, storageUsage } from "./storage-budget";
import {
  IDLE_SWEEP_ON_START,
  nextUtcMidnight,
  PART_PUT_FACTOR,
  PART_PUT_SLACK,
  PART_PUTS_PER_DAY,
  utcDayOf,
  VOD_TTL_SECONDS,
  vodWatchUrl,
} from "./share-limits";
import { abortUpload, discardRow, sweepIdleUploads, tombstoneObject } from "./upload-cleanup";

export { discardRow } from "./upload-cleanup";

/** Which multipart family a row belongs to (clips.kind). */
export type UploadKind = "clip" | "vod";

export const COMPLETE_MAX_BODY_BYTES = 64 * 1024;
const MAX_ETAG_CHARS = 256;
const SNIFF_BYTES = 16;

const TOO_MANY_PENDING_MESSAGE = "Another long clip is still uploading. Try again when it finishes.";
const TOO_MANY_PART_UPLOADS_MESSAGE = "This upload retried too many times. Start the share again.";
const DAILY_PART_LIMIT_MESSAGE = "You've uploaded a lot today. Try again tomorrow.";

function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

function notFound(): Response {
  return jsonResponse({ error: "not_found" }, 404);
}

function tooManyPending(): Response {
  return jsonResponse({ error: "too_many_pending_uploads", message: TOO_MANY_PENDING_MESSAGE }, 429);
}

function quotaExceeded(): Response {
  return jsonResponse({ error: "quota_exceeded", message: QUOTA_EXCEEDED_MESSAGE }, 403);
}

async function countPending(env: Env, userId: number, now: number): Promise<number> {
  const row = await env.DB
    .prepare("SELECT COUNT(*) AS n FROM clips WHERE user_id = ?1 AND kind = 'clip' AND status = 'uploading' AND expires_at > ?2")
    .bind(userId, now)
    .first<{ n: number }>();
  return row?.n ?? 0;
}

function expectedPartLength(row: ClipRow, n: number): number {
  const partSize = row.part_size ?? PART_SIZE_BYTES;
  const partCount = row.part_count ?? 0;
  return n < partCount ? partSize : row.size_bytes - (partCount - 1) * partSize;
}

// ── POST /clips/uploads ────────────────────────────────────────────────────

export async function handleUploadInit(request: Request, env: Env, userId: number | undefined): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);

  const limited = uploadInitLimitOrDeny(userId);
  if (limited) return limited;

  const contentType = mediaTypeOf(request);
  if (!ALLOWED_CONTENT_TYPES.has(contentType)) {
    return jsonResponse({ error: "unsupported_media_type", message: "clip must be video/mp4 or video/webm" }, 415);
  }

  const url = new URL(request.url);
  const size = parseStrictInt(url.searchParams.get("size"));
  if (size === null || size < 1) {
    return jsonResponse({ error: "bad_request", message: "size must be a positive integer" }, 400);
  }
  if (size > MAX_MULTIPART_CLIP_BYTES) {
    return jsonResponse({ error: "payload_too_large", message: "clip exceeds 2 GB" }, 413);
  }
  const duration = parseStrictInt(url.searchParams.get("duration"));
  if (duration === null || duration < 1 || duration > SERVER_MAX_CLIP_SECONDS) {
    return jsonResponse(
      { error: "bad_request", message: `duration must be 1 to ${SERVER_MAX_CLIP_SECONDS} seconds` },
      400,
    );
  }
  const title = clampText(url.searchParams.get("title"), 120);
  const champion = clampText(url.searchParams.get("champion"), 40);
  const narrated = url.searchParams.get("narrated") === "1" ? 1 : 0;

  const now = nowSeconds();

  // Auto-abort the caller's own idle uploads (an app killed mid-upload must not
  // block the next long share for the whole upload window).
  const idle = await env.DB
    .prepare(
      "SELECT id FROM clips WHERE user_id = ?1 AND status = 'uploading' AND kind = 'clip' AND COALESCE(last_activity_at, created_at) <= ?2",
    )
    .bind(userId, now - UPLOAD_IDLE_ABORT_SECONDS)
    .all<{ id: string }>();
  for (const row of idle.results ?? []) {
    await discardRow(env, row.id);
  }

  if ((await countPending(env, userId, now)) >= MAX_PENDING_UPLOADS_PER_USER) return tooManyPending();

  const usage = await activeClipUsage(env.DB, userId, now);
  if (usage && (usage.n >= MAX_ACTIVE_CLIPS_PER_USER || usage.total + size > MAX_ACTIVE_CLIP_BYTES_PER_USER)) {
    return quotaExceeded();
  }
  // One account's share of the pool (reservations count from init).
  if (await overAccountShare(env, userId, (usage?.total ?? 0) + size)) return quotaExceeded();
  // Global storage budget: the declared size is reserved by the row below.
  // Idle reservations of any account are released first when they are in the way.
  if (!clipFits(env, await storageUsage(env.DB), size)) {
    if ((await sweepIdleUploads(env, now, IDLE_SWEEP_ON_START)) === 0
      || !clipFits(env, await storageUsage(env.DB), size)) return storageFull();
  }

  const id = await uniqueSlug(env.DB);
  const ext = contentType === "video/webm" ? "webm" : "mp4";
  const r2Key = `clips/${id}.${ext}`;
  const mpu = await env.CLIPS.createMultipartUpload(r2Key, { httpMetadata: { contentType } });
  const partCount = Math.ceil(size / PART_SIZE_BYTES);
  const expiresAt = now + UPLOAD_WINDOW_SECONDS;

  try {
    await env.DB
      .prepare(
        `INSERT INTO clips
          (id, user_id, r2_key, content_type, size_bytes, duration_s, title, champion, created_at, expires_at, view_count, status,
           upload_id, part_size, part_count, last_activity_at, narrated, has_transcript, kind)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, 0, 'uploading', ?11, ?12, ?13, ?14, ?15, 0, 'clip')`,
      )
      .bind(id, userId, r2Key, contentType, size, duration, title, champion, now, expiresAt,
        mpu.uploadId, PART_SIZE_BYTES, partCount, now, narrated)
      .run();
  } catch (err) {
    await abortUpload(env, r2Key, mpu.uploadId); // no part was uploaded yet
    throw err; // dispatch catch → 502 clip_error
  }

  // Recount after the row is committed: D1 can't hold a transaction across the
  // R2 call, so concurrent inits can slip past the checks above. The recount
  // sees every committed sibling; the loser rolls itself back.
  const pendingAfter = await countPending(env, userId, now);
  const usageAfter = await activeClipUsage(env.DB, userId, now);
  const overPending = pendingAfter > MAX_PENDING_UPLOADS_PER_USER;
  const overQuota = !!usageAfter
    && (usageAfter.n > MAX_ACTIVE_CLIPS_PER_USER || usageAfter.total > MAX_ACTIVE_CLIP_BYTES_PER_USER
      || (await overAccountShare(env, userId, usageAfter.total)));
  const overStorage = !overPending && !overQuota && !clipFits(env, await storageUsage(env.DB), 0);
  if (overPending || overQuota || overStorage) {
    await discardRow(env, id);
    if (overPending) return tooManyPending();
    return overQuota ? quotaExceeded() : storageFull();
  }

  return jsonResponse({ id, part_size: PART_SIZE_BYTES, part_count: partCount, expires_at: expiresAt }, 201);
}

// ── PUT /clips/:id/parts/:n ────────────────────────────────────────────────

export async function handleUploadPart(
  request: Request,
  env: Env,
  userId: number | undefined,
  id: string,
  partNumberRaw: string,
  kind: UploadKind = "clip",
): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);

  const now = nowSeconds();
  const row = await findOwnedUploading(env, userId, id, kind, now);
  if (!row || !row.upload_id || !row.part_count) return notFound();

  const n = parseStrictInt(partNumberRaw);
  if (n === null || n < 1 || n > row.part_count) {
    return jsonResponse({ error: "bad_part_number", message: `part number must be 1 to ${row.part_count}` }, 400);
  }

  const lenHeader = request.headers.get("Content-Length");
  if (lenHeader === null) {
    return jsonResponse({ error: "length_required", message: "Content-Length is required" }, 411);
  }
  const expected = expectedPartLength(row, n);
  if (parseStrictInt(lenHeader) !== expected) {
    return jsonResponse({ error: "part_size_mismatch", expected }, 400);
  }
  if (!request.body) {
    return jsonResponse({ error: "bad_request", message: "empty body" }, 400);
  }

  // Count one attempt against the account's daily part budget first. It is
  // kept per user and day, not per row, so deleting an upload and starting
  // another can't reset it: one account's Class A operations stay bounded.
  const day = utcDayOf(now);
  const counted = await env.DB
    .prepare(
      "INSERT INTO upload_part_usage (user_id, day, puts) VALUES (?1, ?2, 1) ON CONFLICT (user_id, day) DO UPDATE SET puts = puts + 1 WHERE puts < ?3 RETURNING puts",
    )
    .bind(userId, day, PART_PUTS_PER_DAY)
    .first<{ puts: number }>();
  if (!counted) {
    const resetsAt = nextUtcMidnight(now);
    return jsonResponse(
      { error: "daily_part_limit", message: DAILY_PART_LIMIT_MESSAGE, retry_at: resetsAt },
      429,
      { "Retry-After": String(Math.max(1, resetsAt - now)) },
    );
  }

  // Claim one attempt BEFORE the upload (this also records activity). Each row
  // gets at most 2 x part_count + 4 part PUTs, so a looping client can't turn
  // one reservation into unbounded Class A operations.
  const claimed = await env.DB
    .prepare(
      `UPDATE clips SET part_puts = part_puts + 1, last_activity_at = ?3 WHERE id = ?1 AND user_id = ?2 AND kind = ?4 AND status = 'uploading' AND expires_at > ?3 AND part_puts < ${PART_PUT_FACTOR} * part_count + ${PART_PUT_SLACK} RETURNING r2_key, upload_id`,
    )
    .bind(id, userId, now, kind)
    .first<{ r2_key: string; upload_id: string | null }>();
  if (!claimed || !claimed.upload_id) {
    const still = await findOwnedUploading(env, userId, id, kind, now);
    if (still) {
      return jsonResponse({ error: "too_many_part_uploads", message: TOO_MANY_PART_UPLOADS_MESSAGE }, 429);
    }
    return notFound();
  }

  // Known-length request body straight into R2: consumed natively, no JS per
  // chunk. An R2 throw propagates to the dispatch catch (502 clip_error, retryable).
  const part = await env.CLIPS.resumeMultipartUpload(claimed.r2_key, claimed.upload_id).uploadPart(n, request.body);

  return jsonResponse({ part_number: part.partNumber, etag: part.etag }, 200);
}

/**
 * The caller's own still-uploading, unexpired multipart row of `kind`, or
 * null. Never an HLS share (format 'hls'): those take segments, not parts.
 */
async function findOwnedUploading(
  env: Env,
  userId: number,
  id: string,
  kind: UploadKind,
  now: number,
): Promise<ClipRow | null> {
  const row = await env.DB
    .prepare("SELECT * FROM clips WHERE id = ?1 AND user_id = ?2 AND status = 'uploading' AND expires_at > ?3 AND kind = ?4 AND format IS NULL LIMIT 1")
    .bind(id, userId, now, kind)
    .first<ClipRow>();
  return row ?? null;
}

// ── POST /clips/:id/complete ───────────────────────────────────────────────

type ParsedParts = { part_number: number; etag: string }[];

/** Validate the complete body: exactly part_count unique parts 1..part_count. */
export function parseParts(text: string, partCount: number): ParsedParts | null {
  let body: unknown;
  try {
    body = JSON.parse(text);
  } catch {
    return null;
  }
  if (!body || typeof body !== "object") return null;
  const parts = (body as { parts?: unknown }).parts;
  if (!Array.isArray(parts) || parts.length !== partCount) return null;

  const seen = new Set<number>();
  const out: ParsedParts = [];
  for (const p of parts) {
    if (!p || typeof p !== "object") return null;
    const { part_number: num, etag } = p as { part_number?: unknown; etag?: unknown };
    if (typeof num !== "number" || !Number.isInteger(num) || num < 1 || num > partCount) return null;
    if (typeof etag !== "string" || etag.length < 1 || etag.length > MAX_ETAG_CHARS) return null;
    if (seen.has(num)) return null;
    seen.add(num);
    out.push({ part_number: num, etag });
  }
  out.sort((a, b) => a.part_number - b.part_number);
  return out;
}

export async function handleUploadComplete(
  request: Request,
  env: Env,
  userId: number | undefined,
  id: string,
): Promise<Response> {
  return completeUpload(request, env, userId, id, "clip");
}

/** The share link a completed row hands back. */
function shareUrl(env: Env, kind: UploadKind, id: string): string {
  return kind === "vod" ? vodWatchUrl(env, id) : clipPublicUrl(env, id);
}

/** The caller's own unexpired row of `kind`, any status; never an HLS share. */
async function findOwnedLive(env: Env, userId: number, id: string, kind: UploadKind, now: number): Promise<ClipRow | null> {
  const row = await env.DB
    .prepare("SELECT * FROM clips WHERE id = ?1 AND user_id = ?2 AND expires_at > ?3 AND kind = ?4 AND format IS NULL LIMIT 1")
    .bind(id, userId, now, kind)
    .first<ClipRow>();
  return row ?? null;
}

/**
 * Assemble, check and finalize a multipart row of `kind`. Clips start their
 * 3-day TTL at complete; a VOD keeps created_at (its INIT time) and expires
 * VOD_TTL_SECONDS after it.
 */
export async function completeUpload(
  request: Request,
  env: Env,
  userId: number | undefined,
  id: string,
  kind: UploadKind,
): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);

  const now = nowSeconds();
  const row = await findOwnedLive(env, userId, id, kind, now);
  if (!row) return notFound();
  if (row.status === "ready") {
    // Idempotent: a retried complete after a lost response gets the same 201.
    return jsonResponse({ id, url: shareUrl(env, kind, id), expires_at: row.expires_at }, 201);
  }
  if (row.status !== "uploading" || !row.part_count) return notFound();

  const text = await readSmallBody(request, COMPLETE_MAX_BODY_BYTES);
  if (text === null) {
    return jsonResponse({ error: "payload_too_large", message: "complete body exceeds 64 KB" }, 413);
  }
  const parts = parseParts(text, row.part_count);
  if (!parts) {
    return jsonResponse(
      { error: "bad_parts", message: `parts must list each of the ${row.part_count} parts exactly once` },
      400,
    );
  }

  // Idempotency: a previous complete may have assembled the object in R2 and
  // then failed to finalize the row. complete() can't run twice, so if the
  // object is already there at the right size, skip straight to finalize.
  const existing = await env.CLIPS.head(row.r2_key);
  let finalSize: number;
  if (existing && existing.size === row.size_bytes) {
    finalSize = existing.size;
  } else {
    if (!row.upload_id) return notFound();
    try {
      const obj = await env.CLIPS
        .resumeMultipartUpload(row.r2_key, row.upload_id)
        .complete(parts.map((p) => ({ partNumber: p.part_number, etag: p.etag })));
      finalSize = obj.size;
    } catch (err) {
      console.error(JSON.stringify({ scope: "clips.complete", id, error: (err as Error)?.message ?? String(err) }));
      return jsonResponse(
        { error: "clip_error", message: "Clip service hit a temporary error. Try again." },
        502,
      );
    }
  }

  if (finalSize !== row.size_bytes) {
    await discardRow(env, row.id);
    return jsonResponse(
      { error: "size_mismatch", message: "uploaded size does not match the declared size" },
      400,
    );
  }

  const head = await env.CLIPS.get(row.r2_key, {
    range: { offset: 0, length: Math.min(SNIFF_BYTES, row.size_bytes) },
  });
  if (!head) throw new Error("completed clip object is missing");
  const leading = new Uint8Array(await head.arrayBuffer());
  if (!hasVideoMagicBytes(leading, row.content_type)) {
    await discardRow(env, row.id);
    return jsonResponse({ error: "not_video", message: "file content does not match the declared video type" }, 415);
  }

  let expiresAt: number;
  let changes: number;
  if (kind === "vod") {
    // The share TTL runs from INIT (created_at is never rewritten), so the D1
    // expiry never outlives the bucket's 3-day lifecycle rule.
    expiresAt = row.created_at + VOD_TTL_SECONDS;
    const meta = await env.DB
      .prepare(
        "UPDATE clips SET status = 'ready', upload_id = NULL, expires_at = created_at + ?3 WHERE id = ?1 AND user_id = ?2 AND kind = 'vod' AND format IS NULL AND status = 'uploading'",
      )
      .bind(id, userId, VOD_TTL_SECONDS)
      .run();
    changes = meta.meta.changes;
  } else {
    // The clip share TTL starts now, at complete.
    expiresAt = now + CLIP_TTL_SECONDS;
    const meta = await env.DB
      .prepare(
        "UPDATE clips SET status = 'ready', upload_id = NULL, created_at = ?3, expires_at = ?4 WHERE id = ?1 AND user_id = ?2 AND status = 'uploading'",
      )
      .bind(id, userId, now, expiresAt)
      .run();
    changes = meta.meta.changes;
  }
  if (changes !== 1) {
    // A concurrent complete won the race (or the row vanished). Report what's there.
    const again = await findOwnedLive(env, userId, id, kind, now);
    if (again && again.status === "ready") {
      return jsonResponse({ id, url: shareUrl(env, kind, id), expires_at: again.expires_at }, 201);
    }
    // An owner delete, a discard or the purge claimed the row while this call
    // assembled the object (possible when that path's abort failed, so the
    // upload stayed live). Delete what this call just built; if that fails, a
    // tombstone keeps it counted and retried.
    try {
      await env.CLIPS.delete(row.r2_key);
    } catch {
      await tombstoneObject(env, row.r2_key, null, row.size_bytes, kind);
    }
    return notFound();
  }

  return jsonResponse({ id, url: shareUrl(env, kind, id), expires_at: expiresAt }, 201);
}
