/**
 * HLS shares, public side. Only on the host of PUBLIC_BASE (clips.revu.lol);
 * every other host, workers.dev included, gets 404:
 *
 *   POST     /vod-play/:id                          count one play, mint a token
 *   GET|HEAD /vod-hls/:id/:token/index.m3u8          EVENT playlist (ENDLIST once ended)
 *   GET|HEAD /vod-hls/:id/:token/init-:k.mp4         run k's init segment
 *   GET|HEAD /vod-hls/:id/:token/:seq.m4s            one segment, from R2
 *
 * The daily play cap applies at /vod-play only: a reached cap refuses new
 * plays, never people already watching. A media request is checked in this
 * order: host, Sec-Fetch-Site (a cross-site request from an origin outside
 * ALLOWED_ORIGINS is 403), the token (CPU only), the serving switches, and
 * only then D1 (cached per isolate) and R2. Nothing here is ever cached
 * publicly: playlists are no-store, inits and segments private, so Stop
 * sharing reaches every viewer within the 2 s isolate cache (what a browser
 * already downloaded can't be recalled, as with /vod-file).
 */

import { Env } from "./types";
import { Timing } from "./http";
import { ClipRow } from "./clips";
import { serveR2Object } from "./r2-serve";
import { hlsSegmentKey } from "./hls-objects";
import { opsFlagOn } from "./ops-flags";
import { mintPlayToken, playSecretOk, verifyPlayToken } from "./play-token";
import {
  HLS_PLAYABLE_MS,
  HLS_TARGET_DURATION,
  VOD_DAILY_PLAY_CAP,
  hlsPlayable,
  nextUtcMidnight,
  onPublicHost,
  utcDayOf,
  vodServingOn,
} from "./share-limits";

const NOINDEX = "noindex, nofollow";
const PLAYLIST_TYPE = "application/vnd.apple.mpegurl";
const SERVING_OFF_MESSAGE = "Playback is paused right now. Try again later.";
const PLAY_LIMIT_MESSAGE = "This game has been watched a lot today. Try again tomorrow.";
/** Freshness of the per-isolate share state for a playlist and for an init or segment. */
export const PLAYLIST_CACHE_MS = 1000;
export const SEGMENT_CACHE_MS = 2000;
/**
 * A request past the cached edge refreshes the state once (the segment may have
 * been committed since), but never from a state younger than this: requests
 * for segments that don't exist yet can't turn into one D1 read each.
 */
export const EDGE_REFRESH_MIN_MS = 250;
const MAX_SHARES_CACHED = 64;
const MAX_INITS_CACHED = 128;
const MEDIA_PATH = /^\/vod-hls\/([0-9A-Za-z]{7})\/([^/]{1,64})\/(?:(index\.m3u8)|init-(\d)\.mp4|(\d{1,5})\.m4s)$/;

function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

/** Headers every public HLS response carries (CORS itself comes from index.ts withCors). */
function publicHeaders(cacheControl: string, timing?: Timing): Headers {
  const h = new Headers();
  h.set("Cache-Control", cacheControl);
  h.set("X-Content-Type-Options", "nosniff");
  h.set("X-Robots-Tag", NOINDEX);
  h.set("Cross-Origin-Resource-Policy", "same-site");
  h.set("Vary", "Origin");
  if (timing) h.set("Server-Timing", timing.header());
  return h;
}

function publicError(status: number, error: string, message?: string, extra: Record<string, string> = {}, timing?: Timing): Response {
  const headers = publicHeaders("no-store", timing);
  headers.set("Content-Type", "application/json");
  for (const [k, v] of Object.entries(extra)) headers.set(k, v);
  return new Response(JSON.stringify(message === undefined ? { error } : { error, message }), { status, headers });
}

function allowedOrigins(env: Env): string[] {
  return (env.ALLOWED_ORIGINS || "").split(",").map((o) => o.trim()).filter((o) => o.length > 0);
}

/** Fetch metadata: a cross-site request is refused unless its Origin is allowed. */
function crossSiteRefused(request: Request, env: Env): boolean {
  if ((request.headers.get("Sec-Fetch-Site") ?? "").toLowerCase() !== "cross-site") return false;
  const origin = request.headers.get("Origin");
  return !origin || !allowedOrigins(env).includes(origin);
}

