import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { randomOtpCode } from "../src/crypto";
import { addLoginRequest, addSession, addUser } from "./fake-auth";
import { clip, makeRequest, nowSec, runScheduled, sha256Hex } from "./fakes";
import {
  AUTH, CLEAR_COOKIE, OFF_HOST, body, call, resetWebLimiters, seedWebUser, webReq, webSetup,
} from "./web-fixtures";

const ROUTES: Array<[string, string]> = [
  ["GET", "/auth/web/providers"],
  ["POST", "/auth/web/email/start"],
  ["POST", "/auth/web/email/verify"],
  ["GET", "/auth/web/me"],
  ["GET", "/auth/web/clips"],
  ["DELETE", "/auth/web/clips/abc1234"],
  ["POST", "/auth/web/logout"],
  ["POST", "/auth/web/logout-all"],
  ["POST", "/auth/web/account/delete"],
];

beforeEach(() => {
  vi.restoreAllMocks();
  resetWebLimiters();
});
afterEach(() => vi.unstubAllGlobals());

describe("web account API: host pin", () => {
  it("404s every route off the AUTH_BASE host and never sets a cookie", async () => {
    const { env, state } = webSetup();
    const { token } = await seedWebUser(state, "pin@example.com");
    for (const [method, path] of ROUTES) {
      const hasBody = method === "POST" && path !== "/auth/web/logout" && path !== "/auth/web/logout-all";
      const res = await call(env, webReq(path, { method, base: OFF_HOST, cookie: token, body: hasBody ? { email: "pin@example.com" } : undefined }));
      expect(res.status, `${method} ${path}`).toBe(404);
      expect(await body(res)).toEqual({ error: "not_found" });
      expect(res.headers.getSetCookie()).toEqual([]);
      expect(res.headers.get("Access-Control-Allow-Origin")).toBeNull();
      expect(res.headers.get("Cache-Control")).toBe("no-store");
    }
    // The session survived: nothing off-host touched it.
    expect(state.sessions).toHaveLength(1);
  });

  it("answers OPTIONS off-host with a bare 204 (no ACAO, no ACAC)", async () => {
    const { env } = webSetup();
    const res = await call(env, webReq("/auth/web/me", { method: "OPTIONS", base: OFF_HOST, csrf: false }));
    expect(res.status).toBe(204);
    expect(res.headers.get("Access-Control-Allow-Origin")).toBeNull();
    expect(res.headers.get("Access-Control-Allow-Credentials")).toBeNull();
    expect(res.headers.get("Vary")).toBe("Origin");
    expect(res.headers.get("Cache-Control")).toBe("no-store");
  });
});

describe("web account API: preflight", () => {
  it("allows the exact credentialed origin", async () => {
    const { env } = webSetup();
    const res = await call(env, webReq("/auth/web/me", {
      method: "OPTIONS", csrf: false, headers: { "Access-Control-Request-Method": "POST", "Access-Control-Request-Headers": "content-type, x-revu-csrf" },
    }));
    expect(res.status).toBe(204);
    expect(res.headers.get("Access-Control-Allow-Origin")).toBe("https://revu.lol");
    expect(res.headers.get("Access-Control-Allow-Credentials")).toBe("true");
    expect(res.headers.get("Access-Control-Allow-Methods")).toBe("GET, POST, DELETE, OPTIONS");
    expect(res.headers.get("Access-Control-Allow-Headers")).toContain("X-Revu-CSRF");
    expect(res.headers.get("Access-Control-Expose-Headers")).toBe("Retry-After");
    expect(res.headers.get("Access-Control-Max-Age")).toBe("7200");
    expect(res.headers.get("Vary")).toBe("Origin");
    expect(res.headers.get("Cache-Control")).toBe("no-store");
  });

  it("gives a disallowed origin no CORS grant", async () => {
    const { env } = webSetup();
    for (const origin of ["https://evil.example", "null", "http://localhost:8788"]) {
      const res = await call(env, webReq("/auth/web/me", { method: "OPTIONS", csrf: false, origin }));
      expect(res.status).toBe(204);
      expect(res.headers.get("Access-Control-Allow-Origin")).toBeNull();
      expect(res.headers.get("Access-Control-Allow-Credentials")).toBeNull();
      expect(res.headers.get("Vary")).toBe("Origin");
    }
  });

  it("leaves the OPTIONS output of other paths unchanged", async () => {
    const { env } = webSetup();
    const res = await call(env, makeRequest(`${AUTH}/clips/mine`, { method: "OPTIONS", headers: { Origin: "https://revu.lol" } }));
    expect(res.status).toBe(204);
    expect(res.headers.get("Access-Control-Allow-Origin")).toBe("https://revu.lol");
    expect(res.headers.get("Access-Control-Allow-Credentials")).toBeNull();
    expect(res.headers.get("Access-Control-Allow-Headers")).toBe("Authorization, Content-Type");
    expect(res.headers.get("Access-Control-Max-Age")).toBe("86400");
  });
});

