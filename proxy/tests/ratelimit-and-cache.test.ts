import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { Env } from "../src/types";
import { resetRiotBudget } from "../src/riot-budget";
import { mintWebJwt, resetWebSessionLimits } from "../src/web-session";
import { makeEnv, makeFakeDb, sha256Hex } from "./fakes";

// ── P1(c) verification suite ────────────────────────────────────────────────
//
// Covers the two levers added in P1(c) against Riot's shared-key rate limit:
//   1. EDGE CACHE for immutable resources (/match, /timeline): a repeat read
//      must hit the cache, not Riot — one upstream fetch for two GETs. Mutable
//      resources (/account, /rank) must NOT be cached (two GETs → two fetches).
//   2. GLOBAL RATE LIMITER Durable Object: an explicit {allowed:false} → 429
//      rate_limit_aggregate; {allowed:true} → passes through; a THROWING DO →
//      the request still succeeds (fail-OPEN, so a limiter outage never blocks
//      all traffic to Riot).
//
// Mirrors clips.test.ts's style: a fake Env, stubbed global fetch, and (here) an
// in-memory Map-backed fake for caches.default since the vitest pool has no
// `caches` global.

// A desktop session token (every Bearer resolves to a real account session).
const OP_TOKEN = "desktop-session-token";
let sessions: Record<string, number> = {};

function env(overrides: Partial<Env> = {}): Env {
  return makeEnv({
    // Big aggregate cap so the per-token gate never trips in the cache tests;
    // limiter tests override the DO directly.
    AGGREGATE_RPS: "1000",
    PER_TOKEN_RPS: "1000",
    DB: makeFakeDb([], sessions),
    ...overrides,
  });
}

beforeEach(async () => {
  sessions = { [await sha256Hex(OP_TOKEN)]: 5 };
  resetRiotBudget();
  resetWebSessionLimits();
});

// A minimal ExecutionContext. riotGetCached uses ctx.waitUntil for the cache
// put; we make it run the promise inline so the cache is populated before the
// next request in a sequential test (production fire-and-forget is fine because
// real edge requests are far apart, but tests are back-to-back).
function ctx(): ExecutionContext {
  return {
    waitUntil(p: Promise<unknown>) {
      // swallow rejections; the put is best-effort
      void Promise.resolve(p).catch(() => {});
    },
    passThroughOnException() {},
    props: {},
  } as unknown as ExecutionContext;
}

/**
 * Build a fake RATE_LIMITER DurableObjectNamespace whose singleton stub's
 * fetch() is driven by `stubFetch`. Only the methods index.ts touches
 * (idFromName, get, stub.fetch) are implemented. Mirrors index.test.ts.
 */
function fakeRateLimiter(stubFetch: () => Promise<Response>): DurableObjectNamespace {
  const stub = { fetch: stubFetch };
  return {
    idFromName: (_name: string) => ({}) as unknown as DurableObjectId,
    get: (_id: unknown) => stub,
  } as unknown as DurableObjectNamespace;
}

/**
 * In-memory Map-backed fake of Cloudflare's `caches.default`, implementing just
 * the `match`/`put` surface riotGetCached touches. Keyed by the request URL
 * (riotGetCached builds its cache key as `new Request(url)`).
 *
 * Real edge cache strips the stored body on each read; we return a fresh clone
 * per match so the cached body is independently readable, matching how the
 * Worker's clone semantics behave in production.
 */
function fakeCache() {
  const store = new Map<string, Response>();
  let matchCalls = 0;
  let putCalls = 0;
  const cache = {
    async match(req: Request | string): Promise<Response | undefined> {
      matchCalls++;
      const key = typeof req === "string" ? req : req.url;
      const hit = store.get(key);
      return hit ? hit.clone() : undefined;
    },
    async put(req: Request | string, res: Response): Promise<void> {
      putCalls++;
      const key = typeof req === "string" ? req : req.url;
      store.set(key, res.clone());
    },
  };
  return {
    cache,
    store,
    get matchCalls() {
      return matchCalls;
    },
    get putCalls() {
      return putCalls;
    },
  };
}

/** Install the fake cache as globalThis.caches.default; returns the handle. */
function installCache() {
  const fake = fakeCache();
  vi.stubGlobal("caches", { default: fake.cache } as unknown as CacheStorage);
  return fake;
}

function authedReq(path: string): Request {
  return new Request(`https://proxy.example${path}`, {
    headers: { Authorization: `Bearer ${OP_TOKEN}` },
  });
}

