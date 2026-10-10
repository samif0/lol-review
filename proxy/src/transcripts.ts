/**
 * Narration transcripts (3.14.0).
 *
 *   POST   /transcribe                 one base64 MP3 chunk → Whisper segments
 *   PUT    /clips/:id/transcript       owner stores the C2 transcript document
 *   DELETE /clips/:id/transcript       owner removes it (idempotent)
 *   GET    /clip-transcript/:id        public JSON or WebVTT for the share page
 *
 * CPU budget: /transcribe reads the body with request.text() and hands that
 * string to Workers AI unchanged. The Worker never decodes or re-encodes base64
 * and never touches the audio bytes in JS. The audio is never logged or stored.
 *
 * Times are seconds on the clip timeline, rounded to 3 decimals.
 */

import { Env } from "./types";
import { jsonResponse, mediaTypeOf, parseStrictInt, readSmallBody } from "./http";
import { ClipRow, SLUG_REGEX } from "./clips";
import { transcribeLimitOrDeny } from "./user-limits";

export const WHISPER_MODEL = "@cf/openai/whisper-large-v3-turbo";
export const LOL_PROMPT =
  "League of Legends gameplay commentary. Terms: gank, jungler, top lane, mid lane, bot lane, support, ADC, Baron, Rift Herald, dragon, drake, Elder, Flash, Ignite, Teleport, ult, CS, wave, freeze, slow push, recall, ward, vision, objective, teamfight, all-in, cooldown.";

export const TRANSCRIBE_MAX_BODY_BYTES = 1048576;
export const TRANSCRIBE_MAX_CHUNK_MS = 120000;
export const TRANSCRIBE_MIN_OFFSET_MS = -60000;
export const TRANSCRIBE_MAX_OFFSET_MS = 1200000;
export const TRANSCRIBE_USER_DAILY_SECONDS = 7200;
export const TRANSCRIBE_GLOBAL_DAILY_SECONDS = 10800;
// The desktop always encodes 24 kbps mono MP3 (3000 bytes per second). The quota
// is charged from the larger of the declared duration and the duration the body
// size implies at that rate, and a body far larger than its declared duration
// is refused, so a tiny duration_ms can't smuggle minutes of audio past the cap.
export const TRANSCRIBE_AUDIO_BYTES_PER_SECOND = 3000;
const TRANSCRIBE_IMPLIED_SLACK_FACTOR = 1.5;
const TRANSCRIBE_IMPLIED_SLACK_SECONDS = 2;
export const TRANSCRIPT_MAX_BODY_BYTES = 262144;
export const TRANSCRIPT_MAX_SEGMENTS = 3000;
export const TRANSCRIPT_MAX_SEGMENT_CHARS = 500;
// Aggregate stored transcript text per user across live clips. A realistic
// 10-minute narration is about 25 KB, so this is hundreds of full transcripts.
export const TRANSCRIPT_USER_MAX_BYTES = 8 * 1024 * 1024;
// transcribe_usage.user_id 0 is the account-wide (global) daily row.
const GLOBAL_USAGE_USER_ID = 0;
// Desktop clamps end to the clip duration; allow a little slack server-side.
const TRANSCRIPT_END_SLACK_SECONDS = 5;

const QUOTA_MESSAGE = "Daily transcript limit reached. Try again tomorrow.";
const HALLUCINATIONS = new Set(["thanks for watching", "thank you for watching", "please subscribe", "subscribe", "you"]);

export interface TranscriptSegment {
  start: number;
  end: number;
  text: string;
}

interface WhisperSegment {
  start?: number;
  end?: number;
  text?: string;
  avg_logprob?: number;
  no_speech_prob?: number;
}

interface WhisperOutput {
  text?: string;
  segments?: WhisperSegment[];
  transcription_info?: { language?: string };
}

// ── small helpers ──────────────────────────────────────────────────────────

function round3(n: number): number {
  return Math.round(n * 1000) / 1000;
}