describe("web account API: gates", () => {
  it("rejects a missing, null or foreign Origin", async () => {
    const { env } = webSetup();
    for (const origin of [null, "null", "https://evil.example", "https://revu.lol.evil.example", "https://REVU.lol"]) {
      const res = await call(env, webReq("/auth/web/providers", { origin }));
      expect(res.status, String(origin)).toBe(403);
      expect(await body(res)).toEqual({ error: "origin_rejected" });
      expect(res.headers.get("Access-Control-Allow-Origin")).toBeNull();
      expect(res.headers.get("Content-Type")).toBe("application/json");
    }
  });

  it("rejects a cross-site Sec-Fetch-Site even from an allowed origin", async () => {
    const { env } = webSetup();
    for (const site of ["cross-site", "none"]) {
      const res = await call(env, webReq("/auth/web/providers", { fetchSite: site }));
      expect(res.status).toBe(403);
      expect(await body(res)).toEqual({ error: "csrf_rejected" });
      expect(res.headers.get("Access-Control-Allow-Origin")).toBe("https://revu.lol");
    }
    const sameOrigin = await call(env, webReq("/auth/web/providers", { fetchSite: "same-origin" }));
    expect(sameOrigin.status).toBe(200);
    const absent = await call(env, webReq("/auth/web/providers", { fetchSite: null }));
    expect(absent.status).toBe(200);
  });

  it("rejects a top-level cross-site navigation (no Origin)", async () => {
    const { env, state } = webSetup();
    const { token } = await seedWebUser(state, "nav@example.com");
    const res = await call(env, webReq("/auth/web/me", { origin: null, fetchSite: "cross-site", cookie: token }));
    expect(res.status).toBe(403);
    expect(res.headers.getSetCookie()).toEqual([]);
  });

  it("requires X-Revu-CSRF: 1 on every non-GET", async () => {
    const { env, state } = webSetup();
    const { token } = await seedWebUser(state, "csrf@example.com");
    for (const [method, path] of ROUTES.filter(([m]) => m !== "GET")) {
      const res = await call(env, webReq(path, { method, csrf: false, cookie: token, body: { email: "csrf@example.com" } }));
      expect(res.status, `${method} ${path}`).toBe(403);
      expect(await body(res)).toEqual({ error: "csrf_rejected" });
    }
    const wrong = await call(env, webReq("/auth/web/logout", { method: "POST", csrf: false, headers: { "X-Revu-CSRF": "true" } }));
    expect(wrong.status).toBe(403);
    expect(state.sessions).toHaveLength(1);
  });

  it("refuses non-JSON bodies with 415", async () => {
    const { env } = webSetup();
    const res = await call(env, webReq("/auth/web/email/verify", {
      method: "POST", rawBody: JSON.stringify({ email: "a@b.co", code: "X" }), contentType: "text/plain",
    }));
    expect(res.status).toBe(415);
    expect(await body(res)).toEqual({ error: "unsupported_media_type" });
  });

  it("refuses bodies over 4096 bytes and non-object JSON with 400 invalid_json", async () => {
    const { env } = webSetup();
    const big = await call(env, webReq("/auth/web/email/verify", { method: "POST", rawBody: JSON.stringify({ email: "a@b.co", code: "x".repeat(5000) }) }));
    expect(big.status).toBe(400);
    expect(await body(big)).toEqual({ error: "invalid_json" });
    for (const raw of ["[1,2]", "null", "\"str\"", "{not json"]) {
      const res = await call(env, webReq("/auth/web/email/verify", { method: "POST", rawBody: raw }));
      expect(res.status, raw).toBe(400);
      expect(await body(res)).toEqual({ error: "invalid_json" });
    }
    const ok = await call(env, webReq("/auth/web/email/verify", {
      method: "POST", rawBody: JSON.stringify({ email: "a@b.co", code: "ABCD" }), contentType: "application/json; charset=utf-8",
    }));
    expect(ok.status).toBe(400);
    expect(await body(ok)).toEqual({ error: "invalid_or_expired_code" });
  });

  it("404s unknown paths and 405s wrong methods on known ones", async () => {
    const { env } = webSetup();
    const unknown = await call(env, webReq("/auth/web/nope"));
    expect(unknown.status).toBe(404);
    expect(await body(unknown)).toEqual({ error: "not_found" });
    const proto = await call(env, webReq("/auth/web/constructor"));
    expect(proto.status).toBe(404);
    const wrong = await call(env, webReq("/auth/web/me", { method: "POST" }));
    expect(wrong.status).toBe(405);
    expect(await body(wrong)).toEqual({ error: "method_not_allowed" });
  });

  it("rate limits the shared routes at 60 per minute per IP", async () => {
    const { env } = webSetup();
    for (let i = 0; i < 60; i++) expect((await call(env, webReq("/auth/web/providers"))).status).toBe(200);
    const limited = await call(env, webReq("/auth/web/providers"));
    expect(limited.status).toBe(429);
    expect(await body(limited)).toEqual({ error: "rate_limit_auth", retry_after: 60 });
    expect(limited.headers.get("Retry-After")).toBe("60");
    expect(limited.headers.get("Access-Control-Expose-Headers")).toBe("Retry-After");
    const otherIp = await call(env, webReq("/auth/web/providers", { headers: { "CF-Connecting-IP": "198.51.100.1" } }));
    expect(otherIp.status).toBe(200);
  });

  it("turns an unexpected exception into 500 server_error with the web headers", async () => {
    const { env, state, db } = webSetup();
    const { token } = await seedWebUser(state, "boom@example.com");
    db._failNext("FROM sessions WHERE token_hash = ?1 AND kind = 'web'");
    const res = await call(env, webReq("/auth/web/me", { cookie: token }));
    expect(res.status).toBe(500);
    expect(await body(res)).toEqual({ error: "server_error" });
    expect(res.headers.get("Access-Control-Allow-Origin")).toBe("https://revu.lol");
    expect(res.headers.get("Cache-Control")).toBe("no-store");
  });
});

