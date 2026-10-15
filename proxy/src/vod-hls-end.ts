/**
 * Ending an HLS share at its edge: the owner's POST /vod-shares/:id/hls/end,
 * and the settle that the hourly cron (and the owner's next VOD init) runs for
 * a share whose desktop went quiet.
 *
 * An end keeps the committed contiguous segments 0..E-1 (E = vod_hls.segments
 * at that moment, never less than what viewers already saw, so the playlist
 * stays append-only), deletes every row and object at or after E, recounts the
 * stored bytes from the rows and the inits (never trusting hls_bytes), and
 * turns the row into a ready share that expires 3 days after its init.
 *
 * Settle: a share idle for the idle window and not paused, paused for
 * HLS_PAUSE_MAX_SECONDS, or past its upload window is ended at its edge as
 * truncated when it is watchable, and discarded otherwise. Both re-check that
 * condition in the same statement that acts, so a desktop that resumes at the
 * last moment is never cut off.
 */

import { Env } from "./types";
import { jsonResponse, readSmallBody, Timing } from "./http";
import { deleteCalls, deleteKeys, removeHlsShare, segmentKeys, hlsSegmentKey } from "./hls-objects";
import { tombstoneObject } from "./upload-cleanup";
import { spend, HlsBudget } from "./clip-purge";
import { refuse } from "./vod-hls-upload";
import {
  HLS_PAUSE_MAX_SECONDS,
  HLS_PLAYABLE_MS,
  HLS_SETTLE_PER_RUN,
  IDLE_SWEEP_SECONDS,
  VOD_TTL_SECONDS,
  vodWatchUrl,
} from "./share-limits";

const END_MAX_BODY_BYTES = 1024;

function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

interface EndArgs {
  id: string;
  /** The owner, or null for the cron. */
  userId: number | null;
  minSegments: number;
  minMediaMs: number;
  truncated: boolean;
  now: number;
  /** For a settle: idle before this time (not paused), or paused before pausedBefore. */
  settle: { idleBefore: number; pausedBefore: number } | null;
}

interface EndState {
  status: string;
  expires_at: number;
  size_bytes: number;
  segments: number;
  media_ms: number;
  truncated: number;
  ended: number;
  max_seq: number;
}

/**
 * The guard every statement of an end repeats (evaluated before the status
 * changes): an uploading HLS share with run 0, at least minSegments committed
 * and minMediaMs of media, the owner's (inside its upload window) or, for a
 * settle, one that is idle, paused too long, or past its window.
 */
const OPEN = `EXISTS (SELECT 1 FROM clips c JOIN vod_hls h ON h.clip_id = c.id
  WHERE c.id = ?1 AND c.kind = 'vod' AND c.format = 'hls' AND c.status = 'uploading'
    AND (?2 IS NULL OR (c.user_id = ?2 AND c.expires_at > ?3))
    AND h.runs >= 1 AND h.segments >= ?4 AND h.media_ms >= ?5
    AND (?6 IS NULL OR (h.paused_at IS NULL AND COALESCE(c.last_activity_at, c.created_at) <= ?6) OR h.paused_at <= ?7 OR c.expires_at <= ?3))`;

const RECOUNT = "(SELECT COALESCE(SUM(bytes), 0) FROM vod_hls_segments WHERE clip_id = ?1) + (SELECT COALESCE(SUM(length(init)), 0) FROM vod_hls_runs WHERE clip_id = ?1)";

/**
 * One batch: drop the rows at or after the edge, turn the share ready (exact
 * bytes, 3-day expiry from init, duration from the media), mark it ended, read
 * it back. Then delete the objects at or after the edge. Null when the guard
 * failed (nothing changed).
 */