/** Trim, collapse internal whitespace to one space, cap at 500 chars. */
export function cleanText(raw: string): string {
  const t = raw.replace(/\s+/g, " ").trim();
  return t.length > TRANSCRIPT_MAX_SEGMENT_CHARS ? t.slice(0, TRANSCRIPT_MAX_SEGMENT_CHARS).trim() : t;
}

function isHallucination(text: string): boolean {
  const key = text.toLowerCase().replace(/[^\p{L}\p{N}\s]/gu, "").replace(/\s+/g, " ").trim();
  return HALLUCINATIONS.has(key);
}

function utcDay(ms: number): string {
  return new Date(ms).toISOString().slice(0, 10);
}

function secondsUntilUtcMidnight(ms: number): number {
  const s = Math.floor(ms / 1000);
  return Math.max(1, 86400 - (s % 86400));
}

function notFound(): Response {
  return jsonResponse({ error: "not_found" }, 404);
}

// ── POST /transcribe ───────────────────────────────────────────────────────

/** Turn Whisper's output into clip-timeline segments (filters + offset shift). */
export function normalizeWhisperSegments(out: WhisperOutput, offsetMs: number, durationMs: number): TranscriptSegment[] {
  const shift = offsetMs / 1000;
  const segments: TranscriptSegment[] = [];
  for (const seg of out.segments ?? []) {
    const text = cleanText(typeof seg.text === "string" ? seg.text : "");
    if (!text) continue;
    if ((seg.no_speech_prob ?? 0) > 0.6 && (seg.avg_logprob ?? 0) < -1) continue;
    if (isHallucination(text)) continue;
    if (typeof seg.start !== "number" || typeof seg.end !== "number") continue;
    if (!Number.isFinite(seg.start) || !Number.isFinite(seg.end)) continue;
    const start = Math.max(0, round3(seg.start + shift));
    const end = round3(seg.end + shift);
    if (end <= start) continue;
    segments.push({ start, end, text });
  }
  // Fall back to out.text only when Whisper returned no segments at all. When it
  // did and the filters dropped every one, out.text is those same dropped
  // segments joined, so reviving it would undo the filters.
  const hadSegments = Array.isArray(out.segments) && out.segments.length > 0;
  if (segments.length === 0 && !hadSegments && typeof out.text === "string") {
    const text = cleanText(out.text);
    if (text && !isHallucination(text)) {
      const start = Math.max(0, round3(shift));
      const end = round3(shift + durationMs / 1000);
      if (end > start) segments.push({ start, end, text });
    }
  }
  return segments;
}

const USAGE_UPSERT_SQL =
  "INSERT INTO transcribe_usage (user_id, day, audio_seconds, requests) VALUES (?1, ?2, ?3, 1) ON CONFLICT(user_id, day) DO UPDATE SET audio_seconds = audio_seconds + excluded.audio_seconds, requests = requests + 1 RETURNING audio_seconds";
const USAGE_REFUND_SQL =
  "UPDATE transcribe_usage SET audio_seconds = audio_seconds - ?3, requests = requests - 1 WHERE user_id = ?1 AND day = ?2";

async function reserveUsage(env: Env, userId: number, day: string, secs: number): Promise<number> {
  const row = await env.DB.prepare(USAGE_UPSERT_SQL).bind(userId, day, secs).first<{ audio_seconds: number }>();
  return row?.audio_seconds ?? secs;
}

async function refundUsage(env: Env, userId: number, day: string, secs: number): Promise<void> {
  try {
    await env.DB.prepare(USAGE_REFUND_SQL).bind(userId, day, secs).run();
    await env.DB.prepare(USAGE_REFUND_SQL).bind(GLOBAL_USAGE_USER_ID, day, secs).run();
  } catch (err) {
    console.error(JSON.stringify({ scope: "transcribe.refund", error: (err as Error)?.message ?? String(err) }));
  }
}