describe("GET /auth/web/providers", () => {
  const providers = async (over: Parameters<typeof webSetup>[0]) => {
    const { env } = webSetup(over);
    const res = await call(env, webReq("/auth/web/providers"));
    expect(res.status).toBe(200);
    expect(res.headers.get("Access-Control-Allow-Credentials")).toBe("true");
    return (await body(res)).providers;
  };

  it("lists email when Turnstile and Resend are configured", async () => {
    expect(await providers({})).toEqual(["email"]);
  });

  it("is empty when a secret is missing", async () => {
    expect(await providers({ TURNSTILE_SECRET_KEY: undefined })).toEqual([]);
    expect(await providers({ RESEND_API_KEY: "" })).toEqual([]);
  });

  it("is empty when TURNSTILE_HOSTNAMES is empty without dev relax", async () => {
    expect(await providers({ TURNSTILE_HOSTNAMES: "" })).toEqual([]);
    expect(await providers({ TURNSTILE_HOSTNAMES: " , " })).toEqual([]);
  });

  it("allows dev relax only on a localhost AUTH_BASE", async () => {
    const local = webSetup({
      AUTH_BASE: "http://localhost:8787", CREDENTIALED_ORIGINS: "http://localhost:8788", TURNSTILE_HOSTNAMES: "", TURNSTILE_DEV_RELAX: "1",
    });
    const res = await call(local.env, webReq("/auth/web/providers", { base: "http://localhost:8787", origin: "http://localhost:8788" }));
    expect(await body(res)).toEqual({ providers: ["email"] });
    expect(await providers({ TURNSTILE_HOSTNAMES: "", TURNSTILE_DEV_RELAX: "1" })).toEqual([]);
  });
});