async function servingNow(env: Env): Promise<boolean> {
  return vodServingOn(env) && (await opsFlagOn(env, "vod_serving"));
}

// ── playlist text ───────────────────────────────────────────────────────────

export interface PlaylistRun {
  k: number;
  from: number;
  /** Run k's init bytes equal run k-1's (always false for run 0). */
  sameInit: boolean;
}

export interface PlaylistInput {
  /** EXTINF in ms of the committed segments 0..n-1. */
  durations: number[];
  runs: PlaylistRun[];
  ended: boolean;
}

/** EXTINF to exactly 3 decimals from integer milliseconds. */
function extinf(ms: number): string {
  return `${Math.floor(ms / 1000)}.${String(ms % 1000).padStart(3, "0")}`;
}

/**
 * The EVENT playlist, built on each GET from committed rows only, so it only
 * ever grows. A DISCONTINUITY precedes the first segment of every run k >= 1
 * (unless discontinuityAtSameInit is false and the run kept the same init),
 * and an EXT-X-MAP precedes it only when run k's init differs from run k-1's.
 * Two RFC 8216 deviations are deliberate: it updates faster than half the
 * target duration (6.2.1), and TIME-OFFSET=0 sits within three target
 * durations of a live edge (4.3.5.2); native players are never used on it
 * while it grows.
 */
export function buildPlaylist(p: PlaylistInput, opts: { discontinuityAtSameInit?: boolean } = {}): string {
  const discontinuityAtSameInit = opts.discontinuityAtSameInit ?? true;
  const starts = new Map<number, PlaylistRun>();
  for (const run of p.runs) if (run.k >= 1) starts.set(run.from, run);
  const lines = [
    "#EXTM3U",
    "#EXT-X-VERSION:7",
    `#EXT-X-TARGETDURATION:${HLS_TARGET_DURATION}`,
    "#EXT-X-MEDIA-SEQUENCE:0",
    "#EXT-X-PLAYLIST-TYPE:EVENT",
    "#EXT-X-INDEPENDENT-SEGMENTS",
    "#EXT-X-START:TIME-OFFSET=0,PRECISE=YES",
    '#EXT-X-MAP:URI="init-0.mp4"',
  ];
  for (let seq = 0; seq < p.durations.length; seq++) {
    const run = starts.get(seq);
    if (run) {
      if (!run.sameInit || discontinuityAtSameInit) lines.push("#EXT-X-DISCONTINUITY");
      if (!run.sameInit) lines.push(`#EXT-X-MAP:URI="init-${run.k}.mp4"`);
    }
    lines.push(`#EXTINF:${extinf(p.durations[seq])},`, `${seq}.m4s`);
  }
  if (p.ended) lines.push("#EXT-X-ENDLIST");
  return lines.join("\n") + "\n";
}

// ── per-isolate share state ─────────────────────────────────────────────────

interface ShareEntry {
  at: number;
  found: boolean;
  createdAt: number;
  segments: number;
  mediaMs: number;
  ended: boolean;
  runs: PlaylistRun[];
  /** Committed segments 0..segments-1: those rows never change, so they are fetched once. */
  durations: number[];
  sizes: number[];
  /** The playlist built from this state, once (every viewer in the isolate shares it). */
  playlist?: string;
}

const shares = new Map<string, ShareEntry>();
const inits = new Map<string, Uint8Array>();

/** Test hook: forget every cached share state and init. */
export function resetHlsServeCaches(): void {
  shares.clear();
  inits.clear();
}

function remember<V>(map: Map<string, V>, key: string, value: V, max: number): void {
  map.delete(key);
  if (map.size >= max) {
    const oldest = map.keys().next().value;
    if (oldest !== undefined) map.delete(oldest);
  }
  map.set(key, value);
}

/**
 * One D1 round trip (a read-only batch): the share's state, its runs (with
 * whether each init equals the previous run's, compared in SQL), and the
 * committed segments past those already cached.
 */
