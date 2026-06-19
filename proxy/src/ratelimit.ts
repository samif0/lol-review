/**
 * GlobalRateLimiter — a Durable Object that enforces a TRUE aggregate
 * requests-per-second cap across every Worker isolate.
 *
 * Why this exists: the in-isolate aggregate bucket in index.ts counts per
 * isolate. Cloudflare spins up N isolates under load, so the real rate hitting
 * Riot's shared app key is N × AGGREGATE_RPS — enough to trip Riot's app-rate
 * limit and get the key throttled/banned globally. A Durable Object is a single,
 * globally-addressable, single-threaded instance: routing every request through
 * one named DO ("global") gives us one counter for the whole Worker.
 *
 * The DO mirrors index.ts's bump() semantics exactly: a 1-second fixed window
 * that resets the count when the window rolls, increments on each allowed
 * request, and denies once the count reaches the limit.
 *
 * Protocol: callers POST with the limit either as a `?limit=` query param or a
 * JSON body `{ "limit": number }`. Response is JSON `{ "allowed": boolean }`.
 * State lives in instance fields (not storage) — a 1-second window is far
 * shorter than the DO's in-memory lifetime, so persisting it would only add
 * latency for no benefit.
 */

import { Env } from "./types";

const DEFAULT_AGGREGATE_RPS = 18;
const WINDOW_MS = 1000;

export class GlobalRateLimiter {
  private count = 0;
  private windowStartMs = 0;

  // The DurableObjectState/Env are handed in by the runtime. We don't use
  // storage here (the window is sub-second), but keep the standard signature.
  constructor(_state: DurableObjectState, _env: Env) {}

  async fetch(request: Request): Promise<Response> {
    const limit = await this.resolveLimit(request);
    const now = Date.now();

    // Fixed-window roll: same logic as index.ts bump().
    if (now - this.windowStartMs >= WINDOW_MS) {
      this.windowStartMs = now;
      this.count = 0;
    }

    let allowed: boolean;
    if (this.count >= limit) {
      allowed = false;
    } else {
      this.count++;
      allowed = true;
    }

    return new Response(JSON.stringify({ allowed }), {
      headers: { "Content-Type": "application/json" },
    });
  }

  /**
   * Read the aggregate limit from the request: `?limit=` query param first,
   * then a JSON body `{ limit }`. Falls back to DEFAULT_AGGREGATE_RPS for any
   * missing/garbage value so a malformed call can never disable the cap.
   */
  private async resolveLimit(request: Request): Promise<number> {
    const url = new URL(request.url);
    const qp = url.searchParams.get("limit");
    if (qp !== null) {
      const n = parseInt(qp, 10);
      if (Number.isFinite(n) && n > 0) return n;
    }
    try {
      const body = (await request.json()) as { limit?: unknown };
      const n = typeof body.limit === "number" ? body.limit : NaN;
      if (Number.isFinite(n) && n > 0) return n;
    } catch {
      // no/!JSON body — fall through to default
    }
    return DEFAULT_AGGREGATE_RPS;
  }
}
