import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { addLoginRequest, addSession, addUser } from "./fake-auth";
import { makeRequest, nowSec, sha256Hex } from "./fakes";
import {
  AUTH, body, call, cookieToken, lastCode, resetWebLimiters, seedWebUser,
  signInViaApi, stubFetch, webHash, webReq, webSetup,
} from "./web-fixtures";

beforeEach(() => {
  vi.restoreAllMocks();
  resetWebLimiters();
});
afterEach(() => vi.unstubAllGlobals());

const startWith = (payload: Record<string, unknown>, ip = "203.0.113.7") =>
  webReq("/auth/web/email/start", { method: "POST", body: payload, headers: { "CF-Connecting-IP": ip } });
const start = (email: unknown, ip?: string) => startWith({ email, turnstileToken: "tok" }, ip);
const verify = (email: unknown, code: unknown, cookie?: string) =>
  webReq("/auth/web/email/verify", { method: "POST", body: { email, code }, cookie });
const COOKIE_SHAPE = /^__Host-revu_web=[0-9a-f]{48}; Path=\/; Secure; HttpOnly; SameSite=Lax; Max-Age=2592000$/;

describe("POST /auth/web/email/start", () => {
  it("verifies Turnstile strictly, stores a web code and sends the web email", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    const res = await call(env, start("  Player@Example.COM "));
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({ sent: true });

    expect(stub.siteverify).toHaveLength(1);
    const form = stub.siteverify[0];
    expect(form.get("secret")).toBe("test");
    expect(form.get("response")).toBe("tok");
    expect(form.get("remoteip")).toBe("203.0.113.7");
    expect(form.has("idempotency_key")).toBe(false);

    expect(stub.resend).toHaveLength(1);
    const mail = stub.resend[0];
    expect(mail.to).toEqual(["player@example.com"]);
    expect(mail.from).toBe("login@revu.lol");
    expect(mail.subject).toBe("Your Revu sign-in code");
    for (const part of [String(mail.text), String(mail.html)]) {
      expect(part).toContain("revu.lol");
      expect(part).toContain("never ask you to share it");
      expect(part).toContain("If you did not request it, you can safely ignore this email.");
      expect(part).not.toContain("—");
    }
    expect(stub.other).toEqual([]);

    expect(state.loginRequests).toHaveLength(1);
    const row = state.loginRequests[0];
    expect(row).toMatchObject({ email: "player@example.com", purpose: "web", consumed: 0, attempts: 0 });
    expect(row.expires_at - row.created_at).toBe(600);
    expect(row.code).toBe(await sha256Hex(lastCode(stub)));
  });

  it("answers an unknown email exactly like a known one", async () => {
    const { env, state } = webSetup();
    addUser(state, "known@example.com");
    stubFetch();
    const known = await call(env, start("known@example.com"));
    const unknown = await call(env, start("nobody@example.com"));
    expect(known.status).toBe(unknown.status);
    expect(await known.text()).toBe(await unknown.text());
    expect(state.users).toHaveLength(1);
  });

  const turnstileFailures: Array<[string, Record<string, unknown> | "throw"]> = [
    ["success false", { success: false, action: "web_signin", hostname: "revu.lol" }],
    ["hostname mismatch", { success: true, action: "web_signin", hostname: "evil.example" }],
    ["action mismatch", { success: true, action: "web_session", hostname: "revu.lol" }],
    ["action missing", { success: true, hostname: "revu.lol" }],
    ["fetch throws", "throw"],
  ];
  for (const [name, turnstile] of turnstileFailures) {
    it(`403s turnstile_failed without emailing (${name})`, async () => {
      const { env, state } = webSetup();
      const stub = stubFetch({ turnstile });
      const res = await call(env, start("a@example.com"));
      expect(res.status).toBe(403);
      expect(await body(res)).toEqual({ error: "turnstile_failed" });
      expect(stub.resend).toHaveLength(0);
      expect(state.loginRequests).toHaveLength(0);
    });
  }

  it("fails closed with an empty hostname list and no relax (503 before siteverify)", async () => {
    const { env } = webSetup({ TURNSTILE_HOSTNAMES: "" });
    const stub = stubFetch();
    const res = await call(env, start("a@example.com"));
    expect(res.status).toBe(503);
    expect(await body(res)).toEqual({ error: "sign_in_unavailable" });
    expect(stub.siteverify).toHaveLength(0);
    expect(stub.resend).toHaveLength(0);
  });

  it("rejects a hostname list that is empty at verify time", async () => {
    const { verifyTurnstileStrict } = await import("../src/web-email");
    const { env } = webSetup({ TURNSTILE_HOSTNAMES: "" });
    const stub = stubFetch();
    const errors: string[] = [];
    vi.spyOn(console, "error").mockImplementation((line: unknown) => { errors.push(String(line)); });
    expect(await verifyTurnstileStrict("tok", env, null)).toBe(false);
    expect(stub.siteverify).toHaveLength(0);
    expect(errors.some((e) => e.includes("turnstile.config"))).toBe(true);
  });

  it("dev relax on a localhost AUTH_BASE accepts a hostname or action mismatch", async () => {
    const { env } = webSetup({
      AUTH_BASE: "http://localhost:8787", CREDENTIALED_ORIGINS: "http://localhost:8788", TURNSTILE_HOSTNAMES: "", TURNSTILE_DEV_RELAX: "1",
    });
    const stub = stubFetch({ turnstile: { success: true, action: "other", hostname: "example.com" } });
    const res = await call(env, webReq("/auth/web/email/start", {
      method: "POST", base: "http://localhost:8787", origin: "http://localhost:8788", body: { email: "dev@example.com", turnstileToken: "tok" },
    }));
    expect(res.status).toBe(200);
    expect(stub.resend).toHaveLength(1);
  });

  it("dev relax never skips success:false", async () => {
    const { env } = webSetup({ AUTH_BASE: "http://localhost:8787", CREDENTIALED_ORIGINS: "http://localhost:8788", TURNSTILE_DEV_RELAX: "1" });
    stubFetch({ turnstile: { success: false } });
    const res = await call(env, webReq("/auth/web/email/start", {
      method: "POST", base: "http://localhost:8787", origin: "http://localhost:8788", body: { email: "dev@example.com", turnstileToken: "tok" },
    }));
    expect(res.status).toBe(403);
  });

  it("400s a missing, non-string or overlong Turnstile token", async () => {
    const { env } = webSetup();
    const stub = stubFetch();
    const tokens = [undefined, "", 42, "x".repeat(2049)];
    for (const [i, tok] of tokens.entries()) {
      const res = await call(env, startWith({ email: "a@example.com", turnstileToken: tok }, `192.0.2.${i}`));
      expect(res.status).toBe(400);
      expect(await body(res)).toEqual({ error: "turnstile_required" });
    }
    expect(stub.siteverify).toHaveLength(0);
  });

  it("400s an invalid email", async () => {
    const { env } = webSetup();
    stubFetch();
    const emails = ["", "no-at-sign", "a@b", 7, null, `${"x".repeat(250)}@b.co`];
    for (const [i, email] of emails.entries()) {
      const res = await call(env, start(email, `192.0.2.${i}`));
      expect(res.status).toBe(400);
      expect(await body(res)).toEqual({ error: "invalid_email" });
    }
  });

  it("503s when a secret is missing", async () => {
    const { env } = webSetup({ RESEND_API_KEY: "" });
    stubFetch();
    const res = await call(env, start("a@example.com"));
    expect(res.status).toBe(503);
    expect(await body(res)).toEqual({ error: "sign_in_unavailable" });
  });

  it("429s a second web send within 60 s with retry_after 60", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    expect((await call(env, start("cool@example.com"))).status).toBe(200);
    const again = await call(env, start("COOL@example.com", "198.51.100.9"));
    expect(again.status).toBe(429);
    expect(await body(again)).toEqual({ error: "rate_limit_email", retry_after: 60 });
    expect(again.headers.get("Retry-After")).toBe("60");
    expect(again.headers.get("Access-Control-Expose-Headers")).toBe("Retry-After");
    expect(stub.resend).toHaveLength(1);
    expect(state.loginRequests).toHaveLength(1);
  });

  it("does not let a recent desktop login code trigger the web cooldown", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    addLoginRequest(state, { code: "desk", email: "mixed@example.com", purpose: "login", created_at: nowSec() - 5 });
    const res = await call(env, start("mixed@example.com"));
    expect(res.status).toBe(200);
    expect(stub.resend).toHaveLength(1);
  });

  it("429s the fourth code in 15 minutes (any purpose) with retry_after 900", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    const now = nowSec();
    addLoginRequest(state, { code: "d1", email: "busy@example.com", purpose: "login", created_at: now - 800 });
    addLoginRequest(state, { code: "w1", email: "busy@example.com", purpose: "web", created_at: now - 700 });
    addLoginRequest(state, { code: "d2", email: "busy@example.com", purpose: "signup", created_at: now - 600 });
    addLoginRequest(state, { code: "old", email: "busy@example.com", purpose: "web", created_at: now - 1000 });
    const res = await call(env, start("busy@example.com"));
    expect(res.status).toBe(429);
    expect(await body(res)).toEqual({ error: "rate_limit_email", retry_after: 900 });
    expect(res.headers.get("Retry-After")).toBe("900");
    expect(stub.resend).toHaveLength(0);
  });

  it("502s a Resend failure, drops the row, and never logs the address", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch({ resendStatus: 422, resendBody: JSON.stringify({ message: "Invalid `to` field: leak@example.com" }) });
    const errors: string[] = [];
    vi.spyOn(console, "error").mockImplementation((...args: unknown[]) => { errors.push(args.map(String).join(" ")); });
    const res = await call(env, start("leak@example.com"));
    expect(res.status).toBe(502);
    expect(await body(res)).toEqual({ error: "email_send_failed" });
    expect(state.loginRequests).toHaveLength(0);
    expect(errors.length).toBeGreaterThan(0);
    expect(errors.join("\n")).toContain("resend_failed: 422");
    expect(errors.join("\n")).not.toContain("leak@example.com");
    expect(stub.resend).toHaveLength(1);

    // The deleted row does not throttle an immediate retry.
    vi.unstubAllGlobals();
    stubFetch();
    const retry = await call(env, start("leak@example.com"));
    expect(retry.status).toBe(200);
  });

  it("limits start to 5 per minute per IP", async () => {
    const { env } = webSetup();
    stubFetch();
    for (let i = 0; i < 5; i++) await call(env, start(`ip${i}@example.com`));
    const limited = await call(env, start("ip9@example.com"));
    expect(limited.status).toBe(429);
    expect(await body(limited)).toEqual({ error: "rate_limit_auth", retry_after: 60 });
  });
});