async function json(response: Response): Promise<Record<string, unknown>> {
  return (await response.json()) as Record<string, unknown>;
}

describe("edge cache for immutable Riot resources", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("serves a repeat /match GET from cache — only ONE upstream Riot fetch", async () => {
    const cacheHandle = installCache();
    const riotFetch = vi.fn().mockImplementation(async () =>
      new Response(JSON.stringify({ metadata: { matchId: "NA1_123456" } }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", riotFetch);

    const first = await worker.fetch(authedReq("/match/NA1_123456?region=na1"), env(), ctx());
    const second = await worker.fetch(authedReq("/match/NA1_123456?region=na1"), env(), ctx());

    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    // The crux: Riot was hit exactly once; the second read came from the cache.
    expect(riotFetch).toHaveBeenCalledTimes(1);
    // Both responses carry the real body.
    expect((await json(first)).metadata).toEqual({ matchId: "NA1_123456" });
    expect((await json(second)).metadata).toEqual({ matchId: "NA1_123456" });
    // Cache was populated once and probed on both calls.
    expect(cacheHandle.putCalls).toBe(1);
    expect(cacheHandle.matchCalls).toBe(2);
    expect(cacheHandle.store.size).toBe(1);
  });

  it("serves a repeat /timeline GET from cache — only ONE upstream Riot fetch", async () => {
    installCache();
    const riotFetch = vi.fn().mockImplementation(async () =>
      new Response(JSON.stringify({ info: { frames: [] } }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", riotFetch);

    const first = await worker.fetch(authedReq("/timeline/NA1_123456?region=na1"), env(), ctx());
    const second = await worker.fetch(authedReq("/timeline/NA1_123456?region=na1"), env(), ctx());

    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    expect(riotFetch).toHaveBeenCalledTimes(1);
  });

  it("marks cached match responses immutable so the edge pins them", async () => {
    const cacheHandle = installCache();
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
      ),
    );

    const res = await worker.fetch(authedReq("/match/NA1_999999?region=na1"), env(), ctx());
    expect(res.status).toBe(200);
    const stored = cacheHandle.store.get(
      "https://americas.api.riotgames.com/lol/match/v5/matches/NA1_999999",
    );
    expect(stored).toBeDefined();
    expect(stored!.headers.get("Cache-Control")).toBe("public, max-age=31536000, immutable");
  });

  it("does NOT cache a non-200 Riot response (transient errors stay uncached)", async () => {
    const cacheHandle = installCache();
    // mockImplementation (not mockResolvedValue) so every call gets a FRESH
    // Response — a Response body can only be read once, and uncached paths read
    // it on every call.
    const riotFetch = vi.fn().mockImplementation(async () =>
      new Response(JSON.stringify({ status: { message: "Not found" } }), {
        status: 404,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", riotFetch);

    const first = await worker.fetch(authedReq("/match/NA1_404040?region=na1"), env(), ctx());
    const second = await worker.fetch(authedReq("/match/NA1_404040?region=na1"), env(), ctx());

    expect(first.status).toBe(404);
    expect(second.status).toBe(404);
    // A 404 must NOT be pinned, so the second call re-hits Riot.
    expect(riotFetch).toHaveBeenCalledTimes(2);
    expect(cacheHandle.putCalls).toBe(0);
    expect(cacheHandle.store.size).toBe(0);
  });

  it("does NOT cache /account — two GETs cause two upstream fetches", async () => {
    const cacheHandle = installCache();
    const riotFetch = vi.fn().mockImplementation(async () =>
      new Response(JSON.stringify({ puuid: "abc-123" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", riotFetch);

    const first = await worker.fetch(authedReq("/account?riotId=Name%23TAG&region=na1"), env(), ctx());
    const second = await worker.fetch(authedReq("/account?riotId=Name%23TAG&region=na1"), env(), ctx());

    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    // Accounts change — must never be cached.
    expect(riotFetch).toHaveBeenCalledTimes(2);
    expect(cacheHandle.matchCalls).toBe(0); // account never consults the cache
    expect(cacheHandle.putCalls).toBe(0);
  });

  it("does NOT cache /rank — two GETs cause two upstream fetches", async () => {
    const cacheHandle = installCache();
    const riotFetch = vi.fn().mockImplementation(async () =>
      new Response(JSON.stringify([{ tier: "GOLD" }]), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", riotFetch);

    const first = await worker.fetch(authedReq("/rank?puuid=abc-123&region=na1"), env(), ctx());
    const second = await worker.fetch(authedReq("/rank?puuid=abc-123&region=na1"), env(), ctx());

    expect(first.status).toBe(200);
    expect(second.status).toBe(200);
    // Ranks change — must never be cached.
    expect(riotFetch).toHaveBeenCalledTimes(2);
    expect(cacheHandle.matchCalls).toBe(0);
    expect(cacheHandle.putCalls).toBe(0);
  });
});

describe("global aggregate rate limiter (Durable Object gate)", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("denies with 429 rate_limit_aggregate when the DO reports allowed:false", async () => {
    const riotFetch = vi.fn();
    vi.stubGlobal("fetch", riotFetch);

    const denyingLimiter = fakeRateLimiter(async () =>
      new Response(JSON.stringify({ allowed: false }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    const res = await worker.fetch(
      authedReq("/match/NA1_123456?region=na1"),
      env({ RATE_LIMITER: denyingLimiter }),
      ctx(),
    );

    expect(res.status).toBe(429);
    expect(await json(res)).toEqual({ error: "rate_limit_aggregate" });
    // The aggregate gate stopped the request BEFORE Riot was ever called.
    expect(riotFetch).not.toHaveBeenCalled();
  });

  it("passes through when the DO reports allowed:true", async () => {
    const riotFetch = vi.fn().mockResolvedValue(
      new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", riotFetch);

    const allowingLimiter = fakeRateLimiter(async () =>
      new Response(JSON.stringify({ allowed: true }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    const res = await worker.fetch(
      authedReq("/match/NA1_123456?region=na1"),
      env({ RATE_LIMITER: allowingLimiter }),
      ctx(),
    );

    expect(res.status).toBe(200);
    expect(riotFetch).toHaveBeenCalledTimes(1);
  });

  it("FAILS OPEN: a throwing DO still lets the request reach Riot", async () => {
    const riotFetch = vi.fn().mockResolvedValue(
      new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", riotFetch);

    const throwingLimiter = fakeRateLimiter(async () => {
      throw new Error("DO unreachable");
    });

    const res = await worker.fetch(
      authedReq("/match/NA1_123456?region=na1"),
      env({ RATE_LIMITER: throwingLimiter }),
      ctx(),
    );

    // A limiter outage must NEVER block traffic — request succeeds.
    expect(res.status).toBe(200);
    expect(riotFetch).toHaveBeenCalledTimes(1);
  });

  it("FAILS OPEN: a non-OK DO response still lets the request reach Riot", async () => {
    const riotFetch = vi.fn().mockResolvedValue(
      new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", riotFetch);

    const erroringLimiter = fakeRateLimiter(async () => new Response("oops", { status: 503 }));

    const res = await worker.fetch(
      authedReq("/match/NA1_123456?region=na1"),
      env({ RATE_LIMITER: erroringLimiter }),
      ctx(),
    );

    expect(res.status).toBe(200);
    expect(riotFetch).toHaveBeenCalledTimes(1);
  });

  it("FAILS OPEN: malformed DO JSON (no allowed field) is treated as allow", async () => {
    const riotFetch = vi.fn().mockResolvedValue(
      new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", riotFetch);

    // 200 OK but body has no `allowed` field → `allowed !== false` is true → allow.
    const garbageLimiter = fakeRateLimiter(async () =>
      new Response(JSON.stringify({ unexpected: "shape" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    const res = await worker.fetch(
      authedReq("/match/NA1_123456?region=na1"),
      env({ RATE_LIMITER: garbageLimiter }),
      ctx(),
    );

    expect(res.status).toBe(200);
    expect(riotFetch).toHaveBeenCalledTimes(1);
  });
});

// ── The Riot budget: cache hits are free, web can't starve desktop ──────────

/** A limiter that records every DO call and answers from `decide`. */
function recordingLimiter(decide: (u: URL) => Record<string, unknown> = () => ({ allowed: true })) {
  const calls: URL[] = [];
  const ns = fakeRateLimiter(async function (this: unknown, ...args: unknown[]) {
    const u = new URL(String(args[0]));
    calls.push(u);
    return new Response(JSON.stringify(decide(u)), { status: 200, headers: { "Content-Type": "application/json" } });
  } as unknown as () => Promise<Response>);
  return { ns, calls };
}

const WEB_SECRET = "web-jwt-secret";

async function webReq(path: string, ip = "198.51.100.20", token?: string): Promise<Request> {
  const jwt = token ?? (await mintWebJwt({ WEB_JWT_SECRET: WEB_SECRET } as Env, 1800));
  return new Request(`https://proxy.example${path}`, {
    headers: { Authorization: `Bearer ${jwt}`, "CF-Connecting-IP": ip, Origin: "https://revu.lol" },
  });
}

describe("Riot budget", () => {
  beforeEach(() => vi.restoreAllMocks());
  afterEach(() => vi.unstubAllGlobals());

  it("charges only the cache miss: a cached /match costs no DO call and no token slot", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.now()); // one 1-second window
    installCache();
    vi.stubGlobal("fetch", vi.fn().mockImplementation(async () => new Response("{}", { status: 200 })));
    const limiter = recordingLimiter();
    const e = env({ RATE_LIMITER: limiter.ns, PER_TOKEN_RPS: "1" });

    expect((await worker.fetch(authedReq("/match/NA1_7?region=na1"), e, ctx())).status).toBe(200);
    // Same second, per-token cap of 1: a hit must still be served.
    expect((await worker.fetch(authedReq("/match/NA1_7?region=na1"), e, ctx())).status).toBe(200);
    expect((await worker.fetch(authedReq("/timeline/NA1_7?region=na1"), e, ctx())).status).toBe(429);
    expect(limiter.calls).toHaveLength(1);
  });

  it("web calls carry the web share and the per-address cap to the DO", async () => {
    vi.stubGlobal("fetch", vi.fn().mockImplementation(async () => new Response("{}", { status: 200 })));
    const limiter = recordingLimiter();
    const e = env({ RATE_LIMITER: limiter.ns, AGGREGATE_RPS: "18", WEB_JWT_SECRET: WEB_SECRET });
    const res = await worker.fetch(await webReq("/web/account?riotId=A%23B&region=na1"), e, ctx());
    expect(res.status).toBe(200);
    expect(res.headers.get("Access-Control-Allow-Origin")).toBe("https://revu.lol");
    const asked = limiter.calls[0];
    expect(asked.pathname).toBe("/riot");
    expect(asked.searchParams.get("kind")).toBe("web");
    expect(asked.searchParams.get("limit")).toBe("18");
    expect(asked.searchParams.get("web")).toBe("6"); // a third of 18 by default
    expect(asked.searchParams.get("ipLimit")).toBe("3");
    expect(asked.searchParams.get("ip")).toMatch(/^[0-9a-f]{32}$/); // hashed, never the raw address
  });

  it("maps the DO's web and per-address denials to distinct 429s", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    for (const [reason, error] of [["web", "rate_limit_web"], ["ip", "rate_limit_per_ip"]]) {
      resetRiotBudget();
      const limiter = recordingLimiter(() => ({ allowed: false, reason }));
      const res = await worker.fetch(
        await webReq("/web/matches?puuid=p&region=na1"),
        env({ RATE_LIMITER: limiter.ns, WEB_JWT_SECRET: WEB_SECRET }),
        ctx(),
      );
      expect(res.status).toBe(429);
      expect(await json(res)).toEqual({ error });
    }
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("minting more web tokens buys one address no extra capacity", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.now()); // one 1-second window
    vi.stubGlobal("fetch", vi.fn().mockImplementation(async () => new Response("{}", { status: 200 })));
    const e = env({ WEB_JWT_SECRET: WEB_SECRET, WEB_PER_IP_RPS: "2" });
    const ip = "203.0.113.50";
    const statuses: number[] = [];
    for (let i = 0; i < 3; i++) {
      const fresh = await mintWebJwt({ WEB_JWT_SECRET: WEB_SECRET } as Env, 1800);
      statuses.push((await worker.fetch(await webReq("/web/rank?puuid=p&region=na1", ip, fresh), e, ctx())).status);
    }
    expect(statuses).toEqual([200, 200, 429]);
    // Another address is unaffected, and an IPv6 /64 counts as one address.
    expect((await worker.fetch(await webReq("/web/rank?puuid=p&region=na1", "203.0.113.51"), e, ctx())).status).toBe(200);
    const v6a = await worker.fetch(await webReq("/web/rank?puuid=p&region=na1", "2001:db8:1:2::a"), e, ctx());
    const v6b = await worker.fetch(await webReq("/web/rank?puuid=p&region=na1", "2001:db8:1:2:ffff::b"), e, ctx());
    const v6c = await worker.fetch(await webReq("/web/rank?puuid=p&region=na1", "2001:0db8:0001:0002:1:1:1:1"), e, ctx());
    expect([v6a.status, v6b.status, v6c.status]).toEqual([200, 200, 429]);
  });

  it("desktop and web share nothing per address: desktop never gets a per-IP 429", async () => {
    vi.stubGlobal("fetch", vi.fn().mockImplementation(async () => new Response("{}", { status: 200 })));
    const limiter = recordingLimiter((u) => ({ allowed: u.searchParams.get("kind") !== "web" }));
    const e = env({ RATE_LIMITER: limiter.ns, WEB_JWT_SECRET: WEB_SECRET });
    expect((await worker.fetch(await webReq("/web/rank?puuid=p&region=na1"), e, ctx())).status).toBe(429);
    expect((await worker.fetch(authedReq("/rank?puuid=p&region=na1"), e, ctx())).status).toBe(200);
  });
});

describe("POST /web/session", () => {
  beforeEach(() => vi.restoreAllMocks());
  afterEach(() => vi.unstubAllGlobals());

  function sessionReq(ip = "198.51.100.30"): Request {
    return new Request("https://proxy.example/web/session", {
      method: "POST",
      headers: { "Content-Type": "application/json", "CF-Connecting-IP": ip },
      body: JSON.stringify({ turnstileToken: "tok" }),
    });
  }

  function stubSiteverify(result: Record<string, unknown>) {
    const fetchMock = vi.fn().mockImplementation(async () => new Response(JSON.stringify(result), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  const configured = (overrides: Partial<Env> = {}) =>
    env({ TURNSTILE_SECRET_KEY: "ts", WEB_JWT_SECRET: WEB_SECRET, TURNSTILE_HOSTNAMES: "revu.lol,www.revu.lol", ...overrides });

  it("mints a 30-minute token for a Turnstile pass from an allowed hostname", async () => {
    const siteverify = stubSiteverify({ success: true, hostname: "revu.lol" });
    const res = await worker.fetch(sessionReq(), configured(), ctx());
    expect(res.status).toBe(200);
    const out = await json(res);
    expect(out.expiresIn).toBe(1800);
    expect(String(out.token).split(".")).toHaveLength(3);
    expect(siteverify.mock.calls[0][1].signal).toBeInstanceOf(AbortSignal);
  });

  it("refuses a Turnstile token issued for another hostname", async () => {
    stubSiteverify({ success: true, hostname: "evil.example" });
    const res = await worker.fetch(sessionReq(), configured(), ctx());
    expect(res.status).toBe(403);
    expect(await json(res)).toEqual({ error: "turnstile_failed" });
  });

  it("caps live tokens per address globally through the DO", async () => {
    stubSiteverify({ success: true, hostname: "revu.lol" });
    const limiter = recordingLimiter((u) => ({ allowed: u.searchParams.get("ns") !== "mint" }));
    const res = await worker.fetch(sessionReq(), configured({ RATE_LIMITER: limiter.ns }), ctx());
    expect(res.status).toBe(429);
    expect(await json(res)).toEqual({ error: "rate_limit_web_session" });
    const asked = limiter.calls[0];
    expect(asked.pathname).toBe("/key");
    expect(asked.searchParams.get("limit")).toBe("12");
    expect(asked.searchParams.get("window")).toBe(String(30 * 60 * 1000));
  });

  it("fails open when the DO's mint namespace is full, like an outage", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    stubSiteverify({ success: true, hostname: "revu.lol" });
    const limiter = recordingLimiter(() => ({ allowed: false, reason: "full" }));
    const res = await worker.fetch(sessionReq(), configured({ RATE_LIMITER: limiter.ns }), ctx());
    expect(res.status).toBe(200);
  });

  it("does not need the Riot key", async () => {
    stubSiteverify({ success: true, hostname: "revu.lol" });
    const res = await worker.fetch(sessionReq(), configured({ RIOT_API_KEY: "" }), ctx());
    expect(res.status).toBe(200);
  });

  it("answers 503 when Turnstile or the JWT secret is not configured", async () => {
    stubSiteverify({ success: true });
    const res = await worker.fetch(sessionReq(), env({ WEB_JWT_SECRET: WEB_SECRET }), ctx());
    expect(res.status).toBe(503);
    expect(await json(res)).toEqual({ error: "web_session_not_configured" });
  });
});