async function endAtEdge(env: Env, timing: Timing, a: EndArgs): Promise<EndState | null> {
  const guard = [a.id, a.userId, a.now, a.minSegments, a.minMediaMs, a.settle?.idleBefore ?? null, a.settle?.pausedBefore ?? null];
  const results = await timing.db(() => env.DB.batch([
    env.DB
      .prepare(`DELETE FROM vod_hls_segments WHERE clip_id = ?1 AND seq >= (SELECT segments FROM vod_hls WHERE clip_id = ?1) AND ${OPEN} RETURNING seq, bytes`)
      .bind(...guard),
    env.DB
      .prepare(
        `UPDATE clips SET status = 'ready', expires_at = created_at + ?8, size_bytes = ${RECOUNT}, hls_bytes = ${RECOUNT},
           duration_s = (SELECT MAX(1, (media_ms + 999) / 1000) FROM vod_hls WHERE clip_id = ?1)
         WHERE id = ?1 AND ${OPEN}`,
      )
      .bind(...guard, VOD_TTL_SECONDS),
    env.DB
      .prepare(
        "UPDATE vod_hls SET ended = 1, truncated = ?2, paused_at = NULL, paused_reason = NULL WHERE clip_id = ?1 AND ended = 0 AND EXISTS (SELECT 1 FROM clips WHERE id = ?1 AND format = 'hls' AND status = 'ready')",
      )
      .bind(a.id, a.truncated ? 1 : 0),
    env.DB
      .prepare("SELECT c.status, c.expires_at, c.size_bytes, h.segments, h.media_ms, h.truncated, h.ended, h.max_seq FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.id = ?1")
      .bind(a.id),
  ]));
  if (Number((results[1] as D1Result).meta?.changes ?? 0) !== 1) return null;
  const state = ((results[3] as D1Result<EndState>).results ?? [])[0];
  if (!state) return null;

  // Objects at or after the edge: what PUTs in flight stored. A PUT still on
  // the wire deletes its own object when its commit finds the share ended.
  const dropped = ((results[0] as D1Result<{ seq: number; bytes: number }>).results ?? []);
  const from = Number(state.segments);
  const count = Number(state.max_seq) - from + 1;
  if (count > 0 && !(await timing.r2op(() => deleteKeys(env, segmentKeys(a.id, from, count))))) {
    for (const row of dropped) await tombstoneObject(env, hlsSegmentKey(a.id, Number(row.seq)), null, Number(row.bytes), "vod");
  }
  return state;
}

function endBody(env: Env, id: string, s: EndState): Record<string, unknown> {
  return {
    id,
    url: vodWatchUrl(env, id),
    expires_at: Number(s.expires_at),
    size_bytes: Number(s.size_bytes),
    media_ms: Number(s.media_ms),
    segments: Number(s.segments),
    truncated: !!Number(s.truncated),
  };
}

// ── POST /vod-shares/:id/hls/end ────────────────────────────────────────────

export async function handleHlsEnd(request: Request, env: Env, userId: number, id: string): Promise<Response> {
  const timing = new Timing();
  const timed = (res: Response) => {
    res.headers.set("Server-Timing", timing.header());
    return res;
  };
  const text = await readSmallBody(request, END_MAX_BODY_BYTES);
  if (text === null) return refuse(413, "payload_too_large", "end body exceeds 1 KB");
  let body: { segments?: unknown; truncated?: unknown } = {};
  try {
    body = (JSON.parse(text) ?? {}) as typeof body;
  } catch { /* falls through */ }
  const n = body.segments;
  if (typeof n !== "number" || !Number.isInteger(n)) return refuse(400, "bad_request", "segments must be the number of segments");
  if (body.truncated !== undefined && typeof body.truncated !== "boolean") return refuse(400, "bad_request", "truncated must be true or false");

  const now = nowSeconds();
  for (let attempt = 0; attempt < 2; attempt++) {
    // N < 1 never ends a share (segments_incomplete below); a replay still gets its 201.
    const ended = await endAtEdge(env, timing, {
      id, userId, minSegments: Math.max(1, n), minMediaMs: 0, truncated: body.truncated === true, now, settle: null,
    });
    if (ended) return timed(jsonResponse(endBody(env, id, ended), 201));

    const row = await timing.db(() => env.DB
      .prepare("SELECT c.user_id, c.status, c.expires_at, c.size_bytes, h.segments, h.media_ms, h.runs, h.ended, h.truncated, h.max_seq FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.id = ?1 AND c.kind = 'vod' AND c.format = 'hls'")
      .bind(id)
      .first<EndState & { user_id: number; runs: number }>());
    if (!row || Number(row.user_id) !== userId || Number(row.expires_at) <= now) return timed(refuse(404, "not_found"));
    // A replay after a lost response: the same 201.
    if (row.status === "ready" && Number(row.ended) === 1) return timed(jsonResponse(endBody(env, id, row), 201));
    if (row.status !== "uploading") return timed(refuse(404, "not_found"));
    if (Number(row.runs) < 1 || n < 1 || Number(row.segments) < n) {
      return timed(refuse(409, "segments_incomplete", "Some segments are not uploaded yet.", { committed: Number(row.segments) }));
    }
    // Otherwise the share changed between the batch and this read: once more.
  }
  return timed(refuse(503, "busy", "The share changed meanwhile. Try again.", {}, { "Retry-After": "1" }));
}

