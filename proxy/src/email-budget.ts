/**
 * Global daily ceiling on sign-in emails.
 *
 * Every code email (desktop /auth/login and web /auth/web/email/start) first
 * reserves one send in email_send_usage (migration 0005) for the current UTC
 * day, and gives it back if the email is not sent. Past EMAIL_DAILY_SEND_CAP
 * (default 100, which keeps a Resend free plan's 3,000/month intact even under
 * a sustained attack) sign-in emails pause until UTC midnight with a clear
 * 503 email_daily_cap, instead of an attacker burning the provider quota and
 * breaking sign-in for everyone for the rest of the month.
 *
 * The reservation fails OPEN on a database error (for example, before the
 * migration is applied): this is a cost guard, not an auth gate.
 */

import { Env } from "./types";

export const DEFAULT_EMAIL_DAILY_SEND_CAP = 100;
export const EMAIL_CAP_MESSAGE = "Revu has sent all the sign-in emails it can for today. Try again after midnight UTC.";

const RESERVE_SQL =
  "INSERT INTO email_send_usage (day, sends) VALUES (?1, 1) ON CONFLICT(day) DO UPDATE SET sends = sends + 1 RETURNING sends";
const REFUND_SQL = "UPDATE email_send_usage SET sends = sends - 1 WHERE day = ?1 AND sends > 0";
const PRUNE_SQL = "DELETE FROM email_send_usage WHERE day < ?1";

export function emailDailyCap(env: Env): number {
  const n = parseInt(env.EMAIL_DAILY_SEND_CAP || "", 10);
  return Number.isFinite(n) && n > 0 ? n : DEFAULT_EMAIL_DAILY_SEND_CAP;
}

export function utcDay(ms: number): string {
  return new Date(ms).toISOString().slice(0, 10);
}

export function secondsUntilUtcMidnight(ms: number): number {
  const s = Math.floor(ms / 1000);
  return Math.max(1, 86400 - (s % 86400));
}

export type EmailReservation =
  | { ok: true; day: string | null }
  | { ok: false; retryAfter: number };

/** Reserve one sign-in email for today. `day` is null when the counter was unavailable. */
export async function reserveEmailSend(env: Env): Promise<EmailReservation> {
  const now = Date.now();
  const day = utcDay(now);
  let sends: number;
  try {
    const row = await env.DB.prepare(RESERVE_SQL).bind(day).first<{ sends: number }>();
    sends = row?.sends ?? 1;
  } catch (err) {
    console.error(JSON.stringify({ scope: "email_budget.reserve", error: (err as Error).message }));
    return { ok: true, day: null };
  }
  if (sends > emailDailyCap(env)) {
    await refundEmailSend(env, day);
    console.error(JSON.stringify({ scope: "email_budget.cap", day }));
    return { ok: false, retryAfter: secondsUntilUtcMidnight(now) };
  }
  return { ok: true, day };
}

/** Give a reservation back (the email was not sent). Best effort. */
export async function refundEmailSend(env: Env, day: string | null): Promise<void> {
  if (!day) return;
  try {
    await env.DB.prepare(REFUND_SQL).bind(day).run();
  } catch (err) {
    console.error(JSON.stringify({ scope: "email_budget.refund", error: (err as Error).message }));
  }
}

/** Cron: drop counters older than 7 days. */
export async function pruneEmailSendUsage(env: Env): Promise<void> {
  await env.DB.prepare(PRUNE_SQL).bind(utcDay(Date.now() - 7 * 86400 * 1000)).run();
}
