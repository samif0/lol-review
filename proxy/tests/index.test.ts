import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import worker, { GlobalRateLimiter } from "../src/index";
import { Env } from "../src/types";
import { resetRiotBudget } from "../src/riot-budget";
import { accountRegionalFor, regionalFor, RIOT_TIMEOUT_MS } from "../src/riot";
import { clip, dbWithSessions, makeEnv, makeFakeDb, makeFakeR2, runScheduled, sha256Hex } from "./fakes";
import { addSession, addUser, makeAuthState } from "./fake-auth";

const TOKEN = "desktop-session";
const USER = 7;

async function env(overrides: Partial<Env> = {}): Promise<Env> {
  return makeEnv({ DB: await dbWithSessions({ [TOKEN]: USER }), ...overrides });
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

function authed(path: string, token = TOKEN): Request {
  return new Request(`https://proxy.example${path}`, { headers: { Authorization: `Bearer ${token}` } });
}

/**
 * A fake RATE_LIMITER DurableObjectNamespace whose singleton stub's fetch() is
 * driven by `stubFetch`. Only idFromName, get and stub.fetch are implemented.
 */
function fakeRateLimiter(stubFetch: (url: string) => Promise<Response>): DurableObjectNamespace {
  const stub = { fetch: (url: string) => stubFetch(url) };
  return {
    idFromName: (_name: string) => ({}) as unknown as DurableObjectId,
    get: (_id: unknown) => stub,
  } as unknown as DurableObjectNamespace;
}

function okJson(body: unknown = {}): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

async function json(response: Response): Promise<Record<string, unknown>> {
  return await response.json() as Record<string, unknown>;
}

describe("worker proxy", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    resetRiotBudget();
  });
  afterEach(() => vi.unstubAllGlobals());

  it("rejects proxied requests without bearer auth", async () => {
    const response = await worker.fetch(new Request("https://proxy.example/match/NA1_123?region=na1"), await env());
    expect(response.status).toBe(401);
    expect(await json(response)).toEqual({ error: "missing_bearer" });
  });

  it("has no static token allowlist: an unknown bearer is 401 even if it was an operator token", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const response = await worker.fetch(
      authed("/match/NA1_123456?region=na1", "static-op-token"),
      await env({ ALLOWED_TOKENS: "static-op-token" } as Partial<Env>),
    );
    expect(response.status).toBe(401);
    expect(await json(response)).toEqual({ error: "invalid_token" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("answers 503 auth_unavailable, not 401, when the session lookup fails", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const db = await dbWithSessions({ [TOKEN]: USER });
    db._failNext("FROM sessions");
    const response = await worker.fetch(authed("/match/NA1_123456?region=na1"), await env({ DB: db }));
    expect(response.status).toBe(503);
    expect(await json(response)).toEqual({
      error: "auth_unavailable",
      message: "Revu's server is busy. Try again in a moment.",
    });
  });

  it("validates match id shape before calling Riot", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const response = await worker.fetch(authed("/match/not-a-match-id?region=na1"), await env());
    expect(response.status).toBe(400);
    expect(await json(response)).toEqual({ error: "bad_request", message: "invalid matchId shape" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rejects a region that only exists on Object.prototype and never calls Riot", async () => {
    // The region map is a plain object, so a bare index lookup for
    // "__proto__" / "constructor" returns a truthy non-string instead of
    // null. The lookup must be an own-property check, or /rank would build
    // https://__proto__.api.riotgames.com/... with the Riot key attached.
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const e = await env();

    for (const region of ["__proto__", "constructor", "hasOwnProperty", "toString"]) {
      expect(regionalFor(region)).toBeNull();
      expect(accountRegionalFor(region)).toBeNull();
      // Validation runs before the Riot budget is charged, so one session
      // token covers every request here.
      for (const path of [
        `/account?riotId=a%23b&region=${region}`,
        `/matches?puuid=abc&region=${region}`,
        `/match/NA1_123456?region=${region}`,
        `/timeline/NA1_123456?region=${region}`,
        `/rank?puuid=abc&region=${region}`,
      ]) {
        const response = await worker.fetch(authed(path), e, ctx());

        expect(response.status).toBe(400);
        expect(await json(response)).toEqual({
          error: "bad_request",
          message: `unknown region '${region}'`,
        });
      }
    }
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("maps platform region to Riot regional route, with a timeout signal", async () => {
    const fetchMock = vi.fn().mockResolvedValue(okJson());
    vi.stubGlobal("fetch", fetchMock);
    const response = await worker.fetch(authed("/match/NA1_123456?region=na1"), await env());
    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledWith(
      "https://americas.api.riotgames.com/lol/match/v5/matches/NA1_123456",
      expect.objectContaining({ headers: { "X-Riot-Token": "riot-key" }, signal: expect.any(AbortSignal) }),
    );
  });

  it("forwards timeline requests to the Match-V5 timeline endpoint", async () => {
    const fetchMock = vi.fn().mockResolvedValue(okJson());
    vi.stubGlobal("fetch", fetchMock);
    const response = await worker.fetch(authed("/timeline/NA1_123456?region=na1"), await env());
    expect(response.status).toBe(200);
    expect(fetchMock.mock.calls[0][0]).toBe("https://americas.api.riotgames.com/lol/match/v5/matches/NA1_123456/timeline");
  });

  it("forwards rank requests to the platform-routed League-V4 endpoint", async () => {
    const fetchMock = vi.fn().mockResolvedValue(okJson([]));
    vi.stubGlobal("fetch", fetchMock);
    const response = await worker.fetch(authed("/rank?puuid=abc-123&region=na1"), await env());
    expect(response.status).toBe(200);
    expect(fetchMock.mock.calls[0][0]).toBe("https://na1.api.riotgames.com/lol/league/v4/entries/by-puuid/abc-123");
  });

  it("routes Account-V1 for OCE and SEA platforms to asia (Account-V1 has no sea host)", async () => {
    expect(accountRegionalFor("oc1")).toBe("asia");
    expect(accountRegionalFor("sg2")).toBe("asia");
    expect(accountRegionalFor("vn2")).toBe("asia");
    expect(accountRegionalFor("na1")).toBe("americas");
    expect(accountRegionalFor("euw1")).toBe("europe");
    expect(accountRegionalFor("zz9")).toBeNull();

    const fetchMock = vi.fn().mockImplementation(async () => okJson({ puuid: "p" }));
    vi.stubGlobal("fetch", fetchMock);
    const e = await env();
    expect((await worker.fetch(authed("/account?riotId=Name%23OCE&region=oc1"), e)).status).toBe(200);
    expect(fetchMock.mock.calls[0][0]).toBe("https://asia.api.riotgames.com/riot/account/v1/accounts/by-riot-id/Name/OCE");
    // Match-V5 for the same platform still uses sea.
    expect((await worker.fetch(authed("/match/OC1_123?region=oc1"), e)).status).toBe(200);
    expect(fetchMock.mock.calls[1][0]).toBe("https://sea.api.riotgames.com/lol/match/v5/matches/OC1_123");
  });

  it("rejects rank requests with missing params or unknown region", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const e = await env();
    expect((await worker.fetch(authed("/rank?region=na1"), e)).status).toBe(400);
    expect((await worker.fetch(authed("/rank?puuid=abc&region=zz9"), e)).status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("validates timeline match id shape before calling Riot", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const response = await worker.fetch(authed("/timeline/not-a-match-id?region=na1"), await env());
    expect(response.status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("applies per-token rate limits to upstream calls only, not to rejected requests", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.now()); // one 1-second window
    vi.stubGlobal("fetch", vi.fn().mockImplementation(async () => okJson()));
    const limited = await env({ PER_TOKEN_RPS: "1" });
    // Bad requests never reach Riot, so they never spend the token's slot.
    expect((await worker.fetch(authed("/match/bad?region=na1"), limited)).status).toBe(400);
    expect((await worker.fetch(authed("/match/bad?region=na1"), limited)).status).toBe(400);
    expect((await worker.fetch(authed("/match/NA1_1?region=na1"), limited)).status).toBe(200);
    const second = await worker.fetch(authed("/match/NA1_2?region=na1"), limited);
    expect(second.status).toBe(429);
    expect(await json(second)).toEqual({ error: "rate_limit_per_token" });
  });

  it("streams the Riot body through and keeps Retry-After", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ status: { status_code: 429 } }), { status: 429, headers: { "Retry-After": "7" } }),
    ));
    const response = await worker.fetch(authed("/matches?puuid=p&region=na1"), await env());
    expect(response.status).toBe(429);
    expect(response.headers.get("Retry-After")).toBe("7");
    expect(response.headers.get("Content-Type")).toBe("application/json");
    expect(await json(response)).toEqual({ status: { status_code: 429 } });
  });

  it("maps a Riot call that outlives the timeout to 504 riot_timeout", async () => {
    expect(RIOT_TIMEOUT_MS).toBe(10_000);
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new DOMException("timed out", "TimeoutError")));
    const response = await worker.fetch(authed("/account?riotId=A%23B&region=na1"), await env());
    expect(response.status).toBe(504);
    // The desktop shows `message` for a 504, so it must be a sentence, not the code.
    expect(await json(response)).toEqual({
      error: "riot_timeout",
      message: "Riot's servers took too long to answer. Try again in a moment.",
    });
  });

  it("aborts a Riot call whose headers don't arrive within the timeout", async () => {
    // Only the timer functions are faked: the session lookup hashes the token
    // with WebCrypto, which settles outside the fake clock, so wait (with
    // setImmediate, still real) until the upstream fetch has actually started.
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      let signal: AbortSignal | undefined;
      vi.stubGlobal("fetch", vi.fn().mockImplementation((_url: string, init: RequestInit) => {
        signal = init.signal ?? undefined;
        return new Promise((_resolve, reject) => {
          signal?.addEventListener("abort", () => reject(signal?.reason));
        });
      }));
      const pending = worker.fetch(authed("/account?riotId=A%23B&region=na1"), await env());
      while (!signal) await new Promise((r) => setImmediate(r));
      await vi.advanceTimersByTimeAsync(RIOT_TIMEOUT_MS - 1);
      expect(signal?.aborted).toBe(false);
      await vi.advanceTimersByTimeAsync(1);
      const response = await pending;
      expect(response.status).toBe(504);
      expect((await json(response)).error).toBe("riot_timeout");
    } finally {
      vi.useRealTimers();
    }
  });

  it("stops the timeout once headers arrive, so a slow body read isn't cut off", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      let signal: AbortSignal | undefined;
      vi.stubGlobal("fetch", vi.fn().mockImplementation(async (_url: string, init: RequestInit) => {
        signal = init.signal ?? undefined;
        return okJson({ puuid: "p" });
      }));
      const response = await worker.fetch(authed("/account?riotId=A%23B&region=na1"), await env());
      expect(response.status).toBe(200);
      expect(signal).toBeInstanceOf(AbortSignal);
      expect(vi.getTimerCount()).toBe(0); // the timeout was cleared with the headers
      await vi.advanceTimersByTimeAsync(RIOT_TIMEOUT_MS * 2);
      expect(signal?.aborted).toBe(false);
      expect(await json(response)).toEqual({ puuid: "p" });
    } finally {
      vi.useRealTimers();
    }
  });

  it("maps any other upstream failure to 502 proxy_error", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("connection reset")));
    const response = await worker.fetch(authed("/account?riotId=A%23B&region=na1"), await env());
    expect(response.status).toBe(502);
    expect(await json(response)).toEqual({ error: "proxy_error" });
  });

  it("only the Riot routes need the Riot key", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const e = await env({ RIOT_API_KEY: "" });
    const riot = await worker.fetch(authed("/match/NA1_123456?region=na1"), e);
    expect(riot.status).toBe(500);
    expect(await json(riot)).toEqual({ error: "server_misconfigured_no_riot_key" });
    // Sign out still works and still revokes the session.
    const logout = await worker.fetch(
      new Request("https://proxy.example/auth/logout", { method: "POST", headers: { Authorization: `Bearer ${TOKEN}` } }),
      e,
    );
    expect(logout.status).not.toBe(500);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("FAILS OPEN: a throwing rate-limiter DO does not block the request", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const fetchMock = vi.fn().mockResolvedValue(okJson());
    vi.stubGlobal("fetch", fetchMock);
    const brokenLimiter = fakeRateLimiter(async () => { throw new Error("DO unreachable"); });
    const response = await worker.fetch(authed("/match/NA1_123456?region=na1"), await env({ RATE_LIMITER: brokenLimiter }), ctx());
    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("FAILS OPEN: a non-OK DO response does not block the request", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const fetchMock = vi.fn().mockResolvedValue(okJson());
    vi.stubGlobal("fetch", fetchMock);
    const erroringLimiter = fakeRateLimiter(async () => new Response("oops", { status: 500 }));
    const response = await worker.fetch(authed("/match/NA1_123456?region=na1"), await env({ RATE_LIMITER: erroringLimiter }), ctx());
    expect(response.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("denies with 429 aggregate when the DO reports allowed:false", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const denyingLimiter = fakeRateLimiter(async () => okJson({ allowed: false, reason: "aggregate" }));
    const response = await worker.fetch(authed("/match/NA1_123456?region=na1"), await env({ RATE_LIMITER: denyingLimiter }), ctx());
    expect(response.status).toBe(429);
    expect(await json(response)).toEqual({ error: "rate_limit_aggregate" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("asks the DO for the desktop budget with the aggregate limit and no web params", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(okJson()));
    const urls: string[] = [];
    const limiter = fakeRateLimiter(async (u) => { urls.push(u); return okJson({ allowed: true }); });
    await worker.fetch(authed("/match/NA1_123456?region=na1"), await env({ RATE_LIMITER: limiter, AGGREGATE_RPS: "18" }), ctx());
    expect(urls).toHaveLength(1);
    const asked = new URL(urls[0]);
    expect(asked.pathname).toBe("/riot");
    expect(asked.searchParams.get("limit")).toBe("18");
    expect(asked.searchParams.get("kind")).toBeNull();
  });
});

describe("worker error handling", () => {
  beforeEach(() => vi.restoreAllMocks());
  afterEach(() => vi.unstubAllGlobals());

  it("turns any unhandled throw into JSON 500 with CORS headers", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const db = makeFakeDb();
    db._failNext("FROM login_requests");
    const res = await worker.fetch(
      new Request("https://proxy.example/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json", Origin: "https://revu.lol", "CF-Connecting-IP": "198.51.100.9" },
        body: JSON.stringify({ email: "someone@example.com" }),
      }),
      makeEnv({ DB: db }),
    );
    expect(res.status).toBe(500);
    expect(res.headers.get("Access-Control-Allow-Origin")).toBe("https://revu.lol");
    expect((await json(res)).error).toBe("internal");
  });

  it("a malformed web JWT is a 401, not a crash", async () => {
    const res = await worker.fetch(
      new Request("https://proxy.example/web/account?riotId=A%23B&region=na1", {
        headers: { Authorization: "Bearer aaa.bbb.!!!not-base64!!!" },
      }),
      makeEnv({ WEB_JWT_SECRET: "s" }),
    );
    expect(res.status).toBe(401);
    expect(await json(res)).toEqual({ error: "invalid_or_expired_jwt" });
  });

  it("falls back to the watch page redirect when the OG lookup fails", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const db = makeFakeDb();
    db._failNext("FROM clips WHERE id = ?1 AND expires_at > ?2 AND status = 'ready'");
    const res = await worker.fetch(new Request("https://proxy.example/Abc1234"), makeEnv({ DB: db }));
    expect(res.status).toBe(302);
    expect(res.headers.get("Location")).toBe("https://revu.lol/clip.html?id=Abc1234");
  });

  it("logout deletes only the caller's session, then that token is refused", async () => {
    const state = makeAuthState();
    const me = addUser(state, "me@example.com");
    const other = addUser(state, "other@example.com");
    addSession(state, await sha256Hex(TOKEN), me.id);
    addSession(state, await sha256Hex("other-session"), other.id);
    addSession(state, await sha256Hex("my-second-device"), me.id);
    const e = makeEnv({ DB: makeFakeDb([], {}, state) });
    const logout = () => worker.fetch(
      new Request("https://proxy.example/auth/logout", { method: "POST", headers: { Authorization: `Bearer ${TOKEN}` } }),
      e,
    );
    const res = await logout();
    expect(res.status).toBe(200);
    expect(await json(res)).toEqual({ ok: true });
    expect(state.sessions.map((s) => s.token_hash).sort()).toEqual(
      [await sha256Hex("other-session"), await sha256Hex("my-second-device")].sort(),
    );
    const again = await logout();
    expect(again.status).toBe(401);
  });
});