export async function handleTranscribe(request: Request, env: Env, userId: number | undefined): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);

  const limited = transcribeLimitOrDeny(userId);
  if (limited) return limited;

  if (!env.AI) {
    return jsonResponse({ error: "transcribe_unavailable", message: "Transcripts are not available right now." }, 503);
  }

  const url = new URL(request.url);
  const offsetRaw = url.searchParams.get("offset_ms");
  const offsetMs = offsetRaw === null ? 0 : parseStrictInt(offsetRaw);
  if (offsetMs === null || offsetMs < TRANSCRIBE_MIN_OFFSET_MS || offsetMs > TRANSCRIBE_MAX_OFFSET_MS) {
    return jsonResponse({ error: "bad_request", message: "offset_ms must be an integer from -60000 to 1200000" }, 400);
  }
  const durationMs = parseStrictInt(url.searchParams.get("duration_ms"));
  if (durationMs === null || durationMs < 1 || durationMs > TRANSCRIBE_MAX_CHUNK_MS) {
    return jsonResponse({ error: "bad_request", message: "duration_ms must be an integer from 1 to 120000" }, 400);
  }
  const language = url.searchParams.get("language") ?? "en";
  if (language !== "auto" && !/^[a-z]{2}$/.test(language)) {
    return jsonResponse({ error: "bad_request", message: "language must be two lowercase letters or auto" }, 400);
  }
  if (mediaTypeOf(request) !== "text/plain") {
    return jsonResponse({ error: "bad_request", message: "body must be text/plain base64 audio" }, 400);
  }
  const lenHeader = request.headers.get("Content-Length");
  if (lenHeader === null) {
    return jsonResponse({ error: "length_required", message: "Content-Length is required" }, 411);
  }
  const declared = parseStrictInt(lenHeader);
  if (declared === null || declared < 0) {
    return jsonResponse({ error: "bad_request", message: "invalid Content-Length" }, 400);
  }
  if (declared > TRANSCRIBE_MAX_BODY_BYTES) {
    return jsonResponse({ error: "payload_too_large", message: "audio chunk exceeds 1 MB" }, 413);
  }

  const audio = await request.text();
  if (!audio) return jsonResponse({ error: "bad_request", message: "empty audio" }, 400);
  if (audio.length > TRANSCRIBE_MAX_BODY_BYTES) {
    return jsonResponse({ error: "payload_too_large", message: "audio chunk exceeds 1 MB" }, 413);
  }

  // Tie the charge to the audio actually sent (base64 is 4 chars per 3 bytes).
  const declaredSecs = Math.ceil(durationMs / 1000);
  const impliedSecs = Math.ceil((audio.length * 0.75) / TRANSCRIBE_AUDIO_BYTES_PER_SECOND);
  if (impliedSecs > declaredSecs * TRANSCRIBE_IMPLIED_SLACK_FACTOR + TRANSCRIBE_IMPLIED_SLACK_SECONDS) {
    return jsonResponse({ error: "bad_request", message: "audio is longer than duration_ms" }, 400);
  }

  // Reserve the audio seconds up front (user row + global row), refund on deny
  // or failure. RETURNING gives the post-increment totals atomically.
  const nowMs = Date.now();
  const day = utcDay(nowMs);
  const secs = Math.max(declaredSecs, impliedSecs);
  const userTotal = await reserveUsage(env, userId, day, secs);
  let globalTotal: number;
  try {
    globalTotal = await reserveUsage(env, GLOBAL_USAGE_USER_ID, day, secs);
  } catch (err) {
    // Only the user row was reserved; give it back before the 502.
    try {
      await env.DB.prepare(USAGE_REFUND_SQL).bind(userId, day, secs).run();
    } catch { /* best effort */ }
    throw err;
  }
  if (userTotal > TRANSCRIBE_USER_DAILY_SECONDS || globalTotal > TRANSCRIBE_GLOBAL_DAILY_SECONDS) {
    await refundUsage(env, userId, day, secs);
    return jsonResponse(
      { error: "transcribe_quota", message: QUOTA_MESSAGE },
      429,
      { "Retry-After": String(secondsUntilUtcMidnight(nowMs)) },
    );
  }

  let out: WhisperOutput;
  try {
    out = (await env.AI.run(WHISPER_MODEL, {
      audio,
      task: "transcribe",
      ...(language !== "auto" ? { language } : {}),
      vad_filter: true,
      condition_on_previous_text: false,
      initial_prompt: LOL_PROMPT,
    })) as WhisperOutput;
  } catch (err) {
    await refundUsage(env, userId, day, secs);
    // Log the error message only. Never the audio.
    console.error(JSON.stringify({ scope: "transcribe.ai", error: (err as Error)?.message ?? String(err) }));
    return jsonResponse({ error: "transcribe_error", message: "Transcription failed. Try again." }, 502);
  }

  const segments = normalizeWhisperSegments(out ?? {}, offsetMs, durationMs);
  const detected = out?.transcription_info?.language;
  const outLanguage = typeof detected === "string" && /^[A-Za-z-]{2,8}$/.test(detected)
    ? detected.toLowerCase()
    : (language === "auto" ? "" : language);

  return jsonResponse(
    {
      language: outLanguage,
      duration_s: round3(durationMs / 1000),
      text: segments.map((s) => s.text).join(" "),
      segments,
      usage: { audio_seconds_today: userTotal, cap_seconds: TRANSCRIBE_USER_DAILY_SECONDS },
    },
    200,
  );
}

