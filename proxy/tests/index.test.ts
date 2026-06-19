import { beforeEach, describe, expect, it, vi } from "vitest";
import worker, { GlobalRateLimiter } from "../src/index";
import { Env } from "../src/types";

function env(token: string): Env {
  return {
    RIOT_API_KEY: "riot-key",
    ALLOWED_TOKENS: token,
    RESEND_API_KEY: "",
    AGGREGATE_RPS: "100",
    PER_TOKEN_RPS: "20",
    MAGIC_LINK_FROM: "noreply@example.com",
    APP_NAME: "Revu",
    DB: {} as D1Database,
  };
}

// A minimal ExecutionContext for handlers that may call ctx.waitUntil (the
// match/timeline edge cache). In vitest `caches` is absent so the cache path is
// skipped, but we pass a real ctx anyway to mirror production.
function ctx(): ExecutionContext {
  return {
    waitUntil() {},
    passThroughOnException() {},
    props: {},
  } as unknown as ExecutionContext;
}

/**
 * Build a fake RATE_LIMITER DurableObjectNamespace whose singleton stub's
 * fetch() is driven by `stubFetch`. Only the methods index.ts touches
 * (idFromName, get, stub.fetch) are implemented.
 */
function fakeRateLimiter(stubFetch: () => Promise<Response>): DurableObjectNamespace {
  const stub = { fetch: stubFetch };
  return {
    idFromName: (_name: string) => ({}) as unknown as DurableObjectId,
    get: (_id: unknown) => stub,
  } as unknown as DurableObjectNamespace;
}

async function json(response: Response): Promise<Record<string, unknown>> {
  return await response.json() as Record<string, unknown>;
}

