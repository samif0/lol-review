/**
 * Auth endpoint handlers.
 *
 * /auth/login   (POST, { email })             → sends magic-link email
 * /auth/verify  (POST, { code })              → exchanges OTP for session token
 *                                               creates the user on first verify
 *                                               if the email has no account yet
 * /auth/logout  (POST, Bearer <session>)      → invalidates session
 *
 * Invite codes are not used yet. The /auth/signup endpoint is kept as an
 * alias of /auth/login so older clients keep working; once no client ships
 * that route we can remove it.
 */

import {
  createLoginRequest,
  createSession,
  createUser,
  deleteSession,
  findUserByEmail,
  tryConsumeLoginRequest,
} from "./db";
import { randomOtpCode, randomSessionToken, sha256Hex } from "./crypto";
import { sendMagicLinkEmail } from "./email";
import { Env } from "./types";
import { jsonResponse, badRequest } from "./http";

const OTP_LIFETIME_SECONDS = 10 * 60;
const SESSION_LIFETIME_SECONDS = 30 * 24 * 60 * 60;

function isValidEmail(email: string): boolean {
  // Conservative check — Resend will reject actual bad addresses anyway.
  return typeof email === "string"
    && email.length >= 3
    && email.length <= 254
    && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
}

export async function handleLogin(request: Request, env: Env): Promise<Response> {
  let body: { email?: string };
  try {
    body = await request.json();
  } catch {
    return badRequest("invalid_json");
  }

  const email = (body.email ?? "").trim().toLowerCase();
  if (!isValidEmail(email)) return badRequest("invalid_email");

  return sendLoginCode(env, email);
}

/**
 * Kept for backward compatibility with pre-v2.11.5 clients that may still
 * POST to /auth/signup. Behaves identically to /auth/login now (ignores
 * inviteCode). Safe to remove once we no longer ship clients that use it.
 */
export async function handleSignup(request: Request, env: Env): Promise<Response> {
  return handleLogin(request, env);
}

async function sendLoginCode(env: Env, email: string): Promise<Response> {
  const otp = randomOtpCode(8);
  await createLoginRequest(env.DB, otp, email, "login", OTP_LIFETIME_SECONDS);

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

export async function handleVerify(request: Request, env: Env): Promise<Response> {
  let body: { code?: string };
  try {
    body = await request.json();
  } catch {
    return badRequest("invalid_json");
  }

  const code = (body.code ?? "").trim().toUpperCase();
  if (!code) return badRequest("code_required");

  const req = await tryConsumeLoginRequest(env.DB, code);
  if (!req) {
    return jsonResponse({ error: "invalid_or_expired_code" }, 400);
  }

  // Find-or-create. Login is the only flow now; first successful verify for
  // a new email becomes a signup. Invite codes are no longer required.
  let user = await findUserByEmail(env.DB, req.email);
  if (!user) {
    const newId = await createUser(env.DB, req.email, null);
    user = { id: newId, email: req.email, created_at: 0, invite_code_used: null };
  }

  const token = randomSessionToken();
  const tokenHash = await sha256Hex(token);
  const { expires_at } = await createSession(env.DB, tokenHash, user.id, SESSION_LIFETIME_SECONDS);

  return jsonResponse({ session_token: token, expires_at }, 200);
}

export async function handleLogout(tokenHash: string, env: Env): Promise<Response> {
  await deleteSession(env.DB, tokenHash);
  return jsonResponse({ ok: true }, 200);
}
