/**
 * The desktop sign-in chain end to end: send a code, verify it (bound to the
 * email or code-only), attempt caps, expiry, the per-email throttle, the global
 * daily email ceiling, the per-IP limit, sessions and logout, and the cron
 * cleanup of sign-in rows. Resend is stubbed (web-fixtures stubFetch).
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Env } from "../src/types";
import { hmacSha256Hex } from "../src/crypto";
import { addLoginRequest, addSession, addUser } from "./fake-auth";
import { makeRequest, nowSec, runScheduled, sha256Hex } from "./fakes";
import { AUTH, body, call, lastCode, resetWebLimiters, stubFetch, webReq, webSetup } from "./web-fixtures";
import worker, { GlobalRateLimiter } from "../src/index";

let ipSeq = 0;

/** A desktop-shaped request from a fresh address (the per-IP limit is tested separately). */
function desktopReq(path: string, init: { method?: string; body?: unknown; bearer?: string; ip?: string } = {}): Request {
  const headers: Record<string, string> = { "CF-Connecting-IP": init.ip ?? `10.9.${Math.floor(++ipSeq / 250)}.${ipSeq % 250}` };
  if (init.body !== undefined) headers["Content-Type"] = "application/json";
  if (init.bearer) headers.Authorization = `Bearer ${init.bearer}`;
  return makeRequest(`${AUTH}${path}`, {
    method: init.method ?? "POST",
    headers,
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
  });
}

function desktopCode(stub: ReturnType<typeof stubFetch>): string {
  const text = String(stub.resend[stub.resend.length - 1]?.text ?? "");
  const m = /\n\n([A-Z0-9]{8})\n\n/.exec(text);
  if (!m) throw new Error("no code in the last email");
  return m[1];
}

async function login(env: Env, email: string): Promise<Response> {
  return call(env, desktopReq("/auth/login", { body: { email } }));
}

async function verify(env: Env, payload: Record<string, unknown>): Promise<Response> {
  return call(env, desktopReq("/auth/verify", { body: payload }));
}

beforeEach(() => {
  vi.restoreAllMocks();
  resetWebLimiters();
});
afterEach(() => vi.unstubAllGlobals());

describe("desktop sign-in: send a code", () => {
  it("stores only a hash of the code and emails the code", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    const res = await login(env, "Player@Example.com");
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({ sent: true });
    const code = desktopCode(stub);
    expect(state.loginRequests).toHaveLength(1);
    const row = state.loginRequests[0];
    expect(row).toMatchObject({ email: "player@example.com", purpose: "login", consumed: 0, attempts: 0 });
    expect(row.code).toBe(await sha256Hex(code));
    expect(JSON.stringify(state.loginRequests)).not.toContain(code);
    expect(row.expires_at - row.created_at).toBe(600);
  });

  it("with LOGIN_CODE_PEPPER the stored hash is an HMAC, and that code still verifies", async () => {
    const { env, state } = webSetup({ LOGIN_CODE_PEPPER: "pepper" });
    const stub = stubFetch();
    await login(env, "pep@example.com");
    const code = desktopCode(stub);
    expect(state.loginRequests[0].code).toBe(await hmacSha256Hex("pepper", "code:" + code));
    expect(state.loginRequests[0].code).not.toBe(await sha256Hex(code));
    const res = await verify(env, { email: "pep@example.com", code });
    expect(res.status).toBe(200);
  });

  it("429s the fourth code for one email inside 15 minutes", async () => {
    const { env } = webSetup();
    stubFetch();
    for (let i = 0; i < 3; i++) expect((await login(env, "busy@example.com")).status).toBe(200);
    const fourth = await login(env, "busy@example.com");
    expect(fourth.status).toBe(429);
    expect(await body(fourth)).toEqual({ error: "rate_limit_email" });
    expect(fourth.headers.get("Retry-After")).toBe("900");
  });

  it("a failed send drops its row and refunds the daily count, so a retry is not throttled", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-12T08:00:00Z"));
    const { env, state, db } = webSetup();
    stubFetch({ resendStatus: 500 });
    for (let i = 0; i < 4; i++) {
      const res = await login(env, "flaky@example.com");
      expect(res.status).toBe(502);
      expect(await body(res)).toEqual({ error: "email_send_failed" });
    }
    expect(state.loginRequests).toHaveLength(0);
    expect(db._emailSends.get("2026-10-12")).toBe(0);
    stubFetch();
    expect((await login(env, "flaky@example.com")).status).toBe(200);
  });

  it("works with no Riot API key", async () => {
    const { env } = webSetup({ RIOT_API_KEY: "" });
    const stub = stubFetch();
    expect((await login(env, "keyless@example.com")).status).toBe(200);
    expect((await verify(env, { email: "keyless@example.com", code: desktopCode(stub) })).status).toBe(200);
  });

  it("rejects bad JSON and invalid emails, and the invite-code signup alias is gone", async () => {
    const { env } = webSetup();
    stubFetch();
    const bad = await call(env, makeRequest(`${AUTH}/auth/login`, {
      method: "POST", headers: { "Content-Type": "application/json", "CF-Connecting-IP": "10.200.0.1" }, body: "{",
    }));
    expect(bad.status).toBe(400);
    expect((await login(env, "not-an-email")).status).toBe(400);
    const signup = await call(env, desktopReq("/auth/signup", { body: { email: "a@example.com", inviteCode: "X" } }));
    expect(signup.status).toBe(405);
  });
});

