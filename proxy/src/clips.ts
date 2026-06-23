/**
 * Public clip sharing.
 *
 * A logged-in user uploads a local clip file (POST /clips). We store the video
 * bytes in R2 and the metadata in D1, mint a short public slug, and hand back
 * revu.lol/<slug>. Anyone can watch via the slug — no auth to view, auth to
 * upload/delete. Clips auto-expire after 3 days (see CLIP_TTL_SECONDS +
 * purgeExpiredClips, run from the Worker's scheduled handler).
 *
 * Privacy: only the uploader-typed title/champion ever surface publicly. No
 * account, Riot ID, or match data is stored or returned here.
 */

import { Env } from "./types";
import { jsonResponse, badRequest } from "./http";

// 3-day retention (changed from 30d 2026-06-19). Kept here so the upload path and
// the daily purge job agree. TRADE-OFF: a shared link only lives 3 days, after which
// the clip is purged from R2/D1 and the link 404s — keeps storage churn low and frees
// per-user quota fast. Applies to NEW uploads; clips uploaded before this keep the
// expires_at stamped at their upload time (they are NOT retroactively shortened).
export const CLIP_TTL_SECONDS = 3 * 24 * 60 * 60;

// Absolute server-side ceiling on an uploaded file. The desktop app enforces a
// 90-second duration limit (Revu can't downscale source resolution), but a
// public endpoint must bound size regardless of client.
//
// Capped at 100 MB — deliberately UNDER the Cloudflare Workers isolate memory
// limit (~128 MB). The upload handler buffers the body with arrayBuffer()
// before it can measure it, so a ceiling at/above the isolate limit would let a
// large body OOM-crash the isolate before the size check runs (memory-DoS). A
// 90s high-bitrate 1080p clip lands well under this; clients that need more
// must transcode down first.
export const MAX_CLIP_BYTES = 100 * 1024 * 1024;

const ALLOWED_CONTENT_TYPES = new Set(["video/mp4", "video/webm"]);

// Per-user ceilings on *active* (unexpired) clips. Uploads are authed, but
// without a quota a single account could park unbounded R2 storage (100 MB
// per request) on our bill. Raised 50 -> 150 (2026-06-19) for heavy reviewers
// who share most of a game's clips; the byte ceiling scales with it (~22 MB
// avg observed, so 150 fits comfortably under 6 GB).
export const MAX_ACTIVE_CLIPS_PER_USER = 150;
export const MAX_ACTIVE_CLIP_BYTES_PER_USER = 6 * 1024 * 1024 * 1024; // 6 GB

/**
 * Cheap container sniff so the stored object is at least shaped like the
 * declared type — Content-Type alone is caller-asserted. mp4 (ISO BMFF)
 * leads with a size-prefixed "ftyp" box; webm leads with the EBML magic.
 */
function hasVideoMagicBytes(bytes: Uint8Array, contentType: string): boolean {
  if (contentType === "video/webm") {
    return bytes.length >= 4
      && bytes[0] === 0x1a && bytes[1] === 0x45 && bytes[2] === 0xdf && bytes[3] === 0xa3;
  }
  return bytes.length >= 12
    && bytes[4] === 0x66 && bytes[5] === 0x74 && bytes[6] === 0x79 && bytes[7] === 0x70; // "ftyp"
}

// Slug shape: 7 chars of base62. ~62^7 ≈ 3.5e12 — plenty for a 30-day window.
const SLUG_LENGTH = 7;
const SLUG_ALPHABET = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
export const SLUG_REGEX = /^[0-9A-Za-z]{7}$/;

/**
 * First-path segments that must never be treated as a clip slug, even if they
 * happen to be 7 chars. These are real site pages / API prefixes served by
 * Cloudflare Pages or this Worker. Keep in sync when adding top-level pages.
 */
export const RESERVED_SLUGS = new Set([
  "discord",
  "privacy",
  "terms",
  "app",
  "health",
  "auth",
  "web",
  "clips",
  "fonts",
  "assets",
  "static",
  "favicon",
  "robots",
  "sitemap",
  "index",
  // Riot proxy endpoints (desktop, static-token path). These are bare,
  // single-segment, and happen to be 7 base62 chars — so without reserving
  // them the bare-slug router hijacks them and 302-redirects to clip.html,
  // breaking account lookup + match loading (i.e. login). "match" is 5 chars
  // and "/match/<id>" is multi-segment so it's already safe, but reserve it
  // too as defense against a future bare "/match".
  "account",
  "matches",
  "match",
]);