describe("GET /auth/web/me", () => {
  it("returns the account, identities and session expiry", async () => {
    const { env, state } = webSetup();
    const user = addUser(state, "Me@Example.com", 1_700_000_000);
    state.identities.push({
      id: 1, user_id: user.id, provider: "email", subject: "me@example.com", email: "Me@Example.com",
      display_name: null, created_at: 1_700_000_000, last_used_at: 1_700_000_500,
    });
    const expiresAt = nowSec() + 1000;
    const { token } = await seedWebUser(state, "", { user, expiresAt, seed: "me" });
    const res = await call(env, webReq("/auth/web/me", { cookie: token }));
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({
      user: { email: "Me@Example.com", created_at: 1_700_000_000 },
      identities: [{ provider: "email", email: "Me@Example.com", created_at: 1_700_000_000, last_used_at: 1_700_000_500 }],
      session: { expires_at: expiresAt },
    });
    expect(res.headers.getSetCookie()).toEqual([]);
  });

  it("synthesizes the email identity when none is stored (or the query fails)", async () => {
    const { env, state, db } = webSetup();
    const { user, token } = await seedWebUser(state, "noid@example.com");
    const expected = [{ provider: "email", email: "noid@example.com", created_at: user.created_at, last_used_at: null }];
    expect((await body(await call(env, webReq("/auth/web/me", { cookie: token })))).identities).toEqual(expected);
    db._failNext("FROM identities WHERE user_id");
    const res = await call(env, webReq("/auth/web/me", { cookie: token }));
    expect(res.status).toBe(200);
    expect((await body(res)).identities).toEqual(expected);
  });

  it("401s an expired web session and clears the cookie", async () => {
    const { env, state } = webSetup();
    const { token } = await seedWebUser(state, "old@example.com", { expiresAt: nowSec() - 1 });
    const res = await call(env, webReq("/auth/web/me", { cookie: token }));
    expect(res.status).toBe(401);
    expect(await body(res)).toEqual({ error: "signed_out" });
    expect(res.headers.getSetCookie()).toEqual([CLEAR_COOKIE]);
  });

  it("401s a malformed cookie (clearing it) and no cookie (without Set-Cookie)", async () => {
    const { env } = webSetup();
    for (const bad of ["ABCDEF", "z".repeat(48), "a".repeat(47), "A".repeat(48)]) {
      const res = await call(env, webReq("/auth/web/me", { cookie: bad }));
      expect(res.status).toBe(401);
      expect(res.headers.getSetCookie()).toEqual([CLEAR_COOKIE]);
    }
    const none = await call(env, webReq("/auth/web/me"));
    expect(none.status).toBe(401);
    expect(await body(none)).toEqual({ error: "signed_out" });
    expect(none.headers.getSetCookie()).toEqual([]);
  });

  it("ignores a Bearer header", async () => {
    const { env, state } = webSetup();
    const user = addUser(state, "bearer@example.com");
    addSession(state, await sha256Hex("desktop-token"), user.id, "desktop");
    const res = await call(env, webReq("/auth/web/me", { headers: { Authorization: "Bearer desktop-token" } }));
    expect(res.status).toBe(401);
  });

  it("401s with the clear cookie when the session's user is gone", async () => {
    const { env, state } = webSetup();
    const { token } = await seedWebUser(state, "ghost@example.com");
    state.users.length = 0;
    const res = await call(env, webReq("/auth/web/me", { cookie: token }));
    expect(res.status).toBe(401);
    expect(res.headers.getSetCookie()).toEqual([CLEAR_COOKIE]);
  });
});

