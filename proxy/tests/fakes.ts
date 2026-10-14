/**
 * Shared in-memory fakes for the clip, upload and transcript tests.
 *
 * Fake D1 dispatches on SQL substrings, so every statement the Worker runs must
 * have a matching branch here (kept in lockstep with src/). An unknown statement
 * THROWS, so a query drifting away from what the fake models fails loudly
 * instead of silently returning null.
 *
 * Fake R2 enforces the production constraints that matter for CPU safety:
 * put/uploadPart only accept a ReadableStream with a KNOWN length (a request
 * body sent with Content-Length, or a FixedLengthStream readable). A stream the
 * Worker wrapped in JS (TransformStream, custom ReadableStream, pipeThrough) is
 * rejected, exactly like the "must have a known length" failure in Workers.
 */

import { Env } from "../src/types";
import { PART_PUT_FACTOR, PART_PUT_SLACK } from "../src/share-limits";
import { makeFakeR2 } from "./fake-r2";
import { FakeAuthState, handleAuthAll, handleAuthBatch, handleAuthFirst, handleAuthRun } from "./fake-auth";

export { knownLengthStreams, makeFakeAi, makeFakeR2, makeRequest } from "./fake-r2";

// ── D1 ──────────────────────────────────────────────────────────────────────

export interface FakeClip {
  id: string;
  user_id: number;
  r2_key: string;
  content_type: string;
  size_bytes: number;
  duration_s: number | null;
  title: string | null;
  champion: string | null;
  created_at: number;
  expires_at: number;
  view_count: number;
  status: string;
  upload_id: string | null;
  part_size: number | null;
  part_count: number | null;
  last_activity_at: number | null;
  narrated: number;
  has_transcript: number;
  // Migration 0006.
  kind: string;
  part_puts: number;
  quality: string | null;
  play_day: string | null;
  play_count: number;
}

export interface FakeTombstone {
  r2_key: string;
  upload_id: string | null;
  size_bytes: number;
  kind: string;
  created_at: number;
}

export interface FakeVodMeta {
  clip_id: string;
  meta_json: string;
  has_notes: number;
  created_at: number;
}

export interface FakeTranscript {
  clip_id: string;
  language: string | null;
  segments_json: string;
  vtt: string;
  segment_count: number;
  created_at: number;
}

export type FakeDb = D1Database & {
  _clips: Map<string, FakeClip>;
  _transcripts: Map<string, FakeTranscript>;
  /** storage_tombstones by r2_key. */
  _tombstones: Map<string, FakeTombstone>;
  /** vod_share_meta by clip_id. */
  _vodMeta: Map<string, FakeVodMeta>;
  /** vod_share_usage: "user|day" -> starts. */
  _vodUsage: Map<string, number>;
  /** upload_part_usage: "user|day" -> puts. */
  _partUsage: Map<string, number>;
  _usage: Map<string, { audio_seconds: number; requests: number }>;
  /** email_send_usage: day → sends. */
  _emailSends: Map<string, number>;
  _sql: string[];
  /** Make the next statement whose SQL contains `fragment` throw once. */
  /** Make a matching statement throw, after letting `skip` matches through. */
  _failNext(fragment: string, skip?: number): void;
};

const CLIP_DEFAULTS: Omit<FakeClip, "id" | "user_id" | "r2_key" | "content_type" | "size_bytes" | "created_at" | "expires_at"> = {
  duration_s: null,
  title: null,
  champion: null,
  view_count: 0,
  status: "ready",
  upload_id: null,
  part_size: null,
  part_count: null,
  last_activity_at: null,
  narrated: 0,
  has_transcript: 0,
  kind: "clip",
  part_puts: 0,
  quality: null,
  play_day: null,
  play_count: 0,
};