describe("desktop sign-in: the global daily email ceiling", () => {
  it("pauses sign-in emails for everyone past EMAIL_DAILY_SEND_CAP with a clear 503", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-12T22:00:00Z"));
    const { env, db } = webSetup({ EMAIL_DAILY_SEND_CAP: "2" });
    const stub = stubFetch();
    expect((await login(env, "one@example.com")).status).toBe(200);
    expect((await login(env, "two@example.com")).status).toBe(200);
    const third = await login(env, "three@example.com");
    expect(third.status).toBe(503);
    const out = await body(third);
    expect(out.error).toBe("email_daily_cap");
    expect(out.message).toBe("Revu has sent all the sign-in emails it can for today. Try again after midnight UTC.");
    expect(String(out.message)).not.toMatch(/[–—]/);
    expect(third.headers.get("Retry-After")).toBe("7200");
    expect(stub.resend).toHaveLength(2);
    expect(db._emailSends.get("2026-10-12")).toBe(2);

    // The web start shares the same ceiling.
    const web = await call(env, webReq("/auth/web/email/start", { method: "POST", body: { email: "w@example.com", turnstileToken: "t" } }));
    expect(web.status).toBe(503);
    expect(await body(web)).toEqual({ error: "email_daily_cap", retry_after: 7200 });
    expect(stub.resend).toHaveLength(2);
  });

  it("defaults to 100 a day, and a refused send gives its reservation back", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-12T09:00:00Z"));
    const { env, db } = webSetup();
    const stub = stubFetch();
    db._emailSends.set("2026-10-12", 99);
    expect((await login(env, "same@example.com")).status).toBe(200); // the 100th of the day
    expect((await login(env, "same@example.com")).status).toBe(503);
    expect((await login(env, "other@example.com")).status).toBe(503);
    expect(db._emailSends.get("2026-10-12")).toBe(100);
    expect(stub.resend).toHaveLength(1);
  });

  it("fails open when the counter table is unavailable", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const { env, db } = webSetup({ EMAIL_DAILY_SEND_CAP: "1" });
    stubFetch();
    db._failNext("INSERT INTO email_send_usage");
    expect((await login(env, "early@example.com")).status).toBe(200);
  });

  it("the cron drops counters older than 7 days and expired sign-in rows", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-12T09:00:00Z"));
    const { env, db, state } = webSetup();
    db._emailSends.set("2026-10-01", 50);
    db._emailSends.set("2026-10-11", 20);
    const now = nowSec();
    addLoginRequest(state, { code: "old", email: "a@example.com", created_at: now - 9000, expires_at: now - 8400 });
    addLoginRequest(state, { code: "live", email: "b@example.com" });
    await runScheduled(worker, env);
    expect([...db._emailSends.keys()]).toEqual(["2026-10-11"]);
    expect(state.loginRequests.map((r) => r.code)).toEqual(["live"]);
  });
});

