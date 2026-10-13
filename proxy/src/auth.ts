/**
 * Desktop auth endpoint handlers. Sign-in is by emailed one-time code only.
 *
 * /auth/login   (POST, { email })            sends the code email
 * /auth/verify  (POST, { code, email? })     exchanges the code for a session
 *                                            token; the first verify for a new
 *                                            email creates the account
 * /auth/logout  (POST, Bearer <session>)     invalidates that session
 *
 * Verify with { email, code } is bound to the email and capped at
 * MAX_CODE_ATTEMPTS evaluated guesses per code. { code } alone is still
 * accepted for desktop builds that don't send the email yet, unless
 * REQUIRE_VERIFY_EMAIL is "1".
 *
 * None of these use the Riot API key. The old invite-code /auth/signup alias
 * is gone (no shipped client calls it).
 */

import {
  bumpLoginAttempts,
  countRecentLoginRequests,
  createLoginRequest,
  createSession,
  createUser,
  deleteLoginRequestByCode,
  deleteSession,
  findUserByEmail,
  tryConsumeBoundLoginRequest,
  tryConsumeLoginRequest,
  upsertEmailIdentity,
  UserRow,
} from "./db";
import { hmacSha256Hex, randomOtpCode, randomSessionToken, sha256Hex } from "./crypto";
import { sendMagicLinkEmail } from "./email";
import { EMAIL_CAP_MESSAGE, refundEmailSend, reserveEmailSend } from "./email-budget";
import { Env } from "./types";
import { jsonResponse, badRequest } from "./http";

export const OTP_LIFETIME_SECONDS = 10 * 60;
export const SESSION_LIFETIME_SECONDS = 30 * 24 * 60 * 60;

// Per-email send throttle. The per-IP limits in index.ts slow one attacker;
// this bounds what a *distributed* attacker can do to a single inbox.
const EMAIL_SEND_WINDOW_SECONDS = 15 * 60;
const EMAIL_SEND_MAX_PER_WINDOW = 3;
const CODE_MAX_LENGTH = 16;

export function isValidEmail(email: string): boolean {
  // Conservative check; Resend rejects actual bad addresses anyway.
  return typeof email === "string"
    && email.length >= 3
    && email.length <= 254
    && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
}

/** What login_requests.code stores for a code (see db.ts). */
export async function loginCodeHash(env: Env, code: string): Promise<string> {
  return env.LOGIN_CODE_PEPPER ? hmacSha256Hex(env.LOGIN_CODE_PEPPER, "code:" + code) : sha256Hex(code);
}

/** The 503 every sign-in email route answers once the daily ceiling is reached. */
export function emailCapResponse(retryAfter: number): Response {
  return jsonResponse(
    { error: "email_daily_cap", message: EMAIL_CAP_MESSAGE, retry_after: retryAfter },
    503,
    { "Retry-After": String(retryAfter) },
  );
}

async function readJson(request: Request): Promise<Record<string, unknown> | null> {
  try {
    const body = (await request.json()) as unknown;
    return body && typeof body === "object" && !Array.isArray(body) ? (body as Record<string, unknown>) : {};
  } catch {
    return null;
  }
}

export async function handleLogin(request: Request, env: Env): Promise<Response> {
  const body = await readJson(request);
  if (!body) return badRequest("invalid_json");
  const email = (typeof body.email === "string" ? body.email : "").trim().toLowerCase();
  if (!isValidEmail(email)) return badRequest("invalid_email");
  return sendLoginCode(env, email);
}

