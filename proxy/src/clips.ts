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
// public endpoint must bound size regardless of client. A 90s high-bitrate
// 1080p/1440p clip tops out around ~150 MB, so 200 MB leaves headroom.
export const MAX_CLIP_BYTES = 200 * 1024 * 1024;

const ALLOWED_CONTENT_TYPES = new Set(["video/mp4", "video/webm"]);

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

  const lenHeader = request.headers.get("Content-Length");
  const declaredLen = lenHeader ? parseInt(lenHeader, 10) : NaN;
  if (Number.isFinite(declaredLen) && declaredLen > MAX_CLIP_BYTES) {
    return jsonResponse({ error: "payload_too_large", message: "clip exceeds 200 MB" }, 413);
  }

  const body = await request.arrayBuffer();
  if (body.byteLength === 0) {
    return badRequest("empty body");
  }
  // Guard the real size too — Content-Length can lie or be absent.
  if (body.byteLength > MAX_CLIP_BYTES) {
    return jsonResponse({ error: "payload_too_large", message: "clip exceeds 200 MB" }, 413);
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

  const now = Math.floor(Date.now() / 1000);
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
