/**
 * In-memory model of the auth tables (users, login_requests, sessions,
 * identities) plus the account-deletion statements, for makeFakeDb(…, auth).
 *
 * Every statement is matched by its EXACT SQL text (kept in lockstep with
 * src/db.ts), so the desktop findSession can never answer the web query and
 * vice versa. Constraints that matter for the contract are enforced: UNIQUE
 * users.email (ASCII NOCASE), sessions.token_hash PK, login_requests.code PK,
 * identities UNIQUE(provider, subject) with its ON CONFLICT update, the partial
 * one-email-identity-per-user index, and FK cascades from users. SQL lower()
 * and NOCASE fold ASCII only, like SQLite without ICU.
 */

import type { FakeClip, FakeTranscript } from "./fakes";

export interface FakeUser { id: number; email: string; created_at: number; invite_code_used: string | null }
export interface FakeLoginRequest {
  code: string; email: string; purpose: string; created_at: number; expires_at: number; consumed: number; attempts: number;
}
export interface FakeSession { token_hash: string; user_id: number; created_at: number; expires_at: number; kind: string }
export interface FakeIdentity {
  id: number; user_id: number; provider: string; subject: string; email: string | null;
  display_name: string | null; created_at: number; last_used_at: number | null;
}

export interface FakeAuthState {
  users: FakeUser[];
  loginRequests: FakeLoginRequest[];
  identities: FakeIdentity[];
  sessions: FakeSession[];
  clipTranscripts: Map<string, FakeTranscript>;
  transcribeUsage: Map<string, { audio_seconds: number; requests: number }>;
  /** Linked to makeFakeDb's clip store when the state is passed to it. */
  clips: Map<string, FakeClip>;
  nextUserId: number;
  nextIdentityId: number;
}

export interface AuthResult { handled: boolean; value?: unknown }

export function makeAuthState(): FakeAuthState {
  return {
    users: [], loginRequests: [], identities: [], sessions: [],
    clipTranscripts: new Map(), transcribeUsage: new Map(), clips: new Map(),
    nextUserId: 1, nextIdentityId: 1,
  };
}

export function asciiLower(s: string): string {
  return s.replace(/[A-Z]/g, (c) => c.toLowerCase());
}

function nocaseEq(a: string | null, b: string | null): boolean {
  return a !== null && b !== null && asciiLower(a) === asciiLower(b);
}

const nowSec = () => Math.floor(Date.now() / 1000);

// ── seeding helpers for tests ───────────────────────────────────────────────

export function addUser(state: FakeAuthState, email: string, createdAt = nowSec() - 86400): FakeUser {
  const user = { id: state.nextUserId++, email, created_at: createdAt, invite_code_used: null };
  state.users.push(user);
  return user;
}

export function addSession(
  state: FakeAuthState, tokenHash: string, userId: number, kind = "desktop", expiresAt = nowSec() + 3600,
): FakeSession {
  const s = { token_hash: tokenHash, user_id: userId, created_at: nowSec(), expires_at: expiresAt, kind };
  state.sessions.push(s);
  return s;
}

export function addLoginRequest(state: FakeAuthState, row: Partial<FakeLoginRequest> & { code: string; email: string }): FakeLoginRequest {
  const now = nowSec();
  const full = { purpose: "login", created_at: now, expires_at: now + 600, consumed: 0, attempts: 0, ...row };
  state.loginRequests.push(full);
  return full;
}

// ── statement models ────────────────────────────────────────────────────────

const ok = (value: unknown): AuthResult => ({ handled: true, value });
const changes = (n: number) => ({ success: true, meta: { changes: n } });

function userByEmail(state: FakeAuthState, email: string): FakeUser | undefined {
  return state.users.find((u) => nocaseEq(u.email, email));
}

function insertSession(state: FakeAuthState, s: FakeSession): void {
  if (state.sessions.some((x) => x.token_hash === s.token_hash)) throw new Error("UNIQUE constraint failed: sessions.token_hash");
  if (!state.users.some((u) => u.id === s.user_id)) throw new Error("FOREIGN KEY constraint failed");
  state.sessions.push(s);
}

function deleteWhere<T>(list: T[], pred: (x: T) => boolean): number {
  let n = 0;
  for (let i = list.length - 1; i >= 0; i--) {
    if (pred(list[i])) { list.splice(i, 1); n++; }
  }
  return n;
}

