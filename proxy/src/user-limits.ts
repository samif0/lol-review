/**
 * Per-isolate, per-user minute buckets for the clip upload and transcription
 * routes. Modeled on index.ts authIpBuckets/pruneBuckets.
 *
 * These routes must NEVER call rateLimitOrDeny: that spends the Riot Durable
 * Object budget, which is reserved for the Riot passthrough. Multipart part
 * uploads are not bucketed at all; they are bounded by auth, row ownership and
 * part_count.
 */

import { jsonResponse } from "./http";

type Bucket = { count: number; windowStartMs: number };

const WINDOW_MS = 60_000;
const MAX_TRACKED_BUCKETS = 10_000;

export const UPLOAD_INIT_PER_MINUTE_PER_USER = 6;
export const TRANSCRIBE_PER_MINUTE_PER_USER = 30;

const uploadInitBuckets = new Map<string, Bucket>();
const transcribeBuckets = new Map<string, Bucket>();

function pruneBuckets(map: Map<string, Bucket>, nowMs: number): void {
  if (map.size < MAX_TRACKED_BUCKETS) return;
  for (const [key, bucket] of map) {
    if (nowMs - bucket.windowStartMs >= WINDOW_MS) map.delete(key);
  }
  if (map.size >= MAX_TRACKED_BUCKETS) map.clear();
}

function takeOrDeny(map: Map<string, Bucket>, userId: number, limit: number): Response | null {
  const now = Date.now();
  pruneBuckets(map, now);
  const key = String(userId);
  let b = map.get(key);
  if (!b) {
    b = { count: 0, windowStartMs: now };
    map.set(key, b);
  }
  if (now - b.windowStartMs >= WINDOW_MS) {
    b.windowStartMs = now;
    b.count = 0;
  }
  if (b.count >= limit) {
    return jsonResponse(
      { error: "rate_limited", message: "Too many requests. Try again in a minute." },
      429,
      { "Retry-After": "60" },
    );
  }
  b.count++;
  return null;
}

/** POST /clips/uploads: 6 per minute per user. */
export function uploadInitLimitOrDeny(userId: number): Response | null {
  return takeOrDeny(uploadInitBuckets, userId, UPLOAD_INIT_PER_MINUTE_PER_USER);
}

/** POST /transcribe: 30 per minute per user. */
export function transcribeLimitOrDeny(userId: number): Response | null {
  return takeOrDeny(transcribeBuckets, userId, TRANSCRIBE_PER_MINUTE_PER_USER);
}

/** Test hook: forget every bucket in this isolate. */
export function resetUserLimits(): void {
  uploadInitBuckets.clear();
  transcribeBuckets.clear();
}
