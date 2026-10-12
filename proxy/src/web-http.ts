/**
 * HTTP plumbing for the cookie-authenticated web account API (/auth/web/*).
 *
 * Everything here is path-scoped to /auth/web/*: the credentialed CORS
 * headers, the session cookie, the CSRF gate and the per-IP limiters never
 * touch the desktop routes, whose CORS output stays byte-identical.
 *
 * Responses are built with a fresh Headers object and appended Set-Cookie;
 * they must never pass through index.ts withCors (it rebuilds the headers).
 */

import { sha256Hex } from "./crypto";
import { mediaTypeOf, readSmallBody } from "./http";
import { Env } from "./types";

export const WEB_COOKIE = "__Host-revu_web";
export const WEB_COOKIE_MAX_AGE = 30 * 24 * 60 * 60;
export const WEB_BODY_MAX_BYTES = 4096;

const DEFAULT_AUTH_BASE = "https://clips.revu.lol";
const TOKEN_SHAPE = /^[0-9a-f]{48}$/;

export function webSetCookie(token: string): string {
  return `${WEB_COOKIE}=${token}; Path=/; Secure; HttpOnly; SameSite=Lax; Max-Age=${WEB_COOKIE_MAX_AGE}`;
}

export const WEB_CLEAR_COOKIE = `${WEB_COOKIE}=; Path=/; Secure; HttpOnly; SameSite=Lax; Max-Age=0`;

/** The "web:" prefix keeps web and desktop session hashes disjoint. */
export async function webTokenHash(token: string): Promise<string> {
  return sha256Hex("web:" + token);
}

/** The session token from the Cookie header, or null when absent or malformed. */
export function readWebCookie(request: Request): string | null {
  const header = request.headers.get("Cookie");
  if (!header) return null;
  for (const part of header.split(";")) {
    const pair = part.trim();
    const eq = pair.indexOf("=");
    if (eq < 0 || pair.slice(0, eq) !== WEB_COOKIE) continue;
    const value = pair.slice(eq + 1);
    return TOKEN_SHAPE.test(value) ? value : null;
  }
  return null;
}

/** True when the Cookie header carries a session cookie at all, valid or not. */
export function hasWebCookie(request: Request): boolean {
  const header = request.headers.get("Cookie") ?? "";
  return header.split(";").some((part) => part.trim().startsWith(`${WEB_COOKIE}=`));
}

export function authBaseHost(env: Env): string {
  return new URL(env.AUTH_BASE || DEFAULT_AUTH_BASE).host;
}

export function credentialedOrigins(env: Env): string[] {
  return (env.CREDENTIALED_ORIGINS || "")
    .split(",")
    .map((o) => o.trim())
    .filter((o) => o.length > 0);
}

export function turnstileHostnames(env: Env): string[] {
  return (env.TURNSTILE_HOSTNAMES || "")
    .split(",")
    .map((h) => h.trim())
    .filter((h) => h.length > 0);
}

/** Dev relax only ever applies to a localhost AUTH_BASE (wrangler dev). */
export function devRelax(env: Env): boolean {
  return env.TURNSTILE_DEV_RELAX === "1" && (env.AUTH_BASE || "").startsWith("http://localhost:");
}

export function turnstileConfigOk(env: Env): boolean {
  return turnstileHostnames(env).length > 0 || devRelax(env);
}

function originAllowed(origin: string | null, env: Env): origin is string {
  return origin !== null && origin !== "null" && credentialedOrigins(env).includes(origin);
}

