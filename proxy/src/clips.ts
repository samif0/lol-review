/**
 * Public clip sharing.
 *
 * A logged-in user uploads a local clip file (POST /clips). We store the video
 * bytes in R2 and the metadata in D1, mint a short public slug, and hand back
 * revu.lol/<slug>. Anyone can watch via the slug — no auth to view, auth to
 * upload/delete. Clips auto-expire after 3 days (see CLIP_TTL_SECONDS +
 * purgeExpiredClips, run from the Worker's scheduled handler).
 *
 * Privacy: what surfaces publicly is the video, its champion tag, a short
 * title, duration, narrated/transcript flags and the view count. The title is
 * whatever the uploading client sends. Desktop builds up to 3.14.1 send the
 * clip's bookmark note when the user typed no title, so a stored title can be
 * a private note; later builds send a neutral champion and game-time title
 * instead. No account, Riot ID or match data is stored or returned here.
 */

import { Env } from "./types";
import { jsonResponse, badRequest, mediaTypeOf, parseStrictInt } from "./http";

export { purgeExpiredClips } from "./clip-purge";

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
// limit (~128 MB). The upload handler never buffers the body (it is streamed to
// R2 and the cap is enforced against the mandatory Content-Length before a byte
// is read), but keep the ceiling below the isolate limit regardless so no future
// buffered path can OOM the isolate before a size check runs (memory-DoS). A
// 90s high-bitrate 1080p clip lands well under this; clients that need more
// must transcode down first.
export const MAX_CLIP_BYTES = 100 * 1024 * 1024;

export const ALLOWED_CONTENT_TYPES = new Set(["video/mp4", "video/webm"]);

// Per-user ceilings on *active* (unexpired) clips. Uploads are authed, but
// without a quota a single account could park unbounded R2 storage (100 MB
// per request) on our bill. Raised 50 -> 150 (2026-06-19) for heavy reviewers
// who share most of a game's clips; the byte ceiling scales with it (~22 MB
// avg observed, so 150 fits comfortably under 6 GB). Raised 6 -> 12 GiB for
// 3.14.0 10-minute clips (375 to 900 MB each); pending multipart reservations
// count toward it.
export const MAX_ACTIVE_CLIPS_PER_USER = 150;
export const MAX_ACTIVE_CLIP_BYTES_PER_USER = 12 * 1024 * 1024 * 1024; // 12 GiB

export const QUOTA_EXCEEDED_MESSAGE = "Active clip quota reached. Delete old clips or let them expire.";

// ── Multipart (long clip) limits, shared with clip-uploads.ts ──
// Proxy-side duration ceiling: the desktop caps clips at 600 s; 610 s allows
// keyframe slack from stream-copy extraction.
export const SERVER_MAX_CLIP_SECONDS = 610;
export const MAX_MULTIPART_CLIP_BYTES = 2 * 1024 * 1024 * 1024; // 2 GiB
// Chosen by the proxy and returned as part_size; R2 needs equal non-last parts
// of at least 5 MiB.
export const PART_SIZE_BYTES = 16 * 1024 * 1024;
export const MAX_PENDING_UPLOADS_PER_USER = 2;
// A pending upload row lives at most this long before the cron purges it; the
// finished clip's TTL (CLIP_TTL_SECONDS) starts at complete.
export const UPLOAD_WINDOW_SECONDS = 6 * 3600;
// A pending upload with no part activity for this long is auto-aborted by the
// owner's next init.
export const UPLOAD_IDLE_ABORT_SECONDS = 900;

/**
 * Cheap container sniff so the stored object is at least shaped like the
 * declared type — Content-Type alone is caller-asserted. mp4 (ISO BMFF)
 * leads with a size-prefixed "ftyp" box; webm leads with the EBML magic.
 */
export function hasVideoMagicBytes(bytes: Uint8Array, contentType: string): boolean {
  if (contentType === "video/webm") {
    return bytes.length >= 4
      && bytes[0] === 0x1a && bytes[1] === 0x45 && bytes[2] === 0xdf && bytes[3] === 0xa3;
  }
  return bytes.length >= 12
    && bytes[4] === 0x66 && bytes[5] === 0x74 && bytes[6] === 0x79 && bytes[7] === 0x70; // "ftyp"
}