async function sendLoginCode(env: Env, email: string): Promise<Response> {
  const recentSends = await countRecentLoginRequests(env.DB, email, EMAIL_SEND_WINDOW_SECONDS);
  if (recentSends >= EMAIL_SEND_MAX_PER_WINDOW) {
    return jsonResponse({ error: "rate_limit_email" }, 429, { "Retry-After": String(EMAIL_SEND_WINDOW_SECONDS) });
  }

  const reservation = await reserveEmailSend(env);
  if (!reservation.ok) return emailCapResponse(reservation.retryAfter);

  const otp = randomOtpCode(8);
  // Only the hash is stored; the raw code exists in the outbound email alone.
  const codeHash = await loginCodeHash(env, otp);
  try {
    await createLoginRequest(env.DB, codeHash, email, "login", OTP_LIFETIME_SECONDS);
  } catch (err) {
    await refundEmailSend(env, reservation.day);
    throw err;
  }

  try {
    await sendMagicLinkEmail(
      env.RESEND_API_KEY,
      env.MAGIC_LINK_FROM,
      env.APP_NAME || "Revu",
      email,
      otp,
      "login",
    );
  } catch (err) {
    // A code that never arrived must not count toward the per-email throttle
    // or the daily ceiling.
    try { await deleteLoginRequestByCode(env.DB, codeHash); } catch { /* best effort */ }
    await refundEmailSend(env, reservation.day);
    console.error(
      JSON.stringify({
        scope: "auth.sendLoginCode",
        email_hash: (await sha256Hex(email)).slice(0, 8),
        error: (err as Error).message,
      }),
    );
    return jsonResponse({ error: "email_send_failed" }, 502);
  }

  return jsonResponse({ sent: true }, 200);
}

/**
 * Find the account for an email, creating it on first sign-in. A concurrent
 * verify for the same new email can lose the users.email UNIQUE race; the
 * winner's row is then returned as an existing account.
 */
export async function findOrCreateUserByEmail(
  db: D1Database,
  email: string,
): Promise<{ user: UserRow; created: boolean }> {
  const existing = await findUserByEmail(db, email);
  if (existing) return { user: existing, created: false };
  try {
    const newId = await createUser(db, email, null);
    return {
      user: { id: newId, email, created_at: Math.floor(Date.now() / 1000), invite_code_used: null },
      created: true,
    };
  } catch (err) {
    const raced = await findUserByEmail(db, email);
    if (raced) return { user: raced, created: false };
    throw err;
  }
}

export async function handleVerify(request: Request, env: Env): Promise<Response> {
  const body = await readJson(request);
  if (!body) return badRequest("invalid_json");

  const code = (typeof body.code === "string" ? body.code : "").replace(/[\s-]/g, "").toUpperCase();
  if (!code) return badRequest("code_required");
  const rawEmail = typeof body.email === "string" ? body.email.trim().toLowerCase() : "";
  if (rawEmail && !isValidEmail(rawEmail)) return badRequest("invalid_email");
  const emailRequired = env.REQUIRE_VERIFY_EMAIL === "1";
  if (!rawEmail && emailRequired) {
    return jsonResponse({ error: "email_required", message: "Update Revu, then request a new code." }, 400);
  }

  let email: string | null;
  if (rawEmail) {
    // Count the attempt first, so a guess can only consume a code that has
    // seen at most MAX_CODE_ATTEMPTS attempts (this one included). Only once
    // code-only verify is closed: until then a guesser just uses the uncounted
    // code-only path, so counting here would protect nothing and would only
    // let anyone who knows an email burn that user's codes.
    if (emailRequired) await bumpLoginAttempts(env.DB, rawEmail);
    email = code.length > CODE_MAX_LENGTH
      ? null
      : await tryConsumeBoundLoginRequest(env.DB, await loginCodeHash(env, code), rawEmail);
  } else {
    email = code.length > CODE_MAX_LENGTH ? null : await tryConsumeLoginRequest(env.DB, await loginCodeHash(env, code));
  }
  if (!email) return jsonResponse({ error: "invalid_or_expired_code" }, 400);

  // Find-or-create: the first successful verify for a new email is a signup.
  const { user } = await findOrCreateUserByEmail(env.DB, email);

  const token = randomSessionToken();
  const { expires_at } = await createSession(env.DB, await sha256Hex(token), user.id, SESSION_LIFETIME_SECONDS);
  // Best effort: the identities row is informational in v1, so a failure here
  // (or a database without migration 0004) must never fail a desktop sign-in.
  try {
    await upsertEmailIdentity(env.DB, user.id, user.email);
  } catch (err) {
    console.error(JSON.stringify({ scope: "auth.identity", error: (err as Error).message }));
  }

  return jsonResponse({ session_token: token, expires_at }, 200);
}

export async function handleLogout(tokenHash: string, env: Env): Promise<Response> {
  await deleteSession(env.DB, tokenHash);
  return jsonResponse({ ok: true }, 200);
}