/** Map `INSERT INTO x (cols) VALUES (vals)` onto a record (?N, numbers, 'strings'). */
function parseInsert(sql: string, args: unknown[]): Record<string, unknown> {
  const m = /\(([^)]*)\)\s*VALUES\s*\(([^)]*)\)/s.exec(sql);
  if (!m) throw new Error(`fake D1: cannot parse insert: ${sql}`);
  const cols = m[1].split(",").map((c) => c.trim());
  const vals = m[2].split(",").map((v) => v.trim());
  if (cols.length !== vals.length) throw new Error("fake D1: column/value count mismatch");
  const out: Record<string, unknown> = {};
  cols.forEach((col, i) => {
    const v = vals[i];
    if (/^\?\d+$/.test(v)) out[col] = args[Number(v.slice(1)) - 1];
    else if (/^'.*'$/.test(v)) out[col] = v.slice(1, -1);
    else if (/^-?\d+$/.test(v)) out[col] = Number(v);
    else throw new Error(`fake D1: unsupported literal ${v}`);
  });
  return out;
}

/**
 * With `auth`, the auth-table statements (fake-auth.ts) are answered FIRST, ahead
 * of the generic "FROM sessions" branch, and share this DB's clip, transcript and
 * usage stores. Without it, behavior is unchanged.
 */