/** True if `seg` should be handled as a clip watch request (not a real page). */
export function isClipSlug(seg: string): boolean {
  return SLUG_REGEX.test(seg) && !RESERVED_SLUGS.has(seg.toLowerCase());
}

function randomSlug(): string {
  const bytes = new Uint8Array(SLUG_LENGTH);
  crypto.getRandomValues(bytes);
  let out = "";
  for (let i = 0; i < SLUG_LENGTH; i++) {
    out += SLUG_ALPHABET[bytes[i] % SLUG_ALPHABET.length];
  }
  return out;
}

// ── D1 row + helpers ──────────────────────────────────────────────────────

export interface ClipRow {
  id: string;
  user_id: number;
  r2_key: string;
  content_type: string;
  size_bytes: number;
  duration_s: number | null;
  title: string | null;
  champion: string | null;
  created_at: number;
  expires_at: number;
  view_count: number;
  status: string;
}

async function insertClip(db: D1Database, row: ClipRow): Promise<void> {
  await db
    .prepare(
      `INSERT INTO clips
        (id, user_id, r2_key, content_type, size_bytes, duration_s, title, champion, created_at, expires_at, view_count, status)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)`,
    )
    .bind(
      row.id,
      row.user_id,
      row.r2_key,
      row.content_type,
      row.size_bytes,
      row.duration_s,
      row.title,
      row.champion,
      row.created_at,
      row.expires_at,
      row.view_count,
      row.status,
    )
    .run();
}

/** Look up a clip by slug. Returns null if missing OR expired. */
export async function findClip(db: D1Database, id: string): Promise<ClipRow | null> {
  const now = Math.floor(Date.now() / 1000);
  const row = await db
    .prepare("SELECT * FROM clips WHERE id = ?1 AND expires_at > ?2 LIMIT 1")
    .bind(id, now)
    .first<ClipRow>();
  return row ?? null;
}

async function incrementViews(db: D1Database, id: string): Promise<void> {
  await db.prepare("UPDATE clips SET view_count = view_count + 1 WHERE id = ?1").bind(id).run();
}

/** Minimal HTML-attribute/text escaping for user-supplied title/champion. */
function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

/**
 * Per-clip share page (server-rendered) so link unfurlers — Discord, Twitter,
 * Slack, etc. — get real Open Graph **video** tags and embed an inline player.
 * Crawlers don't run JS, so the static clip.html (which fills tags client-side)
 * can't embed; this Worker-rendered head can.
 *
 * Humans are bounced to the nice watch page (WATCH_BASE/clip.html?id=) via a
 * meta-refresh + JS redirect. We serve THIS to everyone hitting a bare slug so a
 * crawler we failed to detect still gets tags rather than a bare redirect.
 *
 * Returns null when the clip is missing/expired so the caller can fall back
 * (redirect to the watch page, which shows a clean "not found" state).
 */
