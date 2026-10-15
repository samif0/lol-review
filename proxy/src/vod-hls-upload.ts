/**
 * HLS shares, desktop side (Bearer, allowlisted owner of the row):
 *
 *   POST /vod-shares/uploads?...&format=hls    reserve (vod-shares.ts calls reserveHlsShare)
 *   PUT  /vod-shares/:id/hls/runs/:k?from&quality&fps   one run's init (ftyp + moov)
 *   PUT  /vod-shares/:id/hls/seg/:seq?ms&run   one segment (moof + mdat), straight to R2
 *   POST /vod-shares/:id/hls/pause             {"reason":"game"}
 *   POST /vod-shares/:id/hls/end               {"segments":N,"truncated":bool} (vod-hls-end.ts)
 *
 * A segment PUT costs two D1 round trips. The claim batch checks the session,
 * the row, the attempt cap, the run and the reservation, and upserts the
 * segment's pending row (its bytes count at once, so an aborted PUT and its
 * retry count once). Then `env.CLIPS.put(key, request.body)` and the commit
 * batch run as one task registered with ctx.waitUntil, so a client that hangs
 * up after the body can't skip the commit. The body is never wrapped in any JS
 * stream: a TransformStream, FixedLengthStream or pipeTo costs CPU per chunk
 * and has ended in exceededCpu here three times (see clips.ts handleUploadClip).
 *
 * Every row lookup is scoped to kind 'vod' AND format 'hls', so these routes
 * never touch an MP4 share or a clip, and the MP4 routes never touch these.
 */

import { Env } from "./types";
import { jsonResponse, parseStrictInt, readSmallBody, Timing } from "./http";
import { hasVideoMagicBytes } from "./clips";
import { storageUsage, vodFits } from "./storage-budget";
import { hlsPrefix, hlsSegmentKey } from "./hls-objects";
import { tombstoneObject } from "./upload-cleanup";
import {
  HLS_GROW_MIN_BYTES,
  HLS_INIT_MAX_BYTES,
  HLS_MAX_RUNS,
  HLS_MAX_SEGMENT_MS,
  HLS_PAUSE_REASONS,
  HLS_PLAYABLE_MS,
  HLS_PUT_SLACK,
  HLS_RESERVATION_SLACK_BYTES,
  HLS_SEGMENT_MAX_BYTES,
  HLS_SEGMENT_SECONDS,
  HLS_TARGET_DURATION,
  PART_PUT_FACTOR,
  VOD_MAX_BYTES,
  VOD_TTL_SECONDS,
  hlsAttemptCap,
  hlsPlayable,
  hlsSlots,
  vodUploadWindowSeconds,
  vodWatchUrl,
} from "./share-limits";

const QUALITIES = new Set(["1080p", "720p"]);
const FRAME_RATES = new Set(["30", "60"]);
const PAUSE_MAX_BODY_BYTES = 1024;
/** Time constant of the ingest EWMA, and the most one gap between commits counts for. */
const INGEST_TAU_MS = 30_000;

/** The guard every owner write repeats: the caller's own uploading HLS share, attempts left. */
const OWNED_OPEN = `kind = 'vod' AND format = 'hls' AND status = 'uploading' AND part_puts < ${PART_PUT_FACTOR} * part_count + ${HLS_PUT_SLACK}`;

function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

export function refuse(status: number, error: string, message?: string, extra: Record<string, unknown> = {}, headers: Record<string, string> = {}): Response {
  const body: Record<string, unknown> = { error };
  if (message !== undefined) body.message = message;
  return jsonResponse({ ...body, ...extra }, status, headers);
}

function timed(res: Response, timing: Timing): Response {
  res.headers.set("Server-Timing", timing.header());
  return res;
}

const TOO_MANY_PUTS_MESSAGE = "This upload retried too many times. Start the share again.";

// ── POST /vod-shares/uploads?format=hls (called by vod-shares.ts) ───────────

/**
 * Step 15 of the VOD init for format=hls: ONE D1 batch and no R2 call (the
 * segments are written one object each). part_count holds the segment slots,
 * so the attempt cap scales with the game's length.
 */