export function makeFakeDb(
  seedClips: Array<Partial<FakeClip> & { id: string }> = [],
  sessions: Record<string, number> = {},
  auth?: FakeAuthState,
): FakeDb {
  const clips = new Map<string, FakeClip>();
  for (const c of seedClips) clips.set(c.id, { ...CLIP_DEFAULTS, ...(c as FakeClip) });
  const transcripts = auth?.clipTranscripts ?? new Map<string, FakeTranscript>();
  const usage = auth?.transcribeUsage ?? new Map<string, { audio_seconds: number; requests: number }>();
  const tombstones = auth?.tombstones ?? new Map<string, FakeTombstone>();
  const vodMeta = auth?.vodShareMeta ?? new Map<string, FakeVodMeta>();
  const vodUsage = auth?.vodShareUsage ?? new Map<string, number>();
  const partUsage = auth?.uploadPartUsage ?? new Map<string, number>();
  if (auth) auth.clips = clips;
  const emailSends = new Map<string, number>();
  const sqlLog: string[] = [];
  const failures: { fragment: string; skip: number }[] = [];

  function maybeFail(sql: string): void {
    const i = failures.findIndex((f) => sql.includes(f.fragment));
    if (i < 0) return;
    if (failures[i].skip > 0) {
      failures[i].skip--;
      return;
    }
    failures.splice(i, 1);
    throw new Error(`fake D1: injected failure for ${sql.slice(0, 60)}`);
  }

  const live = (now: number) => [...clips.values()].filter((c) => c.expires_at > now);

  function prepare(sql: string) {
    let args: unknown[] = [];
    const api = {
      _sql: sql,
      get _args() { return args; },
      bind(...a: unknown[]) {
        if (a.some((v) => v === undefined)) throw new TypeError("D1_TYPE_ERROR: undefined is not a bindable value");
        if (a.length > 100) throw new Error("D1: too many SQL variables");
        args = a;
        return api;
      },
      async first<T>(): Promise<T | null> {
        sqlLog.push(sql);
        maybeFail(sql);
        const authed = auth ? handleAuthFirst(auth, sql, args) : null;
        if (authed?.handled) return authed.value as T | null;
        if (sql.includes("FROM sessions")) {
          const userId = sessions[args[0] as string];
          if (userId === undefined) return null;
          return { token_hash: args[0], user_id: userId, created_at: 0, expires_at: 9e9 } as T;
        }
        if (sql === "INSERT INTO email_send_usage (day, sends) VALUES (?1, 1) ON CONFLICT(day) DO UPDATE SET sends = sends + 1 RETURNING sends") {
          const sends = (emailSends.get(args[0] as string) ?? 0) + 1;
          emailSends.set(args[0] as string, sends);
          return { sends } as T;
        }
        if (sql === "INSERT INTO upload_part_usage (user_id, day, puts) VALUES (?1, ?2, 1) ON CONFLICT (user_id, day) DO UPDATE SET puts = puts + 1 WHERE puts < ?3 RETURNING puts") {
          const key = `${args[0]}|${args[1]}`;
          const cur = partUsage.get(key);
          if (cur !== undefined && cur >= (args[2] as number)) return null;
          const puts = (cur ?? 0) + 1;
          partUsage.set(key, puts);
          return { puts } as T;
        }
        if (sql === "SELECT 1 AS ok FROM vod_sharers WHERE user_id = ?1 LIMIT 1") {
          return auth?.vodSharers.has(args[0] as number) ? ({ ok: 1 } as T) : null;
        }
        if (sql.startsWith("INSERT INTO transcribe_usage") && sql.includes("RETURNING audio_seconds")) {
          const key = `${args[0]}|${args[1]}`;
          const row = usage.get(key) ?? { audio_seconds: 0, requests: 0 };
          row.audio_seconds += args[2] as number;
          row.requests += 1;
          usage.set(key, row);
          return { audio_seconds: row.audio_seconds } as T;
        }
        if (sql.includes("FROM storage_tombstones) AS b")) {
          // Global storage budget: every clips row (any kind, any status, expired
          // or not) plus every tombstone.
          const all = [...clips.values(), ...tombstones.values()];
          return {
            used: all.reduce((s, c) => s + c.size_bytes, 0),
            vod_used: all.filter((c) => c.kind === "vod").reduce((s, c) => s + c.size_bytes, 0),
          } as T;
        }
        if (sql.includes("COALESCE(SUM(size_bytes), 0) AS total FROM clips WHERE user_id = ?1 AND kind = 'clip' AND expires_at > ?2")) {
          const mine = live(args[1] as number).filter((c) => c.user_id === args[0] && c.kind === "clip");
          return { n: mine.length, total: mine.reduce((s, c) => s + c.size_bytes, 0) } as T;
        }
        if (sql.includes("SELECT COUNT(*) AS n FROM clips WHERE user_id = ?1 AND kind = 'clip' AND status = 'uploading' AND expires_at > ?2")) {
          const n = live(args[1] as number).filter((c) => c.user_id === args[0] && c.kind === "clip" && c.status === "uploading").length;
          return { n } as T;
        }
        if (sql.startsWith("SELECT id FROM clips WHERE id")) {
          return clips.has(args[0] as string) ? ({ id: args[0] } as T) : null;
        }
        if (sql.startsWith("SELECT language FROM clip_transcripts WHERE clip_id = ?1")) {
          const t = transcripts.get(args[0] as string);
          return t ? ({ language: t.language } as T) : null;
        }
        if (sql.includes("FROM clip_transcripts t JOIN clips c ON c.id = t.clip_id WHERE c.id = ?1 AND c.expires_at > ?2 AND c.status = 'ready' AND c.kind = 'clip'")) {
          const c = clips.get(args[0] as string);
          const t = transcripts.get(args[0] as string);
          if (!c || !t || c.expires_at <= (args[1] as number) || c.status !== "ready" || c.kind !== "clip") return null;
          return { language: t.language, segments_json: t.segments_json, vtt: t.vtt } as T;
        }
        if (sql.includes("FROM clips WHERE id = ?1 AND expires_at > ?2 AND status = 'ready' AND kind = 'clip' LIMIT 1")) {
          const c = clips.get(args[0] as string);
          return c && c.expires_at > (args[1] as number) && c.status === "ready" && c.kind === "clip" ? (c as T) : null;
        }
        if (sql.includes("FROM clips WHERE id = ?1 AND kind = 'vod' AND status = 'ready' AND expires_at > ?2 LIMIT 1")) {
          const c = clips.get(args[0] as string);
          return c && c.kind === "vod" && c.status === "ready" && c.expires_at > (args[1] as number) ? (c as T) : null;
        }
        if (sql.includes("FROM clips WHERE id = ?1 AND user_id = ?2 AND status = 'uploading' AND expires_at > ?3 AND kind = ?4 LIMIT 1")) {
          const c = clips.get(args[0] as string);
          return c && c.user_id === args[1] && c.status === "uploading" && c.expires_at > (args[2] as number) && c.kind === args[3]
            ? (c as T) : null;
        }
        if (sql.startsWith("UPDATE clips SET part_puts = part_puts + 1, last_activity_at = ?3 WHERE id = ?1 AND user_id = ?2 AND kind = ?4 AND status = 'uploading' AND expires_at > ?3")) {
          if (!sql.endsWith(`AND part_puts < ${PART_PUT_FACTOR} * part_count + ${PART_PUT_SLACK} RETURNING r2_key, upload_id`)) {
            throw new Error(`fake D1: unexpected part claim SQL: ${sql}`);
          }
          const c = clips.get(args[0] as string);
          if (!c || c.user_id !== args[1] || c.kind !== args[3] || c.status !== "uploading" || c.expires_at <= (args[2] as number)) return null;
          if (c.part_puts >= PART_PUT_FACTOR * (c.part_count ?? 0) + PART_PUT_SLACK) return null;
          c.part_puts += 1;
          c.last_activity_at = args[2] as number;
          return { r2_key: c.r2_key, upload_id: c.upload_id } as T;
        }
        if (sql.includes("FROM clips WHERE id = ?1 AND user_id = ?2 AND expires_at > ?3 AND status IN ('uploading', 'ready') AND kind = 'clip' LIMIT 1")) {
          const c = clips.get(args[0] as string);
          return c && c.user_id === args[1] && c.expires_at > (args[2] as number) && (c.status === "uploading" || c.status === "ready")
            && c.kind === "clip" ? (c as T) : null;
        }
        if (sql.includes("FROM clips WHERE id = ?1 AND user_id = ?2 AND expires_at > ?3 AND kind = ?4 LIMIT 1")) {
          const c = clips.get(args[0] as string);
          return c && c.user_id === args[1] && c.expires_at > (args[2] as number) && c.kind === args[3] ? (c as T) : null;
        }
        if (sql.startsWith("SELECT * FROM clips WHERE id = ?1 LIMIT")) {
          return (clips.get(args[0] as string) as T) ?? null;
        }
        if (sql.startsWith("SELECT * FROM clips WHERE id = ?1 AND kind = 'clip' LIMIT")) {
          const c = clips.get(args[0] as string);
          return c && c.kind === "clip" ? (c as T) : null;
        }
        if (sql.startsWith("UPDATE clips SET status = 'purging', expires_at = ?3 WHERE id = ?1 AND user_id = ?2 RETURNING r2_key, upload_id")) {
          const c = clips.get(args[0] as string);
          if (!c || c.user_id !== args[1]) return null;
          Object.assign(c, { status: "purging", expires_at: args[2] });
          return { r2_key: c.r2_key, upload_id: c.upload_id } as T;
        }
        if (sql.startsWith("UPDATE clips SET status = 'purging', expires_at = ?2 WHERE id = ?1 AND status = 'uploading' RETURNING r2_key, upload_id")) {
          const c = clips.get(args[0] as string);
          if (!c || c.status !== "uploading") return null;
          Object.assign(c, { status: "purging", expires_at: args[1] });
          return { r2_key: c.r2_key, upload_id: c.upload_id } as T;
        }
        if (sql.startsWith("SELECT COALESCE(SUM(length(t.segments_json)), 0) AS total FROM clip_transcripts t JOIN clips c ON c.id = t.clip_id WHERE c.user_id = ?1 AND c.expires_at > ?2 AND t.clip_id != ?3")) {
          let total = 0;
          for (const t of transcripts.values()) {
            const c = clips.get(t.clip_id);
            if (c && c.user_id === args[0] && c.expires_at > (args[1] as number) && t.clip_id !== args[2]) total += t.segments_json.length;
          }
          return { total } as T;
        }
        throw new Error(`fake D1: unhandled first() SQL: ${sql}`);
      },
      async run() {
        sqlLog.push(sql);
        maybeFail(sql);
        const authed = auth ? handleAuthRun(auth, sql, args) : null;
        if (authed?.handled) return authed.value as ReturnType<typeof changes>;
        const changes = (n: number) => ({ success: true, meta: { changes: n } });
        if (sql.startsWith("INSERT INTO clips")) {
          const rec = parseInsert(sql, args) as Partial<FakeClip> & { id: string };
          if (clips.has(rec.id)) throw new Error("UNIQUE constraint failed: clips.id");
          clips.set(rec.id, { ...CLIP_DEFAULTS, ...(rec as FakeClip) });
          return changes(1);
        }
        if (sql.startsWith("INSERT INTO clip_transcripts") && sql.includes("ON CONFLICT(clip_id) DO UPDATE")) {
          const rec = parseInsert(sql.split("ON CONFLICT")[0], args) as unknown as FakeTranscript;
          if (!clips.has(rec.clip_id)) throw new Error("FOREIGN KEY constraint failed");
          transcripts.set(rec.clip_id, rec);
          return changes(1);
        }
        if (sql === "INSERT OR IGNORE INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at) VALUES (?1, ?2, ?3, ?4, ?5)") {
          if (tombstones.has(args[0] as string)) return changes(0);
          tombstones.set(args[0] as string, {
            r2_key: args[0] as string, upload_id: args[1] as string | null, size_bytes: args[2] as number,
            kind: args[3] as string, created_at: args[4] as number,
          });
          return changes(1);
        }
        if (sql === "UPDATE clips SET status = 'purging', expires_at = ?2 WHERE id = ?1") {
          const c = clips.get(args[0] as string);
          if (c) Object.assign(c, { status: "purging", expires_at: args[1] });
          return changes(c ? 1 : 0);
        }
        if (sql === "DELETE FROM upload_part_usage WHERE day < ?1") {
          let n = 0;
          for (const key of [...partUsage.keys()]) {
            if (key.split("|")[1] < (args[0] as string)) { partUsage.delete(key); n++; }
          }
          return changes(n);
        }
        if (sql.startsWith("UPDATE clips SET view_count = view_count + 1 WHERE id = ?1")) {
          const c = clips.get(args[0] as string);
          if (c) c.view_count += 1;
          return changes(c ? 1 : 0);
        }
        if (sql.startsWith("UPDATE clips SET last_activity_at = ?1 WHERE id = ?2")) {
          const c = clips.get(args[1] as string);
          if (c) c.last_activity_at = args[0] as number;
          return changes(c ? 1 : 0);
        }
        if (sql.startsWith("UPDATE clips SET status = 'ready', upload_id = NULL, created_at = ?3, expires_at = ?4 WHERE id = ?1 AND user_id = ?2 AND status = 'uploading'")) {
          const c = clips.get(args[0] as string);
          if (!c || c.user_id !== args[1] || c.status !== "uploading") return changes(0);
          Object.assign(c, { status: "ready", upload_id: null, created_at: args[2], expires_at: args[3] });
          return changes(1);
        }
        const flag = /^UPDATE clips SET has_transcript = ([01]) WHERE id = \?1$/.exec(sql);
        if (flag) {
          const c = clips.get(args[0] as string);
          if (c) c.has_transcript = Number(flag[1]);
          return changes(c ? 1 : 0);
        }
        if (sql.startsWith("UPDATE transcribe_usage SET audio_seconds = audio_seconds - ?3, requests = requests - 1 WHERE user_id = ?1 AND day = ?2")) {
          const row = usage.get(`${args[0]}|${args[1]}`);
          if (row) { row.audio_seconds -= args[2] as number; row.requests -= 1; }
          return changes(row ? 1 : 0);
        }
        if (sql.startsWith("DELETE FROM clip_transcripts WHERE clip_id IN (")) {
          let n = 0;
          for (const id of args) if (transcripts.delete(id as string)) n++;
          return changes(n);
        }
        if (sql.startsWith("DELETE FROM clip_transcripts WHERE clip_id = ?1")) {
          return changes(transcripts.delete(args[0] as string) ? 1 : 0);
        }
        if (sql.startsWith("DELETE FROM vod_share_meta WHERE clip_id IN (")) {
          let n = 0;
          for (const id of args) if (vodMeta.delete(id as string)) n++;
          return changes(n);
        }
        if (sql === "DELETE FROM vod_share_meta WHERE clip_id = ?1") {
          return changes(vodMeta.delete(args[0] as string) ? 1 : 0);
        }
        if (sql.startsWith("DELETE FROM storage_tombstones WHERE r2_key IN (")) {
          let n = 0;
          for (const key of args) if (tombstones.delete(key as string)) n++;
          return changes(n);
        }
        if (sql === "DELETE FROM vod_share_usage WHERE day < ?1") {
          let n = 0;
          for (const key of [...vodUsage.keys()]) {
            if (key.split("|")[1] < (args[0] as string)) { vodUsage.delete(key); n++; }
          }
          return changes(n);
        }
        if (sql.startsWith("DELETE FROM clips WHERE id IN (")) {
          let n = 0;
          for (const id of args) {
            if (transcripts.has(id as string)) throw new Error("fake D1: transcript must be deleted before its clip");
            if (vodMeta.has(id as string)) throw new Error("fake D1: VOD meta must be deleted before its clip");
            if (clips.delete(id as string)) n++;
          }
          return changes(n);
        }
        if (sql.startsWith("DELETE FROM clips WHERE id = ?1")) {
          return changes(clips.delete(args[0] as string) ? 1 : 0);
        }
        if (sql.startsWith("DELETE FROM sessions WHERE expires_at <= ?1")) return changes(0);
        if (sql === "DELETE FROM sessions WHERE token_hash = ?1") {
          const had = (args[0] as string) in sessions;
          delete sessions[args[0] as string];
          return changes(had ? 1 : 0);
        }
        if (sql.startsWith("DELETE FROM login_requests WHERE expires_at <= ?1")) return changes(0);
        if (sql === "UPDATE email_send_usage SET sends = sends - 1 WHERE day = ?1 AND sends > 0") {
          const sends = emailSends.get(args[0] as string) ?? 0;
          if (sends > 0) emailSends.set(args[0] as string, sends - 1);
          return changes(sends > 0 ? 1 : 0);
        }
        if (sql === "DELETE FROM email_send_usage WHERE day < ?1") {
          let n = 0;
          for (const day of [...emailSends.keys()]) {
            if (day < (args[0] as string)) { emailSends.delete(day); n++; }
          }
          return changes(n);
        }
        if (sql.startsWith("DELETE FROM transcribe_usage WHERE day < ?1")) {
          let n = 0;
          for (const key of [...usage.keys()]) {
            if (key.split("|")[1] < (args[0] as string)) { usage.delete(key); n++; }
          }
          return changes(n);
        }
        throw new Error(`fake D1: unhandled run() SQL: ${sql}`);
      },
      async all<T>(): Promise<{ results: T[] }> {
        sqlLog.push(sql);
        maybeFail(sql);
        const authed = auth ? handleAuthAll(auth, sql, args) : null;
        if (authed?.handled) return authed.value as { results: T[] };
        if (sql.includes("FROM clips WHERE user_id = ?1 AND expires_at > ?2 ORDER BY created_at DESC")) {
          const results = live(args[1] as number)
            .filter((c) => c.user_id === args[0])
            .sort((a, b) => b.created_at - a.created_at);
          return { results: results as T[] };
        }
        const idle = /^SELECT id FROM clips WHERE user_id = \?1 AND status = 'uploading' AND kind = '(clip|vod)' AND COALESCE\(last_activity_at, created_at\) <= \?2$/.exec(sql);
        if (idle) {
          const results = [...clips.values()]
            .filter((c) => c.user_id === args[0] && c.status === "uploading" && c.kind === idle[1]
              && (c.last_activity_at ?? c.created_at) <= (args[1] as number))
            .map((c) => ({ id: c.id }));
          return { results: results as T[] };
        }
        const sweep = /^SELECT id FROM clips WHERE status = 'uploading' AND COALESCE\(last_activity_at, created_at\) <= \?1 LIMIT (\d+)$/.exec(sql);
        if (sweep) {
          const results = [...clips.values()]
            .filter((c) => c.status === "uploading" && (c.last_activity_at ?? c.created_at) <= (args[0] as number))
            .slice(0, Number(sweep[1]))
            .map((c) => ({ id: c.id }));
          return { results: results as T[] };
        }
        if (sql.startsWith("SELECT clip_id, has_notes FROM vod_share_meta WHERE clip_id IN (")) {
          const results = args.map((id) => vodMeta.get(id as string)).filter((m): m is FakeVodMeta => !!m)
            .map((m) => ({ clip_id: m.clip_id, has_notes: m.has_notes }));
          return { results: results as T[] };
        }
        const tombs = /^SELECT r2_key, upload_id, created_at FROM storage_tombstones LIMIT (\d+)$/.exec(sql);
        if (tombs) {
          const results = [...tombstones.values()].slice(0, Number(tombs[1]))
            .map((t) => ({ r2_key: t.r2_key, upload_id: t.upload_id, created_at: t.created_at }));
          return { results: results as T[] };
        }
        const claim = /^UPDATE clips SET status = 'purging' WHERE id IN \((\?\d+(?:, \?\d+)*)\) AND expires_at <= \?(\d+) RETURNING id, r2_key, upload_id, created_at$/.exec(sql);
        if (claim) {
          const n = claim[1].split(", ").length;
          if (Number(claim[2]) !== n + 1 || args.length !== n + 1) throw new Error("fake D1: claim placeholder mismatch");
          const now = args[n] as number;
          const results: { id: string; r2_key: string; upload_id: string | null; created_at: number }[] = [];
          for (const id of args.slice(0, n)) {
            const c = clips.get(id as string);
            if (!c || c.expires_at > now) continue;
            c.status = "purging";
            results.push({ id: c.id, r2_key: c.r2_key, upload_id: c.upload_id, created_at: c.created_at });
          }
          return { results: results as T[] };
        }
        const expired = /^SELECT id, r2_key, upload_id FROM clips WHERE expires_at <= \?1 LIMIT (\d+)$/.exec(sql);
        if (expired) {
          const results = [...clips.values()]
            .filter((c) => c.expires_at <= (args[0] as number))
            .slice(0, Number(expired[1]))
            .map((c) => ({ id: c.id, r2_key: c.r2_key, upload_id: c.upload_id }));
          return { results: results as T[] };
        }
        throw new Error(`fake D1: unhandled all() SQL: ${sql}`);
      },
    };
    return api;
  }

  // D1 batch: one transaction. Injected failures fire before anything runs.
  async function batch(statements: Array<ReturnType<typeof prepare>>): Promise<unknown[]> {
    if (!auth) {
      const out: unknown[] = [];
      for (const st of statements) out.push(await st.run());
      return out;
    }
    for (const st of statements) { sqlLog.push(st._sql); maybeFail(st._sql); }
    const r = handleAuthBatch(auth, statements.map((st) => ({ sql: st._sql, args: st._args })));
    if (r.handled) return r.value as unknown[];
    throw new Error(`fake D1: unhandled batch SQL: ${statements.map((st) => st._sql).join(" | ")}`);
  }

  return {
    prepare,
    batch,
    _clips: clips,
    _transcripts: transcripts,
    _tombstones: tombstones,
    _vodMeta: vodMeta,
    _vodUsage: vodUsage,
    _partUsage: partUsage,
    _usage: usage,
    _emailSends: emailSends,
    _sql: sqlLog,
    _failNext(fragment: string, skip = 0) { failures.push({ fragment, skip }); },
  } as unknown as FakeDb;
}

