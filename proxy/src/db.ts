/**
 * D1 helpers — thin typed wrappers around the SQL we need.
 */

export interface UserRow {
  id: number;
  email: string;
  created_at: number;
  invite_code_used: string | null;
}

export interface SessionRow {
  token_hash: string;
  user_id: number;
  created_at: number;
  expires_at: number;
}

export interface LoginRequestRow {
  code: string;
  email: string;
  purpose: "signup" | "login" | "web";
  created_at: number;
  expires_at: number;
  consumed: number;
}

// ── Users ───────────────────────────────────────────────────────────────

export async function findUserByEmail(db: D1Database, email: string): Promise<UserRow | null> {
  const row = await db
    .prepare("SELECT id, email, created_at, invite_code_used FROM users WHERE email = ?1 COLLATE NOCASE LIMIT 1")
    .bind(email)
    .first<UserRow>();
  return row ?? null;
}

export async function createUser(db: D1Database, email: string, inviteCode: string | null): Promise<number> {
  const now = Math.floor(Date.now() / 1000);
  const result = await db
    .prepare(
      "INSERT INTO users (email, created_at, invite_code_used) VALUES (?1, ?2, ?3) RETURNING id",
    )
    .bind(email, now, inviteCode)
    .first<{ id: number }>();
  if (!result) throw new Error("createUser: no id returned");
  return result.id;
}

// ── Invite codes ────────────────────────────────────────────────────────

/**
 * Atomic consume: only succeeds if the code exists and is unused. Returns
 * true when the row was flipped, false otherwise (invalid or already used).
 */
export async function tryConsumeInvite(
  db: D1Database,
  code: string,
  userId: number,
): Promise<boolean> {
  const now = Math.floor(Date.now() / 1000);
  const res = await db
    .prepare(
      "UPDATE invite_codes SET used_by = ?2, used_at = ?3 WHERE code = ?1 AND used_by IS NULL",
    )
    .bind(code, userId, now)
    .run();
  return (res.meta.changes ?? 0) > 0;
}

// ── Sessions ────────────────────────────────────────────────────────────

export async function createSession(
  db: D1Database,
  tokenHash: string,
  userId: number,
  lifetimeSeconds: number,
): Promise<{ expires_at: number }> {
  const now = Math.floor(Date.now() / 1000);
  const expiresAt = now + lifetimeSeconds;
  await db
    .prepare(
      "INSERT INTO sessions (token_hash, user_id, created_at, expires_at) VALUES (?1, ?2, ?3, ?4)",
    )
    .bind(tokenHash, userId, now, expiresAt)
    .run();
  return { expires_at: expiresAt };
}

export async function findSession(db: D1Database, tokenHash: string): Promise<SessionRow | null> {
  const now = Math.floor(Date.now() / 1000);
  const row = await db
    .prepare(
      "SELECT token_hash, user_id, created_at, expires_at FROM sessions WHERE token_hash = ?1 AND expires_at > ?2 LIMIT 1",
    )
    .bind(tokenHash, now)
    .first<SessionRow>();
  return row ?? null;
}

export async function deleteSession(db: D1Database, tokenHash: string): Promise<void> {
  await db.prepare("DELETE FROM sessions WHERE token_hash = ?1").bind(tokenHash).run();
}

export async function deleteExpiredSessions(db: D1Database): Promise<void> {
  const now = Math.floor(Date.now() / 1000);
  await db.prepare("DELETE FROM sessions WHERE expires_at <= ?1").bind(now).run();
}

// ── Login requests (one-time codes) ─────────────────────────────────────
//
// The `code` column stores sha256(otp) hex, never the raw code — same
// hash-at-rest treatment as session tokens, so a D1 leak can't hand out
// live login codes.

export async function createLoginRequest(
  db: D1Database,
  codeHash: string,
  email: string,
  purpose: "signup" | "login" | "web",
  lifetimeSeconds: number,
): Promise<void> {
  const now = Math.floor(Date.now() / 1000);
  await db
    .prepare(
      "INSERT INTO login_requests (code, email, purpose, created_at, expires_at, consumed) VALUES (?1, ?2, ?3, ?4, ?5, 0)",
    )
    .bind(codeHash, email, purpose, now, now + lifetimeSeconds)
    .run();
}

/**
 * Count codes issued to an email inside the trailing window. Backs the
 * per-email send throttle — per-IP limits alone don't stop a distributed
 * attacker from bombing one inbox / burning email-send quota.
 */