export async function reserveHlsShare(
  env: Env,
  p: { id: string; userId: number; size: number; duration: number; quality: string; now: number },
): Promise<Record<string, unknown>> {
  const slots = hlsSlots(p.duration);
  const uploadExpiresAt = p.now + vodUploadWindowSeconds(p.size);
  await env.DB.batch([
    env.DB
      .prepare(
        `INSERT INTO clips
          (id, user_id, r2_key, content_type, size_bytes, duration_s, created_at, expires_at, view_count, status,
           upload_id, part_size, part_count, last_activity_at, narrated, has_transcript, kind, quality, part_puts, format, hls_bytes)
         VALUES (?1, ?2, ?3, 'video/mp4', ?4, ?5, ?6, ?7, 0, 'uploading', NULL, NULL, ?8, ?6, 0, 0, 'vod', ?9, 0, 'hls', 0)`,
      )
      .bind(p.id, p.userId, hlsPrefix(p.id), p.size, p.duration, p.now, uploadExpiresAt, slots, p.quality),
    env.DB.prepare("INSERT INTO vod_hls (clip_id) VALUES (?1)").bind(p.id),
  ]);
  return {
    id: p.id,
    format: "hls",
    part_count: slots,
    segment_seconds: HLS_SEGMENT_SECONDS,
    target_duration: HLS_TARGET_DURATION,
    max_segment_bytes: HLS_SEGMENT_MAX_BYTES,
    init_max_bytes: HLS_INIT_MAX_BYTES,
    playable_ms: HLS_PLAYABLE_MS,
    max_runs: HLS_MAX_RUNS,
    expires_at: uploadExpiresAt,
    share_expires_at: p.now + VOD_TTL_SECONDS,
    url: vodWatchUrl(env, p.id),
  };
}

// ── PUT /vod-shares/:id/hls/runs/:k ─────────────────────────────────────────

/** A small binary body with a hard cap (inits only, never segments). */
async function readBytes(request: Request, maxBytes: number): Promise<Uint8Array | null> {
  if (!request.body) return new Uint8Array(0);
  const reader = request.body.getReader();
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
        return null;
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const out = new Uint8Array(total);
  let offset = 0;
  for (const c of chunks) { out.set(c, offset); offset += c.byteLength; }
  return out;
}

/**
 * Runs are append-only: k must be the next run (run 0 from seq 0; run k >= 1
 * from a seq past run k-1's start with no committed segment at or after it),
 * or a replay of the last run with the identical init and `from`. One batch,
 * in an order where no statement changes what a later one's guard reads
 * before that guard runs: the replay pair first, then the new run's insert,
 * its attempt and its count, the last two keyed on the inserted row.
 */