// ── misc helpers ────────────────────────────────────────────────────────────

/**
 * Default test Env. The storage budgets (and one account's share of them)
 * are set far above anything a test stores, so the per-user clip quota tests
 * stay about per-user quotas; tests of the global budget pass
 * TOTAL_BUDGET_BYTES / VOD_BUDGET_BYTES / USER_CLIP_BUDGET_BYTES explicitly
 * (or `undefined` for the production defaults).
 */
export function makeEnv(overrides: Partial<Env> = {}): Env {
  return {
    TOTAL_BUDGET_BYTES: "9999999999999",
    VOD_BUDGET_BYTES: "9999999999999",
    USER_CLIP_BUDGET_BYTES: "9999999999999",
    RIOT_API_KEY: "riot-key",
    RESEND_API_KEY: "",
    AGGREGATE_RPS: "100",
    PER_TOKEN_RPS: "20",
    MAGIC_LINK_FROM: "noreply@example.com",
    APP_NAME: "Revu",
    PUBLIC_BASE: "https://clips.revu.lol",
    WATCH_BASE: "https://revu.lol",
    ALLOWED_ORIGINS: "https://revu.lol",
    DB: makeFakeDb(),
    CLIPS: makeFakeR2().bucket,
    ...overrides,
  };
}

export async function json(response: Response): Promise<Record<string, unknown>> {
  return (await response.json()) as Record<string, unknown>;
}