function deleteClipsWhere(state: FakeAuthState, pred: (c: FakeClip) => boolean): number {
  let n = 0;
  for (const c of [...state.clips.values()]) {
    if (!pred(c)) continue;
    state.clipTranscripts.delete(c.id); // ON DELETE CASCADE
    state.clips.delete(c.id);
    n++;
  }
  return n;
}

export function handleAuthFirst(state: FakeAuthState, sql: string, args: unknown[]): AuthResult {
  const [a1, a2, a3] = args;
  switch (sql) {
    case "SELECT id, email, created_at, invite_code_used FROM users WHERE email = ?1 COLLATE NOCASE LIMIT 1":
      return ok(userByEmail(state, a1 as string) ?? null);
    case "SELECT id, email, created_at, invite_code_used FROM users WHERE id = ?1 LIMIT 1":
      return ok(state.users.find((u) => u.id === a1) ?? null);
    case "INSERT INTO users (email, created_at, invite_code_used) VALUES (?1, ?2, ?3) RETURNING id": {
      if (userByEmail(state, a1 as string)) throw new Error("UNIQUE constraint failed: users.email");
      const user = { id: state.nextUserId++, email: a1 as string, created_at: a2 as number, invite_code_used: a3 as string | null };
      state.users.push(user);
      return ok({ id: user.id });
    }
    case "SELECT COUNT(*) AS n FROM login_requests WHERE email = ?1 AND created_at > ?2":
      return ok({ n: state.loginRequests.filter((r) => nocaseEq(r.email, a1 as string) && r.created_at > (a2 as number)).length });
    case "SELECT COUNT(*) AS n FROM login_requests WHERE email = ?1 AND purpose = 'web' AND created_at > ?2":
      return ok({
        n: state.loginRequests.filter((r) => nocaseEq(r.email, a1 as string) && r.purpose === "web" && r.created_at > (a2 as number)).length,
      });
    case "SELECT code, email, purpose, created_at, expires_at, consumed FROM login_requests WHERE code = ?1 LIMIT 1": {
      const r = state.loginRequests.find((x) => x.code === a1);
      return ok(r ? { code: r.code, email: r.email, purpose: r.purpose, created_at: r.created_at, expires_at: r.expires_at, consumed: r.consumed } : null);
    }
    case "SELECT token_hash, user_id, created_at, expires_at FROM sessions WHERE token_hash = ?1 AND expires_at > ?2 LIMIT 1": {
      // Desktop findSession: no kind filter, exactly like production.
      const s = state.sessions.find((x) => x.token_hash === a1 && x.expires_at > (a2 as number));
      return ok(s ? { token_hash: s.token_hash, user_id: s.user_id, created_at: s.created_at, expires_at: s.expires_at } : null);
    }
    case "SELECT user_id, expires_at FROM sessions WHERE token_hash = ?1 AND kind = 'web' AND expires_at > ?2 LIMIT 1": {
      const s = state.sessions.find((x) => x.token_hash === a1 && x.kind === "web" && x.expires_at > (a2 as number));
      return ok(s ? { user_id: s.user_id, expires_at: s.expires_at } : null);
    }
    case "UPDATE login_requests SET consumed = 1 WHERE code = ?1 AND email = ?2 AND purpose = 'web' AND consumed = 0 AND attempts <= 5 AND expires_at > ?3 RETURNING email": {
      const r = state.loginRequests.find((x) => x.code === a1 && nocaseEq(x.email, a2 as string) && x.purpose === "web"
        && x.consumed === 0 && x.attempts <= 5 && x.expires_at > (a3 as number));
      if (!r) return ok(null);
      r.consumed = 1;
      return ok({ email: r.email });
    }
  }
  return { handled: false };
}