describe("desktop sign-in: verify", () => {
  it("code-only verify (current desktop builds) signs in, creates the account, and the token works", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    await login(env, "new@example.com");
    const res = await verify(env, { code: desktopCode(stub).toLowerCase() });
    expect(res.status).toBe(200);
    const out = await body(res);
    expect(Object.keys(out).sort()).toEqual(["expires_at", "session_token"]);
    expect(state.users.map((u) => u.email)).toEqual(["new@example.com"]);
    expect(state.sessions[0].token_hash).toBe(await sha256Hex(out.session_token as string));
    const mine = await call(env, desktopReq("/clips/mine", { method: "GET", bearer: out.session_token as string }));
    expect(mine.status).toBe(200);
  });

  it("a code is single use", async () => {
    const { env } = webSetup();
    const stub = stubFetch();
    await login(env, "once@example.com");
    const code = desktopCode(stub);
    expect((await verify(env, { email: "once@example.com", code })).status).toBe(200);
    const again = await verify(env, { email: "once@example.com", code });
    expect(again.status).toBe(400);
    expect(await body(again)).toEqual({ error: "invalid_or_expired_code" });
    expect((await verify(env, { code })).status).toBe(400);
  });

  it("an expired code is refused", async () => {
    const { env, state } = webSetup();
    const now = nowSec();
    addLoginRequest(state, { code: await sha256Hex("EXPIRED1"), email: "late@example.com", created_at: now - 700, expires_at: now - 100 });
    expect((await verify(env, { email: "late@example.com", code: "EXPIRED1" })).status).toBe(400);
    expect((await verify(env, { code: "EXPIRED1" })).status).toBe(400);
  });

  it("with an email, the code is bound to it", async () => {
    const { env, state } = webSetup();
    addLoginRequest(state, { code: await sha256Hex("BOUND123"), email: "owner@example.com" });
    expect((await verify(env, { email: "someone@example.com", code: "BOUND123" })).status).toBe(400);
    expect(state.loginRequests[0].consumed).toBe(0);
    expect((await verify(env, { email: "Owner@Example.com", code: "bound-123" })).status).toBe(200);
  });

  it("accepts the right code on the 5th bound attempt", async () => {
    const { env, state } = webSetup({ REQUIRE_VERIFY_EMAIL: "1" });
    addLoginRequest(state, { code: await sha256Hex("RIGHT123"), email: "five@example.com" });
    for (let i = 0; i < 4; i++) expect((await verify(env, { email: "five@example.com", code: `WRONG00${i}` })).status).toBe(400);
    expect((await verify(env, { email: "five@example.com", code: "RIGHT123" })).status).toBe(200);
  });

  it("with REQUIRE_VERIFY_EMAIL=1, burns the code after 5 wrong bound attempts", async () => {
    const { env, state } = webSetup({ REQUIRE_VERIFY_EMAIL: "1" });
    addLoginRequest(state, { code: await sha256Hex("RIGHT456"), email: "burn@example.com" });
    for (let i = 0; i < 5; i++) expect((await verify(env, { email: "burn@example.com", code: `WRONG00${i}` })).status).toBe(400);
    expect(state.loginRequests[0].attempts).toBe(5);
    expect((await verify(env, { email: "burn@example.com", code: "RIGHT456" })).status).toBe(400);
    expect(state.sessions).toHaveLength(0);
  });

  it("a code burned by bound attempts can't be consumed by code-only verify either", async () => {
    const { env, state } = webSetup();
    addLoginRequest(state, { code: await sha256Hex("RIGHT789"), email: "burnt@example.com", attempts: 6 });
    expect((await verify(env, { code: "RIGHT789" })).status).toBe(400);
    expect(state.sessions).toHaveLength(0);
  });

  it("while code-only verify is open, wrong bound guesses don't burn a user's code", async () => {
    // Current desktop builds verify code-only, which is uncounted, so counting
    // bound attempts would add no brute-force protection; it would only let
    // anyone who knows an email lock that user out with a few requests.
    const { env, state } = webSetup();
    addLoginRequest(state, { code: await sha256Hex("VICTIM12"), email: "victim@example.com" });
    for (let i = 0; i < 6; i++) expect((await verify(env, { email: "victim@example.com", code: `WRONG00${i}` })).status).toBe(400);
    expect(state.loginRequests[0].attempts).toBe(0);
    expect((await verify(env, { code: "VICTIM12" })).status).toBe(200);
  });

  it("REQUIRE_VERIFY_EMAIL=1 refuses code-only verify with a message the desktop shows", async () => {
    const { env, state } = webSetup({ REQUIRE_VERIFY_EMAIL: "1" });
    addLoginRequest(state, { code: await sha256Hex("NEEDMAIL"), email: "req@example.com" });
    const res = await verify(env, { code: "NEEDMAIL" });
    expect(res.status).toBe(400);
    expect(await body(res)).toEqual({ error: "email_required", message: "Update Revu, then request a new code." });
    expect((await verify(env, { email: "req@example.com", code: "NEEDMAIL" })).status).toBe(200);
  });

  it("400s a missing code, an invalid email and bad JSON", async () => {
    const { env } = webSetup();
    expect(await body(await verify(env, {}))).toEqual({ error: "bad_request", message: "code_required" });
    expect(await body(await verify(env, { code: "X", email: "nope" }))).toEqual({ error: "bad_request", message: "invalid_email" });
    expect((await verify(env, { code: "X".repeat(40) })).status).toBe(400);
  });

  it("a web code is not redeemable at the desktop verify", async () => {
    const { env, state } = webSetup();
    addLoginRequest(state, { code: await sha256Hex("WEBCODE1"), email: "web@example.com", purpose: "web" });
    expect((await verify(env, { code: "WEBCODE1" })).status).toBe(400);
    expect((await verify(env, { email: "web@example.com", code: "WEBCODE1" })).status).toBe(400);
    expect(state.loginRequests[0].consumed).toBe(0);
  });
});

