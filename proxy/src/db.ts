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
  purpose: "signup" | "login";
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

export async function createLoginRequest(
  db: D1Database,
  code: string,
  email: string,
  purpose: "signup" | "login",
  lifetimeSeconds: number,
): Promise<void> {
  const now = Math.floor(Date.now() / 1000);
  await db
    .prepare(
      "INSERT INTO login_requests (code, email, purpose, created_at, expires_at, consumed) VALUES (?1, ?2, ?3, ?4, ?5, 0)",
    )
    .bind(code, email, purpose, now, now + lifetimeSeconds)
    .run();
}

/**
 * Atomic: look up a login request by code, mark it consumed if it's still
 * valid (unexpired, unconsumed). Returns the pre-consume row or null.
 */
export async function tryConsumeLoginRequest(
  db: D1Database,
  code: string,
): Promise<LoginRequestRow | null> {
  const now = Math.floor(Date.now() / 1000);
  const row = await db
    .prepare(
      "SELECT code, email, purpose, created_at, expires_at, consumed FROM login_requests WHERE code = ?1 LIMIT 1",
    )
    .bind(code)
    .first<LoginRequestRow>();
  if (!row) return null;
  if (row.consumed !== 0) return null;
  if (row.expires_at <= now) return null;

  const upd = await db
    .prepare("UPDATE login_requests SET consumed = 1 WHERE code = ?1 AND consumed = 0")
    .bind(code)
    .run();
  if ((upd.meta.changes ?? 0) === 0) return null; // race: another request consumed it
  return row;
}
