/**
 * Auth endpoint handlers.
 *
 * /auth/signup  (POST, { email, inviteCode }) → sends magic-link email
 * /auth/login   (POST, { email })             → sends magic-link email
 * /auth/verify  (POST, { code })              → exchanges OTP for session token
 * /auth/logout  (POST, Bearer <session>)      → invalidates session
 */

import {
  createLoginRequest,
  createSession,
  createUser,
  deleteSession,
  findUserByEmail,
  tryConsumeInvite,
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

export async function handleSignup(request: Request, env: Env): Promise<Response> {
  let body: { email?: string; inviteCode?: string };
  try {
    body = await request.json();
  } catch {
    return badRequest("invalid_json");
  }

  const email = (body.email ?? "").trim().toLowerCase();
  const inviteCode = (body.inviteCode ?? "").trim().toUpperCase();
  if (!isValidEmail(email)) return badRequest("invalid_email");
  if (!inviteCode) return badRequest("invite_code_required");

  const existing = await findUserByEmail(env.DB, email);
  if (existing) {
    // Don't reveal account existence. Still send the email-as-login flow so
    // the UX is uniform, but skip the invite-consume step.
    return sendCodeFor(env, email, "login");
  }

  // We don't consume the invite yet — only at /auth/verify once we know the
  // email was reachable. That way a typo'd email doesn't burn a code.
  // But we do pre-check the invite exists + is unused so we can reject now.
  const code = await env.DB
    .prepare("SELECT code FROM invite_codes WHERE code = ?1 AND used_by IS NULL LIMIT 1")
    .bind(inviteCode)
    .first<{ code: string }>();
  if (!code) return badRequest("invite_code_invalid_or_used");

  return sendCodeFor(env, email, "signup", inviteCode);
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

  // Uniform response — don't leak whether the email has an account.
  return sendCodeFor(env, email, "login");
}

async function sendCodeFor(
  env: Env,
  email: string,
  purpose: "signup" | "login",
  inviteCode?: string,
): Promise<Response> {
  // We stash invite code in the OTP row's purpose only if signup. Simpler:
  // re-check invite at /auth/verify by looking up the login_request.purpose
  // and the original email's invite_codes binding. For v1 we keep invite
  // tracking stateless: if purpose='signup', /auth/verify will need to
  // re-read inviteCode from a separate mapping. To keep the SQL simple,
  // we store inviteCode into purpose as 'signup:XXXX' — a small hack that
  // avoids adding a column for a column that's only meaningful pre-verify.
  const storedPurpose: "signup" | "login" = purpose === "signup" && inviteCode
    ? (`signup:${inviteCode}` as unknown as "signup")
    : purpose;

  const otp = randomOtpCode(8);
  await createLoginRequest(env.DB, otp, email, storedPurpose, OTP_LIFETIME_SECONDS);

  try {
    await sendMagicLinkEmail(
      env.RESEND_API_KEY,
      env.MAGIC_LINK_FROM,
      env.APP_NAME || "Revu",
      email,
      otp,
      purpose,
    );
  } catch (err) {
    console.error(
      JSON.stringify({
        scope: "auth.sendCode",
        purpose,
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

  // Parse purpose and optional invite code from the stored string.
  let purpose: "signup" | "login";
  let inviteCode: string | null = null;
  if (req.purpose.startsWith("signup:")) {
    purpose = "signup";
    inviteCode = req.purpose.slice("signup:".length) || null;
  } else if (req.purpose === "signup" || req.purpose === "login") {
    purpose = req.purpose;
  } else {
    return badRequest("bad_request_state");
  }

  // Signup path: create user + consume invite atomically-ish (two SQL ops,
  // D1 doesn't give us a transaction in the Workers SDK at the moment;
  // acceptable because invite-code consume is atomic and idempotent).
  let userId: number;
  if (purpose === "signup") {
    const existing = await findUserByEmail(env.DB, req.email);
    if (existing) {
      // Shouldn't normally happen — race between two signup flows.
      userId = existing.id;
    } else {
      if (!inviteCode) {
        return badRequest("invite_code_missing_on_signup_request");
      }
      userId = await createUser(env.DB, req.email, inviteCode);
      const consumed = await tryConsumeInvite(env.DB, inviteCode, userId);
      if (!consumed) {
        // Invite got consumed by someone else between /auth/signup and now.
        // Roll back the user row and fail.
        await env.DB.prepare("DELETE FROM users WHERE id = ?1").bind(userId).run();
        return jsonResponse({ error: "invite_code_already_used" }, 400);
      }
    }
  } else {
    const existing = await findUserByEmail(env.DB, req.email);
    if (!existing) {
      // Login code was issued for an email with no user (we send codes
      // uniformly to avoid leaking account existence). Fail quietly.
      return jsonResponse({ error: "invalid_or_expired_code" }, 400);
    }
    userId = existing.id;
  }

  // Issue session token.
  const token = randomSessionToken();
  const tokenHash = await sha256Hex(token);
  const { expires_at } = await createSession(env.DB, tokenHash, userId, SESSION_LIFETIME_SECONDS);

  return jsonResponse({ session_token: token, expires_at }, 200);
}

export async function handleLogout(tokenHash: string, env: Env): Promise<Response> {
  await deleteSession(env.DB, tokenHash);
  return jsonResponse({ ok: true }, 200);
}