describe("desktop sessions", () => {
  it("an expired session is refused like an unknown one", async () => {
    const { env, state } = webSetup();
    const user = addUser(state, "exp@example.com");
    addSession(state, await sha256Hex("expired-token"), user.id, "desktop", nowSec() - 1);
    const res = await call(env, desktopReq("/clips/mine", { method: "GET", bearer: "expired-token" }));
    expect(res.status).toBe(401);
    expect(await body(res)).toEqual({ error: "invalid_token" });
  });

  it("logout needs a valid session and revokes exactly it", async () => {
    const { env, state } = webSetup();
    const user = addUser(state, "out@example.com");
    addSession(state, await sha256Hex("keep-token"), user.id);
    addSession(state, await sha256Hex("drop-token"), user.id);
    expect((await call(env, desktopReq("/auth/logout", { bearer: "nope" }))).status).toBe(401);
    expect((await call(env, desktopReq("/auth/logout", { bearer: "drop-token" }))).status).toBe(200);
    expect(state.sessions.map((s) => s.token_hash)).toEqual([await sha256Hex("keep-token")]);
    expect((await call(env, desktopReq("/clips/mine", { method: "GET", bearer: "drop-token" }))).status).toBe(401);
    expect((await call(env, desktopReq("/clips/mine", { method: "GET", bearer: "keep-token" }))).status).toBe(200);
  });
});

