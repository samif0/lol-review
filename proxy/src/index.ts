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

// ── Dispatch ────────────────────────────────────────────────────────────

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/health") {
      return jsonResponse({ ok: true, ts: Date.now() }, 200);
    }

    if (!env.RIOT_API_KEY) {
      return jsonResponse({ error: "server_misconfigured_no_riot_key" }, 500);
    }

    // ── Auth endpoints (public, IP-rate-limited) ──
    if (url.pathname === "/auth/signup" && request.method === "POST") {
      const limited = authRateLimit(request);
      if (limited) return limited;
      return handleSignup(request, env);
    }
    if (url.pathname === "/auth/login" && request.method === "POST") {
      const limited = authRateLimit(request);
      if (limited) return limited;
      return handleLogin(request, env);
    }
    if (url.pathname === "/auth/verify" && request.method === "POST") {
      const limited = authRateLimit(request);
      if (limited) return limited;
      return handleVerify(request, env);
    }
    if (url.pathname === "/auth/logout" && request.method === "POST") {
      const auth = await authOrDeny(request, env);
      if (auth instanceof Response) return auth;
      const authHeader = request.headers.get("Authorization") ?? "";
      const token = authHeader.slice("Bearer ".length).trim();
      const fullHash = await sha256Hex(token);
      return handleLogout(fullHash, env);
    }

    // ── Proxy endpoints (authed, per-token-rate-limited) ──
    if (request.method !== "GET") {
      return jsonResponse({ error: "method_not_allowed" }, 405);
    }

    const auth = await authOrDeny(request, env);
    if (auth instanceof Response) return auth;

    const limited = rateLimitOrDeny(auth.tokenHash, env);
    if (limited) return limited;

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

    return response;
  },
};
