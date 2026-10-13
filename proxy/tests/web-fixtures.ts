/**
 * Shared fixtures for the /auth/web/* tests: an Env wired to the auth fake,
 * browser-shaped requests, a stubbed global fetch for Turnstile siteverify and
 * Resend (no network), and session helpers.
 */

import { vi } from "vitest";
import worker from "../src/index";
import { Env } from "../src/types";
import { webLimiters } from "../src/web-http";
import { addSession, addUser, FakeAuthState, FakeUser, makeAuthState } from "./fake-auth";
import { FakeClip, FakeDb, makeEnv, makeFakeDb, makeFakeR2, sha256Hex } from "./fakes";

export const AUTH = "https://clips.revu.lol";
export const SITE = "https://revu.lol";
export const OFF_HOST = "https://revu-proxy.lol-review.workers.dev";
export const SITEVERIFY = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
export const RESEND = "https://api.resend.com/emails";
export const COOKIE_NAME = "__Host-revu_web";
export const CLEAR_COOKIE = "__Host-revu_web=; Path=/; Secure; HttpOnly; SameSite=Lax; Max-Age=0";

export interface WebSetup {
  env: Env;
  state: FakeAuthState;
  db: FakeDb;
  r2: ReturnType<typeof makeFakeR2>;
}

export function webSetup(
  overrides: Partial<Env> = {},
  seedClips: Array<Partial<FakeClip> & { id: string }> = [],
): WebSetup {
  const state = makeAuthState();
  const db = makeFakeDb(seedClips, {}, state);
  const r2 = makeFakeR2();
  const env = makeEnv({
    AUTH_BASE: AUTH,
    CREDENTIALED_ORIGINS: "https://revu.lol,https://www.revu.lol",
    TURNSTILE_HOSTNAMES: "revu.lol,www.revu.lol",
    TURNSTILE_SECRET_KEY: "test",
    RESEND_API_KEY: "test",
    RIOT_API_KEY: "test",
    MAGIC_LINK_FROM: "login@revu.lol",
    APP_NAME: "Revu",
    DB: db,
    CLIPS: r2.bucket,
    ...overrides,
  });
  return { env, state, db, r2 };
}

export function resetWebLimiters(): void {
  webLimiters.emailStart.reset();
  webLimiters.emailVerify.reset();
  webLimiters.general.reset();
}

export interface WebReqInit {
  method?: string;
  body?: unknown;
  rawBody?: string;
  cookie?: string;
  origin?: string | null;
  csrf?: boolean;
  contentType?: string;
  fetchSite?: string | null;
  base?: string;
  headers?: Record<string, string>;
}

/** A request shaped like the site's fetch(credentials: 'include') calls. */
export function webReq(path: string, init: WebReqInit = {}): Request {
  const method = init.method ?? "GET";
  const headers = new Headers(init.headers ?? {});
  const origin = init.origin === undefined ? SITE : init.origin;
  if (origin !== null) headers.set("Origin", origin);
  const site = init.fetchSite === undefined ? "same-site" : init.fetchSite;
  if (site !== null) headers.set("Sec-Fetch-Site", site);
  if (init.csrf ?? method !== "GET") headers.set("X-Revu-CSRF", "1");
  if (init.cookie !== undefined) headers.set("Cookie", `${COOKIE_NAME}=${init.cookie}`);
  if (!headers.has("CF-Connecting-IP")) headers.set("CF-Connecting-IP", "203.0.113.7");
  let body: string | undefined;
  if (init.rawBody !== undefined) body = init.rawBody;
  else if (init.body !== undefined) body = JSON.stringify(init.body);
  if (body !== undefined) headers.set("Content-Type", init.contentType ?? "application/json");
  return new Request(`${init.base ?? AUTH}${path}`, { method, headers, body });
}

export async function call(env: Env, request: Request): Promise<Response> {
  return worker.fetch(request, env, {
    waitUntil() {},
    passThroughOnException() {},
    props: {},
  } as unknown as ExecutionContext);
}

export async function body(res: Response): Promise<Record<string, unknown>> {
  return (await res.json()) as Record<string, unknown>;
}

/** The session token from a verify response's Set-Cookie, or null. */
export function cookieToken(res: Response): string | null {
  const cookie = res.headers.getSetCookie()[0] ?? "";
  const m = /^__Host-revu_web=([0-9a-f]{48});/.exec(cookie);
  return m ? m[1] : null;
}

export interface FetchStub {
  siteverify: Array<URLSearchParams>;
  resend: Array<Record<string, unknown>>;
  other: string[];
}

export interface FetchStubOptions {
  turnstile?: Record<string, unknown> | "throw";
  resendStatus?: number;
  resendBody?: string;
}

/** Stub global fetch: siteverify and Resend only; anything else is recorded and 500s. */
export function stubFetch(opts: FetchStubOptions = {}): FetchStub {
  const calls: FetchStub = { siteverify: [], resend: [], other: [] };
  const turnstile = opts.turnstile ?? { success: true, action: "web_signin", hostname: "revu.lol" };
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input instanceof Request ? input.url : input);
    if (url === SITEVERIFY) {
      calls.siteverify.push(new URLSearchParams(String(init?.body ?? "")));
      if (turnstile === "throw") throw new TypeError("network down");
      return new Response(JSON.stringify(turnstile), { status: 200, headers: { "Content-Type": "application/json" } });
    }
    if (url === RESEND) {
      calls.resend.push(JSON.parse(String(init?.body ?? "{}")) as Record<string, unknown>);
      return new Response(opts.resendBody ?? JSON.stringify({ id: "email-1" }), { status: opts.resendStatus ?? 200 });
    }
    calls.other.push(url);
    return new Response("unexpected", { status: 500 });
  }));
  return calls;
}

/** The 8-character code from the most recent Resend call's plain text. */
export function lastCode(stub: FetchStub): string {
  const text = String(stub.resend[stub.resend.length - 1]?.text ?? "");
  const m = /\n\n([A-Z0-9]{8})\n\n/.exec(text);
  if (!m) throw new Error("no code in the last email");
  return m[1];
}

export async function webHash(token: string): Promise<string> {
  return sha256Hex("web:" + token);
}

function hexToken(seed: string): string {
  return Array.from({ length: 48 }, (_, i) => "0123456789abcdef"[(seed.charCodeAt(i % seed.length) + i) % 16]).join("");
}

/** Seed a user with a live web session; returns the raw cookie token. */
export async function seedWebUser(
  state: FakeAuthState,
  email: string,
  opts: { user?: FakeUser; expiresAt?: number; seed?: string } = {},
): Promise<{ user: FakeUser; token: string }> {
  const user = opts.user ?? addUser(state, email);
  const token = hexToken(opts.seed ?? email);
  addSession(state, await webHash(token), user.id, "web", opts.expiresAt);
  return { user, token };
}

/** Seed a desktop session (plain sha256 of the Bearer token). */
export async function seedDesktopSession(state: FakeAuthState, userId: number, token: string): Promise<void> {
  addSession(state, await sha256Hex(token), userId, "desktop");
}

/** Full sign-in through the API: start (stubbed email) then verify. */
export async function signInViaApi(env: Env, email: string, stub: FetchStub, cookie?: string): Promise<Response> {
  const start = await call(env, webReq("/auth/web/email/start", { method: "POST", body: { email, turnstileToken: "tok" } }));
  if (start.status !== 200) throw new Error(`start failed: ${start.status}`);
  return call(env, webReq("/auth/web/email/verify", { method: "POST", body: { email, code: lastCode(stub) }, cookie }));
}