// ── Transcript documents (C2) and VTT (C3) ─────────────────────────────────

/** Validate a C2 document's shape. Returns null when it is not acceptable. */
function parseTranscriptDoc(text: string): { language: string; segments: TranscriptSegment[] } | null {
  let doc: unknown;
  try {
    doc = JSON.parse(text);
  } catch {
    return null;
  }
  if (!doc || typeof doc !== "object" || Array.isArray(doc)) return null;
  const { version, language, segments } = doc as { version?: unknown; language?: unknown; segments?: unknown };
  if (version !== 1) return null;
  if (!Array.isArray(segments) || segments.length > TRANSCRIPT_MAX_SEGMENTS) return null;

  let lang = "";
  if (language !== undefined && language !== null) {
    if (typeof language !== "string") return null;
    lang = language.trim();
    if (lang.length !== 0 && (lang.length < 2 || lang.length > 8)) return null;
  }

  const out: TranscriptSegment[] = [];
  for (const seg of segments) {
    if (!seg || typeof seg !== "object") return null;
    const { start, end, text: segText } = seg as { start?: unknown; end?: unknown; text?: unknown };
    if (typeof start !== "number" || !Number.isFinite(start)) return null;
    if (typeof end !== "number" || !Number.isFinite(end)) return null;
    if (typeof segText !== "string") return null;
    out.push({ start, end, text: segText });
  }
  return { language: lang, segments: out };
}

/** Clamp, clean, drop empty/inverted segments, sort by start, round. */
export function normalizeTranscriptSegments(segments: TranscriptSegment[], durationS: number | null): TranscriptSegment[] {
  const out: TranscriptSegment[] = [];
  for (const seg of segments) {
    const text = cleanText(seg.text);
    if (!text) continue;
    let end = seg.end;
    if (durationS !== null && durationS > 0) end = Math.min(end, durationS + TRANSCRIPT_END_SLACK_SECONDS);
    const start = round3(Math.max(0, seg.start));
    end = round3(end);
    if (end <= start) continue;
    out.push({ start, end, text });
  }
  out.sort((a, b) => a.start - b.start || a.end - b.end);
  return out;
}

function vttTimestamp(seconds: number): string {
  const totalMs = Math.max(0, Math.round(seconds * 1000));
  const h = Math.floor(totalMs / 3600000);
  const m = Math.floor((totalMs % 3600000) / 60000);
  const s = Math.floor((totalMs % 60000) / 1000);
  const ms = totalMs % 1000;
  const pad = (n: number, w: number) => String(n).padStart(w, "0");
  return `${pad(h, 2)}:${pad(m, 2)}:${pad(s, 2)}.${pad(ms, 3)}`;
}

