/**
 * Revu — Riot API proxy (Cloudflare Worker).
 *
 * Two auth paths:
 *   Path A (static tokens): bearer token is in Worker's ALLOWED_TOKENS secret.
 *   Path B (session tokens): bearer token was issued by /auth/verify, stored in D1.
 *
 * Desktop app calls this Worker → Worker forwards to Riot using server-side
 * RIOT_API_KEY. Users never see the Riot key.
 */

import { handleLogin, handleLogout, handleSignup, handleVerify } from "./auth";
import { findSession } from "./db";
import { Env } from "./types";
import { sha256Hex, sha256Prefix } from "./crypto";
import { badRequest, jsonResponse } from "./http";

// ── Region mapping ──────────────────────────────────────────────────────

const PLATFORM_TO_REGIONAL: Record<string, string> = {
  na1: "americas",
  br1: "americas",
  la1: "americas",
  la2: "americas",
  euw1: "europe",
  eun1: "europe",
  tr1: "europe",
  ru: "europe",
  me1: "europe",
  kr: "asia",
  jp1: "asia",
  oc1: "sea",
  ph2: "sea",
  sg2: "sea",
  th2: "sea",
  tw2: "sea",
  vn2: "sea",
};

function regionalFor(platform: string): string | null {
  return PLATFORM_TO_REGIONAL[platform.toLowerCase()] ?? null;
}

// ── Rate limiting ───────────────────────────────────────────────────────

type Bucket = { count: number; windowStartMs: number };
const perTokenBuckets = new Map<string, Bucket>();
const aggregateBucket: Bucket = { count: 0, windowStartMs: 0 };

function bump(bucket: Bucket, limit: number, nowMs: number): boolean {
  if (nowMs - bucket.windowStartMs >= 1000) {
    bucket.windowStartMs = nowMs;
    bucket.count = 0;
  }
  if (bucket.count >= limit) return false;
  bucket.count++;
  return true;
}

function rateLimitOrDeny(tokenHash: string, env: Env): Response | null {
  const now = Date.now();
  const aggRps = parseInt(env.AGGREGATE_RPS || "18", 10);
  const perRps = parseInt(env.PER_TOKEN_RPS || "2", 10);

  let b = perTokenBuckets.get(tokenHash);
  if (!b) {
    b = { count: 0, windowStartMs: now };
    perTokenBuckets.set(tokenHash, b);
  }
  if (!bump(b, perRps, now)) {
    return jsonResponse({ error: "rate_limit_per_token" }, 429, { "Retry-After": "1" });
  }
  if (!bump(aggregateBucket, aggRps, now)) {
    b.count = Math.max(0, b.count - 1);
    return jsonResponse({ error: "rate_limit_aggregate" }, 429, { "Retry-After": "1" });
  }
  return null;
}

// ── Auth resolution ─────────────────────────────────────────────────────
//
// Returns { tokenHash, userId? } on success, Response (401) on failure.

async function authOrDeny(
  request: Request,
  env: Env,
): Promise<{ tokenHash: string; userId?: number } | Response> {
  const authHeader = request.headers.get("Authorization") ?? "";
  const prefix = "Bearer ";
  if (!authHeader.startsWith(prefix)) {
    return jsonResponse({ error: "missing_bearer" }, 401);
  }
  const token = authHeader.slice(prefix.length).trim();
  if (!token) return jsonResponse({ error: "empty_token" }, 401);

  const tokenHash = await sha256Hex(token);

  // Path A — static token allowlist. The raw token is what's in ALLOWED_TOKENS.
  const allowed = (env.ALLOWED_TOKENS || "")
    .split(",")
    .map((t) => t.trim())
    .filter((t) => t.length > 0);
  if (allowed.includes(token)) {
    return { tokenHash: tokenHash.slice(0, 16) };
  }

  // Path B — session token. We stored sha256(token); look it up.
  const session = await findSession(env.DB, tokenHash);
  if (session) {
    return { tokenHash: tokenHash.slice(0, 16), userId: session.user_id };
  }

  return jsonResponse({ error: "invalid_token" }, 401);
}

// ── Riot passthrough ────────────────────────────────────────────────────

async function riotGet(url: string, env: Env): Promise<Response> {
  const r = await fetch(url, { headers: { "X-Riot-Token": env.RIOT_API_KEY } });
  const body = await r.text();
  const headers = new Headers({ "Content-Type": "application/json" });
  const retryAfter = r.headers.get("Retry-After");
  if (retryAfter) headers.set("Retry-After", retryAfter);
  return new Response(body, { status: r.status, headers });
}

