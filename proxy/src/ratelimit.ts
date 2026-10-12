/**
 * GlobalRateLimiter: a Durable Object that enforces TRUE cross-isolate limits.
 *
 * Why this exists: in-isolate counters count per isolate. Cloudflare spins up N
 * isolates under load, so the real rate hitting Riot's shared app key would be
 * N x AGGREGATE_RPS, enough to trip Riot's app-rate limit and get the key
 * throttled or banned globally. A Durable Object is a single, globally
 * addressable, single-threaded instance: routing every request through one
 * named DO ("global") gives one counter for the whole Worker.
 *
 * Two endpoints, both POST, both answering JSON `{ allowed, reason? }`:
 *
 *   /  (or /riot)   One upstream Riot call. 1-second fixed window.
 *     limit=N       aggregate cap for everyone (also accepted as JSON {limit})
 *     kind=web      the caller is the anonymous web review tool
 *     web=N         web's share of the aggregate; desktop keeps the rest
 *     ip=K&ipLimit=N  per-address cap across every web token (web only)
 *   Desktop may use the whole aggregate when web is idle; web can never take
 *   more than its share, so signed-in desktop users always keep a reserve.
 *
 *   /key            Generic keyed fixed window: ns, key, limit, window (ms).
 *     Used for web-session mints per address, sign-in requests per address and
 *     clip view de-duplication. Namespaces are a fixed allowlist and each is
 *     capped in size. A full namespace answers reason "full", which callers
 *     treat like an outage (takeGlobalKey: sign-in and mints fail open, view
 *     counting fails closed), so filling it can't lock everyone out.
 *
 * State lives in instance fields, never storage: it is best effort and resets
 * if the object is evicted while idle, which only ever forgives counters.
 */

import { Env } from "./types";

const DEFAULT_AGGREGATE_RPS = 18;
const WINDOW_MS = 1000;
const KEY_NAMESPACES = new Set(["mint", "view", "auth"]);
const MAX_KEYS_PER_NAMESPACE = 20_000;
const MAX_KEY_LENGTH = 128;
const MAX_KEY_WINDOW_MS = 24 * 60 * 60 * 1000;

interface KeyEntry {
  count: number;
  startMs: number;
  windowMs: number;
}

type Verdict = { allowed: boolean; reason?: string };

function positiveInt(value: string | null, max = 1_000_000): number | null {
  if (value === null || !/^\d{1,9}$/.test(value)) return null;
  const n = Number(value);
  return n > 0 && n <= max ? n : null;
}

export class GlobalRateLimiter {
  private windowStartMs = 0;
  private total = 0;
  private web = 0;
  private perIp = new Map<string, number>();
  private keyed = new Map<string, Map<string, KeyEntry>>();

  // The DurableObjectState/Env are handed in by the runtime. Storage is not
  // used (the windows are short and best effort), but keep the signature.
  constructor(_state: DurableObjectState, _env: Env) {}

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const verdict = url.pathname === "/key" ? this.takeKey(url) : this.takeRiot(url, await this.resolveLimit(request, url));
    return new Response(JSON.stringify(verdict), { headers: { "Content-Type": "application/json" } });
  }

  private takeRiot(url: URL, limit: number): Verdict {
    const now = Date.now();
    if (now - this.windowStartMs >= WINDOW_MS) {
      this.windowStartMs = now;
      this.total = 0;
      this.web = 0;
      this.perIp.clear();
    }
    if (this.total >= limit) return { allowed: false, reason: "aggregate" };

    const isWeb = url.searchParams.get("kind") === "web";
    let ip: string | null = null;
    let ipLimit: number | null = null;
    if (isWeb) {
      const webLimit = Math.min(limit, positiveInt(url.searchParams.get("web")) ?? limit);
      if (this.web >= webLimit) return { allowed: false, reason: "web" };
      ip = url.searchParams.get("ip");
      ipLimit = positiveInt(url.searchParams.get("ipLimit"));
      if (ip && ip.length <= MAX_KEY_LENGTH && ipLimit !== null && (this.perIp.get(ip) ?? 0) >= ipLimit) {
        return { allowed: false, reason: "ip" };
      }
    }

    this.total++;
    if (isWeb) {
      this.web++;
      // Only allowed requests add entries, so the map never outgrows the cap.
      if (ip && ip.length <= MAX_KEY_LENGTH && ipLimit !== null) this.perIp.set(ip, (this.perIp.get(ip) ?? 0) + 1);
    }
    return { allowed: true };
  }

  private takeKey(url: URL): Verdict {
    const ns = url.searchParams.get("ns") ?? "";
    const key = url.searchParams.get("key") ?? "";
    const limit = positiveInt(url.searchParams.get("limit"));
    const windowMs = positiveInt(url.searchParams.get("window"), MAX_KEY_WINDOW_MS);
    if (!KEY_NAMESPACES.has(ns) || !key || key.length > MAX_KEY_LENGTH || limit === null || windowMs === null) {
      return { allowed: false, reason: "bad_request" };
    }

    let entries = this.keyed.get(ns);
    if (!entries) {
      entries = new Map();
      this.keyed.set(ns, entries);
    }
    const now = Date.now();
    const entry = entries.get(key);
    if (entry && now - entry.startMs < entry.windowMs) {
      if (entry.count >= limit) return { allowed: false, reason: "limit" };
      entry.count++;
      return { allowed: true };
    }
    if (!entry && entries.size >= MAX_KEYS_PER_NAMESPACE) {
      for (const [k, e] of entries) {
        if (now - e.startMs >= e.windowMs) entries.delete(k);
      }
      // Still full: don't forget live entries an attacker could then replay.
      // "full" is not a per-key deny; callers fall back to their outage policy.
      if (entries.size >= MAX_KEYS_PER_NAMESPACE) return { allowed: false, reason: "full" };
    }
    entries.set(key, { count: 1, startMs: now, windowMs });
    return { allowed: true };
  }

  /**
   * The aggregate limit: `?limit=` first, then a JSON body `{ limit }`. Falls
   * back to DEFAULT_AGGREGATE_RPS for any missing or garbage value so a
   * malformed call can never disable the cap.
   */
  private async resolveLimit(request: Request, url: URL): Promise<number> {
    const qp = positiveInt(url.searchParams.get("limit"));
    if (qp !== null) return qp;
    try {
      const body = (await request.json()) as { limit?: unknown };
      const n = typeof body.limit === "number" ? body.limit : NaN;
      if (Number.isFinite(n) && n > 0) return n;
    } catch {
      // no or non-JSON body: fall through to the default
    }
    return DEFAULT_AGGREGATE_RPS;
  }
}