// The sniff needs the first 12 bytes (mp4 "ftyp" box check reads [4..8)); read a
// round 16 from the stored object.
const MAGIC_PREFIX_BYTES = 16;

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
  // Added by migration 0003. Legacy rows read back with the column defaults.
  narrated?: number;
  has_transcript?: number;
  upload_id?: string | null;
  part_size?: number | null;
  part_count?: number | null;
  last_activity_at?: number | null;
}

/** Public share URL for a clip slug (what POST /clips and complete return). */
export function clipPublicUrl(env: Env, id: string): string {
  const base = (env.PUBLIC_BASE || "https://clips.revu.lol").replace(/\/+$/, "");
  return `${base}/${id}`;
}

/**
 * Active (unexpired) clip usage for a user: COUNT and SUM(size_bytes) over every
 * live row, pending multipart reservations included (no status filter).
 */
export async function activeClipUsage(
  db: D1Database,
  userId: number,
  now: number,
): Promise<{ n: number; total: number } | null> {
  return db
    .prepare("SELECT COUNT(*) AS n, COALESCE(SUM(size_bytes), 0) AS total FROM clips WHERE user_id = ?1 AND expires_at > ?2")
    .bind(userId, now)
    .first<{ n: number; total: number }>();
}

async function insertClip(db: D1Database, row: ClipRow): Promise<void> {
  await db
    .prepare(
      `INSERT INTO clips
        (id, user_id, r2_key, content_type, size_bytes, duration_s, title, champion, created_at, expires_at, view_count, status, narrated)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)`,
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
      row.narrated ? 1 : 0,
    )
    .run();
}

/**
 * Look up a PUBLIC clip by slug. Returns null if missing, expired, or still a
 * pending multipart upload (status 'uploading'), so every public route 404s it.
 */