describe("cron: login_requests purge", () => {
  it("purges codes expired over an hour ago and keeps the rest", async () => {
    const { env, state } = webSetup();
    const now = nowSec();
    addLoginRequest(state, { code: "old", email: "a@b.co", expires_at: now - 3601 });
    addLoginRequest(state, { code: "recent", email: "a@b.co", expires_at: now - 600 });
    addLoginRequest(state, { code: "live", email: "a@b.co", expires_at: now + 600 });
    await runScheduled(worker, env);
    expect(state.loginRequests.map((r) => r.code).sort()).toEqual(["live", "recent"]);
  });

  it("keeps running the other steps when the purge fails", async () => {
    const { env, state, db } = webSetup();
    const user = addUser(state, "cron@example.com");
    addSession(state, "expired-hash", user.id, "web", nowSec() - 10);
    state.transcribeUsage.set(`${user.id}|2000-01-01`, { audio_seconds: 1, requests: 1 });
    db._failNext("DELETE FROM login_requests WHERE expires_at");
    const errors: string[] = [];
    vi.spyOn(console, "error").mockImplementation((line: unknown) => { errors.push(String(line)); });
    await runScheduled(worker, env);
    expect(errors.some((e) => e.includes("cron.login_requests"))).toBe(true);
    expect(state.sessions).toHaveLength(0);
    expect(state.transcribeUsage.size).toBe(0);
  });
});

describe("clip OG page on the cookie origin", () => {
  it("sends a strict CSP and runs no script", async () => {
    const { env } = webSetup({}, [clip({ id: "Csp1234", title: "<b>hi</b>" })]);
    const res = await call(env, makeRequest(`${AUTH}/Csp1234`));
    expect(res.status).toBe(200);
    expect(res.headers.get("Content-Security-Policy")).toBe(
      "default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
    );
    const html = await res.text();
    expect(html).not.toContain("<script");
    expect(html).toContain('http-equiv="refresh" content="0; url=https://revu.lol/clip.html?id=Csp1234"');
    expect(html).toContain('<a href="https://revu.lol/clip.html?id=Csp1234">');
  });
});

describe("randomOtpCode", () => {
  it("draws 8 characters from the unambiguous alphabet", () => {
    const alphabet = /^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{8}$/;
    const seen = new Set<string>();
    for (let i = 0; i < 2000; i++) {
      const code = randomOtpCode(8);
      expect(code).toMatch(alphabet);
      for (const ch of code) seen.add(ch);
    }
    expect(seen.size).toBe(31);
  });

  it("only maps bytes below 248, so every character is equally likely", () => {
    const bytes = [255, 248, 0, 247, 250, 30, 31, 61, 62, 100, 249, 200, 5, 6];
    let pos = 0;
    vi.spyOn(crypto, "getRandomValues").mockImplementation(<T extends ArrayBufferView | null>(arr: T): T => {
      const view = arr as unknown as Uint8Array;
      for (let i = 0; i < view.length; i++) view[i] = bytes[pos++ % bytes.length];
      return arr;
    });
    // 255, 248, 250 and 249 are rejected; the rest map by b % 31.
    const alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    const expected = [0, 247, 30, 31, 61, 62, 100, 200].map((b) => alphabet[b % 31]).join("");
    expect(randomOtpCode(8)).toBe(expected);
  });
});