async function handleAccount(url: URL, env: Env): Promise<Response> {
  const riotId = url.searchParams.get("riotId");
  const platform = url.searchParams.get("region");
  if (!riotId || !platform) return badRequest("riotId and region required");
  const regional = regionalFor(platform);
  if (!regional) return badRequest(`unknown region '${platform}'`);
  const hashPos = riotId.indexOf("#");
  if (hashPos < 1 || hashPos === riotId.length - 1) {
    return badRequest("riotId must be gameName#tagLine");
  }
  const gameName = encodeURIComponent(riotId.slice(0, hashPos));
  const tagLine = encodeURIComponent(riotId.slice(hashPos + 1));
  const u = `https://${regional}.api.riotgames.com/riot/account/v1/accounts/by-riot-id/${gameName}/${tagLine}`;
  return riotGet(u, env);
}

async function handleMatches(url: URL, env: Env): Promise<Response> {
  const puuid = url.searchParams.get("puuid");
  const platform = url.searchParams.get("region");
  const count = url.searchParams.get("count") ?? "20";
  const queue = url.searchParams.get("queue");
  if (!puuid || !platform) return badRequest("puuid and region required");
  const regional = regionalFor(platform);
  if (!regional) return badRequest(`unknown region '${platform}'`);
  const params = new URLSearchParams();
  params.set("count", count);
  if (queue) params.set("queue", queue);
  const u = `https://${regional}.api.riotgames.com/lol/match/v5/matches/by-puuid/${encodeURIComponent(puuid)}/ids?${params.toString()}`;
  return riotGet(u, env);
}

async function handleMatch(matchId: string, url: URL, env: Env): Promise<Response> {
  const platform = url.searchParams.get("region");
  if (!platform) return badRequest("region required");
  const regional = regionalFor(platform);
  if (!regional) return badRequest(`unknown region '${platform}'`);
  if (!/^[A-Z0-9]{2,5}_[0-9]+$/.test(matchId)) {
    return badRequest("invalid matchId shape");
  }
  const u = `https://${regional}.api.riotgames.com/lol/match/v5/matches/${encodeURIComponent(matchId)}`;
  return riotGet(u, env);
}

// ── Rate limit by IP (auth endpoints) ───────────────────────────────────
//
// Signup/login/verify are public and unauthenticated. Throttle by CF-Connecting-IP
// to slow brute force. 5 req/min is plenty for humans.

const authIpBuckets = new Map<string, { count: number; windowStartMs: number }>();

function authRateLimit(request: Request): Response | null {
  const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const now = Date.now();
  let b = authIpBuckets.get(ip);
  if (!b) {
    b = { count: 0, windowStartMs: now };
    authIpBuckets.set(ip, b);
  }
  if (now - b.windowStartMs >= 60_000) {
    b.windowStartMs = now;
    b.count = 0;
  }
  if (b.count >= 5) {
    return jsonResponse({ error: "rate_limit_auth" }, 429, { "Retry-After": "60" });
  }
  b.count++;
  return null;
}

// ── Web review tool: Turnstile + JWT session ────────────────────────────
//
// The browser-based web review tool can't carry a static bearer token (any
// token shipped in JS is public). Instead:
//   1. Browser solves a Turnstile challenge (invisible for ~99% of humans).
//   2. POST /web/session with the Turnstile token → server verifies it with
//      Cloudflare's siteverify API, mints a short-lived HMAC-signed JWT.
//   3. Browser uses the JWT in Authorization headers for /web/* Riot calls.
// Per-IP rate limits guard /web/session against token grinding.

const webIpBuckets = new Map<string, { count: number; windowStartMs: number }>();

function webSessionRateLimit(request: Request): Response | null {
  const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const now = Date.now();
  let b = webIpBuckets.get(ip);
  if (!b) {
    b = { count: 0, windowStartMs: now };
    webIpBuckets.set(ip, b);
  }
  if (now - b.windowStartMs >= 60_000) {
    b.windowStartMs = now;
    b.count = 0;
  }
  if (b.count >= 10) {
    return jsonResponse({ error: "rate_limit_web_session" }, 429, { "Retry-After": "60" });
  }
  b.count++;
  return null;
}

async function verifyTurnstile(token: string, env: Env, ip: string | null): Promise<boolean> {
  if (!env.TURNSTILE_SECRET_KEY) return false;
  const body = new URLSearchParams();
  body.set("secret", env.TURNSTILE_SECRET_KEY);
  body.set("response", token);
  if (ip) body.set("remoteip", ip);
  try {
    const res = await fetch(
      "https://challenges.cloudflare.com/turnstile/v0/siteverify",
      {
        method: "POST",
        body,
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
      },
    );
    if (!res.ok) return false;
    const data = (await res.json()) as { success?: boolean };
    return data.success === true;
  } catch {
    return false;
  }
}