export async function renderClipOgPage(env: Env, id: string): Promise<Response | null> {
  const clip = await findClip(env.DB, id);
  if (!clip) return null;

  const publicBase = (env.PUBLIC_BASE || "https://clips.revu.lol").replace(/\/+$/, "");
  const watchBase = (env.WATCH_BASE || "https://revu.lol").replace(/\/+$/, "");
  const videoUrl = `${publicBase}/clip-file/${id}`;
  const watchUrl = `${watchBase}/clip.html?id=${id}`;
  const videoType = clip.content_type === "video/webm" ? "video/webm" : "video/mp4";

  const rawTitle = (clip.title || "").trim() || "League clip";
  const champ = (clip.champion || "").trim();
  const title = escapeHtml(champ ? `${rawTitle} — ${champ}` : rawTitle);
  const desc = escapeHtml(
    champ ? `${champ} clip shared with Revu. Watch in your browser.` : "Watch this League of Legends clip, shared with Revu.",
  );

  // Default 16:9; only used as a hint for the embed aspect ratio.
  const w = 1280;
  const h = 720;

  const html = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<title>${title} - Revu</title>
<meta name="description" content="${desc}" />

<meta property="og:site_name" content="Revu" />
<meta property="og:title" content="${title}" />
<meta property="og:description" content="${desc}" />
<meta property="og:type" content="video.other" />
<meta property="og:url" content="${publicBase}/${id}" />
<meta property="og:image" content="${watchBase}/og-image.jpg" />

<meta property="og:video" content="${videoUrl}" />
<meta property="og:video:secure_url" content="${videoUrl}" />
<meta property="og:video:type" content="${videoType}" />
<meta property="og:video:width" content="${w}" />
<meta property="og:video:height" content="${h}" />

<meta name="twitter:card" content="player" />
<meta name="twitter:title" content="${title}" />
<meta name="twitter:description" content="${desc}" />
<meta name="twitter:image" content="${watchBase}/og-image.jpg" />
<meta name="twitter:player" content="${watchUrl}" />
<meta name="twitter:player:width" content="${w}" />
<meta name="twitter:player:height" content="${h}" />
<meta name="twitter:player:stream" content="${videoUrl}" />
<meta name="twitter:player:stream:content_type" content="${videoType}" />

<meta http-equiv="refresh" content="0; url=${watchUrl}" />
<link rel="canonical" href="${watchUrl}" />
</head>
<body>
<p>Loading clip… <a href="${watchUrl}">Watch it here</a>.</p>
<script>location.replace(${JSON.stringify(watchUrl)});</script>
</body>
</html>`;

  return new Response(html, {
    status: 200,
    headers: {
      "Content-Type": "text/html; charset=utf-8",
      "X-Content-Type-Options": "nosniff",
      // Short cache: clip metadata (title/views) can change, and a deleted clip
      // should stop unfurling reasonably soon.
      "Cache-Control": "public, max-age=300",
    },
  });
}

/** Generate a slug not already taken. A few tries is overwhelmingly enough. */
async function uniqueSlug(db: D1Database): Promise<string> {
  for (let attempt = 0; attempt < 6; attempt++) {
    const slug = randomSlug();
    const existing = await db.prepare("SELECT id FROM clips WHERE id = ?1 LIMIT 1").bind(slug).first();
    if (!existing) return slug;
  }
  throw new Error("could not allocate a unique clip id");
}

// ── Handlers ──────────────────────────────────────────────────────────────

/**
 * POST /clips — upload a clip. Auth is resolved by the caller (index.ts) and the
 * uploader's user id is passed in; uploads require a real account (session, not
 * a static operator token), so userId must be present.
 */
export async function handleUploadClip(
  request: Request,
  env: Env,
  userId: number | undefined,
): Promise<Response> {
  if (userId === undefined) {
    // Static operator tokens (Path A) have no user id — they can't own clips.
    return jsonResponse({ error: "login_required" }, 403);
  }

  const contentType = (request.headers.get("Content-Type") || "").split(";")[0].trim().toLowerCase();
  if (!ALLOWED_CONTENT_TYPES.has(contentType)) {
    return jsonResponse({ error: "unsupported_media_type", message: "clip must be video/mp4 or video/webm" }, 415);
  }

  const tooLargeMessage = `clip exceeds ${Math.floor(MAX_CLIP_BYTES / (1024 * 1024))} MB`;

  // Fast-reject on a declared length before reading a single byte. Honest
  // clients (the desktop app always sets Content-Length) never reach the
  // streaming guard below.
  const lenHeader = request.headers.get("Content-Length");
  const declaredLen = lenHeader ? parseInt(lenHeader, 10) : NaN;
  if (Number.isFinite(declaredLen) && declaredLen > MAX_CLIP_BYTES) {
    return jsonResponse({ error: "payload_too_large", message: tooLargeMessage }, 413);
  }

  if (!request.body) {
    return badRequest("empty body");
  }

  const now = Math.floor(Date.now() / 1000);

  // Pre-upload quota check on what we know UP FRONT: the active-clip COUNT (which
  // doesn't depend on this file's size) and, when the client declares a length,
  // the byte total. The honest desktop always sends Content-Length, so the byte
  // ceiling is enforced here before a single byte is streamed; a missing length
  // still gets the COUNT gate now and the precise byte recount after the stream.
  const usage = await env.DB
    .prepare("SELECT COUNT(*) AS n, COALESCE(SUM(size_bytes), 0) AS total FROM clips WHERE user_id = ?1 AND expires_at > ?2")
    .bind(userId, now)
    .first<{ n: number; total: number }>();
  const declaredAddsBytes = Number.isFinite(declaredLen) ? declaredLen : 0;
  if (usage && (usage.n >= MAX_ACTIVE_CLIPS_PER_USER || usage.total + declaredAddsBytes > MAX_ACTIVE_CLIP_BYTES_PER_USER)) {
    return jsonResponse(
      { error: "quota_exceeded", message: "active clip quota reached — delete old clips or let them expire" },
      403,
    );
  }

  const url = new URL(request.url);
  const title = clampText(url.searchParams.get("title"), 120);
  const champion = clampText(url.searchParams.get("champion"), 40);
  const durationRaw = url.searchParams.get("duration");
  const durationParsed = durationRaw ? parseInt(durationRaw, 10) : NaN;
  const duration = Number.isFinite(durationParsed) && durationParsed > 0 ? durationParsed : null;

  const id = await uniqueSlug(env.DB);
  const ext = contentType === "video/webm" ? "webm" : "mp4";
  const r2Key = `clips/${id}.${ext}`;

  // STREAM the body into R2 instead of buffering the whole file in the isolate and
  // re-copying it into one contiguous ArrayBuffer. That buffer+recopy is O(file
  // size) CPU per request, and several concurrent uploads sharing one isolate's CPU
  // budget tripped the Workers CPU limit ("exceededCpu" → a raw 5xx the desktop
  // showed as "sharing temporarily unavailable"). The guard piped into the stream
  // keeps the memory-DoS cap (abort past MAX_CLIP_BYTES) and the magic-byte sniff.
  //
  // R2.put() rejects a plain ReadableStream of UNKNOWN length ("Provided readable
  // stream must have a known length"). The honest client always sends Content-Length,
  // so wrap the guarded stream in a FixedLengthStream(declaredLen) to give R2 the
  // length — the common, CPU-cheap path. When Content-Length is absent (a misbehaving
  // client / the abuse case the DoS cap exists for), there is no length to declare, so
  // fall back to the bounded buffered read: it still enforces the cap and never holds
  // more than MAX_CLIP_BYTES, it just isn't the streaming fast path.
  const guard = new ClipUploadGuard(MAX_CLIP_BYTES, contentType);
  let uploadedBytes = 0;
  try {
    const guarded = request.body.pipeThrough(guard.transform);
    if (Number.isFinite(declaredLen) && declaredLen > 0) {
      // Known length → stream straight to R2 via FixedLengthStream (no full buffer).
      const fixed = new FixedLengthStream(declaredLen);
      const pumped = guarded.pipeTo(fixed.writable); // propagates guard errors
      await env.CLIPS.put(r2Key, fixed.readable, { httpMetadata: { contentType } });
      await pumped; // surface a guard/length error that R2 didn't already throw
      uploadedBytes = guard.bytesSeen;
    } else {
      // Unknown length → bounded buffered read (rare path; still cap-enforced).
      const buf = await readGuardedToBuffer(guarded);
      uploadedBytes = buf.byteLength;
      await env.CLIPS.put(r2Key, buf, { httpMetadata: { contentType } });
    }
  } catch (err) {
    // The guard signals oversize / bad-magic / empty by erroring the stream, which
    // rejects the put/pipe; map those to the same client errors the buffered path
    // returned. Best-effort delete any partial object R2 may have started.
    try { await env.CLIPS.delete(r2Key); } catch { /* best effort */ }
    if (err instanceof PayloadTooLargeError) {
      return jsonResponse({ error: "payload_too_large", message: tooLargeMessage }, 413);
    }
    if (err instanceof BadMagicError) {
      return jsonResponse(
        { error: "unsupported_media_type", message: "file content does not match the declared video type" },
        415,
      );
    }
    if (err instanceof EmptyBodyError) {
      return badRequest("empty body");
    }
    throw err; // genuine R2 failure → bubbles to the dispatch catch (clean 502)
  }

  const expiresAt = now + CLIP_TTL_SECONDS;
  try {
    await insertClip(env.DB, {
      id,
      user_id: userId,
      r2_key: r2Key,
      content_type: contentType,
      size_bytes: uploadedBytes,
      duration_s: duration,
      title,
      champion,
      created_at: now,
      expires_at: expiresAt,
      view_count: 0,
      status: "ready",
    });
  } catch (err) {
    // Roll back the orphaned object if the row insert fails.
    try { await env.CLIPS.delete(r2Key); } catch { /* best effort */ }
    throw err;
  }

  // Re-check quota AFTER the row is committed. D1 has no cross-statement
  // transaction we can hold across the R2 put, so the pre-insert check above is
  // racy under concurrent uploads. This post-insert recount sees every
  // committed sibling row, so a parallel flood that slipped past the first
  // check is caught here and rolled back — the cap can be momentarily reached
  // but not exceeded.
  const postUsage = await env.DB
    .prepare("SELECT COUNT(*) AS n, COALESCE(SUM(size_bytes), 0) AS total FROM clips WHERE user_id = ?1 AND expires_at > ?2")
    .bind(userId, now)
    .first<{ n: number; total: number }>();
  if (postUsage && (postUsage.n > MAX_ACTIVE_CLIPS_PER_USER || postUsage.total > MAX_ACTIVE_CLIP_BYTES_PER_USER)) {
    try { await env.CLIPS.delete(r2Key); } catch { /* best effort */ }
    try { await env.DB.prepare("DELETE FROM clips WHERE id = ?1").bind(id).run(); } catch { /* best effort */ }
    return jsonResponse(
      { error: "quota_exceeded", message: "active clip quota reached — delete old clips or let them expire" },
      403,
    );
  }

  const base = (env.PUBLIC_BASE || "https://clips.revu.lol").replace(/\/+$/, "");
  return jsonResponse({ id, url: `${base}/${id}`, expires_at: expiresAt }, 201);
}

/** GET /clip-meta/:id — public JSON for the watch page. 404 if missing/expired. */
export async function handleClipMeta(env: Env, id: string): Promise<Response> {
  if (!SLUG_REGEX.test(id)) return jsonResponse({ error: "not_found" }, 404);
  const clip = await findClip(env.DB, id);
  if (!clip) return jsonResponse({ error: "not_found" }, 404);
  return jsonResponse(
    {
      id: clip.id,
      title: clip.title,
      champion: clip.champion,
      duration_s: clip.duration_s,
      created_at: clip.created_at,
      expires_at: clip.expires_at,
      view_count: clip.view_count,
      content_type: clip.content_type,
    },
    200,
  );
}

/**
 * GET /clip-file/:id — stream the video bytes from R2. Honors Range so the
 * browser <video> element can seek. 404 if missing/expired.
 */
export async function handleClipFile(request: Request, env: Env, id: string): Promise<Response> {
  if (!SLUG_REGEX.test(id)) return jsonResponse({ error: "not_found" }, 404);
  const clip = await findClip(env.DB, id);
  if (!clip) return jsonResponse({ error: "not_found" }, 404);

  const rangeHeader = request.headers.get("Range");
  const range = parseRange(rangeHeader, clip.size_bytes);

  const obj = await env.CLIPS.get(clip.r2_key, range ? { range: { offset: range.offset, length: range.length } } : undefined);
  if (!obj) return jsonResponse({ error: "not_found" }, 404);

  const headers = new Headers();
  headers.set("Content-Type", clip.content_type);
  headers.set("Accept-Ranges", "bytes");
  // Clips are immutable for their lifetime — cache hard.
  headers.set("Cache-Control", "public, max-age=86400, immutable");
  // The bytes are user-uploaded: never let a browser second-guess the type,
  // and sandbox any context that somehow renders the response as a document.
  headers.set("X-Content-Type-Options", "nosniff");
  headers.set("Content-Security-Policy", "sandbox");
  headers.set(
    "Content-Disposition",
    `inline; filename="revu-clip-${clip.id}.${clip.content_type === "video/webm" ? "webm" : "mp4"}"`,
  );
  const etag = obj.httpEtag;
  if (etag) headers.set("ETag", etag);

  if (range) {
    headers.set("Content-Length", String(range.length));
    headers.set("Content-Range", `bytes ${range.offset}-${range.offset + range.length - 1}/${clip.size_bytes}`);
    return new Response(obj.body, { status: 206, headers });
  }

  headers.set("Content-Length", String(clip.size_bytes));
  return new Response(obj.body, { status: 200, headers });
}

/** POST /clip-view/:id — best-effort view increment. Always 200 (cheap, public). */
export async function handleClipView(env: Env, id: string): Promise<Response> {
  if (!SLUG_REGEX.test(id)) return jsonResponse({ ok: true }, 200);
  try {
    const clip = await findClip(env.DB, id);
    if (clip) await incrementViews(env.DB, id);
  } catch {
    // Views are not worth failing a request over.
  }
  return jsonResponse({ ok: true }, 200);
}

/** DELETE /clips/:id — owner-only delete. Requires a session (userId). */
export async function handleDeleteClip(env: Env, id: string, userId: number | undefined): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);
  if (!SLUG_REGEX.test(id)) return jsonResponse({ error: "not_found" }, 404);

  // Look up without the expiry filter so an owner can still delete an
  // already-expired-but-not-yet-purged clip.
  const clip = await env.DB.prepare("SELECT * FROM clips WHERE id = ?1 LIMIT 1").bind(id).first<ClipRow>();
  if (!clip) return jsonResponse({ error: "not_found" }, 404);
  if (clip.user_id !== userId) return jsonResponse({ error: "forbidden" }, 403);

  try { await env.CLIPS.delete(clip.r2_key); } catch { /* best effort; row removal is the source of truth */ }
  await env.DB.prepare("DELETE FROM clips WHERE id = ?1").bind(id).run();
  return jsonResponse({ ok: true }, 200);
}

/** GET /clips/mine — list the caller's own clips (newest first). */
export async function handleListMyClips(env: Env, userId: number | undefined): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);
  const now = Math.floor(Date.now() / 1000);
  const base = (env.PUBLIC_BASE || "https://clips.revu.lol").replace(/\/+$/, "");
  const res = await env.DB
    .prepare("SELECT * FROM clips WHERE user_id = ?1 AND expires_at > ?2 ORDER BY created_at DESC LIMIT 200")
    .bind(userId, now)
    .all<ClipRow>();
  const clips = (res.results ?? []).map((c) => ({
    id: c.id,
    url: `${base}/${c.id}`,
    title: c.title,
    champion: c.champion,
    duration_s: c.duration_s,
    size_bytes: c.size_bytes,
    created_at: c.created_at,
    expires_at: c.expires_at,
    view_count: c.view_count,
  }));
  return jsonResponse({ clips }, 200);
}

/**
 * Purge clips whose retention window has passed. Called from the Worker's
 * scheduled (cron) handler. Deletes R2 objects first, then the rows.
 */
export async function purgeExpiredClips(env: Env): Promise<number> {
  const now = Math.floor(Date.now() / 1000);
  const res = await env.DB
    .prepare("SELECT id, r2_key FROM clips WHERE expires_at <= ?1 LIMIT 1000")
    .bind(now)
    .all<{ id: string; r2_key: string }>();
  const rows = res.results ?? [];
  for (const row of rows) {
    try { await env.CLIPS.delete(row.r2_key); } catch { /* keep going; row delete below */ }
  }
  if (rows.length > 0) {
    await env.DB.prepare("DELETE FROM clips WHERE expires_at <= ?1").bind(now).run();
  }
  return rows.length;
}

// ── small utils ────────────────────────────────────────────────────────────

/** Stream errored because the body exceeded the byte cap. */
class PayloadTooLargeError extends Error {}
/** Stream errored because the leading bytes aren't the declared video type. */
class BadMagicError extends Error {}
/** Stream errored because the body had zero bytes. */
class EmptyBodyError extends Error {}

// Magic-byte sniff needs the first 12 bytes (mp4 "ftyp" box check reads [4..8)).
const MAGIC_PREFIX_BYTES = 16;

/**
 * A pass-through guard for the clip upload stream. Sits between the request body
 * and R2.put: it counts bytes and ERRORS the stream past `maxBytes` (memory-DoS
 * cap, enforced even when Content-Length is absent/lying), validates the leading
 * bytes against the declared content type (magic-byte sniff), and rejects an
 * empty body — all WITHOUT buffering the whole file. Only a tiny header slice
 * (<= MAGIC_PREFIX_BYTES) is ever held; the rest flows straight through to R2.
 *
 * On any violation the underlying TransformStream is errored with the matching
 * typed error, which rejects the awaiting R2.put so the caller can map it to the
 * right HTTP status (same contract the old buffered path returned).
 */
class ClipUploadGuard {
  readonly transform: TransformStream<Uint8Array, Uint8Array>;
  bytesSeen = 0;

  constructor(maxBytes: number, contentType: string) {
    let header: Uint8Array | null = null; // accumulates up to MAGIC_PREFIX_BYTES
    let validated = false;

    const validateMagic = (controller: TransformStreamDefaultController<Uint8Array>): boolean => {
      if (validated || header === null) return true;
      if (!hasVideoMagicBytes(header, contentType)) {
        controller.error(new BadMagicError());
        return false;
      }
      validated = true;
      return true;
    };

    this.transform = new TransformStream<Uint8Array, Uint8Array>({
      transform: (chunk, controller) => {
        if (chunk.byteLength === 0) return;
        this.bytesSeen += chunk.byteLength;
        if (this.bytesSeen > maxBytes) {
          controller.error(new PayloadTooLargeError());
          return;
        }

        // Collect the header slice for the magic-byte sniff. Once we have enough
        // (or the body ends), validate before passing anything further downstream.
        if (!validated) {
          if (header === null) {
            header = chunk.slice(0, Math.min(chunk.byteLength, MAGIC_PREFIX_BYTES));
          } else if (header.length < MAGIC_PREFIX_BYTES) {
            const need = MAGIC_PREFIX_BYTES - header.length;
            const merged = new Uint8Array(header.length + Math.min(need, chunk.byteLength));
            merged.set(header, 0);
            merged.set(chunk.slice(0, Math.min(need, chunk.byteLength)), header.length);
            header = merged;
          }
          if (header.length >= MAGIC_PREFIX_BYTES && !validateMagic(controller)) return;
        }

        controller.enqueue(chunk);
      },
      flush: (controller) => {
        if (this.bytesSeen === 0) {
          controller.error(new EmptyBodyError());
          return;
        }
        // A body shorter than MAGIC_PREFIX_BYTES never tripped the mid-stream
        // validation — validate the short header now (and reject if it's bogus).
        validateMagic(controller);
      },
    });
  }
}

/**
 * Drain an already-guarded stream into one ArrayBuffer. Used only on the no-
 * Content-Length fallback (R2 needs a known length to stream; without one we have
 * to materialize). The guard upstream already capped bytes + sniffed magic and
 * propagates a typed error here, so this never holds more than the cap.
 */
async function readGuardedToBuffer(stream: ReadableStream<Uint8Array>): Promise<ArrayBuffer> {
  const reader = stream.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (value) { chunks.push(value); total += value.byteLength; }
    }
  } finally {
    reader.releaseLock();
  }
  const out = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) { out.set(chunk, offset); offset += chunk.byteLength; }
  return out.buffer;
}

function clampText(value: string | null, max: number): string | null {
  if (value === null) return null;
  const t = value.trim();
  if (!t) return null;
  return t.length > max ? t.slice(0, max) : t;
}

/**
 * Parse a single-range `Range: bytes=start-end` header into an R2-friendly
 * { offset, length }. Returns null for absent/unsatisfiable/multi-range headers
 * (caller then serves the whole object).
 */
function parseRange(header: string | null, size: number): { offset: number; length: number } | null {
  if (!header) return null;
  const m = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
  if (!m) return null;
  const startStr = m[1];
  const endStr = m[2];

  if (startStr === "" && endStr === "") return null;

  let offset: number;
  let end: number;
  if (startStr === "") {
    // suffix range: last N bytes
    const suffix = parseInt(endStr, 10);
    if (!Number.isFinite(suffix) || suffix <= 0) return null;
    offset = Math.max(0, size - suffix);
    end = size - 1;
  } else {
    offset = parseInt(startStr, 10);
    end = endStr === "" ? size - 1 : parseInt(endStr, 10);
  }

  if (!Number.isFinite(offset) || !Number.isFinite(end)) return null;
  if (offset < 0 || offset >= size) return null;
  if (end >= size) end = size - 1;
  if (end < offset) return null;

  return { offset, length: end - offset + 1 };
}