describe("full-game share routing and housekeeping", () => {
  beforeEach(() => vi.restoreAllMocks());
  afterEach(() => vi.unstubAllGlobals());

  it("answers an unknown /vod-shares path with 404 before auth, and never reaches Riot", async () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
    const e = await env();
    for (const [method, path] of [["GET", "/vod-shares/abc1234"], ["POST", "/vod-shares/status"], ["GET", "/vod-shares/uploads"], ["DELETE", "/vod-shares/abc1234/markers"]]) {
      const res = await worker.fetch(new Request(`https://proxy.example${path}`, { method }), e);
      expect(res.status, `${method} ${path}`).toBe(404);
      expect(await json(res)).toEqual({ error: "not_found" });
    }
    const status = await worker.fetch(new Request("https://proxy.example/vod-shares/status"), e);
    expect(status.status).toBe(401);
    expect(await json(status)).toEqual({ error: "unauthorized" });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("sends the bare slug of a full-game share to its watch page, never to the clip OG page", async () => {
    const db = makeFakeDb([clip({ id: "Vod0001", kind: "vod", r2_key: "vods/Vod0001.mp4" })]);
    const res = await worker.fetch(new Request("https://proxy.example/Vod0001"), makeEnv({ DB: db }));
    expect(res.status).toBe(302);
    expect(res.headers.get("Location")).toBe("https://revu.lol/v/Vod0001");
  });

  it("the hourly cron retries storage tombstones and prunes VOD start counts older than 7 days", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-14T05:00:00Z"));
    vi.spyOn(console, "log").mockImplementation(() => {});
    const db = makeFakeDb();
    const r2 = makeFakeR2();
    const upload = await r2.bucket.createMultipartUpload("vods/Gone001.mp4");
    r2.store.set("clips/Gone002.mp4", new Uint8Array([1]));
    db._tombstones.set("vods/Gone001.mp4", { r2_key: "vods/Gone001.mp4", upload_id: upload.uploadId, size_bytes: 9, kind: "vod", created_at: 1 });
    db._tombstones.set("clips/Gone002.mp4", { r2_key: "clips/Gone002.mp4", upload_id: null, size_bytes: 1, kind: "clip", created_at: 1 });
    for (const day of ["2026-10-06", "2026-10-07", "2026-10-14"]) db._vodUsage.set(`${USER}|${day}`, 1);

    const e = makeEnv({ DB: db, CLIPS: r2.bucket });
    await runScheduled(worker, e);
    expect(db._tombstones.size).toBe(0);
    expect(r2.calls.deleted.sort()).toEqual(["clips/Gone002.mp4", "vods/Gone001.mp4"]);
    expect(r2.uploads.get(upload.uploadId)!.state).toBe("aborted");
    expect([...db._vodUsage.keys()].sort()).toEqual([`${USER}|2026-10-07`, `${USER}|2026-10-14`]);
  });
});

