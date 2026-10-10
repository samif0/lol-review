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

export const COMPLETE_MAX_BODY_BYTES = 64 * 1024;
const MAX_ETAG_CHARS = 256;
const SNIFF_BYTES = 16;

const TOO_MANY_PENDING_MESSAGE = "Another long clip is still uploading. Try again when it finishes.";

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

/** Best-effort abort of a row's multipart upload (may be completed or gone). */
async function abortQuietly(env: Env, r2Key: string, uploadId: string | null | undefined): Promise<void> {
  if (!uploadId) return;
  try {
    await env.CLIPS.resumeMultipartUpload(r2Key, uploadId).abort();
  } catch {
    // Already completed, already aborted, or unknown to R2.
  }
}

/**
 * Remove a still-pending row everywhere: abort its upload, delete its key,
 * transcript, row. The row is first claimed with a guarded UPDATE (status
 * 'uploading' → 'purging', expired as of now), so a row that a concurrent
 * complete just finalized to 'ready' is never touched. A claimed row can't be
 * finalized (complete requires 'uploading'), is hidden from every route
 * (expired), and is retried by the hourly purge if this cleanup stops midway.
 */
async function discardRow(env: Env, id: string): Promise<void> {
  const claimed = await env.DB
    .prepare(
      "UPDATE clips SET status = 'purging', expires_at = ?2 WHERE id = ?1 AND status = 'uploading' RETURNING r2_key, upload_id",
    )
    .bind(id, nowSeconds())
    .first<{ r2_key: string; upload_id: string | null }>();
  if (!claimed) return;
  await abortQuietly(env, claimed.r2_key, claimed.upload_id);
  try {
    await env.CLIPS.delete(claimed.r2_key);
  } catch {
    // Keep the claimed (expired) row so the hourly purge retries the delete
    // instead of orphaning the object.
    return;
  }
  await env.DB.prepare("DELETE FROM clip_transcripts WHERE clip_id = ?1").bind(id).run();
  await env.DB.prepare("DELETE FROM clips WHERE id = ?1").bind(id).run();
}

async function countPending(env: Env, userId: number, now: number): Promise<number> {
  const row = await env.DB
    .prepare("SELECT COUNT(*) AS n FROM clips WHERE user_id = ?1 AND status = 'uploading' AND expires_at > ?2")
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
      "SELECT id FROM clips WHERE user_id = ?1 AND status = 'uploading' AND COALESCE(last_activity_at, created_at) <= ?2",
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
           upload_id, part_size, part_count, last_activity_at, narrated, has_transcript)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, 0, 'uploading', ?11, ?12, ?13, ?14, ?15, 0)`,
      )
      .bind(id, userId, r2Key, contentType, size, duration, title, champion, now, expiresAt,
        mpu.uploadId, PART_SIZE_BYTES, partCount, now, narrated)
      .run();
  } catch (err) {
    await abortQuietly(env, r2Key, mpu.uploadId);
    throw err; // dispatch catch → 502 clip_error
  }

  // Recount after the row is committed: D1 can't hold a transaction across the
  // R2 call, so concurrent inits can slip past the checks above. The recount
  // sees every committed sibling; the loser rolls itself back.
  const pendingAfter = await countPending(env, userId, now);
  const usageAfter = await activeClipUsage(env.DB, userId, now);
  const overPending = pendingAfter > MAX_PENDING_UPLOADS_PER_USER;
  const overQuota = !!usageAfter
    && (usageAfter.n > MAX_ACTIVE_CLIPS_PER_USER || usageAfter.total > MAX_ACTIVE_CLIP_BYTES_PER_USER);
  if (overPending || overQuota) {
    await discardRow(env, id);
    return overPending ? tooManyPending() : quotaExceeded();
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
): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);

  const now = nowSeconds();
  const row = await env.DB
    .prepare("SELECT * FROM clips WHERE id = ?1 AND user_id = ?2 AND status = 'uploading' AND expires_at > ?3 LIMIT 1")
    .bind(id, userId, now)
    .first<ClipRow>();
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

  // Known-length request body straight into R2: consumed natively, no JS per
  // chunk. An R2 throw propagates to the dispatch catch (502 clip_error, retryable).
  const part = await env.CLIPS.resumeMultipartUpload(row.r2_key, row.upload_id).uploadPart(n, request.body);

  await env.DB.prepare("UPDATE clips SET last_activity_at = ?1 WHERE id = ?2").bind(now, id).run();

  return jsonResponse({ part_number: part.partNumber, etag: part.etag }, 200);
}

// ── POST /clips/:id/complete ───────────────────────────────────────────────

type ParsedParts = { part_number: number; etag: string }[];

/** Validate the complete body: exactly part_count unique parts 1..part_count. */
function parseParts(text: string, partCount: number): ParsedParts | null {
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
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);

  const now = nowSeconds();
  const row = await env.DB
    .prepare("SELECT * FROM clips WHERE id = ?1 AND user_id = ?2 AND expires_at > ?3 LIMIT 1")
    .bind(id, userId, now)
    .first<ClipRow>();
  if (!row) return notFound();
  if (row.status === "ready") {
    // Idempotent: a retried complete after a lost response gets the same 201.
    return jsonResponse({ id, url: clipPublicUrl(env, id), expires_at: row.expires_at }, 201);
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

  // The share TTL starts now, at complete.
  const expiresAt = now + CLIP_TTL_SECONDS;
  const meta = await env.DB
    .prepare(
      "UPDATE clips SET status = 'ready', upload_id = NULL, created_at = ?3, expires_at = ?4 WHERE id = ?1 AND user_id = ?2 AND status = 'uploading'",
    )
    .bind(id, userId, now, expiresAt)
    .run();
  if (meta.meta.changes !== 1) {
    // A concurrent complete won the race (or the row vanished). Report what's there.
    const again = await env.DB
      .prepare("SELECT * FROM clips WHERE id = ?1 AND user_id = ?2 AND expires_at > ?3 LIMIT 1")
      .bind(id, userId, now)
      .first<ClipRow>();
    if (again && again.status === "ready") {
      return jsonResponse({ id, url: clipPublicUrl(env, id), expires_at: again.expires_at }, 201);
    }
    return notFound();
  }

  return jsonResponse({ id, url: clipPublicUrl(env, id), expires_at: expiresAt }, 201);
}