describe("POST /auth/web/email/verify", () => {
  it("creates the account, its email identity and one web session for a new email", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    const res = await signInViaApi(env, "New@Example.com", stub);
    expect(res.status).toBe(200);
    const text = await res.text();
    const json = JSON.parse(text) as Record<string, unknown>;
    expect(json.ok).toBe(true);
    expect(json.new_account).toBe(true);
    expect(json.user).toEqual({ email: "new@example.com", created_at: expect.any(Number) });
    expect(text).not.toMatch(/[0-9a-f]{48}/);

    expect(state.users).toHaveLength(1);
    expect(state.identities).toHaveLength(1);
    expect(state.identities[0]).toMatchObject({ user_id: state.users[0].id, provider: "email", subject: "new@example.com" });
    expect(state.sessions).toHaveLength(1);
    expect(state.sessions[0].kind).toBe("web");
    expect(state.sessions[0].expires_at - state.sessions[0].created_at).toBe(2592000);

    const cookies = res.headers.getSetCookie();
    expect(cookies).toHaveLength(1);
    expect(cookies[0]).toMatch(COOKIE_SHAPE);
    const token = cookieToken(res) as string;
    expect(state.sessions[0].token_hash).toBe(await webHash(token));
    expect(state.sessions[0].token_hash).not.toBe(await sha256Hex(token));

    const me = await call(env, webReq("/auth/web/me", { cookie: token }));
    expect(me.status).toBe(200);
  });

  it("signs an existing user into the same account", async () => {
    const { env, state } = webSetup();
    const existing = addUser(state, "Desk@Example.com", 1_600_000_000);
    const stub = stubFetch();
    const res = await signInViaApi(env, "desk@example.com", stub);
    expect(res.status).toBe(200);
    const json = await body(res);
    expect(json.new_account).toBe(false);
    expect(json.user).toEqual({ email: "Desk@Example.com", created_at: 1_600_000_000 });
    expect(state.users).toHaveLength(1);
    expect(state.sessions[0].user_id).toBe(existing.id);
    expect(state.identities[0]).toMatchObject({ user_id: existing.id, subject: "desk@example.com", email: "Desk@Example.com" });
  });

  it("binds the code to its email", async () => {
    const { env } = webSetup();
    const stub = stubFetch();
    await call(env, start("right@example.com"));
    const code = lastCode(stub);
    const wrong = await call(env, verify("wrong@example.com", code));
    expect(wrong.status).toBe(400);
    expect(await body(wrong)).toEqual({ error: "invalid_or_expired_code" });
    expect(wrong.headers.getSetCookie()).toEqual([]);
    expect((await call(env, verify("right@example.com", code))).status).toBe(200);
  });

  it("accepts the right code on the 5th attempt", async () => {
    const { env } = webSetup();
    const stub = stubFetch();
    await call(env, start("five@example.com"));
    for (let i = 0; i < 4; i++) expect((await call(env, verify("five@example.com", "WRONGXXX"))).status).toBe(400);
    expect((await call(env, verify("five@example.com", lastCode(stub)))).status).toBe(200);
  });

  it("refuses the right code after 5 failures", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    await call(env, start("six@example.com"));
    for (let i = 0; i < 5; i++) await call(env, verify("six@example.com", "WRONGXXX"));
    const res = await call(env, verify("six@example.com", lastCode(stub)));
    expect(res.status).toBe(400);
    expect(await body(res)).toEqual({ error: "invalid_or_expired_code" });
    expect(state.sessions).toHaveLength(0);
  });

  it("refuses a replayed code", async () => {
    const { env } = webSetup();
    const stub = stubFetch();
    await call(env, start("replay@example.com"));
    const code = lastCode(stub);
    expect((await call(env, verify("replay@example.com", code))).status).toBe(200);
    expect((await call(env, verify("replay@example.com", code))).status).toBe(400);
  });

  it("refuses desktop codes, expired codes and overlong input", async () => {
    const { env, state } = webSetup();
    const now = nowSec();
    addLoginRequest(state, { code: await sha256Hex("DESKTOP1"), email: "d@example.com", purpose: "login" });
    addLoginRequest(state, { code: await sha256Hex("EXPIRED1"), email: "d@example.com", purpose: "web", expires_at: now - 1 });
    expect((await call(env, verify("d@example.com", "DESKTOP1"))).status).toBe(400);
    expect((await call(env, verify("d@example.com", "EXPIRED1"))).status).toBe(400);
    const long = await call(env, verify("d@example.com", "A".repeat(17)));
    expect(long.status).toBe(400);
    expect(await body(long)).toEqual({ error: "invalid_or_expired_code" });
    expect(state.loginRequests.find((r) => r.purpose === "login")?.consumed).toBe(0);
  });

  it("400s code_required and invalid_email", async () => {
    const { env } = webSetup();
    const empty = await call(env, verify("a@example.com", " - "));
    expect(empty.status).toBe(400);
    expect(await body(empty)).toEqual({ error: "code_required" });
    const missing = await call(env, verify("a@example.com", undefined));
    expect(await body(missing)).toEqual({ error: "code_required" });
    const bad = await call(env, verify("nope", "ABCDEFGH"));
    expect(await body(bad)).toEqual({ error: "invalid_email" });
  });

  it("accepts lowercase and dashed input", async () => {
    const { env } = webSetup();
    const stub = stubFetch();
    await call(env, start("dash@example.com"));
    const code = lastCode(stub);
    const typed = `${code.slice(0, 4).toLowerCase()}-${code.slice(4).toLowerCase()} `;
    expect((await call(env, verify(" DASH@example.com", typed))).status).toBe(200);
  });

  it("rotates the session: re-verify ends the cookie the browser held", async () => {
    const { env, state } = webSetup();
    const user = addUser(state, "rot@example.com");
    const { token: oldToken } = await seedWebUser(state, "", { user, seed: "rotation" });
    addLoginRequest(state, { code: await sha256Hex("ROTATE22"), email: "rot@example.com", purpose: "web" });
    const res = await call(env, verify("rot@example.com", "ROTATE22", oldToken));
    expect(res.status).toBe(200);
    const newToken = cookieToken(res) as string;
    expect(newToken).not.toBe(oldToken);
    expect((await call(env, webReq("/auth/web/me", { cookie: oldToken }))).status).toBe(401);
    expect((await call(env, webReq("/auth/web/me", { cookie: newToken }))).status).toBe(200);
    expect(state.sessions.filter((s) => s.kind === "web")).toHaveLength(1);
  });

  it("limits verify to 10 per minute per IP", async () => {
    const { env } = webSetup();
    for (let i = 0; i < 10; i++) await call(env, verify("a@example.com", "WRONGXXX"));
    const limited = await call(env, verify("a@example.com", "WRONGXXX"));
    expect(limited.status).toBe(429);
    expect(await body(limited)).toEqual({ error: "rate_limit_auth", retry_after: 60 });
  });
});