export async function countRecentLoginRequests(
  db: D1Database,
  email: string,
  windowSeconds: number,
): Promise<number> {
  const cutoff = Math.floor(Date.now() / 1000) - windowSeconds;
  const row = await db
    .prepare("SELECT COUNT(*) AS n FROM login_requests WHERE email = ?1 AND created_at > ?2")
    .bind(email, cutoff)
    .first<{ n: number }>();
  return row?.n ?? 0;
}

/**
 * Atomic: look up a login request by code hash, mark it consumed if it's
 * still valid (unexpired, unconsumed). Returns the pre-consume row or null.
 */
export async function tryConsumeLoginRequest(
  db: D1Database,
  codeHash: string,
): Promise<LoginRequestRow | null> {
  const now = Math.floor(Date.now() / 1000);
  const row = await db
    .prepare(
      "SELECT code, email, purpose, created_at, expires_at, consumed FROM login_requests WHERE code = ?1 LIMIT 1",
    )
    .bind(codeHash)
    .first<LoginRequestRow>();
  if (!row) return null;
  if (row.purpose === 'web') return null; // web codes are redeemable only at /auth/web/email/verify
  if (row.consumed !== 0) return null;
  if (row.expires_at <= now) return null;

  const upd = await db
    .prepare("UPDATE login_requests SET consumed = 1 WHERE code = ?1 AND consumed = 0")
    .bind(codeHash)
    .run();
  if ((upd.meta.changes ?? 0) === 0) return null; // race: another request consumed it
  return row;
}

// ── Web accounts (revu.lol, cookie sessions) ────────────────────────────
//
// Web sessions live in the same `sessions` table with kind = 'web' and store
// sha256Hex("web:" + token), so a web token is never a valid Bearer and a
// desktop token is never a valid cookie. Desktop helpers above stay untouched.

export interface WebSessionRow {
  user_id: number;
  expires_at: number;
}

export interface IdentityRow {
  provider: string;
  email: string | null;
  display_name: string | null;
  created_at: number;
  last_used_at: number | null;
}

export interface ClipObjectRow {
  id: string;
  r2_key: string;
  upload_id: string | null;
}

export async function findWebSession(db: D1Database, tokenHash: string): Promise<WebSessionRow | null> {
  const now = Math.floor(Date.now() / 1000);
  const row = await db
    .prepare("SELECT user_id, expires_at FROM sessions WHERE token_hash = ?1 AND kind = 'web' AND expires_at > ?2 LIMIT 1")
    .bind(tokenHash, now)
    .first<WebSessionRow>();
  return row ?? null;
}

export async function createWebSession(
  db: D1Database,
  tokenHash: string,
  userId: number,
  lifetimeSeconds: number,
): Promise<{ expires_at: number }> {
  const now = Math.floor(Date.now() / 1000);
  const expiresAt = now + lifetimeSeconds;
  await db
    .prepare("INSERT INTO sessions (token_hash, user_id, created_at, expires_at, kind) VALUES (?1, ?2, ?3, ?4, 'web')")
    .bind(tokenHash, userId, now, expiresAt)
    .run();
  return { expires_at: expiresAt };
}

export async function deleteWebSession(db: D1Database, tokenHash: string): Promise<void> {
  await db.prepare("DELETE FROM sessions WHERE token_hash = ?1 AND kind = 'web'").bind(tokenHash).run();
}

export async function deleteWebSessionsForUser(db: D1Database, userId: number): Promise<void> {
  await db.prepare("DELETE FROM sessions WHERE user_id = ?1 AND kind = 'web'").bind(userId).run();
}

export async function findUserById(db: D1Database, id: number): Promise<UserRow | null> {
  const row = await db
    .prepare("SELECT id, email, created_at, invite_code_used FROM users WHERE id = ?1 LIMIT 1")
    .bind(id)
    .first<UserRow>();
  return row ?? null;
}

/**
 * Atomic per-email send throttle for web codes: inserts the code only when the
 * email got fewer than 3 codes (any purpose) in the last 15 minutes AND no web
 * code in the last 60 seconds. Returns false when either cap blocked it.
 */
export async function insertWebLoginRequestIfAllowed(
  db: D1Database,
  codeHash: string,
  email: string,
  lifetimeSeconds: number,
): Promise<boolean> {
  const now = Math.floor(Date.now() / 1000);
  const res = await db
    .prepare(
      "INSERT INTO login_requests (code, email, purpose, created_at, expires_at, consumed, attempts) SELECT ?1, ?2, 'web', ?3, ?4, 0, 0 WHERE (SELECT COUNT(*) FROM login_requests WHERE email = ?2 AND created_at > ?5) < 3 AND NOT EXISTS (SELECT 1 FROM login_requests WHERE email = ?2 AND purpose = 'web' AND created_at > ?6)",
    )
    .bind(codeHash, email, now, now + lifetimeSeconds, now - 900, now - 60)
    .run();
  return res.meta.changes === 1;
}

