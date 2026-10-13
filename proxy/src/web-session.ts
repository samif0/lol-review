/**
 * Web review tool sessions: Turnstile + a short-lived signed JWT.
 *
 * The browser tool on revu.lol can't carry a static bearer token (any token
 * shipped in JS is public). Instead:
 *   1. The browser solves a Turnstile challenge (invisible for most people).
 *   2. POST /web/session with the Turnstile token: the Worker checks it with
 *      siteverify and mints a 30-minute HMAC-signed JWT.
 *   3. The browser sends the JWT as a Bearer on /web/* Riot calls.
 *
 * Abuse limits: mints are capped per address per isolate AND globally in the
 * rate limiter Durable Object, and every /web/* Riot call is charged to a
 * per-address budget shared by all of that address's tokens (riot-budget.ts),
 * so minting more tokens buys no extra Riot capacity.
 *
 * Unrelated to the cookie-authenticated web ACCOUNT API (/auth/web/*).
 */

import { Env } from "./types";
import { badRequest, jsonResponse } from "./http";
import { sha256Hex } from "./crypto";
import { clientIpKey, hashedClientIpKey } from "./client-ip";
import { takeGlobalKey } from "./riot-budget";
import { devRelax, turnstileHostnames } from "./web-http";

export const WEB_JWT_TTL_SECONDS = 30 * 60;
const SITEVERIFY_URL = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
const TURNSTILE_TIMEOUT_MS = 10_000;
const TURNSTILE_TOKEN_MAX = 2048;
const MINTS_PER_MINUTE_PER_ISOLATE = 10;
const MINT_WINDOW_MS = WEB_JWT_TTL_SECONDS * 1000;
const DEFAULT_MINTS_PER_WINDOW = 12;

// ── Per-isolate first filter on mints ───────────────────────────────────

type Bucket = { count: number; windowStartMs: number };
const mintBuckets = new Map<string, Bucket>();
const MAX_TRACKED = 10_000;

function mintRateLimited(request: Request): boolean {
  const ip = clientIpKey(request);
  const now = Date.now();
  if (mintBuckets.size >= MAX_TRACKED) {
    for (const [k, b] of mintBuckets) if (now - b.windowStartMs >= 60_000) mintBuckets.delete(k);
    if (mintBuckets.size >= MAX_TRACKED) mintBuckets.clear();
  }
  let b = mintBuckets.get(ip);
  if (!b || now - b.windowStartMs >= 60_000) {
    b = { count: 0, windowStartMs: now };
    mintBuckets.set(ip, b);
  }
  if (b.count >= MINTS_PER_MINUTE_PER_ISOLATE) return true;
  b.count++;
  return false;
}

/** Test hook. */
export function resetWebSessionLimits(): void {
  mintBuckets.clear();
}

// ── Turnstile ───────────────────────────────────────────────────────────

/**
 * siteverify with a timeout. When TURNSTILE_HOSTNAMES is set (production), the
 * token must also have been issued for one of those hostnames, unless the
 * localhost-only dev relax is on. Any network error or timeout fails closed.
 */
async function verifyTurnstile(token: string, env: Env, ip: string | null): Promise<boolean> {
  if (!env.TURNSTILE_SECRET_KEY) return false;
  const body = new URLSearchParams();
  body.set("secret", env.TURNSTILE_SECRET_KEY);
  body.set("response", token);
  if (ip) body.set("remoteip", ip);
  try {
    const res = await fetch(SITEVERIFY_URL, {
      method: "POST",
      body,
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      signal: AbortSignal.timeout(TURNSTILE_TIMEOUT_MS),
    });
    if (!res.ok) return false;
    const data = (await res.json()) as { success?: unknown; hostname?: unknown };
    if (data.success !== true) return false;
    const hostnames = turnstileHostnames(env);
    if (hostnames.length === 0 || devRelax(env)) return true;
    return typeof data.hostname === "string" && hostnames.includes(data.hostname);
  } catch {
    return false;
  }
}

// ── JWT ─────────────────────────────────────────────────────────────────

function b64urlEncode(bytes: Uint8Array): string {
  let bin = "";
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function b64urlEncodeString(s: string): string {
  return b64urlEncode(new TextEncoder().encode(s));
}

/** Throws on malformed input; callers decode inside a try. */
function b64urlDecode(s: string): Uint8Array {
  const pad = s.length % 4 === 0 ? "" : "=".repeat(4 - (s.length % 4));
  const bin = atob(s.replace(/-/g, "+").replace(/_/g, "/") + pad);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

async function hmacSign(secret: string, message: string): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(message)));
}