export async function handleHlsRunPut(request: Request, env: Env, userId: number, id: string, kRaw: string): Promise<Response> {
  const timing = new Timing();
  const url = new URL(request.url);
  const k = parseStrictInt(kRaw);
  const from = parseStrictInt(url.searchParams.get("from"));
  const resolution = url.searchParams.get("quality") ?? "";
  const fps = url.searchParams.get("fps") ?? "";
  if (k === null || k < 0) return refuse(400, "bad_request", "run must be 0 or more");
  if (from === null || from < 0) return refuse(400, "bad_request", "from must be the run's first segment");
  if (!QUALITIES.has(resolution)) return refuse(400, "bad_request", "quality must be 1080p or 720p");
  if (!FRAME_RATES.has(fps)) return refuse(400, "bad_request", "fps must be 30 or 60");
  const lenHeader = request.headers.get("Content-Length");
  if (lenHeader === null) return refuse(411, "length_required", "Content-Length is required");
  const len = parseStrictInt(lenHeader);
  if (len === null || len < 0) return refuse(400, "bad_request", "Content-Length must be a byte count");
  if (len > HLS_INIT_MAX_BYTES) return refuse(413, "payload_too_large", `an init can be at most ${HLS_INIT_MAX_BYTES} bytes`);
  if (k >= HLS_MAX_RUNS) return refuse(429, "too_many_runs", `a share can have at most ${HLS_MAX_RUNS} runs`);
  const init = await readBytes(request, HLS_INIT_MAX_BYTES);
  if (init === null) return refuse(413, "payload_too_large", `an init can be at most ${HLS_INIT_MAX_BYTES} bytes`);
  if (init.byteLength !== len) return refuse(400, "bad_request", "the body does not match Content-Length");
  if (len < 1 || !hasVideoMagicBytes(init, "video/mp4")) return refuse(415, "not_video", "an init must start with an ftyp box");

  const quality = `${resolution}${fps}`;
  const now = nowSeconds();
  const owned = `id = ?1 AND user_id = ?2 AND expires_at > ?3 AND ${OWNED_OPEN}`;
  const same = "EXISTS (SELECT 1 FROM vod_hls_runs WHERE clip_id = ?1 AND k = ?4 AND from_seq = ?5 AND init = ?6)";
  const inserted = "EXISTS (SELECT 1 FROM vod_hls_runs WHERE clip_id = ?1 AND k = ?2)";
  const results = await timing.db(() => env.DB.batch([
    // Replay: identical bytes and from on the last run.
    env.DB
      .prepare(`UPDATE vod_hls SET paused_at = NULL, paused_reason = NULL WHERE clip_id = ?1 AND runs = ?4 + 1 AND ${same} AND EXISTS (SELECT 1 FROM clips WHERE ${owned})`)
      .bind(id, userId, now, k, from, init),
    env.DB
      .prepare(`UPDATE clips SET part_puts = part_puts + 1, last_activity_at = ?3 WHERE ${owned} AND (SELECT runs FROM vod_hls WHERE clip_id = ?1) = ?4 + 1 AND ${same}`)
      .bind(id, userId, now, k, from, init),
    // New run.
    env.DB
      .prepare(
        `INSERT INTO vod_hls_runs (clip_id, k, from_seq, init, quality)
         SELECT ?1, ?4, ?5, ?6, ?7 FROM clips c JOIN vod_hls h ON h.clip_id = c.id
         WHERE c.id = ?1 AND c.user_id = ?2 AND c.expires_at > ?3 AND c.kind = 'vod' AND c.format = 'hls' AND c.status = 'uploading'
           AND c.part_puts < ${PART_PUT_FACTOR} * c.part_count + ${HLS_PUT_SLACK} AND h.runs = ?4 AND ?5 < c.part_count
           AND CASE WHEN ?4 = 0 THEN ?5 = 0
                    ELSE ?5 > (SELECT from_seq FROM vod_hls_runs WHERE clip_id = ?1 AND k = ?4 - 1)
                         AND NOT EXISTS (SELECT 1 FROM vod_hls_segments WHERE clip_id = ?1 AND seq >= ?5 AND committed = 1) END`,
      )
      .bind(id, userId, now, k, from, init, quality),
    env.DB
      .prepare(`UPDATE clips SET part_puts = part_puts + 1, last_activity_at = ?3, hls_bytes = hls_bytes + ?4, quality = ?5 WHERE id = ?1 AND (SELECT runs FROM vod_hls WHERE clip_id = ?1) = ?2 AND ${inserted}`)
      .bind(id, k, now, len, quality),
    env.DB
      .prepare(`UPDATE vod_hls SET runs = runs + 1, paused_at = NULL, paused_reason = NULL WHERE clip_id = ?1 AND runs = ?2 AND ${inserted}`)
      .bind(id, k),
  ]));
  if (changes(results[2]) === 1) return timed(jsonResponse({ k, from, quality }, 200), timing);
  if (changes(results[1]) === 1) {
    const run = await timing.db(() => env.DB.prepare("SELECT quality FROM vod_hls_runs WHERE clip_id = ?1 AND k = ?2").bind(id, k).first<{ quality: string }>());
    return timed(jsonResponse({ k, from, quality: run?.quality ?? quality, already: true }, 200), timing);
  }

  const row = await timing.db(() => env.DB
    .prepare(`SELECT part_puts, part_count FROM clips WHERE id = ?1 AND user_id = ?2 AND kind = 'vod' AND format = 'hls' AND status = 'uploading' AND expires_at > ?3`)
    .bind(id, userId, now)
    .first<{ part_puts: number; part_count: number }>());
  if (!row) return timed(refuse(404, "not_found"), timing);
  if (Number(row.part_puts) >= hlsAttemptCap(Number(row.part_count))) return timed(refuse(429, "too_many_part_uploads", TOO_MANY_PUTS_MESSAGE), timing);
  return timed(refuse(409, "run_conflict", "this run does not follow the share's runs"), timing);
}

function changes(result: unknown): number {
  return Number((result as D1Result | undefined)?.meta?.changes ?? 0);
}

function rowsOf<T>(result: unknown): T[] {
  return ((result as D1Result<T> | undefined)?.results ?? []) as T[];
}