async function fetchShare(env: Env, timing: Timing, id: string, prev: ShareEntry | undefined): Promise<ShareEntry> {
  const now = nowSeconds();
  const known = prev?.found ? prev.durations.length : 0;
  const [stateRes, runsRes, segsRes] = await timing.db(() => env.DB.batch([
    env.DB
      .prepare(
        "SELECT c.created_at, h.segments, h.media_ms, h.ended FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.id = ?1 AND c.kind = 'vod' AND c.format = 'hls' AND c.status IN ('ready', 'uploading') AND c.expires_at > ?2",
      )
      .bind(id, now),
    env.DB
      .prepare(
        "SELECT r.k, r.from_seq, (r.k > 0 AND r.init = (SELECT p.init FROM vod_hls_runs p WHERE p.clip_id = r.clip_id AND p.k = r.k - 1)) AS same_init FROM vod_hls_runs r WHERE r.clip_id = ?1 ORDER BY r.k",
      )
      .bind(id),
    env.DB
      .prepare(
        "SELECT seq, duration_ms, bytes FROM vod_hls_segments WHERE clip_id = ?1 AND committed = 1 AND seq >= ?2 AND seq < (SELECT segments FROM vod_hls WHERE clip_id = ?1) ORDER BY seq",
      )
      .bind(id, known),
  ]));
  const state = ((stateRes as D1Result<{ created_at: number; segments: number; media_ms: number; ended: number }>).results ?? [])[0];
  if (!state) return { at: Date.now(), found: false, createdAt: 0, segments: 0, mediaMs: 0, ended: false, runs: [], durations: [], sizes: [] };

  // The cached rows are reused only while they belong to the same share (an
  // id is never reused while its rows exist, but a cache can outlive them).
  const start = prev?.found && prev.createdAt === Number(state.created_at) ? known : 0;
  if (start !== known) return fetchShare(env, timing, id, undefined);
  const durations = start > 0 ? prev!.durations.slice() : [];
  const sizes = start > 0 ? prev!.sizes.slice() : [];
  for (const r of (segsRes as D1Result<{ seq: number; duration_ms: number; bytes: number }>).results ?? []) {
    if (Number(r.seq) !== durations.length) break; // never list past a hole
    durations.push(Number(r.duration_ms));
    sizes.push(Number(r.bytes));
  }
  const runs = ((runsRes as D1Result<{ k: number; from_seq: number; same_init: number | null }>).results ?? [])
    .map((r) => ({ k: Number(r.k), from: Number(r.from_seq), sameInit: Number(r.same_init) === 1 }));
  return {
    at: Date.now(), found: true, createdAt: Number(state.created_at), segments: durations.length, mediaMs: Number(state.media_ms),
    ended: Number(state.ended) === 1, runs, durations, sizes,
  };
}

/** The share's state, from the isolate cache when it is fresher than maxAgeMs. */
async function shareState(env: Env, timing: Timing, id: string, maxAgeMs: number): Promise<ShareEntry> {
  const cached = shares.get(id);
  if (cached && Date.now() - cached.at < maxAgeMs) return cached;
  const entry = await fetchShare(env, timing, id, cached);
  remember(shares, id, entry, MAX_SHARES_CACHED);
  return entry;
}

/** The state for an init or segment request; `past` says when it asks beyond the cached state. */
async function shareStateFor(env: Env, timing: Timing, id: string, past: (e: ShareEntry) => boolean): Promise<ShareEntry> {
  const entry = await shareState(env, timing, id, SEGMENT_CACHE_MS);
  if (entry.found && past(entry) && Date.now() - entry.at >= EDGE_REFRESH_MIN_MS) return shareState(env, timing, id, 0);
  return entry;
}

// ── POST /vod-play/:id ──────────────────────────────────────────────────────