export function handleAuthRun(state: FakeAuthState, sql: string, args: unknown[]): AuthResult {
  const [a1, a2, a3, a4, a5, a6] = args;
  switch (sql) {
    case "INSERT INTO login_requests (code, email, purpose, created_at, expires_at, consumed) VALUES (?1, ?2, ?3, ?4, ?5, 0)": {
      if (state.loginRequests.some((r) => r.code === a1)) throw new Error("UNIQUE constraint failed: login_requests.code");
      state.loginRequests.push({
        code: a1 as string, email: a2 as string, purpose: a3 as string, created_at: a4 as number, expires_at: a5 as number, consumed: 0, attempts: 0,
      });
      return ok(changes(1));
    }
    case "INSERT INTO login_requests (code, email, purpose, created_at, expires_at, consumed, attempts) SELECT ?1, ?2, 'web', ?3, ?4, 0, 0 WHERE (SELECT COUNT(*) FROM login_requests WHERE email = ?2 AND created_at > ?5) < 3 AND NOT EXISTS (SELECT 1 FROM login_requests WHERE email = ?2 AND purpose = 'web' AND created_at > ?6)": {
      const mine = state.loginRequests.filter((r) => nocaseEq(r.email, a2 as string));
      const windowCount = mine.filter((r) => r.created_at > (a5 as number)).length;
      const recentWeb = mine.some((r) => r.purpose === "web" && r.created_at > (a6 as number));
      if (!(windowCount < 3) || recentWeb) return ok(changes(0));
      if (state.loginRequests.some((r) => r.code === a1)) throw new Error("UNIQUE constraint failed: login_requests.code");
      state.loginRequests.push({
        code: a1 as string, email: a2 as string, purpose: "web", created_at: a3 as number, expires_at: a4 as number, consumed: 0, attempts: 0,
      });
      return ok(changes(1));
    }
    case "UPDATE login_requests SET consumed = 1 WHERE code = ?1 AND consumed = 0": {
      const r = state.loginRequests.find((x) => x.code === a1 && x.consumed === 0);
      if (r) r.consumed = 1;
      return ok(changes(r ? 1 : 0));
    }
    case "UPDATE login_requests SET attempts = attempts + 1 WHERE email = ?1 AND purpose = 'web' AND consumed = 0 AND expires_at > ?2": {
      let n = 0;
      for (const r of state.loginRequests) {
        if (nocaseEq(r.email, a1 as string) && r.purpose === "web" && r.consumed === 0 && r.expires_at > (a2 as number)) { r.attempts++; n++; }
      }
      return ok(changes(n));
    }
    case "DELETE FROM login_requests WHERE code = ?1":
      return ok(changes(deleteWhere(state.loginRequests, (r) => r.code === a1)));
    case "DELETE FROM login_requests WHERE expires_at <= ?1":
      return ok(changes(deleteWhere(state.loginRequests, (r) => r.expires_at <= (a1 as number))));
    case "INSERT INTO sessions (token_hash, user_id, created_at, expires_at) VALUES (?1, ?2, ?3, ?4)":
      // kind takes its column default, 'desktop'.
      insertSession(state, { token_hash: a1 as string, user_id: a2 as number, created_at: a3 as number, expires_at: a4 as number, kind: "desktop" });
      return ok(changes(1));
    case "INSERT INTO sessions (token_hash, user_id, created_at, expires_at, kind) VALUES (?1, ?2, ?3, ?4, 'web')":
      insertSession(state, { token_hash: a1 as string, user_id: a2 as number, created_at: a3 as number, expires_at: a4 as number, kind: "web" });
      return ok(changes(1));
    case "DELETE FROM sessions WHERE token_hash = ?1":
      return ok(changes(deleteWhere(state.sessions, (s) => s.token_hash === a1)));
    case "DELETE FROM sessions WHERE token_hash = ?1 AND kind = 'web'":
      return ok(changes(deleteWhere(state.sessions, (s) => s.token_hash === a1 && s.kind === "web")));
    case "DELETE FROM sessions WHERE user_id = ?1 AND kind = 'web'":
      return ok(changes(deleteWhere(state.sessions, (s) => s.user_id === a1 && s.kind === "web")));
    case "DELETE FROM sessions WHERE expires_at <= ?1":
      return ok(changes(deleteWhere(state.sessions, (s) => s.expires_at <= (a1 as number))));
    case "INSERT INTO identities (user_id, provider, subject, email, created_at, last_used_at) VALUES (?1, 'email', lower(?2), ?2, ?3, ?3) ON CONFLICT(provider, subject) DO UPDATE SET last_used_at = excluded.last_used_at": {
      if (!state.users.some((u) => u.id === a1)) throw new Error("FOREIGN KEY constraint failed");
      const subject = asciiLower(a2 as string);
      const conflict = state.identities.find((i) => i.provider === "email" && i.subject === subject);
      if (conflict) {
        conflict.last_used_at = a3 as number;
        return ok(changes(1));
      }
      if (state.identities.some((i) => i.user_id === a1 && i.provider === "email")) {
        throw new Error("UNIQUE constraint failed: identities.user_id");
      }
      state.identities.push({
        id: state.nextIdentityId++, user_id: a1 as number, provider: "email", subject, email: a2 as string,
        display_name: null, created_at: a3 as number, last_used_at: a3 as number,
      });
      return ok(changes(1));
    }
    // ── deleteAccountRows (one batch) ──
    case "DELETE FROM clip_transcripts WHERE clip_id IN (SELECT id FROM clips WHERE user_id = ?1)": {
      let n = 0;
      for (const c of state.clips.values()) if (c.user_id === a1 && state.clipTranscripts.delete(c.id)) n++;
      return ok(changes(n));
    }
    case "DELETE FROM clips WHERE user_id = ?1":
      return ok(changes(deleteClipsWhere(state, (c) => c.user_id === a1)));
    case "DELETE FROM sessions WHERE user_id = ?1":
      return ok(changes(deleteWhere(state.sessions, (s) => s.user_id === a1)));
    case "DELETE FROM identities WHERE user_id = ?1":
      return ok(changes(deleteWhere(state.identities, (i) => i.user_id === a1)));
    case "DELETE FROM transcribe_usage WHERE user_id = ?1": {
      let n = 0;
      for (const key of [...state.transcribeUsage.keys()]) {
        if (key.split("|")[0] === String(a1)) { state.transcribeUsage.delete(key); n++; }
      }
      return ok(changes(n));
    }
    case "DELETE FROM login_requests WHERE email = ?2":
      return ok(changes(deleteWhere(state.loginRequests, (r) => nocaseEq(r.email, a2 as string))));
    case "DELETE FROM users WHERE id = ?1": {
      const n = deleteWhere(state.users, (u) => u.id === a1);
      // FK ON DELETE CASCADE from sessions, clips (and their transcripts) and identities.
      deleteWhere(state.sessions, (s) => s.user_id === a1);
      deleteWhere(state.identities, (i) => i.user_id === a1);
      deleteClipsWhere(state, (c) => c.user_id === a1);
      return ok(changes(n));
    }
  }
  return { handled: false };
}