// ── PUT /vod-shares/:id/hls/seg/:seq ────────────────────────────────────────

interface SegmentPut {
  id: string;
  seq: number;
  run: number;
  ms: number;
  len: number;
  tokenHash: string;
}

interface ShareState {
  status: string;
  segments: number;
  media_ms: number;
  runs: number;
  ended: number;
  row_committed: number | null;
}

/**
 * The claim, ONE batch. The first statement evaluates every guard against the
 * state before the batch and, only when they all hold, writes the pending row
 * with a negative marker in duration_ms (no real duration is negative). The
 * next statements are keyed on that marker, so they apply exactly when the
 * first did, without D1's changes(); the last one restores the real duration.
 * D1 runs a batch as one transaction, so nobody else ever reads the marker.
 * The session check is folded into the first statement (no separate lookup).
 */
async function claimSegment(env: Env, timing: Timing, p: SegmentPut, now: number): Promise<boolean> {
  const mark = -1 - Math.floor(Math.random() * 2_147_483_646);
  const marked = "EXISTS (SELECT 1 FROM vod_hls_segments WHERE clip_id = ?1 AND seq = ?2 AND duration_ms = ?3)";
  const results = await timing.db(() => env.DB.batch([
    env.DB
      .prepare(
        `INSERT INTO vod_hls_segments (clip_id, seq, duration_ms, bytes, committed)
         SELECT ?1, ?2, ?3, 0, 0 FROM clips c
         WHERE c.id = ?1 AND c.user_id = (SELECT user_id FROM sessions WHERE token_hash = ?4 AND expires_at > ?5)
           AND c.kind = 'vod' AND c.format = 'hls' AND c.status = 'uploading' AND c.expires_at > ?5
           AND ?2 < c.part_count AND c.part_puts < ${PART_PUT_FACTOR} * c.part_count + ${HLS_PUT_SLACK}
           AND EXISTS (SELECT 1 FROM vod_hls_runs WHERE clip_id = ?1 AND k = ?6 AND from_seq <= ?2)
           AND NOT EXISTS (SELECT 1 FROM vod_hls_runs WHERE clip_id = ?1 AND k > ?6 AND from_seq <= ?2)
           AND NOT EXISTS (SELECT 1 FROM vod_hls_segments WHERE clip_id = ?1 AND seq = ?2 AND committed = 1)
           AND c.hls_bytes - COALESCE((SELECT bytes FROM vod_hls_segments WHERE clip_id = ?1 AND seq = ?2), 0) + ?7 <= c.size_bytes + ?8
         ON CONFLICT (clip_id, seq) DO UPDATE SET duration_ms = excluded.duration_ms`,
      )
      .bind(p.id, p.seq, mark, p.tokenHash, now, p.run, p.len, HLS_RESERVATION_SLACK_BYTES),
    env.DB
      .prepare(`UPDATE clips SET part_puts = part_puts + 1, last_activity_at = ?4, hls_bytes = hls_bytes - (SELECT bytes FROM vod_hls_segments WHERE clip_id = ?1 AND seq = ?2) + ?5 WHERE id = ?1 AND ${marked}`)
      .bind(p.id, p.seq, mark, now, p.len),
    env.DB
      .prepare(`UPDATE vod_hls SET max_seq = MAX(max_seq, ?2), paused_at = NULL, paused_reason = NULL WHERE clip_id = ?1 AND ${marked}`)
      .bind(p.id, p.seq, mark),
    env.DB
      .prepare("UPDATE vod_hls_segments SET bytes = ?4, duration_ms = ?5 WHERE clip_id = ?1 AND seq = ?2 AND duration_ms = ?3 RETURNING seq")
      .bind(p.id, p.seq, mark, p.len, p.ms),
  ]));
  return rowsOf(results[3]).length === 1;
}