function b64urlEncode(bytes: Uint8Array): string {
  let bin = "";
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function b64urlEncodeString(s: string): string {
  return b64urlEncode(new TextEncoder().encode(s));
}

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
    ["sign", "verify"],
  );
  const sig = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(message));
  return new Uint8Array(sig);
}

interface WebJwtPayload {
  iat: number; // issued at, seconds
  exp: number; // expiry, seconds
  jti: string; // unique id, used for per-token rate-limit bucket key
}

async function mintWebJwt(env: Env, ttlSeconds: number): Promise<string> {
  if (!env.WEB_JWT_SECRET) throw new Error("WEB_JWT_SECRET not set");
  const now = Math.floor(Date.now() / 1000);
  const payload: WebJwtPayload = {
    iat: now,
    exp: now + ttlSeconds,
    jti: crypto.randomUUID(),
  };
  const header = b64urlEncodeString(JSON.stringify({ alg: "HS256", typ: "JWT" }));
  const body = b64urlEncodeString(JSON.stringify(payload));
  const signingInput = `${header}.${body}`;
  const sig = await hmacSign(env.WEB_JWT_SECRET, signingInput);
  return `${signingInput}.${b64urlEncode(sig)}`;
}

async function verifyWebJwt(token: string, env: Env): Promise<WebJwtPayload | null> {
  if (!env.WEB_JWT_SECRET) return null;
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  const [headerB64, bodyB64, sigB64] = parts;
  const signingInput = `${headerB64}.${bodyB64}`;
  const expectedSig = await hmacSign(env.WEB_JWT_SECRET, signingInput);
  const givenSig = b64urlDecode(sigB64);
  if (expectedSig.length !== givenSig.length) return null;
  let diff = 0;
  for (let i = 0; i < expectedSig.length; i++) diff |= expectedSig[i] ^ givenSig[i];
  if (diff !== 0) return null;
  try {
    const payload = JSON.parse(new TextDecoder().decode(b64urlDecode(bodyB64))) as WebJwtPayload;
    const now = Math.floor(Date.now() / 1000);
    if (typeof payload.exp !== "number" || payload.exp < now) return null;
    if (typeof payload.jti !== "string") return null;
    return payload;
  } catch {
    return null;
  }
}

async function handleWebSession(request: Request, env: Env): Promise<Response> {
  if (!env.TURNSTILE_SECRET_KEY || !env.WEB_JWT_SECRET) {
    return jsonResponse({ error: "web_session_not_configured" }, 500);
  }
  let token = "";
  try {
    const body = (await request.json()) as { turnstileToken?: string };
    token = (body.turnstileToken || "").trim();
  } catch {
    return badRequest("invalid_json");
  }
  if (!token) return badRequest("turnstileToken required");

  const ip = request.headers.get("CF-Connecting-IP");
  const ok = await verifyTurnstile(token, env, ip);
  if (!ok) return jsonResponse({ error: "turnstile_failed" }, 403);

  const ttlSeconds = 30 * 60; // 30 minutes
  const jwt = await mintWebJwt(env, ttlSeconds);
  return jsonResponse({ token: jwt, expiresIn: ttlSeconds }, 200);
}

async function authWebJwtOrDeny(
  request: Request,
  env: Env,
): Promise<{ tokenHash: string } | Response> {
  const authHeader = request.headers.get("Authorization") ?? "";
  if (!authHeader.startsWith("Bearer ")) {
    return jsonResponse({ error: "missing_bearer" }, 401);
  }
  const token = authHeader.slice("Bearer ".length).trim();
  if (!token) return jsonResponse({ error: "empty_token" }, 401);
  const payload = await verifyWebJwt(token, env);
  if (!payload) return jsonResponse({ error: "invalid_or_expired_jwt" }, 401);
  // Use the JWT's jti as the rate-limit bucket key — sha256-prefixed to match
  // the shape rateLimitOrDeny expects.
  const tokenHash = await sha256Hex(payload.jti);
  return { tokenHash: tokenHash.slice(0, 16) };
}

// ── CORS ────────────────────────────────────────────────────────────────
//
// The desktop app calls this Worker from a native HTTP client — no CORS
// involved. The web review tool calls it from a browser, which means we
// need to allow its Origin and answer preflight OPTIONS requests.

function corsHeadersFor(origin: string | null, env: Env): Record<string, string> {
  const allowed = (env.ALLOWED_ORIGINS || "")
    .split(",")
    .map((o) => o.trim())
    .filter((o) => o.length > 0);
  if (!origin || !allowed.includes(origin)) return {};
  return {
    "Access-Control-Allow-Origin": origin,
    "Vary": "Origin",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Access-Control-Allow-Headers": "Authorization, Content-Type",
    "Access-Control-Max-Age": "86400",
  };
}