interface WebJwtPayload {
  iat: number; // issued at, seconds
  exp: number; // expiry, seconds
  jti: string; // unique id, the per-token rate-limit key
}

export async function mintWebJwt(env: Env, ttlSeconds: number): Promise<string> {
  if (!env.WEB_JWT_SECRET) throw new Error("WEB_JWT_SECRET not set");
  const now = Math.floor(Date.now() / 1000);
  const payload: WebJwtPayload = { iat: now, exp: now + ttlSeconds, jti: crypto.randomUUID() };
  const header = b64urlEncodeString(JSON.stringify({ alg: "HS256", typ: "JWT" }));
  const signingInput = `${header}.${b64urlEncodeString(JSON.stringify(payload))}`;
  return `${signingInput}.${b64urlEncode(await hmacSign(env.WEB_JWT_SECRET, signingInput))}`;
}

/** The payload of a valid, unexpired token; null for anything else, never a throw. */
export async function verifyWebJwt(token: string, env: Env): Promise<WebJwtPayload | null> {
  if (!env.WEB_JWT_SECRET) return null;
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  const [headerB64, bodyB64, sigB64] = parts;
  try {
    const expectedSig = await hmacSign(env.WEB_JWT_SECRET, `${headerB64}.${bodyB64}`);
    const givenSig = b64urlDecode(sigB64);
    if (expectedSig.length !== givenSig.length) return null;
    let diff = 0;
    for (let i = 0; i < expectedSig.length; i++) diff |= expectedSig[i] ^ givenSig[i];
    if (diff !== 0) return null;
    const payload = JSON.parse(new TextDecoder().decode(b64urlDecode(bodyB64))) as WebJwtPayload;
    const now = Math.floor(Date.now() / 1000);
    if (typeof payload.exp !== "number" || payload.exp < now) return null;
    if (typeof payload.jti !== "string") return null;
    return payload;
  } catch {
    return null;
  }
}

// ── Routes ──────────────────────────────────────────────────────────────

/** POST /web/session { turnstileToken } → { token, expiresIn }. */
export async function handleWebSession(request: Request, env: Env): Promise<Response> {
  if (mintRateLimited(request)) {
    return jsonResponse({ error: "rate_limit_web_session" }, 429, { "Retry-After": "60" });
  }
  if (!env.TURNSTILE_SECRET_KEY || !env.WEB_JWT_SECRET) {
    return jsonResponse({ error: "web_session_not_configured" }, 503);
  }
  let token = "";
  try {
    const body = (await request.json()) as { turnstileToken?: unknown };
    token = typeof body.turnstileToken === "string" ? body.turnstileToken.trim() : "";
  } catch {
    return badRequest("invalid_json");
  }
  if (!token || token.length > TURNSTILE_TOKEN_MAX) return badRequest("turnstileToken required");

  if (!(await verifyTurnstile(token, env, request.headers.get("CF-Connecting-IP")))) {
    return jsonResponse({ error: "turnstile_failed" }, 403);
  }

  // Global cap on live tokens per address: a token lives MINT_WINDOW_MS, so at
  // most WEB_SESSIONS_PER_IP can be alive at once. Fails open when the
  // limiter is down; the per-isolate filter above still applies.
  const limit = parseInt(env.WEB_SESSIONS_PER_IP || "", 10);
  const perWindow = Number.isFinite(limit) && limit > 0 ? limit : DEFAULT_MINTS_PER_WINDOW;
  if (!(await takeGlobalKey(env, "mint", await hashedClientIpKey(request), perWindow, MINT_WINDOW_MS, true))) {
    return jsonResponse({ error: "rate_limit_web_session" }, 429, { "Retry-After": "300" });
  }

  const jwt = await mintWebJwt(env, WEB_JWT_TTL_SECONDS);
  return jsonResponse({ token: jwt, expiresIn: WEB_JWT_TTL_SECONDS }, 200);
}

/** Bearer JWT for /web/* → the per-token and per-address rate-limit keys. */
export async function authWebJwtOrDeny(
  request: Request,
  env: Env,
): Promise<{ tokenHash: string; ipKey: string } | Response> {
  const authHeader = request.headers.get("Authorization") ?? "";
  if (!authHeader.startsWith("Bearer ")) return jsonResponse({ error: "missing_bearer" }, 401);
  const token = authHeader.slice("Bearer ".length).trim();
  if (!token) return jsonResponse({ error: "empty_token" }, 401);
  const payload = await verifyWebJwt(token, env);
  if (!payload) return jsonResponse({ error: "invalid_or_expired_jwt" }, 401);
  const tokenHash = (await sha256Hex("jti:" + payload.jti)).slice(0, 16);
  return { tokenHash, ipKey: await hashedClientIpKey(request) };
}