/** Why a claim was refused, or "grow" when only the reservation is short. */
async function explainClaim(env: Env, timing: Timing, p: SegmentPut, now: number): Promise<Response | "grow"> {
  const [session, found] = await timing.db(() => env.DB.batch([
    env.DB.prepare("SELECT user_id FROM sessions WHERE token_hash = ?1 AND expires_at > ?2 LIMIT 1").bind(p.tokenHash, now),
    env.DB
      .prepare(
        `SELECT c.user_id, c.status, c.expires_at, c.part_count, c.part_puts, c.size_bytes, c.hls_bytes, h.segments, h.media_ms, h.runs, h.ended,
           (SELECT committed FROM vod_hls_segments WHERE clip_id = ?1 AND seq = ?2) AS row_committed,
           (SELECT bytes FROM vod_hls_segments WHERE clip_id = ?1 AND seq = ?2) AS row_bytes,
           (SELECT COUNT(*) FROM vod_hls_runs WHERE clip_id = ?1 AND k = ?3 AND from_seq <= ?2) AS run_starts,
           (SELECT COUNT(*) FROM vod_hls_runs WHERE clip_id = ?1 AND k > ?3 AND from_seq <= ?2) AS later_runs
         FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.id = ?1 AND c.kind = 'vod' AND c.format = 'hls'`,
      )
      .bind(p.id, p.seq, p.run),
  ]));
  const user = rowsOf<{ user_id: number }>(session)[0];
  if (!user) return refuse(401, "unauthorized");
  const row = rowsOf<Record<string, number | string | null>>(found)[0];
  if (!row || Number(row.user_id) !== Number(user.user_id)) return refuse(404, "not_found");
  const shown = (row.status === "uploading" || row.status === "ready") && Number(row.expires_at) > now;
  if (shown && Number(row.row_committed) === 1) {
    return jsonResponse({ seq: p.seq, already: true, ...progress(row as unknown as ShareState) }, 200);
  }
  if (row.status !== "uploading" || Number(row.expires_at) <= now) return refuse(404, "not_found");
  if (p.seq >= Number(row.part_count)) return refuse(400, "bad_segment_number", `segment number must be 0 to ${Number(row.part_count) - 1}`);
  if (Number(row.part_puts) >= hlsAttemptCap(Number(row.part_count))) return refuse(429, "too_many_part_uploads", TOO_MANY_PUTS_MESSAGE);
  if (Number(row.run_starts) !== 1 || Number(row.later_runs) !== 0) return refuse(409, "run_mismatch", "this run does not cover the segment");
  if (Number(row.hls_bytes) - Number(row.row_bytes ?? 0) + p.len > Number(row.size_bytes) + HLS_RESERVATION_SLACK_BYTES) return "grow";
  // Changed between the claim and this read (a concurrent PUT): safe to retry.
  return refuse(503, "busy", "The share changed meanwhile. Try again.", {}, { "Retry-After": "1" });
}

/**
 * Grow an exhausted reservation by max(64 MiB, 2%), never past VOD_MAX_BYTES,
 * and only while the storage pool still has room for the growth.
 */
async function growReservation(env: Env, timing: Timing, id: string): Promise<boolean> {
  const row = await timing.db(() => env.DB
    .prepare("SELECT size_bytes FROM clips WHERE id = ?1 AND format = 'hls' AND status = 'uploading'")
    .bind(id)
    .first<{ size_bytes: number }>());
  if (!row) return false;
  const size = Number(row.size_bytes);
  const growth = Math.min(VOD_MAX_BYTES - size, Math.max(HLS_GROW_MIN_BYTES, Math.ceil(size * 0.02)));
  if (growth <= 0) return false;
  if (!vodFits(env, await timing.db(() => storageUsage(env.DB)), growth)) return false;
  await timing.db(() => env.DB
    .prepare("UPDATE clips SET size_bytes = MIN(size_bytes + ?2, ?3) WHERE id = ?1 AND format = 'hls' AND status = 'uploading' AND size_bytes = ?4")
    .bind(id, growth, VOD_MAX_BYTES, size)
    .run());
  return true;
}

function progress(s: ShareState): { committed: number; media_ms: number; playable: boolean } {
  const state = { runs: Number(s.runs), media_ms: Number(s.media_ms), ended: Number(s.ended), segments: Number(s.segments) };
  return { committed: state.segments, media_ms: state.media_ms, playable: hlsPlayable(state) };
}

type Commit =
  | { kind: "committed" | "already"; state: ShareState }
  | { kind: "mismatch" }
  | { kind: "stopped" };

/**
 * Store and commit, as one task. The commit batch marks the row committed only
 * while the share is still uploading and no later run covers the seq, moves
 * the contiguous edge forward with a gap query (the end of the committed run
 * that starts at the old edge; no recursive CTE), adds the newly contiguous
 * media, updates the ingest EWMA and the commit time, and reads the state back.
 */