export async function countRecentWebLoginRequests(
  db: D1Database,
  email: string,
  windowSeconds: number,
): Promise<number> {
  const cutoff = Math.floor(Date.now() / 1000) - windowSeconds;
  const row = await db
    .prepare("SELECT COUNT(*) AS n FROM login_requests WHERE email = ?1 AND purpose = 'web' AND created_at > ?2")
    .bind(email, cutoff)
    .first<{ n: number }>();
  return row?.n ?? 0;
}

export async function deleteLoginRequestByCode(db: D1Database, codeHash: string): Promise<void> {
  await db.prepare("DELETE FROM login_requests WHERE code = ?1").bind(codeHash).run();
}

/** Every verify attempt counts against all live web codes for the email. */
export async function bumpWebLoginAttempts(db: D1Database, email: string): Promise<void> {
  const now = Math.floor(Date.now() / 1000);
  await db
    .prepare("UPDATE login_requests SET attempts = attempts + 1 WHERE email = ?1 AND purpose = 'web' AND consumed = 0 AND expires_at > ?2")
    .bind(email, now)
    .run();
}

/**
 * Atomic consume of a web code bound to its email. Runs AFTER the attempt bump,
 * so at most 5 guesses are ever evaluated per code. Returns the email or null.
 */
export async function tryConsumeWebLoginRequest(
  db: D1Database,
  codeHash: string,
  email: string,
): Promise<string | null> {
  const now = Math.floor(Date.now() / 1000);
  const row = await db
    .prepare(
      "UPDATE login_requests SET consumed = 1 WHERE code = ?1 AND email = ?2 AND purpose = 'web' AND consumed = 0 AND attempts <= 5 AND expires_at > ?3 RETURNING email",
    )
    .bind(codeHash, email, now)
    .first<{ email: string }>();
  return row?.email ?? null;
}

/** Lowercasing happens in SQL (lower(?2)) so subjects match the 0004 backfill. */
export async function upsertEmailIdentity(db: D1Database, userId: number, email: string): Promise<void> {
  const now = Math.floor(Date.now() / 1000);
  await db
    .prepare(
      "INSERT INTO identities (user_id, provider, subject, email, created_at, last_used_at) VALUES (?1, 'email', lower(?2), ?2, ?3, ?3) ON CONFLICT(provider, subject) DO UPDATE SET last_used_at = excluded.last_used_at",
    )
    .bind(userId, email, now)
    .run();
}

export async function listIdentities(db: D1Database, userId: number): Promise<IdentityRow[]> {
  const res = await db
    .prepare("SELECT provider, email, display_name, created_at, last_used_at FROM identities WHERE user_id = ?1 ORDER BY id")
    .bind(userId)
    .all<IdentityRow>();
  return res.results ?? [];
}

/** Keeps an hour of margin so rows the 15-minute send throttle counts survive. */
export async function deleteExpiredLoginRequests(db: D1Database): Promise<void> {
  const cutoff = Math.floor(Date.now() / 1000) - 3600;
  await db.prepare("DELETE FROM login_requests WHERE expires_at <= ?1").bind(cutoff).run();
}

export async function listClipObjectsForUser(db: D1Database, userId: number): Promise<ClipObjectRow[]> {
  const res = await db
    .prepare("SELECT id, r2_key, upload_id FROM clips WHERE user_id = ?1")
    .bind(userId)
    .all<ClipObjectRow>();
  return res.results ?? [];
}

/**
 * Remove every D1 row of an account in ONE batch (a single transaction):
 * transcripts, clips, sessions of every kind, identities, transcribe usage,
 * login codes for the email, then the user. R2 objects are deleted by the caller.
 */
export async function deleteAccountRows(db: D1Database, userId: number, email: string): Promise<void> {
  await db.batch([
    db.prepare("DELETE FROM clip_transcripts WHERE clip_id IN (SELECT id FROM clips WHERE user_id = ?1)").bind(userId),
    db.prepare("DELETE FROM clips WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM sessions WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM identities WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM transcribe_usage WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM login_requests WHERE email = ?2").bind(userId, email),
    db.prepare("DELETE FROM users WHERE id = ?1").bind(userId),
  ]);
}