/** Minimal bytes that pass the mp4 (ISO BMFF "ftyp") magic-byte sniff. */
export function mp4Bytes(extra = 0): Uint8Array {
  const head = [0, 0, 0, 16, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6f, 0x6d, 0, 0, 0, 1];
  return new Uint8Array([...head, ...new Array(extra).fill(0)]);
}

export function nowSec(): number {
  return Math.floor(Date.now() / 1000);
}

export function clip(over: Partial<FakeClip> = {}): FakeClip {
  const now = nowSec();
  return {
    ...CLIP_DEFAULTS,
    id: "abc1234",
    user_id: 1,
    r2_key: "clips/abc1234.mp4",
    content_type: "video/mp4",
    size_bytes: 10,
    duration_s: 5,
    title: "nice play",
    champion: "Ahri",
    created_at: now,
    expires_at: now + 1000,
    ...over,
  };
}

/** sha256 helper mirroring src/crypto.ts so tests can seed session hashes. */
export async function sha256Hex(input: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(input));
  return Array.from(new Uint8Array(digest)).map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** A fake DB with one session per token: { token: userId }. */
export async function dbWithSessions(
  tokens: Record<string, number>,
  seedClips: Array<Partial<FakeClip> & { id: string }> = [],
): Promise<FakeDb> {
  const sessions: Record<string, number> = {};
  for (const [token, userId] of Object.entries(tokens)) sessions[await sha256Hex(token)] = userId;
  return makeFakeDb(seedClips, sessions);
}

/** Run the Worker's scheduled handler and wait for its waitUntil work. */
export async function runScheduled(
  worker: { scheduled(c: ScheduledController, e: Env, x: ExecutionContext): Promise<void> },
  env: Env,
): Promise<void> {
  const pending: Promise<unknown>[] = [];
  const ctx = { waitUntil(p: Promise<unknown>) { pending.push(p); }, passThroughOnException() {}, props: {} };
  await worker.scheduled({} as ScheduledController, env, ctx as unknown as ExecutionContext);
  await Promise.all(pending);
}