/** The only place a play of an HLS share is counted (VOD_DAILY_PLAY_CAP per UTC day). */
export async function handleVodPlay(request: Request, env: Env): Promise<Response> {
  if (!onPublicHost(request, env)) return publicError(404, "not_found");
  const m = /^\/vod-play\/([0-9A-Za-z]{7})$/.exec(new URL(request.url).pathname);
  if (!m) return publicError(404, "not_found");
  if (request.method !== "POST") return publicError(405, "method_not_allowed");
  const origin = request.headers.get("Origin");
  if (!origin || !allowedOrigins(env).includes(origin)) return publicError(403, "forbidden_origin");
  if (!playSecretOk(env) || !(await servingNow(env))) return publicError(503, "vod_serving_disabled", SERVING_OFF_MESSAGE);

  const id = m[1];
  const timing = new Timing();
  const now = nowSeconds();
  const today = utcDayOf(now);
  const counted = await timing.db(() => env.DB
    .prepare(
      `UPDATE clips SET play_count = CASE WHEN play_day = ?2 THEN play_count + 1 ELSE 1 END, play_day = ?2
       WHERE id = ?1 AND kind = 'vod' AND format = 'hls' AND status IN ('ready', 'uploading') AND expires_at > ?3
         AND (play_day IS NOT ?2 OR play_count < ?4)
         AND EXISTS (SELECT 1 FROM vod_hls h WHERE h.clip_id = ?1 AND ((h.runs >= 1 AND h.media_ms >= ?5) OR (h.ended = 1 AND h.segments >= 1)))
       RETURNING play_count`,
    )
    .bind(id, today, now, VOD_DAILY_PLAY_CAP, HLS_PLAYABLE_MS)
    .first<{ play_count: number }>());
  if (counted) {
    const headers = publicHeaders("no-store", timing);
    headers.set("Content-Type", "application/json");
    return new Response(JSON.stringify(await mintPlayToken(env, id, now)), { status: 200, headers });
  }

  const row = await timing.db(() => env.DB
    .prepare(
      "SELECT c.play_day, c.play_count, h.runs, h.media_ms, h.ended, h.segments FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.id = ?1 AND c.kind = 'vod' AND c.format = 'hls' AND c.status IN ('ready', 'uploading') AND c.expires_at > ?2",
    )
    .bind(id, now)
    .first<{ play_day: string | null; play_count: number; runs: number; media_ms: number; ended: number; segments: number }>());
  if (!row) return publicError(404, "not_found", undefined, {}, timing);
  if (!hlsPlayable(row)) return publicError(404, "not_ready", "The game is still starting to upload.", { "Retry-After": "2" }, timing);
  if (row.play_day === today && Number(row.play_count) >= VOD_DAILY_PLAY_CAP) {
    return publicError(429, "play_limit", PLAY_LIMIT_MESSAGE, { "Retry-After": String(Math.max(1, nextUtcMidnight(now) - now)) }, timing);
  }
  return publicError(503, "busy", "Try again in a moment.", { "Retry-After": "1" }, timing);
}

// ── GET|HEAD /vod-hls/:id/:token/... ────────────────────────────────────────

export async function handleHlsMedia(request: Request, env: Env): Promise<Response> {
  if (!onPublicHost(request, env)) return publicError(404, "not_found");
  const m = MEDIA_PATH.exec(new URL(request.url).pathname);
  if (!m) return publicError(404, "not_found");
  if (request.method !== "GET" && request.method !== "HEAD") return publicError(405, "method_not_allowed");
  if (crossSiteRefused(request, env)) return publicError(403, "forbidden_origin");
  const [, id, token, playlist, initK, seqRaw] = m;
  const now = nowSeconds();
  // CPU only, before any D1 or R2 work.
  if (!playSecretOk(env)) return publicError(503, "vod_serving_disabled", SERVING_OFF_MESSAGE);
  if (!(await verifyPlayToken(env, id, token, now))) return publicError(403, "bad_token");
  if (!(await servingNow(env))) return publicError(503, "vod_serving_disabled", SERVING_OFF_MESSAGE);

  const timing = new Timing();
  if (playlist) return servePlaylist(request, env, timing, id);
  if (initK !== undefined) return serveInit(request, env, timing, id, Number(initK));
  return serveSegment(request, env, timing, id, Number(seqRaw));
}

async function servePlaylist(request: Request, env: Env, timing: Timing, id: string): Promise<Response> {
  const entry = await shareState(env, timing, id, PLAYLIST_CACHE_MS);
  if (!entry.found) return publicError(404, "not_found", undefined, {}, timing);
  if (!hlsPlayable({ runs: entry.runs.length, media_ms: entry.mediaMs, ended: entry.ended, segments: entry.segments })) {
    return publicError(404, "not_ready", "The game is still starting to upload.", { "Retry-After": "2" }, timing);
  }
  const text = (entry.playlist ??= buildPlaylist({ durations: entry.durations, runs: entry.runs, ended: entry.ended }));
  const headers = publicHeaders("no-store", timing);
  headers.set("Content-Type", PLAYLIST_TYPE);
  return new Response(request.method === "HEAD" ? null : text, { status: 200, headers });
}