export async function findClip(db: D1Database, id: string): Promise<ClipRow | null> {
  const now = Math.floor(Date.now() / 1000);
  const row = await db
    .prepare("SELECT * FROM clips WHERE id = ?1 AND expires_at > ?2 AND status = 'ready' LIMIT 1")
    .bind(id, now)
    .first<ClipRow>();
  return row ?? null;
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
 * meta-refresh (no script: this origin holds the web session cookie, so the
 * page ships a strict CSP). We serve THIS to everyone hitting a bare slug so a
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
  // A middle dot, not a dash: user-facing copy never uses em or en dashes.
  const title = escapeHtml(champ ? `${rawTitle} · ${champ}` : rawTitle);
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
<meta name="color-scheme" content="dark" />
<style>html,body{background:#212121;color:#ececec;font-family:'Segoe UI',system-ui,sans-serif}</style>
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
</body>
</html>`;

  return new Response(html, {
    status: 200,
    headers: {
      "Content-Type": "text/html; charset=utf-8",
      "X-Content-Type-Options": "nosniff",
      // clips.revu.lol also holds the web session cookie, so this page runs no
      // script at all: the meta refresh and the link do the redirect.
      "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
      // Short cache: clip metadata (title/views) can change, and a deleted clip
      // should stop unfurling reasonably soon.
      "Cache-Control": "public, max-age=300",
    },
  });
}

/** Generate a slug not already taken. A few tries is overwhelmingly enough. */
export async function uniqueSlug(db: D1Database): Promise<string> {
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

  const contentType = mediaTypeOf(request);
  if (!ALLOWED_CONTENT_TYPES.has(contentType)) {
    return jsonResponse({ error: "unsupported_media_type", message: "clip must be video/mp4 or video/webm" }, 415);
  }

  const tooLargeMessage = `clip exceeds ${Math.floor(MAX_CLIP_BYTES / (1024 * 1024))} MB`;

  // A declared Content-Length is REQUIRED, and it is checked before a single body
  // byte is read. R2 can only stream a body natively when its length is known up
  // front; the only alternative for an undeclared body is materialising it in
  // isolate memory up to the cap, which costs roughly twice the cap (chunk list
  // plus the merged copy), over the ~128 MB isolate limit: a memory-DoS lever for
  // any logged-in user. The desktop app always sets Content-Length
  // (ClipUploadService.cs), the same rule the multipart part route already
  // enforces, so refusing an undeclared body loses nothing. Strict digits only:
  // parseInt would accept "1e9" as 1.
  const declaredLen = parseStrictInt(request.headers.get("Content-Length"));
  if (declaredLen === null || declaredLen < 0) {
    return jsonResponse({ error: "length_required", message: "Content-Length is required" }, 411);
  }
  if (declaredLen > MAX_CLIP_BYTES) {
    return jsonResponse({ error: "payload_too_large", message: tooLargeMessage }, 413);
  }
  if (declaredLen === 0 || !request.body) {
    return badRequest("empty body");
  }

  const now = Math.floor(Date.now() / 1000);

  // Pre-upload quota check on what we know UP FRONT: the active-clip COUNT and the
  // byte total including this file's declared length, so the byte ceiling is
  // enforced here before a single byte is streamed. A precise recount runs after
  // the row is committed (see below) to close the concurrent-upload race.
  const usage = await activeClipUsage(env.DB, userId, now);
  if (usage && (usage.n >= MAX_ACTIVE_CLIPS_PER_USER || usage.total + declaredLen > MAX_ACTIVE_CLIP_BYTES_PER_USER)) {
    return jsonResponse(
      { error: "quota_exceeded", message: QUOTA_EXCEEDED_MESSAGE },
      403,
    );
  }

  const url = new URL(request.url);
  const title = clampText(url.searchParams.get("title"), 120);
  const champion = clampText(url.searchParams.get("champion"), 40);
  const durationRaw = url.searchParams.get("duration");
  const durationParsed = durationRaw ? parseInt(durationRaw, 10) : NaN;
  const duration = Number.isFinite(durationParsed) && durationParsed > 0 ? durationParsed : null;
  const narrated = url.searchParams.get("narrated") === "1" ? 1 : 0;

  const id = await uniqueSlug(env.DB);
  const ext = contentType === "video/webm" ? "webm" : "mp4";
  const r2Key = `clips/${id}.${ext}`;

  // Hand the request body to R2 with ZERO JS stream processing on the hot path.
  //
  // History (all O(filesize) CPU → Cloudflare "exceededCpu" → raw 503 → desktop
  // "temporarily unavailable"):
  //   1. Original: arrayBuffer() the whole file then re-copy into one ArrayBuffer.
  //   2. "Streaming fix": pipe request.body through a guard TransformStream — but a
  //      TransformStream runs a JS `transform` callback for EVERY chunk.
  //   3. "Peek + relay": read the header, then relay chunks through a custom pull()
  //      ReadableStream into FixedLengthStream — STILL a JS pull() per chunk plus a
  //      pipeTo copy. Measured live 2026-06-30: a 30 MB clip burned cpuMs=1088 on
  //      SUCCESS and got outcome=exceededCpu (cpuMs up to ~941, and instant 10 ms
  //      kills on a warm isolate whose per-request budget was already drained by a
  //      prior upload — that's the "2 succeed then it breaks" pattern).
  //
  // The fix: `env.CLIPS.put(r2Key, request.body, ...)`. The request carries a
  // Content-Length (required above; the desktop always sets it —
  // ClipUploadService.cs), so R2 treats request.body as a KNOWN-LENGTH stream and
  // consumes it in the runtime's C++ layer — the isolate never touches the bulk
  // bytes, so upload CPU no longer scales with file size. No TransformStream, no
  // FixedLengthStream, no pipeTo, no custom ReadableStream, no allSettled: one
  // producer, one consumer, one await. Cloudflare enforces the declared length on
  // the wire, so the stored object is at most MAX_CLIP_BYTES.
  //
  // The magic-byte sniff runs AFTER the put, on a 16-byte ranged read of the
  // stored object (the same check multipart complete does), so it costs O(1)
  // regardless of file size and stays off the streaming hot path. It is what makes
  // the declared video type mean something: without it any bytes at all would be
  // served under video/mp4 or video/webm.
  const uploadedBytes = declaredLen;
  let leading: Uint8Array;
  try {
    await env.CLIPS.put(r2Key, request.body, { httpMetadata: { contentType } });
    const head = await env.CLIPS.get(r2Key, {
      range: { offset: 0, length: Math.min(MAGIC_PREFIX_BYTES, declaredLen) },
    });
    if (!head) throw new Error("uploaded clip object is missing");
    leading = new Uint8Array(await head.arrayBuffer());
  } catch (err) {
    // Best-effort delete any partial object R2 may have started.
    try { await env.CLIPS.delete(r2Key); } catch { /* best effort */ }
    throw err; // genuine R2 failure → bubbles to the dispatch catch (clean 502)
  }
  if (!hasVideoMagicBytes(leading, contentType)) {
    try { await env.CLIPS.delete(r2Key); } catch { /* best effort */ }
    return jsonResponse(
      { error: "unsupported_media_type", message: "file content does not match the declared video type" },
      415,
    );
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
      narrated,
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
  const postUsage = await activeClipUsage(env.DB, userId, now);
  if (postUsage && (postUsage.n > MAX_ACTIVE_CLIPS_PER_USER || postUsage.total > MAX_ACTIVE_CLIP_BYTES_PER_USER)) {
    try { await env.CLIPS.delete(r2Key); } catch { /* best effort */ }
    try { await env.DB.prepare("DELETE FROM clips WHERE id = ?1").bind(id).run(); } catch { /* best effort */ }
    return jsonResponse(
      { error: "quota_exceeded", message: QUOTA_EXCEEDED_MESSAGE },
      403,
    );
  }

  return jsonResponse({ id, url: clipPublicUrl(env, id), expires_at: expiresAt }, 201);
}

/** GET /clip-meta/:id — public JSON for the watch page. 404 if missing/expired. */
export async function handleClipMeta(env: Env, id: string): Promise<Response> {
  if (!SLUG_REGEX.test(id)) return jsonResponse({ error: "not_found" }, 404);
  const clip = await findClip(env.DB, id);
  if (!clip) return jsonResponse({ error: "not_found" }, 404);
  const hasTranscript = !!clip.has_transcript;
  let transcriptLanguage: string | null = null;
  if (hasTranscript) {
    const t = await env.DB
      .prepare("SELECT language FROM clip_transcripts WHERE clip_id = ?1")
      .bind(id)
      .first<{ language: string | null }>();
    transcriptLanguage = t ? (t.language ?? "") : null;
  }
  // Never expose status, upload_id, last_activity_at or user_id.
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
      narrated: !!clip.narrated,
      has_transcript: hasTranscript,
      transcript_language: transcriptLanguage,
    },
    200,
  );
}

/** True when an If-None-Match / If-Range value names `etag` (weak tags compare equal). */
function etagMatches(header: string, etag: string): boolean {
  if (header.trim() === "*") return true;
  const strip = (t: string) => t.trim().replace(/^W\//, "");
  return header.split(",").some((t) => strip(t) === strip(etag));
}

/**
 * GET|HEAD /clip-file/:id — the video bytes from R2. 404 if missing/expired.
 *
 *   - Range: a single `bytes=` range is a 206; one that starts past the end is a
 *     416 whose Content-Range is the unsatisfied form (an asterisk, then the
 *     size); anything unparseable is ignored (full 200), as RFC 9110 allows.
 *   - If-None-Match matching the ETag is a 304 with no body.
 *   - If-Range that no longer matches the ETag ignores the Range (full 200).
 *   - HEAD reads only the object's metadata (R2 head), never its body.
 * Conditional requests and HEAD cost one R2 head; a plain GET costs one get.
 */
export async function handleClipFile(request: Request, env: Env, id: string): Promise<Response> {
  if (!SLUG_REGEX.test(id)) return jsonResponse({ error: "not_found" }, 404);
  const clip = await findClip(env.DB, id);
  if (!clip) return jsonResponse({ error: "not_found" }, 404);
  const size = clip.size_bytes;

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

  const isHead = request.method === "HEAD";
  const ifNoneMatch = request.headers.get("If-None-Match");
  const ifRange = request.headers.get("If-Range");
  let range = parseRange(request.headers.get("Range"), size);

  // Metadata first when the answer depends on the ETag, or no body is wanted.
  let etag: string | null = null;
  if (isHead || ifNoneMatch !== null || (range !== null && ifRange !== null)) {
    const meta = await env.CLIPS.head(clip.r2_key);
    if (!meta) return jsonResponse({ error: "not_found" }, 404);
    etag = meta.httpEtag || null;
    if (etag) headers.set("ETag", etag);
    if (etag && ifNoneMatch !== null && etagMatches(ifNoneMatch, etag)) {
      headers.delete("Content-Type");
      return new Response(null, { status: 304, headers });
    }
    // If-Range carries an ETag here (we send no Last-Modified); a stale one
    // means "send the whole current file".
    if (range !== null && ifRange !== null && !(etag && etagMatches(ifRange, etag))) range = null;
  }

  if (range === "unsatisfiable") {
    headers.set("Content-Range", `bytes */${size}`);
    headers.delete("Content-Type");
    return new Response(null, { status: 416, headers });
  }

  if (range) {
    headers.set("Content-Length", String(range.length));
    headers.set("Content-Range", `bytes ${range.offset}-${range.offset + range.length - 1}/${size}`);
  } else {
    headers.set("Content-Length", String(size));
  }
  const status = range ? 206 : 200;
  if (isHead) return new Response(null, { status, headers });

  const obj = await env.CLIPS.get(clip.r2_key, range ? { range: { offset: range.offset, length: range.length } } : undefined);
  if (!obj) return jsonResponse({ error: "not_found" }, 404);
  if (obj.httpEtag) headers.set("ETag", obj.httpEtag);
  return new Response(obj.body, { status, headers });
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

  // A pending multipart upload is aborted first (it may already be completed or
  // gone, so this is best effort), then the key is ALWAYS deleted: that removes
  // a completed-but-unfinalized object as well as a normal ready clip.
  if (clip.upload_id) {
    try { await env.CLIPS.resumeMultipartUpload(clip.r2_key, clip.upload_id).abort(); } catch { /* best effort */ }
  }
  try { await env.CLIPS.delete(clip.r2_key); } catch { /* best effort; row removal is the source of truth */ }
  await env.DB.prepare("DELETE FROM clip_transcripts WHERE clip_id = ?1").bind(id).run();
  await env.DB.prepare("DELETE FROM clips WHERE id = ?1").bind(id).run();
  return jsonResponse({ ok: true }, 200);
}

/** GET /clips/mine — list the caller's own clips (newest first). */
export async function handleListMyClips(env: Env, userId: number | undefined): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);
  const now = Math.floor(Date.now() / 1000);
  const res = await env.DB
    .prepare("SELECT * FROM clips WHERE user_id = ?1 AND expires_at > ?2 ORDER BY created_at DESC LIMIT 200")
    .bind(userId, now)
    .all<ClipRow>();
  const clips = (res.results ?? []).map((c) => ({
    id: c.id,
    url: clipPublicUrl(env, c.id),
    title: c.title,
    champion: c.champion,
    duration_s: c.duration_s,
    size_bytes: c.size_bytes,
    created_at: c.created_at,
    expires_at: c.expires_at,
    view_count: c.view_count,
    status: c.status,
    narrated: !!c.narrated,
    has_transcript: !!c.has_transcript,
  }));
  return jsonResponse({ clips }, 200);
}

// ── small utils ────────────────────────────────────────────────────────────

export function clampText(value: string | null, max: number): string | null {
  if (value === null) return null;
  const t = value.trim();
  if (!t) return null;
  return t.length > max ? t.slice(0, max) : t;
}

/**
 * Parse a single-range `Range: bytes=start-end` header into an R2-friendly
 * { offset, length }. Returns "unsatisfiable" for a well-formed range that
 * starts at or past the end (or a zero-length suffix), and null for an absent,
 * malformed, inverted or multi-range header (caller serves the whole object).
 */
export function parseRange(header: string | null, size: number): { offset: number; length: number } | "unsatisfiable" | null {
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
    if (!Number.isFinite(suffix)) return null;
    if (suffix <= 0 || size <= 0) return "unsatisfiable";
    offset = Math.max(0, size - suffix);
    end = size - 1;
  } else {
    offset = parseInt(startStr, 10);
    end = endStr === "" ? size - 1 : parseInt(endStr, 10);
  }

  if (!Number.isFinite(offset) || !Number.isFinite(end)) return null;
  if (endStr !== "" && startStr !== "" && end < offset) return null;
  if (offset >= size) return "unsatisfiable";
  if (end >= size) end = size - 1;

  return { offset, length: end - offset + 1 };
}