describe("GlobalRateLimiter durable object", () => {
  function limiter() {
    const ns = new GlobalRateLimiter({} as DurableObjectState, {} as Env);
    const call = (query: string, path = "/") =>
      ns.fetch(new Request(`https://rl.internal${path}?${query}`, { method: "POST" })).then((r) => r.json() as Promise<{ allowed: boolean; reason?: string }>);
    return call;
  }

  it("allows up to the limit then denies within a 1s fixed window", async () => {
    const call = limiter();
    expect((await call("limit=2")).allowed).toBe(true);
    expect((await call("limit=2")).allowed).toBe(true);
    expect((await call("limit=2")).allowed).toBe(false); // 3rd in the same window denied
  });

  it("resets the count when the 1-second window rolls", async () => {
    const call = limiter();
    const nowSpy = vi.spyOn(Date, "now");
    try {
      nowSpy.mockReturnValue(1_000_000);
      expect((await call("limit=1")).allowed).toBe(true);
      expect((await call("limit=1")).allowed).toBe(false);
      nowSpy.mockReturnValue(1_001_000); // +1s → window rolls
      expect((await call("limit=1")).allowed).toBe(true);
    } finally {
      nowSpy.mockRestore();
    }
  });

  it("caps web at its share and keeps the rest of the aggregate for desktop", async () => {
    vi.spyOn(Date, "now").mockReturnValue(5_000_000);
    const call = limiter();
    const web = (ip: string) => call(`limit=6&kind=web&web=2&ip=${ip}&ipLimit=5`, "/riot");
    expect((await web("a")).allowed).toBe(true);
    expect((await web("b")).allowed).toBe(true);
    expect(await web("c")).toEqual({ allowed: false, reason: "web" });
    // Desktop still has the remaining 4 of 6.
    for (let i = 0; i < 4; i++) expect((await call("limit=6", "/riot")).allowed).toBe(true);
    expect(await call("limit=6", "/riot")).toEqual({ allowed: false, reason: "aggregate" });
    vi.restoreAllMocks();
  });

  it("lets desktop use the whole aggregate while web is idle", async () => {
    vi.spyOn(Date, "now").mockReturnValue(6_000_000);
    const call = limiter();
    for (let i = 0; i < 6; i++) expect((await call("limit=6", "/riot")).allowed).toBe(true);
    expect((await call("limit=6&kind=web&web=2", "/riot")).allowed).toBe(false);
    vi.restoreAllMocks();
  });

  it("caps one address across all of its web tokens", async () => {
    vi.spyOn(Date, "now").mockReturnValue(7_000_000);
    const call = limiter();
    const web = (ip: string) => call(`limit=18&kind=web&web=6&ip=${ip}&ipLimit=2`, "/riot");
    expect((await web("same")).allowed).toBe(true);
    expect((await web("same")).allowed).toBe(true);
    expect(await web("same")).toEqual({ allowed: false, reason: "ip" });
    expect((await web("other")).allowed).toBe(true);
    vi.restoreAllMocks();
  });

  it("keyed windows: allowlisted namespaces only, limit per key, reset after the window", async () => {
    const nowSpy = vi.spyOn(Date, "now").mockReturnValue(8_000_000);
    const call = limiter();
    const key = (k: string) => call(`ns=view&key=${k}&limit=1&window=3600000`, "/key");
    expect((await key("v1")).allowed).toBe(true);
    expect((await key("v1")).allowed).toBe(false);
    expect((await key("v2")).allowed).toBe(true);
    nowSpy.mockReturnValue(8_000_000 + 3_600_000);
    expect((await key("v1")).allowed).toBe(true);
    expect(await call("ns=nope&key=x&limit=1&window=1000", "/key")).toEqual({ allowed: false, reason: "bad_request" });
    expect(await call("ns=view&key=x&limit=0&window=1000", "/key")).toEqual({ allowed: false, reason: "bad_request" });
    vi.restoreAllMocks();
  });
});