/** D1 returns a BLOB as an array of byte numbers; node:sqlite as a Uint8Array. */
function blobBytes(value: unknown): Uint8Array {
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  return new Uint8Array(value as number[]);
}

async function serveInit(request: Request, env: Env, timing: Timing, id: string, k: number): Promise<Response> {
  const entry = await shareStateFor(env, timing, id, (e) => k >= e.runs.length);
  if (!entry.found || k >= entry.runs.length) return publicError(404, "not_found", undefined, {}, timing);

  const key = `${id}:${entry.createdAt}:${k}`;
  let bytes = inits.get(key);
  if (!bytes) {
    const row = await timing.db(() => env.DB.prepare("SELECT init FROM vod_hls_runs WHERE clip_id = ?1 AND k = ?2").bind(id, k).first<{ init: unknown }>());
    if (!row) return publicError(404, "not_found", undefined, {}, timing);
    bytes = blobBytes(row.init);
    remember(inits, key, bytes, MAX_INITS_CACHED); // runs never change their init
  }
  const headers = publicHeaders("private, max-age=3600", timing);
  headers.set("Content-Type", "video/mp4");
  headers.set("Content-Length", String(bytes.byteLength));
  headers.set("Content-Security-Policy", "sandbox");
  // A copy: the cached bytes must outlive this response's body.
  return new Response(request.method === "HEAD" ? null : bytes.slice(), { status: 200, headers });
}

async function serveSegment(request: Request, env: Env, timing: Timing, id: string, seq: number): Promise<Response> {
  // Asked past what this isolate knows: it may have been committed since. Look once.
  const entry = await shareStateFor(env, timing, id, (e) => seq >= e.segments);
  if (!entry.found || seq >= entry.segments) return publicError(404, "not_found", undefined, {}, timing);

  const headers = publicHeaders("private, max-age=3600");
  headers.set("Content-Type", "video/mp4");
  headers.set("Accept-Ranges", "bytes");
  headers.set("Content-Security-Policy", "sandbox");
  const res = await timing.r2op(() => serveR2Object(request, env.CLIPS, hlsSegmentKey(id, seq), entry.sizes[seq], headers));
  if (res.status === 404) return publicError(404, "not_found", undefined, {}, timing); // stopped meanwhile
  const out = new Headers(res.headers);
  out.set("Server-Timing", timing.header());
  return new Response(res.body, { status: res.status, headers: out });
}

// ── /vod-meta fields ────────────────────────────────────────────────────────

interface HlsMetaRow {
  segments: number;
  media_ms: number;
  runs: number;
  ended: number;
  truncated: number;
  paused_reason: string | null;
  last_commit_ms: number | null;
  ingest_milli: number | null;
}

/** The fields /vod-meta adds for an HLS share. Never hls_bytes, part_puts, keys or tokens. */
export async function hlsMetaFields(env: Env, row: ClipRow, now: number): Promise<Record<string, unknown>> {
  const s = await env.DB
    .prepare("SELECT segments, media_ms, runs, ended, truncated, paused_reason, last_commit_ms, ingest_milli FROM vod_hls WHERE clip_id = ?1")
    .bind(row.id)
    .first<HlsMetaRow>();
  const state: HlsMetaRow = s ?? {
    segments: 0, media_ms: 0, runs: 0, ended: 0, truncated: 0, paused_reason: null, last_commit_ms: null, ingest_milli: null,
  };
  const nowMs = Date.now();
  return {
    format: "hls",
    playable: hlsPlayable(state),
    media_seconds: Number(state.media_ms) / 1000,
    ingest_x: state.ingest_milli === null ? null : Number(state.ingest_milli) / 1000,
    last_commit_age_s: state.last_commit_ms === null ? null : Math.max(0, Math.floor((nowMs - Number(state.last_commit_ms)) / 1000)),
    paused_reason: state.paused_reason ?? null,
    ended: Number(state.ended) === 1,
    truncated: Number(state.truncated) === 1,
    play_limited: row.play_day === utcDayOf(now) && Number(row.play_count ?? 0) >= VOD_DAILY_PLAY_CAP,
    progress: { parts: Number(state.segments), part_count: Math.max(1, Number(row.part_count) || 1) },
  };
}
