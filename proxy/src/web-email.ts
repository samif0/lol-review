/**
 * Email-code sign-in for revu.lol (/auth/web/providers, /auth/web/email/*).
 *
 * Web codes are purpose 'web' rows in login_requests. They are issued only
 * after a strict Turnstile check, throttled per email by one atomic
 * conditional insert, bound to their email on verify, and capped at 5
 * evaluated guesses per code (attempts are bumped BEFORE the consume).
 * Desktop /auth/verify refuses them (see tryConsumeLoginRequest).
 */

import {
  bumpWebLoginAttempts,
  countRecentWebLoginRequests,
  createWebSession,
  deleteLoginRequestByCode,
  deleteWebSession,
  insertWebLoginRequestIfAllowed,
  tryConsumeWebLoginRequest,
  upsertEmailIdentity,
} from "./db";
import {
  findOrCreateUserByEmail,
  isValidEmail,
  OTP_LIFETIME_SECONDS,
  SESSION_LIFETIME_SECONDS,
} from "./auth";
import { randomOtpCode, randomSessionToken, sha256Hex } from "./crypto";
import { sendMagicLinkEmail } from "./email";
import { Env } from "./types";
import {
  devRelax,
  readJsonObject,
  readWebCookie,
  turnstileConfigOk,
  turnstileHostnames,
  webJson,
  webLimiters,
  webRateLimited,
  webSetCookie,
  webTokenHash,
} from "./web-http";

export const TURNSTILE_SITEVERIFY_URL = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
export const TURNSTILE_ACTION = "web_signin";
const TURNSTILE_TIMEOUT_MS = 10_000;
const TURNSTILE_TOKEN_MAX = 2048;
const CODE_MAX_LENGTH = 16;
const WEB_COOLDOWN_SECONDS = 60;
const EMAIL_WINDOW_SECONDS = 15 * 60;

/** Which sign-in providers are usable right now. Empty means fail closed. */
export function webProviders(env: Env): string[] {
  const ready = !!env.TURNSTILE_SECRET_KEY && !!env.RESEND_API_KEY && turnstileConfigOk(env);
  return ready ? ["email"] : [];
}

export function handleProviders(request: Request, env: Env): Response {
  return webJson({ providers: webProviders(env) }, 200, request, env);
}

function normalizeEmail(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const email = value.trim().toLowerCase();
  return isValidEmail(email) ? email : null;
}

/** Remove all whitespace and dashes, then uppercase ("abcd-efgh" works). */
function normalizeCode(value: unknown): string {
  if (typeof value !== "string") return "";
  return value.replace(/[\s-]/g, "").toUpperCase();
}

/**
 * Strict siteverify: success, the web_signin action and an allowed hostname.
 * Only dev relax (localhost AUTH_BASE) skips the action and hostname checks.
 * Any network error or timeout fails closed.
 */
export async function verifyTurnstileStrict(token: string, env: Env, ip: string | null): Promise<boolean> {
  if (!env.TURNSTILE_SECRET_KEY) return false;
  const relax = devRelax(env);
  const hostnames = turnstileHostnames(env);
  if (!relax && hostnames.length === 0) {
    console.error(JSON.stringify({ scope: "turnstile.config", error: "TURNSTILE_HOSTNAMES is empty" }));
    return false;
  }
  const form = new URLSearchParams();
  form.set("secret", env.TURNSTILE_SECRET_KEY);
  form.set("response", token);
  if (ip) form.set("remoteip", ip);
  try {
    const res = await fetch(TURNSTILE_SITEVERIFY_URL, {
      method: "POST",
      body: form,
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      signal: AbortSignal.timeout(TURNSTILE_TIMEOUT_MS),
    });
    if (!res.ok) return false;
    const data = (await res.json()) as { success?: unknown; action?: unknown; hostname?: unknown };
    if (data.success !== true) return false;
    if (relax) return true;
    if (data.action !== TURNSTILE_ACTION) return false;
    return typeof data.hostname === "string" && hostnames.includes(data.hostname);
  } catch {
    return false;
  }
}