/** CORS headers for an /auth/web/* response (Vary and Cache-Control always). */
export function webCorsHeaders(origin: string | null, env: Env, preflight: boolean): Headers {
  const headers = new Headers({ "Vary": "Origin", "Cache-Control": "no-store" });
  if (!originAllowed(origin, env)) return headers;
  headers.set("Access-Control-Allow-Origin", origin);
  headers.set("Access-Control-Allow-Credentials", "true");
  headers.set("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
  headers.set("Access-Control-Allow-Headers", "Content-Type, X-Revu-CSRF");
  headers.set("Access-Control-Expose-Headers", "Retry-After");
  if (preflight) headers.set("Access-Control-Max-Age", "7200");
  return headers;
}

export interface WebJsonOptions {
  setCookie?: string;
  retryAfter?: number;
}

export function webJson(
  body: Record<string, unknown>,
  status: number,
  request: Request,
  env: Env,
  opts: WebJsonOptions = {},
): Response {
  const headers = webCorsHeaders(request.headers.get("Origin"), env, false);
  headers.set("Content-Type", "application/json");
  let payload = body;
  if (opts.retryAfter !== undefined) {
    headers.set("Retry-After", String(opts.retryAfter));
    payload = { ...body, retry_after: opts.retryAfter };
  }
  if (opts.setCookie) headers.append("Set-Cookie", opts.setCookie);
  return new Response(JSON.stringify(payload), { status, headers });
}

/**
 * OPTIONS /auth/web/*. Off the AUTH_BASE host, or for an origin outside the
 * allowlist, the 204 carries only Vary and Cache-Control.
 */
export function webPreflight(request: Request, url: URL, env: Env): Response {
  const origin = url.host === authBaseHost(env) ? request.headers.get("Origin") : null;
  return new Response(null, { status: 204, headers: webCorsHeaders(origin, env, true) });
}

/** Host pin, Origin allowlist, Sec-Fetch-Site and the X-Revu-CSRF header. */
export function gateWebRequest(request: Request, url: URL, env: Env): Response | null {
  if (url.host !== authBaseHost(env)) {
    // Never echo CORS headers (or a cookie) off the pinned host.
    return new Response(JSON.stringify({ error: "not_found" }), {
      status: 404,
      headers: { "Content-Type": "application/json", "Vary": "Origin", "Cache-Control": "no-store" },
    });
  }
  if (!originAllowed(request.headers.get("Origin"), env)) {
    return webJson({ error: "origin_rejected" }, 403, request, env);
  }
  const site = request.headers.get("Sec-Fetch-Site");
  if (site !== null && site !== "same-site" && site !== "same-origin") {
    return webJson({ error: "csrf_rejected" }, 403, request, env);
  }
  if (request.method !== "GET" && request.headers.get("X-Revu-CSRF") !== "1") {
    return webJson({ error: "csrf_rejected" }, 403, request, env);
  }
  return null;
}

// ── Per-IP limiters ─────────────────────────────────────────────────────
//
// Same in-memory, per-isolate pattern as authRateLimit in index.ts. They slow
// a single client; they are not a durable global limit.

type Bucket = { count: number; windowStartMs: number };
const MAX_TRACKED_IPS = 10_000;

export interface IpLimiter {
  /** True when the request fits; false when the caller must answer 429. */
  take(request: Request): boolean;
  /** Test hook: forget every bucket. */
  reset(): void;
}

export function makeIpLimiter(limit: number, windowMs: number): IpLimiter {
  const buckets = new Map<string, Bucket>();
  return {
    take(request: Request): boolean {
      const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
      const now = Date.now();
      if (buckets.size >= MAX_TRACKED_IPS) {
        for (const [key, bucket] of buckets) {
          if (now - bucket.windowStartMs >= windowMs) buckets.delete(key);
        }
        if (buckets.size >= MAX_TRACKED_IPS) buckets.clear();
      }
      let b = buckets.get(ip);
      if (!b) {
        b = { count: 0, windowStartMs: now };
        buckets.set(ip, b);
      }
      if (now - b.windowStartMs >= windowMs) {
        b.windowStartMs = now;
        b.count = 0;
      }
      if (b.count >= limit) return false;
      b.count++;
      return true;
    },
    reset(): void {
      buckets.clear();
    },
  };
}

export const webLimiters = {
  emailStart: makeIpLimiter(5, 60_000),
  emailVerify: makeIpLimiter(10, 60_000),
  general: makeIpLimiter(60, 60_000),
};

export function webRateLimited(request: Request, env: Env): Response {
  return webJson({ error: "rate_limit_auth" }, 429, request, env, { retryAfter: 60 });
}

/**
 * Parse a small JSON object body. Returns the object, or the 415/400 error
 * response the contract requires.
 */
export async function readJsonObject(
  request: Request,
  env: Env,
): Promise<Record<string, unknown> | Response> {
  if (mediaTypeOf(request) !== "application/json") {
    return webJson({ error: "unsupported_media_type" }, 415, request, env);
  }
  const text = await readSmallBody(request, WEB_BODY_MAX_BYTES);
  if (text === null) return webJson({ error: "invalid_json" }, 400, request, env);
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return webJson({ error: "invalid_json" }, 400, request, env);
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return webJson({ error: "invalid_json" }, 400, request, env);
  }
  return parsed as Record<string, unknown>;
}
