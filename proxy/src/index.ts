/**
 * Revu proxy (Cloudflare Worker).
 *
 * Callers and how they authenticate:
 *   - Desktop app: `Authorization: Bearer <session token>` issued by
 *     /auth/verify and stored hashed in D1 `sessions`. Every Bearer route
 *     resolves to a real account; there is no static token allowlist.
 *   - Web review tool on revu.lol: a 30-minute JWT from /web/session
 *     (Turnstile), for the /web/* Riot passthrough only (web-session.ts).
 *   - Web accounts on revu.lol: a cookie, read only by /auth/web/* (web-auth.ts).
 *
 * The Worker forwards Riot calls with the server-side RIOT_API_KEY; users never
 * see it. Only the Riot passthrough needs that key: sign-in, clips and
 * transcripts keep working while it is missing or being rotated.
 */

import { handleLogin, handleLogout, handleVerify } from "./auth";
import { findSession, deleteExpiredSessions, deleteExpiredLoginRequests } from "./db";
import { Env } from "./types";
import { GlobalRateLimiter } from "./ratelimit";
import { sha256Hex } from "./crypto";
import { jsonResponse } from "./http";
import { clientIpKey, hashedClientIpKey } from "./client-ip";
import {
  handleClipFile,
  handleClipMeta,
  handleDeleteClip,
  handleListMyClips,
  handleUploadClip,
  isClipSlug,
  purgeExpiredClips,
  renderClipOgPage,
} from "./clips";
import { handleClipView } from "./clip-views";
import { handleUploadComplete, handleUploadInit, handleUploadPart } from "./clip-uploads";
import { dispatchWebAccount } from "./web-auth";
import { webJson, webPreflight } from "./web-http";
import { authWebJwtOrDeny, handleWebSession } from "./web-session";
import { chargeRiot, takeGlobalKey } from "./riot-budget";
import { dispatchRiot } from "./riot";
import { pruneEmailSendUsage } from "./email-budget";
import { clipUploadLimitOrDeny } from "./user-limits";
import {
  handleDeleteTranscript,
  handleGetTranscript,
  handlePutTranscript,
  handleTranscribe,
  pruneTranscribeUsage,
} from "./transcripts";

// ── Auth resolution ─────────────────────────────────────────────────────

interface DesktopAuth {
  /** 16-hex prefix of sha256(token): the rate-limit and log key. */
  tokenHash: string;
  /** Full sha256(token): the sessions row key. */
  fullHash: string;
  userId: number;
}

/**
 * Bearer session → account. 401 for a missing or unknown token; 503 when D1
 * can't answer, so a database hiccup never looks like a revoked session (the
 * desktop app treats a 401 as "sign in again").
 */
async function authOrDeny(request: Request, env: Env): Promise<DesktopAuth | Response> {
  const authHeader = request.headers.get("Authorization") ?? "";
  const prefix = "Bearer ";
  if (!authHeader.startsWith(prefix)) return jsonResponse({ error: "missing_bearer" }, 401);
  const token = authHeader.slice(prefix.length).trim();
  if (!token) return jsonResponse({ error: "empty_token" }, 401);
  // Web sessions are stored as sha256("web:" + token) (web-http.ts) and the
  // lookup below has no kind filter, so a Bearer of "web:<cookie token>" would
  // hash to the web row. Desktop tokens are hex and never contain ':'.
  if (token.startsWith("web:")) return jsonResponse({ error: "invalid_token" }, 401);

  const fullHash = await sha256Hex(token);
  let session;
  try {
    session = await findSession(env.DB, fullHash);
  } catch (err) {
    console.error(JSON.stringify({ scope: "auth.session_lookup", error: (err as Error).message }));
    return jsonResponse({ error: "auth_unavailable" }, 503, { "Retry-After": "5" });
  }
  if (!session) return jsonResponse({ error: "invalid_token" }, 401);
  return { tokenHash: fullHash.slice(0, 16), fullHash, userId: session.user_id };
}

/**
 * Auth for the 3.14.0 owner routes (multipart uploads, transcripts). Same
 * resolution as authOrDeny, but any 401 is the contract's single
 * `unauthorized` code. Legacy routes keep their historical codes.
 */
async function ownerAuthOrDeny(request: Request, env: Env): Promise<DesktopAuth | Response> {
  const auth = await authOrDeny(request, env);
  if (auth instanceof Response && auth.status === 401) return jsonResponse({ error: "unauthorized" }, 401);
  return auth;
}

// ── Per-IP limit on the public sign-in routes ───────────────────────────
//
// /auth/login and /auth/verify are public. Five per minute per address in this
// isolate is a cheap first filter; ten per minute per address across every
// isolate (rate limiter Durable Object, fails open) is the real cap.