function withCors(response: Response, cors: Record<string, string>): Response {
  if (Object.keys(cors).length === 0) return response;
  const headers = new Headers(response.headers);
  for (const [k, v] of Object.entries(cors)) headers.set(k, v);
  return new Response(response.body, { status: response.status, headers });
}

// ── Dispatch ────────────────────────────────────────────────────────────

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const origin = request.headers.get("Origin");
    const cors = corsHeadersFor(origin, env);

    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204, headers: cors });
    }

    if (url.pathname === "/health") {
      return withCors(jsonResponse({ ok: true, ts: Date.now() }, 200), cors);
    }

    if (!env.RIOT_API_KEY) {
      return withCors(jsonResponse({ error: "server_misconfigured_no_riot_key" }, 500), cors);
    }

    // ── Auth endpoints (public, IP-rate-limited) ──
    if (url.pathname === "/auth/signup" && request.method === "POST") {
      const limited = authRateLimit(request);
      if (limited) return withCors(limited, cors);
      return withCors(await handleSignup(request, env), cors);
    }
    if (url.pathname === "/auth/login" && request.method === "POST") {
      const limited = authRateLimit(request);
      if (limited) return withCors(limited, cors);
      return withCors(await handleLogin(request, env), cors);
    }
    if (url.pathname === "/auth/verify" && request.method === "POST") {
      const limited = authRateLimit(request);
      if (limited) return withCors(limited, cors);
      return withCors(await handleVerify(request, env), cors);
    }
    if (url.pathname === "/auth/logout" && request.method === "POST") {
      const auth = await authOrDeny(request, env);
      if (auth instanceof Response) return withCors(auth, cors);
      const authHeader = request.headers.get("Authorization") ?? "";
      const token = authHeader.slice("Bearer ".length).trim();
      const fullHash = await sha256Hex(token);
      return withCors(await handleLogout(fullHash, env), cors);
    }

    // ── Web review tool: Turnstile-gated session mint ──
    if (url.pathname === "/web/session" && request.method === "POST") {
      const limited = webSessionRateLimit(request);
      if (limited) return withCors(limited, cors);
      return withCors(await handleWebSession(request, env), cors);
    }

    // ── Web review tool: Riot passthrough endpoints (JWT-authed) ──
    if (url.pathname.startsWith("/web/") && request.method === "GET") {
      const auth = await authWebJwtOrDeny(request, env);
      if (auth instanceof Response) return withCors(auth, cors);

      const limited = rateLimitOrDeny(auth.tokenHash, env);
      if (limited) return withCors(limited, cors);

      const t0 = Date.now();
      let response: Response;
      try {
        if (url.pathname === "/web/account") {
          response = await handleAccount(url, env);
        } else if (url.pathname === "/web/matches") {
          response = await handleMatches(url, env);
        } else if (url.pathname.startsWith("/web/match/")) {
          const matchId = url.pathname.slice("/web/match/".length);
          response = await handleMatch(matchId, url, env);
        } else {
          response = jsonResponse({ error: "not_found" }, 404);
        }
      } catch (err) {
        console.error(
          JSON.stringify({
            token: auth.tokenHash,
            path: url.pathname,
            error: (err as Error).message,
          }),
        );
        response = jsonResponse({ error: "proxy_error" }, 502);
      }
      console.log(
        JSON.stringify({
          token: auth.tokenHash,
          path: url.pathname,
          status: response.status,
          ms: Date.now() - t0,
        }),
      );
      return withCors(response, cors);
    }

    // ── Desktop / static-token proxy endpoints (authed, per-token-rate-limited) ──
    if (request.method !== "GET") {
      return withCors(jsonResponse({ error: "method_not_allowed" }, 405), cors);
    }

    const auth = await authOrDeny(request, env);
    if (auth instanceof Response) return withCors(auth, cors);

    const limited = rateLimitOrDeny(auth.tokenHash, env);
    if (limited) return withCors(limited, cors);

    const t0 = Date.now();
    let response: Response;
    try {
      if (url.pathname === "/account") {
        response = await handleAccount(url, env);
      } else if (url.pathname === "/matches") {
        response = await handleMatches(url, env);
      } else if (url.pathname.startsWith("/match/")) {
        const matchId = url.pathname.slice("/match/".length);
        response = await handleMatch(matchId, url, env);
      } else {
        response = jsonResponse({ error: "not_found" }, 404);
      }
    } catch (err) {
      console.error(
        JSON.stringify({
          token: auth.tokenHash,
          path: url.pathname,
          error: (err as Error).message,
        }),
      );
      response = jsonResponse({ error: "proxy_error" }, 502);
    }

    console.log(
      JSON.stringify({
        token: auth.tokenHash,
        userId: auth.userId,
        path: url.pathname,
        status: response.status,
        ms: Date.now() - t0,
      }),
    );

    return withCors(response, cors);
  },
};
