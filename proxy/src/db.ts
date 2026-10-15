/**
 * D1 helpers — thin typed wrappers around the SQL we need.
 */

import { HLS_TOMBSTONE_DELAY_SECONDS, HLS_TOMBSTONE_EXTRA_KEYS } from "./share-limits";

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
// The `code` column stores a hash of the code, never the raw code (see
// loginCodeHash in auth.ts: plain sha256, or HMAC-SHA256 with the
// LOGIN_CODE_PEPPER secret when it is set). Without the pepper a leaked table
// could still be brute-forced offline, because codes are only about 40 bits,
// so set the pepper in production. The hourly cron deletes rows about an hour
// after they expire, so a sign-in email address is kept for hours, not forever.

/** A code is burned once this many verify attempts have counted against it. */
export const MAX_CODE_ATTEMPTS = 5;

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
 * Desktop verify by code alone (clients that don't send the email yet).
 * Atomic consume of a live desktop code. A code burned by bound attempts
 * (more than MAX_CODE_ATTEMPTS) can't be consumed here either, and web codes
 * are redeemable only at /auth/web/email/verify. Returns the email or null.
 */
export async function tryConsumeLoginRequest(db: D1Database, codeHash: string): Promise<string | null> {
  const now = Math.floor(Date.now() / 1000);
  const row = await db
    .prepare(
      "UPDATE login_requests SET consumed = 1 WHERE code = ?1 AND purpose != 'web' AND consumed = 0 AND attempts <= 5 AND expires_at > ?2 RETURNING email",
    )
    .bind(codeHash, now)
    .first<{ email: string }>();
  return row?.email ?? null;
}

/** Every bound desktop verify attempt counts against all live desktop codes for the email. */
export async function bumpLoginAttempts(db: D1Database, email: string): Promise<void> {
  const now = Math.floor(Date.now() / 1000);
  await db
    .prepare("UPDATE login_requests SET attempts = attempts + 1 WHERE email = ?1 AND purpose != 'web' AND consumed = 0 AND expires_at > ?2")
    .bind(email, now)
    .run();
}

/**
 * Atomic consume of a desktop code bound to its email. Runs AFTER the attempt
 * bump, so at most MAX_CODE_ATTEMPTS guesses are evaluated per code.
 */
export async function tryConsumeBoundLoginRequest(
  db: D1Database,
  codeHash: string,
  email: string,
): Promise<string | null> {
  const now = Math.floor(Date.now() / 1000);
  const row = await db
    .prepare(
      "UPDATE login_requests SET consumed = 1 WHERE code = ?1 AND email = ?2 AND purpose != 'web' AND consumed = 0 AND attempts <= 5 AND expires_at > ?3 RETURNING email",
    )
    .bind(codeHash, email, now)
    .first<{ email: string }>();
  return row?.email ?? null;
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
  size_bytes: number;
  kind: string;
  /** 'hls' for a share stored as segments under r2_key (a prefix), else NULL. */
  format: string | null;
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
    .prepare("SELECT id, r2_key, upload_id, size_bytes, kind, format FROM clips WHERE user_id = ?1")
    .bind(userId)
    .all<ClipObjectRow>();
  return res.results ?? [];
}

// D1 allows at most 100 bound parameters per statement.
const TOMBSTONE_ID_CHUNK = 90;

/**
 * Remove every D1 row of an account in ONE batch (a single transaction):
 * tombstones first for the rows whose R2 delete failed (`tombstoneIds`), then
 * transcripts, VOD share meta, clips, sessions of every kind, identities,
 * transcribe usage, VOD share and part upload usage, allowlist rows, login codes for the
 * email, then the user. R2 objects are deleted by the caller.
 *
 * A tombstone (storage_tombstones) outlives the user, stays counted in the
 * storage budget and is retried by the hourly purge. The tombstones are copied
 * with INSERT ... SELECT in chunks, not one statement per row, so the batch
 * stays small whatever the number of clips.
 *
 * HLS shares (format 'hls', claimed by the caller before their keys were
 * deleted) always get a prefix tombstone for keys 0..max_seq+8, not purged
 * for HLS_TOMBSTONE_DELAY_SECONDS: those in `hlsTombstoneIds` (their key
 * delete failed) count every stored byte, the others only their pending rows
 * (what a PUT in flight may still store). Their segment, run and state rows go
 * with the clips.
 */
export async function deleteAccountRows(
  db: D1Database,
  userId: number,
  email: string,
  tombstoneIds: string[] = [],
  hlsTombstoneIds: string[] = [],
): Promise<void> {
  const now = Math.floor(Date.now() / 1000);
  const notBefore = now + HLS_TOMBSTONE_DELAY_SECONDS;
  const extra = HLS_TOMBSTONE_EXTRA_KEYS + 1;
  const tombstones: D1PreparedStatement[] = [];
  for (let i = 0; i < tombstoneIds.length; i += TOMBSTONE_ID_CHUNK) {
    const ids = tombstoneIds.slice(i, i + TOMBSTONE_ID_CHUNK);
    const marks = ids.map((_, j) => `?${j + 3}`).join(", ");
    tombstones.push(
      db
        .prepare(
          `INSERT OR IGNORE INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at) SELECT r2_key, upload_id, size_bytes, kind, ?2 FROM clips WHERE user_id = ?1 AND format IS NULL AND id IN (${marks})`,
        )
        .bind(userId, now, ...ids),
    );
  }
  for (let i = 0; i < hlsTombstoneIds.length; i += TOMBSTONE_ID_CHUNK) {
    const ids = hlsTombstoneIds.slice(i, i + TOMBSTONE_ID_CHUNK);
    const marks = ids.map((_, j) => `?${j + 5}`).join(", ");
    tombstones.push(
      db
        .prepare(
          `INSERT OR IGNORE INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at, key_count, not_before) SELECT c.r2_key, NULL, MAX(c.size_bytes, c.hls_bytes), 'vod', ?2, h.max_seq + ?4, ?3 FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.user_id = ?1 AND c.format = 'hls' AND c.id IN (${marks})`,
        )
        .bind(userId, now, notBefore, extra, ...ids),
    );
  }
  tombstones.push(
    db
      .prepare(
        "INSERT OR IGNORE INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at, key_count, not_before) SELECT c.r2_key, NULL, (SELECT COALESCE(SUM(s.bytes), 0) FROM vod_hls_segments s WHERE s.clip_id = c.id AND s.committed = 0), 'vod', ?2, h.max_seq + ?4, ?3 FROM clips c JOIN vod_hls h ON h.clip_id = c.id WHERE c.user_id = ?1 AND c.format = 'hls' AND h.max_seq >= 0",
      )
      .bind(userId, now, notBefore, extra),
  );
  await db.batch([
    ...tombstones,
    db.prepare("DELETE FROM clip_transcripts WHERE clip_id IN (SELECT id FROM clips WHERE user_id = ?1)").bind(userId),
    db.prepare("DELETE FROM vod_share_meta WHERE clip_id IN (SELECT id FROM clips WHERE user_id = ?1)").bind(userId),
    db.prepare("DELETE FROM vod_hls_segments WHERE clip_id IN (SELECT id FROM clips WHERE user_id = ?1)").bind(userId),
    db.prepare("DELETE FROM vod_hls_runs WHERE clip_id IN (SELECT id FROM clips WHERE user_id = ?1)").bind(userId),
    db.prepare("DELETE FROM vod_hls WHERE clip_id IN (SELECT id FROM clips WHERE user_id = ?1)").bind(userId),
    db.prepare("DELETE FROM clips WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM sessions WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM identities WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM transcribe_usage WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM vod_share_usage WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM upload_part_usage WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM vod_sharers WHERE user_id = ?1").bind(userId),
    db.prepare("DELETE FROM login_requests WHERE email = ?2").bind(userId, email),
    db.prepare("DELETE FROM users WHERE id = ?1").bind(userId),
  ]);
}
