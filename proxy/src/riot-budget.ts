/**
 * The Riot API budget, and the GlobalRateLimiter Durable Object client.
 *
 * chargeRiot() runs immediately before an upstream Riot call and never for a
 * cache hit or a request that fails validation. It applies, in order:
 *   1. a per-token gate (per isolate, cheap, no round trip);
 *   2. for the anonymous web tool, a per-address gate (per isolate first filter);
 *   3. the Durable Object: the aggregate cap, web's share of it, and the
 *      per-address cap across every web token, all global.
 *
 * FAIL OPEN on the Riot budget: if the DO is unbound, errors or times out the
 * request is allowed. A limiter outage must never take down all traffic to
 * Riot; the per-isolate gates and Riot's own 429s remain as backstops.
 *
 * Clip, sign-in and transcript routes must never call chargeRiot.
 */

import { Env } from "./types";
import { jsonResponse } from "./http";

export type RiotCaller =
  | { kind: "desktop"; tokenHash: string }
  | { kind: "web"; tokenHash: string; ipKey: string };

type Bucket = { count: number; windowStartMs: number };

const WINDOW_MS = 1000;
const MAX_TRACKED_BUCKETS = 10_000;
const DO_TIMEOUT_MS = 2000;

const perTokenBuckets = new Map<string, Bucket>();
const webIpBuckets = new Map<string, Bucket>();

function positiveIntOr(value: string | undefined, fallback: number): number {
  const n = value === undefined ? NaN : parseInt(value, 10);
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

export interface RiotBudget {
  aggregateRps: number;
  perTokenRps: number;
  webRps: number;
  webPerIpRps: number;
}

/**
 * AGGREGATE_RPS stays the hard cap for everyone. WEB_AGGREGATE_RPS (default a
 * third of it) is the most the anonymous web tool may use; WEB_PER_IP_RPS
 * (default 3) caps one address across all of its web tokens.
 */
export function riotBudget(env: Env): RiotBudget {
  const aggregateRps = positiveIntOr(env.AGGREGATE_RPS, 18);
  const webDefault = Math.max(1, Math.floor(aggregateRps / 3));
  return {
    aggregateRps,
    perTokenRps: positiveIntOr(env.PER_TOKEN_RPS, 2),
    webRps: Math.min(aggregateRps, positiveIntOr(env.WEB_AGGREGATE_RPS, webDefault)),
    webPerIpRps: positiveIntOr(env.WEB_PER_IP_RPS, 3),
  };
}

function pruneBuckets(map: Map<string, Bucket>, nowMs: number): void {
  if (map.size < MAX_TRACKED_BUCKETS) return;
  for (const [key, bucket] of map) {
    if (nowMs - bucket.windowStartMs >= WINDOW_MS) map.delete(key);
  }
  if (map.size >= MAX_TRACKED_BUCKETS) map.clear();
}

/** Take one slot from `key`'s 1-second bucket. Returns the bucket, or null when full. */
function take(map: Map<string, Bucket>, key: string, limit: number, nowMs: number): Bucket | null {
  pruneBuckets(map, nowMs);
  let b = map.get(key);
  if (!b) {
    b = { count: 0, windowStartMs: nowMs };
    map.set(key, b);
  }
  if (nowMs - b.windowStartMs >= WINDOW_MS) {
    b.windowStartMs = nowMs;
    b.count = 0;
  }
  if (b.count >= limit) return null;
  b.count++;
  return b;
}

function refund(b: Bucket | null): void {
  if (b) b.count = Math.max(0, b.count - 1);
}

function tooMany(error: string): Response {
  return jsonResponse({ error }, 429, { "Retry-After": "1" });
}

/** POST to the singleton DO. Null when it is unbound, errors or times out. */
async function askLimiter(env: Env, path: string, params: URLSearchParams): Promise<{ allowed?: boolean; reason?: string } | null> {
  const ns = env.RATE_LIMITER;
  if (!ns) return null;
  try {
    const stub = ns.get(ns.idFromName("global"));
    const res = await stub.fetch(`https://rate-limiter.internal${path}?${params.toString()}`, {
      method: "POST",
      signal: AbortSignal.timeout(DO_TIMEOUT_MS),
    });
    if (!res.ok) {
      console.error(JSON.stringify({ scope: "ratelimit.do", path, status: res.status }));
      return null;
    }
    return (await res.json()) as { allowed?: boolean; reason?: string };
  } catch (err) {
    console.error(JSON.stringify({ scope: "ratelimit.do", path, error: (err as Error).message }));
    return null;
  }
}

/** Spend one upstream Riot call for `caller`, or return the 429 to send. */
export async function chargeRiot(env: Env, caller: RiotCaller): Promise<Response | null> {
  const budget = riotBudget(env);
  const now = Date.now();

  const tokenSlot = take(perTokenBuckets, caller.tokenHash, budget.perTokenRps, now);
  if (!tokenSlot) return tooMany("rate_limit_per_token");

  let ipSlot: Bucket | null = null;
  const params = new URLSearchParams({ limit: String(budget.aggregateRps) });
  if (caller.kind === "web") {
    ipSlot = take(webIpBuckets, caller.ipKey, budget.webPerIpRps, now);
    if (!ipSlot) {
      refund(tokenSlot);
      return tooMany("rate_limit_per_ip");
    }
    params.set("kind", "web");
    params.set("web", String(budget.webRps));
    params.set("ip", caller.ipKey);
    params.set("ipLimit", String(budget.webPerIpRps));
  }

  const verdict = await askLimiter(env, "/riot", params);
  if (verdict && verdict.allowed === false) {
    refund(tokenSlot);
    refund(ipSlot);
    const error = verdict.reason === "ip" ? "rate_limit_per_ip" : verdict.reason === "web" ? "rate_limit_web" : "rate_limit_aggregate";
    return tooMany(error);
  }
  return null; // allowed, or fail open
}

/**
 * One slot of a global keyed window in the DO (namespaces: mint, view, auth).
 * Returns `whenUnavailable` when the DO can't give a per-key answer: it is
 * unbound or unreachable, the namespace is full, or it rejected the request
 * as malformed. Only a "limit" verdict (this key used up its window) is a
 * real deny. Otherwise anyone able to fill a namespace with fresh keys (an
 * IPv6 /48 rotating /64s, say) would lock every caller out of a route that is
 * meant to fail open, such as sign-in.
 */
export async function takeGlobalKey(
  env: Env,
  ns: "mint" | "view" | "auth",
  key: string,
  limit: number,
  windowMs: number,
  whenUnavailable: boolean,
): Promise<boolean> {
  const params = new URLSearchParams({ ns, key, limit: String(limit), window: String(windowMs) });
  const verdict = await askLimiter(env, "/key", params);
  if (!verdict) return whenUnavailable;
  if (verdict.allowed !== false) return true;
  if (verdict.reason === "full" || verdict.reason === "bad_request") {
    console.error(JSON.stringify({ scope: "ratelimit.key", ns, reason: verdict.reason }));
    return whenUnavailable;
  }
  return false;
}

/** Test hook: forget every per-isolate bucket. */
export function resetRiotBudget(): void {
  perTokenBuckets.clear();
  webIpBuckets.clear();
}
