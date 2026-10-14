/**
 * Fixtures for the full-game (VOD) share tests: a real-SQLite D1 with every
 * migration applied, the fake R2, seeded users/sessions/allowlist, and request
 * helpers for every VOD route. No personal data: users are example.com.
 */

import worker from "../src/index";
import { Env } from "../src/types";
import { makeEnv, makeFakeR2, makeRequest, mp4Bytes, sha256Hex } from "./fakes";
import { makeSqliteD1, SqliteD1 } from "./sqlite-d1";

export const BASE = "https://clips.revu.lol";
export const MiB = 1024 * 1024;

export interface VodSetup {
  db: SqliteD1;
  r2: ReturnType<typeof makeFakeR2>;
  env: Env;
  /** user id per seeded token */
  users: Record<string, number>;
}

export function nowSec(): number {
  return Math.floor(Date.now() / 1000);
}

/**
 * Seed one user + desktop session per token. Tokens listed in `allow` get a
 * vod_sharers row. Budgets default to the production values unless overridden.
 */
export async function vodSetup(
  opts: { tokens?: string[]; allow?: string[]; env?: Partial<Env> } = {},
): Promise<VodSetup> {
  const db = makeSqliteD1();
  const r2 = makeFakeR2();
  const tokens = opts.tokens ?? ["sess-owner", "sess-other"];
  const allow = new Set(opts.allow ?? ["sess-owner"]);
  const users: Record<string, number> = {};
  const now = nowSec();
  let n = 0;
  for (const token of tokens) {
    n++;
    db.x("INSERT INTO users (email, created_at) VALUES (?1, ?2)", `user${n}@example.com`, now - 86400);
    const id = db.q<{ id: number }>("SELECT id FROM users WHERE email = ?1", `user${n}@example.com`)[0].id;
    users[token] = id;
    db.x("INSERT INTO sessions (token_hash, user_id, created_at, expires_at) VALUES (?1, ?2, ?3, ?4)", await sha256Hex(token), id, now, now + 86400);
    if (allow.has(token)) db.x("INSERT INTO vod_sharers (user_id, added_at) VALUES (?1, ?2)", id, now);
  }
  const env = makeEnv({
    DB: db,
    CLIPS: r2.bucket,
    TOTAL_BUDGET_BYTES: undefined,
    VOD_BUDGET_BYTES: undefined,
    USER_CLIP_BUDGET_BYTES: undefined,
    ...(opts.env ?? {}),
  });
  return { db, r2, env, users };
}

const auth = (token: string) => ({ Authorization: `Bearer ${token}` });

export function vodInit(
  env: Env,
  token: string,
  q: { size?: number | string; duration?: number | string; quality?: string; type?: string } = {},
): Promise<Response> {
  const params = new URLSearchParams();
  if (q.size !== undefined) params.set("size", String(q.size));
  if (q.duration !== undefined) params.set("duration", String(q.duration));
  if (q.quality !== undefined) params.set("quality", q.quality);
  return worker.fetch(
    makeRequest(`${BASE}/vod-shares/uploads?${params}`, {
      method: "POST",
      headers: { ...auth(token), "Content-Type": q.type ?? "video/mp4", "Content-Length": "0" },
    }),
    env,
  );
}

/** A good init: 1 part of `size` bytes, 30 minutes, 1080p. */
export function vodInitOk(env: Env, token: string, size = 500): Promise<Response> {
  return vodInit(env, token, { size, duration: 1800, quality: "1080p" });
}

export function vodPart(env: Env, token: string, id: string, n: number, bytes: Uint8Array): Promise<Response> {
  return worker.fetch(
    makeRequest(`${BASE}/vod-shares/${id}/parts/${n}`, {
      method: "PUT",
      headers: { ...auth(token), "Content-Type": "application/octet-stream", "Content-Length": String(bytes.byteLength) },
      body: bytes,
    }),
    env,
  );
}

export function vodComplete(env: Env, token: string, id: string, parts: unknown[]): Promise<Response> {
  const body = JSON.stringify({ parts });
  return worker.fetch(
    makeRequest(`${BASE}/vod-shares/${id}/complete`, {
      method: "POST",
      headers: { ...auth(token), "Content-Type": "application/json", "Content-Length": String(body.length) },
      body,
    }),
    env,
  );
}

export function vodMarkers(env: Env, token: string, id: string, doc: unknown): Promise<Response> {
  const body = typeof doc === "string" ? doc : JSON.stringify(doc);
  return worker.fetch(
    makeRequest(`${BASE}/vod-shares/${id}/markers`, {
      method: "PUT",
      headers: { ...auth(token), "Content-Type": "application/json", "Content-Length": String(new TextEncoder().encode(body).byteLength) },
      body,
    }),
    env,
  );
}

export function vodStatus(env: Env, token: string): Promise<Response> {
  return worker.fetch(makeRequest(`${BASE}/vod-shares/status`, { headers: auth(token) }), env);
}

export function get(env: Env, path: string, init: RequestInit = {}): Promise<Response> {
  return worker.fetch(makeRequest(`${BASE}${path}`, init), env);
}

export function del(env: Env, token: string, id: string): Promise<Response> {
  return worker.fetch(makeRequest(`${BASE}/clips/${id}`, { method: "DELETE", headers: auth(token) }), env);
}

/** `size` bytes that pass the mp4 sniff. */
export function payload(size: number): Uint8Array {
  const bytes = new Uint8Array(size);
  bytes.set(mp4Bytes().slice(0, Math.min(16, size)), 0);
  for (let i = 16; i < size; i += 4096) bytes[i] = i % 251;
  return bytes;
}

/** init + every part + complete. Returns the id and the init body. */
export async function shareVod(
  s: VodSetup,
  token = "sess-owner",
  size = 500,
): Promise<{ id: string; init: Record<string, unknown>; complete: Record<string, unknown> }> {
  const res = await vodInitOk(s.env, token, size);
  if (res.status !== 201) throw new Error(`init failed: ${res.status} ${await res.text()}`);
  const init = (await res.json()) as Record<string, unknown>;
  const id = init.id as string;
  const partSize = init.part_size as number;
  const bytes = payload(size);
  const parts: { part_number: number; etag: string }[] = [];
  for (let n = 1, off = 0; off < size; n++, off += partSize) {
    const p = await vodPart(s.env, token, id, n, bytes.slice(off, off + partSize));
    if (p.status !== 200) throw new Error(`part failed: ${p.status} ${await p.text()}`);
    const out = (await p.json()) as { part_number: number; etag: string };
    parts.push(out);
  }
  const done = await vodComplete(s.env, token, id, parts);
  if (done.status !== 201) throw new Error(`complete failed: ${done.status} ${await done.text()}`);
  return { id, init, complete: (await done.json()) as Record<string, unknown> };
}

export async function body(res: Response): Promise<Record<string, unknown>> {
  return (await res.json()) as Record<string, unknown>;
}

export const MARKERS_DOC = {
  version: 1,
  gameTimeAtVideoStart: 12.345,
  game: { champion: "Jinx", opponentChampion: "Caitlyn", role: "bottom", result: "win", durationS: 1834, playedAt: "2026-10-12" },
  markers: [
    { t: 312.04, kind: "DRAGON", label: "Dragon", tone: "gold" },
    { t: 95, kind: "KILL", label: "First blood", tone: "win" },
  ],
  notes: null,
};