export function handleAuthAll(state: FakeAuthState, sql: string, args: unknown[]): AuthResult {
  const [a1] = args;
  switch (sql) {
    case "SELECT provider, email, display_name, created_at, last_used_at FROM identities WHERE user_id = ?1 ORDER BY id":
      return ok({
        results: state.identities
          .filter((i) => i.user_id === a1)
          .sort((x, y) => x.id - y.id)
          .map((i) => ({ provider: i.provider, email: i.email, display_name: i.display_name, created_at: i.created_at, last_used_at: i.last_used_at })),
      });
    case "SELECT id, r2_key, upload_id FROM clips WHERE user_id = ?1":
      return ok({
        results: [...state.clips.values()].filter((c) => c.user_id === a1).map((c) => ({ id: c.id, r2_key: c.r2_key, upload_id: c.upload_id })),
      });
  }
  return { handled: false };
}

/**
 * D1 batch semantics: every statement runs in one transaction, so a failure
 * (or an unmodelled statement) rolls the whole batch back.
 */
export function handleAuthBatch(state: FakeAuthState, statements: Array<{ sql: string; args: unknown[] }>): AuthResult {
  const snapshot = {
    users: state.users.map((u) => ({ ...u })),
    loginRequests: state.loginRequests.map((r) => ({ ...r })),
    identities: state.identities.map((i) => ({ ...i })),
    sessions: state.sessions.map((s) => ({ ...s })),
    clips: new Map(state.clips),
    clipTranscripts: new Map(state.clipTranscripts),
    transcribeUsage: new Map(state.transcribeUsage),
  };
  const restore = () => {
    state.users.splice(0, state.users.length, ...snapshot.users);
    state.loginRequests.splice(0, state.loginRequests.length, ...snapshot.loginRequests);
    state.identities.splice(0, state.identities.length, ...snapshot.identities);
    state.sessions.splice(0, state.sessions.length, ...snapshot.sessions);
    for (const [name, saved] of [["clips", snapshot.clips], ["clipTranscripts", snapshot.clipTranscripts], ["transcribeUsage", snapshot.transcribeUsage]] as const) {
      const live = state[name] as Map<string, unknown>;
      live.clear();
      for (const [k, v] of saved as Map<string, unknown>) live.set(k, v);
    }
  };
  const results: unknown[] = [];
  try {
    for (const st of statements) {
      const r = handleAuthRun(state, st.sql, st.args);
      if (!r.handled) {
        restore();
        return { handled: false };
      }
      results.push(r.value);
    }
  } catch (err) {
    restore();
    throw err;
  }
  return ok(results);
}