type Bucket = { count: number; windowStartMs: number };
const authIpBuckets = new Map<string, Bucket>();
const MAX_TRACKED_BUCKETS = 10_000;

async function authRateLimit(request: Request, env: Env): Promise<Response | null> {
  const ip = clientIpKey(request);
  const now = Date.now();
  if (authIpBuckets.size >= MAX_TRACKED_BUCKETS) {
    for (const [key, b] of authIpBuckets) if (now - b.windowStartMs >= 60_000) authIpBuckets.delete(key);
    if (authIpBuckets.size >= MAX_TRACKED_BUCKETS) authIpBuckets.clear();
  }
  let b = authIpBuckets.get(ip);
  if (!b || now - b.windowStartMs >= 60_000) {
    b = { count: 0, windowStartMs: now };
    authIpBuckets.set(ip, b);
  }
  const limited = jsonResponse({ error: "rate_limit_auth" }, 429, { "Retry-After": "60" });
  if (b.count >= 5) return limited;
  b.count++;
  if (!(await takeGlobalKey(env, "auth", await hashedClientIpKey(request), 10, 60_000, true))) return limited;
  return null;
}

// ── CORS ────────────────────────────────────────────────────────────────
//
// The desktop app calls this Worker from a native HTTP client: no CORS. The
// web review tool and the clip page call it from a browser, so their Origin is
// allowed and preflight OPTIONS is answered.