async function storeAndCommit(env: Env, timing: Timing, p: SegmentPut, body: ReadableStream): Promise<Commit> {
  const key = hlsSegmentKey(p.id, p.seq);
  // Known-length request body straight into R2: consumed natively, no JS per chunk.
  await timing.r2op(() => env.CLIPS.put(key, body, { httpMetadata: { contentType: "video/mp4" } }));

  const nowMs = Date.now();
  const uploading = "EXISTS (SELECT 1 FROM clips WHERE id = ?1 AND format = 'hls' AND status = 'uploading')";
  const results = await timing.db(() => env.DB.batch([
    env.DB
      .prepare(
        `UPDATE vod_hls_segments SET committed = 1
         WHERE clip_id = ?1 AND seq = ?2 AND committed = 0 AND bytes = ?3 AND duration_ms = ?4 AND ${uploading}
           AND NOT EXISTS (SELECT 1 FROM vod_hls_runs WHERE clip_id = ?1 AND k > ?5 AND from_seq <= ?2)`,
      )
      .bind(p.id, p.seq, p.len, p.ms, p.run),
    env.DB
      .prepare(
        `WITH cur AS (SELECT segments AS s0, last_commit_ms AS t0 FROM vod_hls WHERE clip_id = ?1),
         edge AS (
           SELECT CASE WHEN EXISTS (SELECT 1 FROM vod_hls_segments WHERE clip_id = ?1 AND seq = cur.s0 AND committed = 1)
             THEN (SELECT MIN(s.seq) + 1 FROM vod_hls_segments s
                   WHERE s.clip_id = ?1 AND s.committed = 1 AND s.seq >= cur.s0
                     AND NOT EXISTS (SELECT 1 FROM vod_hls_segments t WHERE t.clip_id = ?1 AND t.seq = s.seq + 1 AND t.committed = 1))
             ELSE cur.s0 END AS s1 FROM cur),
         gain AS (
           SELECT COALESCE(SUM(s.duration_ms), 0) AS ms FROM vod_hls_segments s, cur, edge
           WHERE s.clip_id = ?1 AND s.committed = 1 AND s.seq >= cur.s0 AND s.seq < edge.s1),
         gap AS (SELECT CAST(MAX(1, MIN(?3 - cur.t0, ${INGEST_TAU_MS})) AS INTEGER) AS dt FROM cur)
         UPDATE vod_hls SET
           segments = (SELECT s1 FROM edge),
           media_ms = media_ms + (SELECT ms FROM gain),
           ingest_milli = CASE WHEN last_commit_ms IS NULL THEN NULL
             WHEN ingest_milli IS NULL THEN CAST((SELECT ms FROM gain) * 1000 / (SELECT dt FROM gap) AS INTEGER)
             ELSE CAST((ingest_milli * ${INGEST_TAU_MS} + (SELECT ms FROM gain) * 1000) / ((SELECT dt FROM gap) + ${INGEST_TAU_MS}) AS INTEGER) END,
           last_commit_ms = CAST(?3 AS INTEGER)
         WHERE clip_id = ?1 AND ${uploading}
           AND EXISTS (SELECT 1 FROM vod_hls_segments WHERE clip_id = ?1 AND seq = ?2 AND committed = 1)`,
      )
      .bind(p.id, p.seq, nowMs),
    env.DB
      .prepare(
        `SELECT c.status, h.segments, h.media_ms, h.runs, h.ended,
           (SELECT committed FROM vod_hls_segments WHERE clip_id = ?1 AND seq = ?2) AS row_committed
         FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.id = ?1`,
      )
      .bind(p.id, p.seq),
  ]));
  const state = rowsOf<ShareState>(results[2])[0];
  if (changes(results[0]) === 1 && state) return { kind: "committed", state };
  if (state && Number(state.row_committed) === 1 && (state.status === "uploading" || state.status === "ready")) {
    return { kind: "already", state }; // a concurrent retry of the same seq committed it
  }
  if (state && state.status === "uploading" && state.row_committed !== null) return { kind: "mismatch" };

  // Stopped, discarded or ended past this seq while the body was on the wire:
  // the cleanup may already have run, so delete what this call stored. If R2
  // refuses, a tombstone keeps the bytes counted and the hourly purge retries.
  try {
    await timing.r2op(() => env.CLIPS.delete(key));
  } catch {
    await tombstoneObject(env, key, null, p.len, "vod");
  }
  return { kind: "stopped" };
}