describe("web and desktop credentials do not cross", () => {
  it("a web cookie token sent as a Bearer is refused", async () => {
    const { env } = webSetup();
    const stub = stubFetch();
    const token = cookieToken(await signInViaApi(env, "cross@example.com", stub)) as string;
    const res = await call(env, makeRequest(`${AUTH}/clips/mine`, { headers: { Authorization: `Bearer ${token}` } }));
    expect(res.status).toBe(401);
  });

  it("a web cookie token sent as a Bearer with the web: prefix is refused and the web session survives", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    const token = cookieToken(await signInViaApi(env, "prefix@example.com", stub)) as string;
    const hash = await webHash(token);
    expect(state.sessions.filter((s) => s.token_hash === hash)).toHaveLength(1);
    const bearer = { Authorization: `Bearer web:${token}` };
    const attempts = [
      makeRequest(`${AUTH}/clips/mine`, { headers: bearer }),
      makeRequest(`${AUTH}/clips`, { method: "POST", headers: { ...bearer, "Content-Type": "video/mp4" }, body: "x" }),
      makeRequest(`${AUTH}/auth/logout`, { method: "POST", headers: bearer }),
    ];
    for (const req of attempts) {
      const res = await call(env, req);
      expect(res.status).toBe(401);
      expect(await body(res)).toEqual({ error: "invalid_token" });
    }
    expect(state.sessions.filter((s) => s.token_hash === hash)).toHaveLength(1);
    expect((await call(env, webReq("/auth/web/me", { cookie: token }))).status).toBe(200);
  });

  it("a desktop session token sent as the cookie is refused", async () => {
    const { env, state } = webSetup();
    const user = addUser(state, "desk2@example.com");
    const desktopToken = "a".repeat(48);
    addSession(state, await sha256Hex(desktopToken), user.id, "desktop");
    const res = await call(env, webReq("/auth/web/me", { cookie: desktopToken }));
    expect(res.status).toBe(401);
    expect(await body(res)).toEqual({ error: "signed_out" });
  });

  it("desktop /auth/verify refuses a web code, which still works on the web", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    await call(env, start("both@example.com"));
    const code = lastCode(stub);
    const desk = await call(env, makeRequest(`${AUTH}/auth/verify`, {
      method: "POST", headers: { "Content-Type": "application/json", "CF-Connecting-IP": "192.0.2.50" }, body: JSON.stringify({ code }),
    }));
    expect(desk.status).toBe(400);
    expect(await body(desk)).toEqual({ error: "invalid_or_expired_code" });
    expect(state.sessions).toHaveLength(0);
    expect((await call(env, verify("both@example.com", code))).status).toBe(200);
    expect(stub.other).toEqual([]);
  });
});