export async function handleEmailStart(request: Request, env: Env): Promise<Response> {
  if (!webLimiters.emailStart.take(request)) return webRateLimited(request, env);
  if (webProviders(env).length === 0) return webJson({ error: "sign_in_unavailable" }, 503, request, env);

  const body = await readJsonObject(request, env);
  if (body instanceof Response) return body;

  const email = normalizeEmail(body.email);
  if (!email) return webJson({ error: "invalid_email" }, 400, request, env);

  const token = body.turnstileToken;
  if (typeof token !== "string" || token.length === 0 || token.length > TURNSTILE_TOKEN_MAX) {
    return webJson({ error: "turnstile_required" }, 400, request, env);
  }
  if (!(await verifyTurnstileStrict(token, env, request.headers.get("CF-Connecting-IP")))) {
    return webJson({ error: "turnstile_failed" }, 403, request, env);
  }

  const otp = randomOtpCode(8);
  // Only the hash is stored; the raw code exists in the outbound email alone.
  const codeHash = await sha256Hex(otp);
  const inserted = await insertWebLoginRequestIfAllowed(env.DB, codeHash, email, OTP_LIFETIME_SECONDS);
  if (!inserted) {
    const recentWeb = await countRecentWebLoginRequests(env.DB, email, WEB_COOLDOWN_SECONDS);
    const retryAfter = recentWeb >= 1 ? WEB_COOLDOWN_SECONDS : EMAIL_WINDOW_SECONDS;
    return webJson({ error: "rate_limit_email" }, 429, request, env, { retryAfter });
  }

  try {
    await sendMagicLinkEmail(env.RESEND_API_KEY, env.MAGIC_LINK_FROM, env.APP_NAME || "Revu", email, otp, "web");
  } catch (err) {
    // Drop the row so an immediate retry is not throttled by a code that never arrived.
    try { await deleteLoginRequestByCode(env.DB, codeHash); } catch { /* best effort */ }
    console.error(
      JSON.stringify({
        scope: "web.email_start",
        email_hash: (await sha256Hex(email)).slice(0, 8),
        error: (err as Error).message,
      }),
    );
    return webJson({ error: "email_send_failed" }, 502, request, env);
  }

  // Identical whether or not an account exists for the email.
  return webJson({ sent: true }, 200, request, env);
}

export async function handleEmailVerify(request: Request, env: Env): Promise<Response> {
  if (!webLimiters.emailVerify.take(request)) return webRateLimited(request, env);

  const body = await readJsonObject(request, env);
  if (body instanceof Response) return body;

  const email = normalizeEmail(body.email);
  if (!email) return webJson({ error: "invalid_email" }, 400, request, env);
  const code = normalizeCode(body.code);
  if (!code) return webJson({ error: "code_required" }, 400, request, env);

  // Count the attempt first, so a guess can only consume a code that has seen
  // at most 5 attempts (this one included).
  await bumpWebLoginAttempts(env.DB, email);
  if (code.length > CODE_MAX_LENGTH) return webJson({ error: "invalid_or_expired_code" }, 400, request, env);

  const consumed = await tryConsumeWebLoginRequest(env.DB, await sha256Hex(code), email);
  if (!consumed) return webJson({ error: "invalid_or_expired_code" }, 400, request, env);

  const { user, created } = await findOrCreateUserByEmail(env.DB, consumed);
  try {
    await upsertEmailIdentity(env.DB, user.id, user.email);
  } catch (err) {
    console.error(JSON.stringify({ scope: "web.identity", error: (err as Error).message }));
  }

  // Rotate: the session this browser already holds (if any) ends now.
  const previous = readWebCookie(request);
  if (previous) await deleteWebSession(env.DB, await webTokenHash(previous));

  const token = randomSessionToken();
  await createWebSession(env.DB, await webTokenHash(token), user.id, SESSION_LIFETIME_SECONDS);

  return webJson(
    { ok: true, new_account: created, user: { email: user.email, created_at: user.created_at } },
    200,
    request,
    env,
    { setCookie: webSetCookie(token) },
  );
}