export async function handleHlsSegmentPut(
  request: Request,
  env: Env,
  ctx: ExecutionContext | undefined,
  tokenHash: string,
  id: string,
  seqRaw: string,
): Promise<Response> {
  const timing = new Timing();
  const url = new URL(request.url);
  const seq = parseStrictInt(seqRaw);
  if (seq === null || seq < 0) return refuse(400, "bad_segment_number", "segment number must be 0 or more");
  const ms = parseStrictInt(url.searchParams.get("ms"));
  if (ms === null || ms < 1 || ms > HLS_MAX_SEGMENT_MS) {
    return refuse(400, "bad_duration", `ms must be the segment's duration, 1 to ${HLS_MAX_SEGMENT_MS}`);
  }
  const run = parseStrictInt(url.searchParams.get("run"));
  if (run === null || run < 0) return refuse(400, "bad_request", "run must be the segment's run");
  if (run >= HLS_MAX_RUNS) return refuse(409, "run_mismatch", "this run does not cover the segment");
  const lenHeader = request.headers.get("Content-Length");
  if (lenHeader === null) return refuse(411, "length_required", "Content-Length is required");
  const len = parseStrictInt(lenHeader);
  if (len === null || len < 1 || !request.body) return refuse(400, "bad_request", "empty body");
  if (len > HLS_SEGMENT_MAX_BYTES) return refuse(413, "payload_too_large", `a segment can be at most ${HLS_SEGMENT_MAX_BYTES} bytes`);

  const p: SegmentPut = { id, seq, run, ms, len, tokenHash };
  const now = nowSeconds();
  let claimed = await claimSegment(env, timing, p, now);
  if (!claimed) {
    let why = await explainClaim(env, timing, p, now);
    if (why === "grow" && (await growReservation(env, timing, id))) {
      claimed = await claimSegment(env, timing, p, now);
      if (!claimed) why = await explainClaim(env, timing, p, now);
    }
    if (!claimed) {
      if (why === "grow") return timed(refuse(413, "over_reservation", "This share is larger than the space it reserved."), timing);
      return timed(why, timing);
    }
  }

  const task = storeAndCommit(env, timing, p, request.body);
  // The runtime keeps the task alive if the client hangs up after its body.
  ctx?.waitUntil(task.then(() => undefined, () => undefined));
  const outcome = await task;
  if (outcome.kind === "stopped") return timed(refuse(404, "not_found"), timing);
  if (outcome.kind === "mismatch") return timed(refuse(409, "run_mismatch", "a newer run or upload replaced this segment"), timing);
  const out: Record<string, unknown> = { seq, ...progress(outcome.state) };
  if (outcome.kind === "already") out.already = true;
  return timed(jsonResponse(out, 200), timing);
}

// ── POST /vod-shares/:id/hls/pause ──────────────────────────────────────────

/** Idempotent. The next accepted segment or run PUT clears it; the cron ends one paused 6 h. */
export async function handleHlsPause(request: Request, env: Env, userId: number, id: string): Promise<Response> {
  const timing = new Timing();
  const text = await readSmallBody(request, PAUSE_MAX_BODY_BYTES);
  if (text === null) return refuse(413, "payload_too_large", "pause body exceeds 1 KB");
  let reason: unknown = null;
  try {
    reason = (JSON.parse(text) as { reason?: unknown })?.reason;
  } catch { /* falls through */ }
  if (typeof reason !== "string" || !HLS_PAUSE_REASONS.has(reason)) return refuse(400, "bad_request", "reason must be \"game\"");

  const now = nowSeconds();
  const paused = await timing.db(() => env.DB
    .prepare(
      "UPDATE vod_hls SET paused_at = COALESCE(paused_at, ?3), paused_reason = ?4 WHERE clip_id = ?1 AND EXISTS (SELECT 1 FROM clips WHERE id = ?1 AND user_id = ?2 AND kind = 'vod' AND format = 'hls' AND status = 'uploading' AND expires_at > ?3)",
    )
    .bind(id, userId, now, reason)
    .run());
  if (paused.meta.changes !== 1) return timed(refuse(404, "not_found"), timing);
  return timed(jsonResponse({ paused: true, reason }, 200), timing);
}