function corsHeadersFor(origin: string | null, env: Env): Record<string, string> {
  const allowed = (env.ALLOWED_ORIGINS || "")
    .split(",")
    .map((o) => o.trim())
    .filter((o) => o.length > 0);
  if (!origin || !allowed.includes(origin)) return {};
  return {
    "Access-Control-Allow-Origin": origin,
    "Vary": "Origin",
    "Access-Control-Allow-Methods": "GET, HEAD, POST, PUT, DELETE, OPTIONS",
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

// ── Clip dispatch ─────────────────────────────────────────────────────────
//
// Returns a Response if the request targets a clip route, else null. Public
// routes (clip-meta, clip-file, clip-view, clip-transcript) need no auth;
// uploads, deletes and listing require a real account session. None of them
// spend the Riot budget.

async function dispatchClips(request: Request, url: URL, env: Env): Promise<Response | null> {
  const path = url.pathname;

  // Public reads, no auth.
  if (request.method === "GET" && path.startsWith("/clip-meta/")) {
    return handleClipMeta(env, path.slice("/clip-meta/".length));
  }
  if ((request.method === "GET" || request.method === "HEAD") && path.startsWith("/clip-file/")) {
    return handleClipFile(request, env, path.slice("/clip-file/".length));
  }
  if (request.method === "POST" && path.startsWith("/clip-view/")) {
    return handleClipView(request, env, path.slice("/clip-view/".length));
  }
  if ((request.method === "GET" || request.method === "HEAD") && path.startsWith("/clip-transcript/")) {
    return handleGetTranscript(request, env, path.slice("/clip-transcript/".length));
  }

  // Authed routes: uploads, listing, deletes.
  if (path === "/clips" && request.method === "POST") {
    const auth = await authOrDeny(request, env);
    if (auth instanceof Response) return auth;
    // A per-user bucket, never the Riot budget (see user-limits.ts).
    const limited = clipUploadLimitOrDeny(auth.userId);
    if (limited) return limited;
    return handleUploadClip(request, env, auth.userId);
  }
  if (path === "/clips/mine" && request.method === "GET") {
    const auth = await authOrDeny(request, env);
    if (auth instanceof Response) return auth;
    return handleListMyClips(env, auth.userId);
  }

  // 3.14.0 multipart uploads + transcripts. They sit before the generic
  // DELETE /clips/ matcher so /clips/:id/transcript isn't eaten by it.
  if (path === "/clips/uploads" && request.method === "POST") {
    const auth = await ownerAuthOrDeny(request, env);
    if (auth instanceof Response) return auth;
    return handleUploadInit(request, env, auth.userId);
  }
  const partMatch = request.method === "PUT" ? /^\/clips\/([0-9A-Za-z]{7})\/parts\/(\d{1,5})$/.exec(path) : null;
  if (partMatch) {
    const auth = await ownerAuthOrDeny(request, env);
    if (auth instanceof Response) return auth;
    return handleUploadPart(request, env, auth.userId, partMatch[1], partMatch[2]);
  }
  const completeMatch = request.method === "POST" ? /^\/clips\/([0-9A-Za-z]{7})\/complete$/.exec(path) : null;
  if (completeMatch) {
    const auth = await ownerAuthOrDeny(request, env);
    if (auth instanceof Response) return auth;
    return handleUploadComplete(request, env, auth.userId, completeMatch[1]);
  }
  const transcriptMatch = request.method === "PUT" || request.method === "DELETE"
    ? /^\/clips\/([0-9A-Za-z]{7})\/transcript$/.exec(path)
    : null;
  if (transcriptMatch) {
    const auth = await ownerAuthOrDeny(request, env);
    if (auth instanceof Response) return auth;
    return request.method === "PUT"
      ? handlePutTranscript(request, env, auth.userId, transcriptMatch[1])
      : handleDeleteTranscript(env, auth.userId, transcriptMatch[1]);
  }
  if (path === "/transcribe" && request.method === "POST") {
    const auth = await ownerAuthOrDeny(request, env);
    if (auth instanceof Response) return auth;
    return handleTranscribe(request, env, auth.userId);
  }

  if (path.startsWith("/clips/") && request.method === "DELETE") {
    const auth = await authOrDeny(request, env);
    if (auth instanceof Response) return auth;
    return handleDeleteClip(env, path.slice("/clips/".length), auth.userId);
  }

  return null;
}

// ── Riot passthrough ────────────────────────────────────────────────────

/** Run one passthrough request with its budget charge, logging and error mapping. */
async function proxyRiot(
  path: string,
  url: URL,
  env: Env,
  ctx: ExecutionContext | undefined,
  charge: () => Promise<Response | null>,
  logKey: Record<string, unknown>,
): Promise<Response> {
  if (!env.RIOT_API_KEY) return jsonResponse({ error: "server_misconfigured_no_riot_key" }, 500);
  const t0 = Date.now();
  let response: Response;
  try {
    response = await dispatchRiot(path, url, env, ctx, charge);
  } catch (err) {
    console.error(JSON.stringify({ ...logKey, path: url.pathname, error: (err as Error).message }));
    response = jsonResponse({ error: "proxy_error" }, 502);
  }
  console.log(JSON.stringify({ ...logKey, path: url.pathname, status: response.status, ms: Date.now() - t0 }));
  return response;
}

// ── Dispatch ────────────────────────────────────────────────────────────

async function route(request: Request, url: URL, env: Env, ctx: ExecutionContext | undefined, cors: Record<string, string>): Promise<Response> {
  if (request.method === "OPTIONS") {
    // The credentialed web account API has its own host-pinned preflight.
    if (url.pathname.startsWith("/auth/web/")) return webPreflight(request, url, env);
    return new Response(null, { status: 204, headers: cors });
  }

  if (url.pathname === "/health") {
    return withCors(jsonResponse({ ok: true, ts: Date.now() }, 200), cors);
  }

  // ── Bare clip slug → watch page ──
  // clips.revu.lol/<id>: a single path segment shaped like a clip slug (7
  // base62 chars) that isn't a reserved word. Link unfurlers get a
  // server-rendered page with per-clip Open Graph video tags (crawlers don't run
  // JS); humans are bounced to the watch page by its meta refresh. A missing or
  // expired clip, or a lookup failure, is a plain redirect to the watch page.
  if (request.method === "GET" || request.method === "HEAD") {
    const seg = url.pathname.slice(1);
    if (seg.indexOf("/") === -1 && isClipSlug(seg)) {
      const watchBase = (env.WATCH_BASE || "https://revu.lol").replace(/\/+$/, "");
      try {
        const og = await renderClipOgPage(env, seg);
        if (og) return og;
      } catch (err) {
        console.error(JSON.stringify({ scope: "clips.og", error: (err as Error)?.message ?? String(err) }));
      }
      return Response.redirect(`${watchBase}/clip.html?id=${seg}`, 302);
    }
  }

  // ── Clip API (public reads, authed writes) ──
  // Wrapped: an R2 or D1 failure on the upload path becomes a clean, retryable
  // 502 {clip_error} for the desktop instead of a raw Cloudflare 503.
  try {
    const clipResponse = await dispatchClips(request, url, env);
    if (clipResponse) return withCors(clipResponse, cors);
  } catch (err) {
    console.error(
      JSON.stringify({
        scope: "clips.dispatch",
        path: url.pathname,
        method: request.method,
        error: (err as Error)?.message ?? String(err),
      }),
    );
    return withCors(
      jsonResponse({ error: "clip_error", message: "Clip service hit a temporary error. Try again." }, 502),
      cors,
    );
  }

  // ── Web accounts (revu.lol, cookie sessions; host-pinned to AUTH_BASE) ──
  // Never wrapped in withCors: these responses carry their own CORS and Set-Cookie.
  try {
    const webResponse = await dispatchWebAccount(request, url, env);
    if (webResponse) return webResponse;
  } catch (err) {
    console.error(JSON.stringify({ scope: "web.dispatch", path: url.pathname, method: request.method, error: (err as Error)?.message ?? String(err) }));
    return webJson({ error: "server_error" }, 500, request, env);
  }

  // ── Desktop sign-in (public, IP-rate-limited; no Riot key needed) ──
  if (url.pathname === "/auth/login" && request.method === "POST") {
    const limited = await authRateLimit(request, env);
    if (limited) return withCors(limited, cors);
    return withCors(await handleLogin(request, env), cors);
  }
  if (url.pathname === "/auth/verify" && request.method === "POST") {
    const limited = await authRateLimit(request, env);
    if (limited) return withCors(limited, cors);
    return withCors(await handleVerify(request, env), cors);
  }
  if (url.pathname === "/auth/logout" && request.method === "POST") {
    const auth = await authOrDeny(request, env);
    if (auth instanceof Response) return withCors(auth, cors);
    return withCors(await handleLogout(auth.fullHash, env), cors);
  }

  // ── Web review tool: Turnstile-gated JWT mint ──
  if (url.pathname === "/web/session" && request.method === "POST") {
    return withCors(await handleWebSession(request, env), cors);
  }

  // ── Web review tool: Riot passthrough (JWT; web budget + per-address cap) ──
  if (url.pathname.startsWith("/web/") && request.method === "GET") {
    const auth = await authWebJwtOrDeny(request, env);
    if (auth instanceof Response) return withCors(auth, cors);
    const charge = () => chargeRiot(env, { kind: "web", tokenHash: auth.tokenHash, ipKey: auth.ipKey });
    const response = await proxyRiot(url.pathname.slice("/web".length), url, env, ctx, charge, { token: auth.tokenHash });
    return withCors(response, cors);
  }

  // ── Desktop Riot passthrough (session token; desktop budget) ──
  if (request.method !== "GET") {
    return withCors(jsonResponse({ error: "method_not_allowed" }, 405), cors);
  }
  const auth = await authOrDeny(request, env);
  if (auth instanceof Response) return withCors(auth, cors);
  const charge = () => chargeRiot(env, { kind: "desktop", tokenHash: auth.tokenHash });
  const response = await proxyRiot(url.pathname, url, env, ctx, charge, { token: auth.tokenHash, userId: auth.userId });
  return withCors(response, cors);
}

// Workers requires Durable Object classes to be exported from the main module
// (the entry referenced by wrangler.toml `main`). Re-export it here.
export { GlobalRateLimiter };

export default {
  async scheduled(_controller: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
    // Hourly housekeeping, in order: purge expired clips and expired pending
    // uploads (R2 + D1), stale sessions, expired sign-in codes (with their
    // email addresses), old transcribe_usage rows, then old email send counts.
    // Each step is isolated so one failure doesn't skip the rest.
    ctx.waitUntil(
      (async () => {
        try {
          const purged = await purgeExpiredClips(env);
          console.log(JSON.stringify({ scope: "cron.purge", clips: purged }));
        } catch (err) {
          console.error(JSON.stringify({ scope: "cron.purge", error: (err as Error).message }));
        }
        try {
          await deleteExpiredSessions(env.DB);
        } catch (err) {
          console.error(JSON.stringify({ scope: "cron.sessions", error: (err as Error).message }));
        }
        try {
          await deleteExpiredLoginRequests(env.DB);
        } catch (err) {
          console.error(JSON.stringify({ scope: "cron.login_requests", error: (err as Error).message }));
        }
        try {
          await pruneTranscribeUsage(env);
        } catch (err) {
          console.error(JSON.stringify({ scope: "cron.transcribe_usage", error: (err as Error).message }));
        }
        try {
          await pruneEmailSendUsage(env);
        } catch (err) {
          console.error(JSON.stringify({ scope: "cron.email_send_usage", error: (err as Error).message }));
        }
      })(),
    );
  },

  async fetch(request: Request, env: Env, ctx?: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);
    const cors = corsHeadersFor(request.headers.get("Origin"), env);
    // Last line of defense: any throw becomes JSON with CORS headers, so a
    // browser sees a typed error instead of an opaque network failure and the
    // desktop sees a status it can act on instead of a raw Cloudflare 1101.
    try {
      return await route(request, url, env, ctx, cors);
    } catch (err) {
      console.error(JSON.stringify({
        scope: "worker.unhandled",
        path: url.pathname,
        method: request.method,
        error: (err as Error)?.message ?? String(err),
      }));
      return withCors(jsonResponse({ error: "internal", message: "Revu hit a temporary error. Try again." }, 500), cors);
    }
  },
};