describe("per-IP limit on the public sign-in routes", () => {
  it("allows 5 a minute per address in an isolate, then 429s; another address is unaffected", async () => {
    const { env } = webSetup();
    stubFetch();
    const statuses: number[] = [];
    for (let i = 0; i < 6; i++) {
      statuses.push((await call(env, desktopReq("/auth/verify", { body: { code: "WRONG000" }, ip: "192.0.2.77" }))).status);
    }
    expect(statuses).toEqual([400, 400, 400, 400, 400, 429]);
    expect((await call(env, desktopReq("/auth/verify", { body: { code: "WRONG000" }, ip: "192.0.2.78" }))).status).toBe(400);
  });

  it("also asks the rate limiter DO for a global per-address cap (hashed address)", async () => {
    const asked: URL[] = [];
    const limiter = {
      idFromName: () => ({}),
      get: () => ({ fetch: async (u: string) => { asked.push(new URL(u)); return new Response(JSON.stringify({ allowed: false })); } }),
    } as unknown as DurableObjectNamespace;
    const { env } = webSetup({ RATE_LIMITER: limiter });
    const res = await call(env, desktopReq("/auth/login", { body: { email: "x@example.com" }, ip: "192.0.2.90" }));
    expect(res.status).toBe(429);
    expect(await body(res)).toEqual({ error: "rate_limit_auth" });
    expect(asked[0].searchParams.get("ns")).toBe("auth");
    expect(asked[0].searchParams.get("limit")).toBe("10");
    expect(asked[0].searchParams.get("key")).not.toContain("192.0.2.90");
  });

  it("fails open when the DO's auth namespace is full: one flood can't lock everyone out of sign-in", async () => {
    // About 20k fresh address keys inside the 60 s window (an IPv6 /48 rotating
    // its /64s, say) fills the namespace. "full" is not a per-key verdict, so
    // the global cap falls back to its fail-open policy, as for an outage.
    vi.spyOn(console, "error").mockImplementation(() => {});
    const rl = new GlobalRateLimiter({} as DurableObjectState, {} as Env);
    const keyCall = (key: string) =>
      rl
        .fetch(new Request(`https://rl.internal/key?ns=auth&key=${key}&limit=10&window=60000`, { method: "POST" }))
        .then((r) => r.json() as Promise<{ allowed: boolean; reason?: string }>);
    for (let i = 0; i < 20_000; i++) await keyCall(`flood-${i}`);
    expect(await keyCall("one-more")).toEqual({ allowed: false, reason: "full" });

    const limiter = {
      idFromName: () => ({}),
      get: () => ({ fetch: (u: string, init?: RequestInit) => rl.fetch(new Request(u, init)) }),
    } as unknown as DurableObjectNamespace;
    const { env } = webSetup({ RATE_LIMITER: limiter });
    const stub = stubFetch();
    const sent = await login(env, "flooded@example.com");
    expect(sent.status).toBe(200);
    const signedIn = await verify(env, { email: "flooded@example.com", code: desktopCode(stub) });
    expect(signedIn.status).toBe(200);
  });

  it("a per-key limit verdict from the DO still denies", async () => {
    const limiter = {
      idFromName: () => ({}),
      get: () => ({ fetch: async () => new Response(JSON.stringify({ allowed: false, reason: "limit" })) }),
    } as unknown as DurableObjectNamespace;
    const { env } = webSetup({ RATE_LIMITER: limiter });
    stubFetch();
    const res = await login(env, "limited@example.com");
    expect(res.status).toBe(429);
    expect(await body(res)).toEqual({ error: "rate_limit_auth" });
  });
});

describe("web sign-in shares the pepper and the ceiling", () => {
  it("a peppered web code verifies through the web flow", async () => {
    const { env, state } = webSetup({ LOGIN_CODE_PEPPER: "pepper" });
    const stub = stubFetch();
    const start = await call(env, webReq("/auth/web/email/start", { method: "POST", body: { email: "pw@example.com", turnstileToken: "t" } }));
    expect(start.status).toBe(200);
    const code = lastCode(stub);
    expect(state.loginRequests[0].code).toBe(await hmacSha256Hex("pepper", "code:" + code));
    const res = await call(env, webReq("/auth/web/email/verify", { method: "POST", body: { email: "pw@example.com", code } }));
    expect(res.status).toBe(200);
  });

  it("a throttled web start refunds its daily reservation", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-12T09:00:00Z"));
    const { env, db } = webSetup();
    stubFetch();
    const start = () => call(env, webReq("/auth/web/email/start", { method: "POST", body: { email: "cool@example.com", turnstileToken: "t" } }));
    expect((await start()).status).toBe(200);
    expect((await start()).status).toBe(429); // 60 s cooldown
    expect(db._emailSends.get("2026-10-12")).toBe(1);
  });
});