/** C3 cue-text escaping: `-->` becomes `->`, then & < > are entity-escaped. */
export function escapeVttText(text: string): string {
  let t = text;
  while (t.includes("-->")) t = t.replace(/-->/g, "->");
  return t.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

/** Build the C3 WebVTT file for normalized segments. */
export function buildVtt(segments: TranscriptSegment[]): string {
  let vtt = "WEBVTT\n\n";
  segments.forEach((seg, i) => {
    vtt += `${i + 1}\n${vttTimestamp(seg.start)} --> ${vttTimestamp(seg.end)}\n${escapeVttText(seg.text)}\n\n`;
  });
  return vtt;
}

// ── PUT /clips/:id/transcript ──────────────────────────────────────────────

export async function handlePutTranscript(
  request: Request,
  env: Env,
  userId: number | undefined,
  id: string,
): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);

  const now = Math.floor(Date.now() / 1000);
  const row = await env.DB
    .prepare("SELECT * FROM clips WHERE id = ?1 AND user_id = ?2 AND expires_at > ?3 AND status IN ('uploading', 'ready') LIMIT 1")
    .bind(id, userId, now)
    .first<ClipRow>();
  if (!row) return notFound();

  const text = await readSmallBody(request, TRANSCRIPT_MAX_BODY_BYTES);
  if (text === null) {
    return jsonResponse({ error: "payload_too_large", message: "transcript exceeds 256 KB" }, 413);
  }
  const doc = parseTranscriptDoc(text);
  if (!doc) {
    return jsonResponse({ error: "bad_transcript", message: "body must be a version 1 transcript document" }, 400);
  }

  const segments = normalizeTranscriptSegments(doc.segments, row.duration_s);
  if (segments.length === 0) {
    await env.DB.prepare("DELETE FROM clip_transcripts WHERE clip_id = ?1").bind(id).run();
    await env.DB.prepare("UPDATE clips SET has_transcript = 0 WHERE id = ?1").bind(id).run();
    return jsonResponse({ ok: true, segment_count: 0 }, 200);
  }

  const normalized = JSON.stringify({ version: 1, language: doc.language, segments });
  // Cap what one account can keep in D1 (this clip's old transcript excluded,
  // since the upsert replaces it).
  const stored = await env.DB
    .prepare(
      "SELECT COALESCE(SUM(length(t.segments_json)), 0) AS total FROM clip_transcripts t JOIN clips c ON c.id = t.clip_id WHERE c.user_id = ?1 AND c.expires_at > ?2 AND t.clip_id != ?3",
    )
    .bind(userId, now, id)
    .first<{ total: number }>();
  if ((stored?.total ?? 0) + normalized.length > TRANSCRIPT_USER_MAX_BYTES) {
    return jsonResponse({ error: "payload_too_large", message: "transcript storage limit reached" }, 413);
  }
  // The VTT is built from segments_json on each GET, never stored: its entity
  // escaping can grow text about 5x. The NOT NULL column gets an empty string.
  await env.DB
    .prepare(
      `INSERT INTO clip_transcripts (clip_id, language, segments_json, vtt, segment_count, created_at)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6)
       ON CONFLICT(clip_id) DO UPDATE SET language = excluded.language, segments_json = excluded.segments_json,
         vtt = excluded.vtt, segment_count = excluded.segment_count, created_at = excluded.created_at`,
    )
    .bind(id, doc.language, normalized, "", segments.length, now)
    .run();
  await env.DB.prepare("UPDATE clips SET has_transcript = 1 WHERE id = ?1").bind(id).run();

  return jsonResponse({ ok: true, segment_count: segments.length }, 200);
}

// ── DELETE /clips/:id/transcript ───────────────────────────────────────────