// ── settle (cron, and the owner's next VOD init) ────────────────────────────

const SETTLE = "(h.paused_at IS NULL AND COALESCE(c.last_activity_at, c.created_at) <= ?1) OR h.paused_at <= ?2 OR c.expires_at <= ?3";

/**
 * Settle one HLS share that went quiet: idle for `idleSeconds` and not paused,
 * paused for HLS_PAUSE_MAX_SECONDS, or past its upload window. Watchable: end
 * it at its edge as truncated. Otherwise: discard it. Returns what happened;
 * "kept" when it is no longer quiet (it resumed meanwhile) or already gone.
 */
export async function settleHlsShare(
  env: Env,
  id: string,
  now: number,
  idleSeconds: number,
  budget: HlsBudget = { calls: Number.POSITIVE_INFINITY },
): Promise<"ended" | "discarded" | "kept"> {
  const timing = new Timing();
  const settle = { idleBefore: now - idleSeconds, pausedBefore: now - HLS_PAUSE_MAX_SECONDS };
  const ended = await endAtEdge(env, timing, {
    id, userId: null, minSegments: 1, minMediaMs: HLS_PLAYABLE_MS, truncated: true, now, settle,
  });
  if (ended) {
    budget.calls -= deleteCalls(Number(ended.max_seq) - Number(ended.segments) + 1); // already spent
    return "ended";
  }
  const claimed = await env.DB
    .prepare(
      `UPDATE clips SET status = 'purging', expires_at = ?4 WHERE id = ?5 AND format = 'hls' AND status = 'uploading'
         AND EXISTS (SELECT 1 FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.id = ?5 AND (${SETTLE}))
       RETURNING id`,
    )
    .bind(settle.idleBefore, settle.pausedBefore, now, now, id)
    .first<{ id: string }>();
  if (!claimed) return "kept";
  // A claimed row whose keys don't fit this run's budget (or whose R2 delete
  // fails) stays claimed and expired, counted, for the hourly purge.
  const maxSeq = await env.DB.prepare("SELECT max_seq FROM vod_hls WHERE clip_id = ?1").bind(id).first<{ max_seq: number }>();
  const seqs = Number(maxSeq?.max_seq ?? -1);
  if (spend(budget, deleteCalls(seqs + 1))) await removeHlsShare(env, id, now, seqs);
  return "discarded";
}

/**
 * Cron, before purgeExpiredClips: settle at most HLS_SETTLE_PER_RUN quiet HLS
 * shares (idle 30 minutes and not paused, paused 6 hours, or past the upload
 * window). A share past its window is hidden from viewers until this runs.
 */
export async function settleHlsShares(env: Env, budget: HlsBudget, now = nowSeconds()): Promise<{ ended: number; discarded: number }> {
  const res = await env.DB
    .prepare(`SELECT c.id FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.format = 'hls' AND c.status = 'uploading' AND (${SETTLE}) LIMIT ${HLS_SETTLE_PER_RUN}`)
    .bind(now - IDLE_SWEEP_SECONDS, now - HLS_PAUSE_MAX_SECONDS, now)
    .all<{ id: string }>();
  const out = { ended: 0, discarded: 0 };
  for (const row of res.results ?? []) {
    if (budget.calls <= 0) break;
    const done = await settleHlsShare(env, row.id, now, IDLE_SWEEP_SECONDS, budget);
    if (done === "ended") out.ended++;
    else if (done === "discarded") out.discarded++;
  }
  return out;
}