describe("worker proxy", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("rejects proxied requests without bearer auth", async () => {
    const response = await worker.fetch(
      new Request("https://proxy.example/match/NA1_123?region=na1"),
      env("static-token"),
    );

    expect(response.status).toBe(401);
    expect(await json(response)).toEqual({ error: "missing_bearer" });
  });

  it("validates match id shape before calling Riot", async () => {
    const token = "shape-token";
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    const response = await worker.fetch(
      new Request("https://proxy.example/match/not-a-match-id?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      env(token),
    );

    expect(response.status).toBe(400);
    expect(await json(response)).toEqual({
      error: "bad_request",
      message: "invalid matchId shape",
    });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("maps platform region to Riot regional route", async () => {
    const token = "region-token";
    const fetchMock = vi.fn().mockResolvedValue(
      new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const response = await worker.fetch(
      new Request("https://proxy.example/match/NA1_123456?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      env(token),
    );

    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledWith(
      "https://americas.api.riotgames.com/lol/match/v5/matches/NA1_123456",
      { headers: { "X-Riot-Token": "riot-key" } },
    );
  });

  it("forwards timeline requests to the Match-V5 timeline endpoint", async () => {
    const token = "timeline-token";
    const fetchMock = vi.fn().mockResolvedValue(
      new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const response = await worker.fetch(
      new Request("https://proxy.example/timeline/NA1_123456?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      env(token),
    );

    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledWith(
      "https://americas.api.riotgames.com/lol/match/v5/matches/NA1_123456/timeline",
      { headers: { "X-Riot-Token": "riot-key" } },
    );
  });

  it("forwards rank requests to the platform-routed League-V4 endpoint", async () => {
    const token = "rank-token";
    const fetchMock = vi.fn().mockResolvedValue(
      new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const response = await worker.fetch(
      new Request("https://proxy.example/rank?puuid=abc-123&region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      env(token),
    );

    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledWith(
      "https://na1.api.riotgames.com/lol/league/v4/entries/by-puuid/abc-123",
      { headers: { "X-Riot-Token": "riot-key" } },
    );
  });

  it("rejects rank requests with missing params or unknown region", async () => {
    const token = "rank-bad-token";
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    const missing = await worker.fetch(
      new Request("https://proxy.example/rank?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      env(token),
    );
    expect(missing.status).toBe(400);

    const unknown = await worker.fetch(
      new Request("https://proxy.example/rank?puuid=abc&region=zz9", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      env(token),
    );
    expect(unknown.status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("validates timeline match id shape before calling Riot", async () => {
    const token = "timeline-shape-token";
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    const response = await worker.fetch(
      new Request("https://proxy.example/timeline/not-a-match-id?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      env(token),
    );

    expect(response.status).toBe(400);
    expect(await json(response)).toEqual({
      error: "bad_request",
      message: "invalid matchId shape",
    });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("applies per-token rate limits offline", async () => {
    const token = "rate-token";
    const limitedEnv = { ...env(token), PER_TOKEN_RPS: "1" };

    const first = await worker.fetch(
      new Request("https://proxy.example/match/bad?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      limitedEnv,
    );
    const second = await worker.fetch(
      new Request("https://proxy.example/match/bad?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      limitedEnv,
    );

    expect(first.status).toBe(400);
    expect(second.status).toBe(429);
    expect(await json(second)).toEqual({ error: "rate_limit_per_token" });
  });

  it("FAILS OPEN: a throwing rate-limiter DO does not block the request", async () => {
    // A limiter outage must NEVER take down all traffic to Riot. When the
    // GlobalRateLimiter DO throws, the aggregate gate is treated as allowed.
    const token = "failopen-token";
    const fetchMock = vi.fn().mockResolvedValue(
      new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const brokenLimiter = fakeRateLimiter(async () => {
      throw new Error("DO unreachable");
    });

    const response = await worker.fetch(
      new Request("https://proxy.example/match/NA1_123456?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      { ...env(token), RATE_LIMITER: brokenLimiter },
      ctx(),
    );

    // Request still reached Riot and succeeded despite the DO throwing.
    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("FAILS OPEN: a non-OK DO response does not block the request", async () => {
    const token = "failopen-500-token";
    const fetchMock = vi.fn().mockResolvedValue(
      new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const erroringLimiter = fakeRateLimiter(async () =>
      new Response("oops", { status: 500 }),
    );

    const response = await worker.fetch(
      new Request("https://proxy.example/match/NA1_123456?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      { ...env(token), RATE_LIMITER: erroringLimiter },
      ctx(),
    );

    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("denies with 429 aggregate when the DO reports allowed:false", async () => {
    const token = "agg-deny-token";
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);

    const denyingLimiter = fakeRateLimiter(async () =>
      new Response(JSON.stringify({ allowed: false }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );

    const response = await worker.fetch(
      new Request("https://proxy.example/match/NA1_123456?region=na1", {
        headers: { Authorization: `Bearer ${token}` },
      }),
      { ...env(token), RATE_LIMITER: denyingLimiter },
      ctx(),
    );

    expect(response.status).toBe(429);
    expect(await json(response)).toEqual({ error: "rate_limit_aggregate" });
    // Riot was never called — the aggregate gate stopped it first.
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe("GlobalRateLimiter durable object", () => {
  it("allows up to the limit then denies within a 1s fixed window", async () => {
    const ns = new GlobalRateLimiter({} as DurableObjectState, {} as Env);
    const call = () =>
      ns
        .fetch(new Request("https://rl.internal/?limit=2", { method: "POST" }))
        .then((r) => r.json() as Promise<{ allowed: boolean }>);

    expect((await call()).allowed).toBe(true);
    expect((await call()).allowed).toBe(true);
    expect((await call()).allowed).toBe(false); // 3rd in the same window denied
  });

  it("resets the count when the 1-second window rolls", async () => {
    const ns = new GlobalRateLimiter({} as DurableObjectState, {} as Env);
    const call = () =>
      ns
        .fetch(new Request("https://rl.internal/?limit=1", { method: "POST" }))
        .then((r) => r.json() as Promise<{ allowed: boolean }>);

    const nowSpy = vi.spyOn(Date, "now");
    try {
      nowSpy.mockReturnValue(1_000_000);
      expect((await call()).allowed).toBe(true);
      expect((await call()).allowed).toBe(false); // over cap, same window
      nowSpy.mockReturnValue(1_001_000); // +1s → window rolls
      expect((await call()).allowed).toBe(true);
    } finally {
      nowSpy.mockRestore();
    }
  });
});