export async function handleDeleteTranscript(env: Env, userId: number | undefined, id: string): Promise<Response> {
  if (userId === undefined) return jsonResponse({ error: "login_required" }, 403);
  // Owner check without the expiry filter (like DELETE /clips/:id), so an owner
  // can clean up an expired-but-not-yet-purged clip.
  const row = await env.DB.prepare("SELECT * FROM clips WHERE id = ?1 LIMIT 1").bind(id).first<ClipRow>();
  if (!row || row.user_id !== userId) return notFound();
  await env.DB.prepare("DELETE FROM clip_transcripts WHERE clip_id = ?1").bind(id).run();
  await env.DB.prepare("UPDATE clips SET has_transcript = 0 WHERE id = ?1").bind(id).run();
  return jsonResponse({ ok: true }, 200);
}

// ── GET|HEAD /clip-transcript/:id (public) ─────────────────────────────────

const TRANSCRIPT_RESPONSE_HEADERS: Record<string, string> = {
  "X-Content-Type-Options": "nosniff",
  "Content-Security-Policy": "sandbox",
  "Cache-Control": "public, max-age=300",
};

/** Stored segments were normalized at PUT; keep only well-formed ones for VTT. */
function storedSegments(raw: unknown): TranscriptSegment[] {
  if (!Array.isArray(raw)) return [];
  const out: TranscriptSegment[] = [];
  for (const seg of raw) {
    if (!seg || typeof seg !== "object") continue;
    const { start, end, text } = seg as { start?: unknown; end?: unknown; text?: unknown };
    if (typeof start !== "number" || !Number.isFinite(start)) continue;
    if (typeof end !== "number" || !Number.isFinite(end)) continue;
    if (typeof text !== "string") continue;
    out.push({ start, end, text });
  }
  return out;
}

export async function handleGetTranscript(request: Request, env: Env, id: string): Promise<Response> {
  const headOnly = request.method === "HEAD";
  const fail = (): Response => {
    const res = jsonResponse({ error: "not_found" }, 404, TRANSCRIPT_RESPONSE_HEADERS);
    return headOnly ? new Response(null, { status: 404, headers: res.headers }) : res;
  };
  if (!SLUG_REGEX.test(id)) return fail();

  const now = Math.floor(Date.now() / 1000);
  const row = await env.DB
    .prepare(
      "SELECT t.language, t.segments_json FROM clip_transcripts t JOIN clips c ON c.id = t.clip_id WHERE c.id = ?1 AND c.expires_at > ?2 AND c.status = 'ready'",
    )
    .bind(id, now)
    .first<{ language: string | null; segments_json: string }>();
  if (!row) return fail();

  let segments: unknown = [];
  try {
    const doc = JSON.parse(row.segments_json) as unknown;
    segments = Array.isArray(doc) ? doc : ((doc as { segments?: unknown })?.segments ?? []);
  } catch {
    segments = [];
  }

  const url = new URL(request.url);
  let body: string;
  let contentType: string;
  if (url.searchParams.get("format") === "vtt") {
    body = buildVtt(storedSegments(segments));
    contentType = "text/vtt; charset=utf-8";
  } else {
    body = JSON.stringify({ id, language: row.language ?? "", segments });
    contentType = "application/json; charset=utf-8";
  }

  const headers = new Headers(TRANSCRIPT_RESPONSE_HEADERS);
  headers.set("Content-Type", contentType);
  return new Response(headOnly ? null : body, { status: 200, headers });
}

// ── cron ───────────────────────────────────────────────────────────────────

/** Drop transcribe_usage rows older than 7 days (UTC). Returns the cutoff day. */
export async function pruneTranscribeUsage(env: Env): Promise<string> {
  const cutoff = utcDay(Date.now() - 7 * 86400 * 1000);
  await env.DB.prepare("DELETE FROM transcribe_usage WHERE day < ?1").bind(cutoff).run();
  return cutoff;
}
