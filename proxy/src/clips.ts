/**
 * Public clip sharing.
 *
 * A logged-in user uploads a local clip file (POST /clips). We store the video
 * bytes in R2 and the metadata in D1, mint a short public slug, and hand back
 * revu.lol/<slug>. Anyone can watch via the slug — no auth to view, auth to
 * upload/delete. Clips auto-expire after 30 days (see purgeExpiredClips, run
 * from the Worker's scheduled handler).
 *
 * Privacy: only the uploader-typed title/champion ever surface publicly. No
 * account, Riot ID, or match data is stored or returned here.
 */

import { Env } from "./types";
import { jsonResponse, badRequest } from "./http";

// 30-day retention. Kept here so the upload path and the purge job agree.
export const CLIP_TTL_SECONDS = 30 * 24 * 60 * 60;

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
// without a quota a single account could park unbounded R2 storage (200 MB
// per request) on our bill.
export const MAX_ACTIVE_CLIPS_PER_USER = 50;
export const MAX_ACTIVE_CLIP_BYTES_PER_USER = 2 * 1024 * 1024 * 1024; // 2 GB

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

  // Read the body through a byte-counting guard that ABORTS once the cap is
  // exceeded, so a missing/lying Content-Length can't stream an oversized body
  // fully into the isolate (memory-DoS). We never buffer more than
  // MAX_CLIP_BYTES + one chunk.
  let body: ArrayBuffer;
  try {
    body = await readBodyCapped(request, MAX_CLIP_BYTES);
  } catch (err) {
    if (err instanceof PayloadTooLargeError) {
      return jsonResponse({ error: "payload_too_large", message: tooLargeMessage }, 413);
    }
    throw err;
  }
  if (body.byteLength === 0) {
    return badRequest("empty body");
  }
  if (!hasVideoMagicBytes(new Uint8Array(body, 0, Math.min(body.byteLength, 16)), contentType)) {
    return jsonResponse(
      { error: "unsupported_media_type", message: "file content does not match the declared video type" },
      415,
    );
  }

  const now = Math.floor(Date.now() / 1000);

  // Quota check (active clips only — expired ones purge daily and stop counting).
  const usage = await env.DB
    .prepare("SELECT COUNT(*) AS n, COALESCE(SUM(size_bytes), 0) AS total FROM clips WHERE user_id = ?1 AND expires_at > ?2")
    .bind(userId, now)
    .first<{ n: number; total: number }>();
  if (usage && (usage.n >= MAX_ACTIVE_CLIPS_PER_USER || usage.total + body.byteLength > MAX_ACTIVE_CLIP_BYTES_PER_USER)) {
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

  await env.CLIPS.put(r2Key, body, {
    httpMetadata: { contentType },
  });

  const expiresAt = now + CLIP_TTL_SECONDS;
  try {
    await insertClip(env.DB, {
      id,
      user_id: userId,
      r2_key: r2Key,
      content_type: contentType,
      size_bytes: body.byteLength,
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

/** Thrown by readBodyCapped when the streamed body exceeds the byte cap. */
class PayloadTooLargeError extends Error {}

/**
 * Read a request body into an ArrayBuffer, aborting as soon as the running
 * total exceeds `maxBytes`. Unlike `request.arrayBuffer()`, this never holds
 * more than the cap (plus one in-flight chunk) in memory, so an oversized or
 * Content-Length-spoofing upload can't OOM the Worker isolate. Falls back to a
 * bounded `arrayBuffer()` read if the body isn't a readable stream.
 */
async function readBodyCapped(request: Request, maxBytes: number): Promise<ArrayBuffer> {
  const stream = request.body;
  if (!stream) {
    const buf = await request.arrayBuffer();
    if (buf.byteLength > maxBytes) throw new PayloadTooLargeError();
    return buf;
  }

  const reader = stream.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (!value) continue;
      total += value.byteLength;
      if (total > maxBytes) {
        try { await reader.cancel(); } catch { /* best effort */ }
        throw new PayloadTooLargeError();
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }

  const out = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    out.set(chunk, offset);
    offset += chunk.byteLength;
  }
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
