/**
 * Fixtures for the HLS share tests: a real-SQLite D1 (every migration, 0007
 * included), the fake R2, HLS switched on with a play secret, request helpers
 * for every HLS route, fMP4-shaped bytes, and counters for D1 statements and
 * R2 calls. No personal data: users are example.com.
 */

import worker from "../src/index";
import { Env } from "../src/types";
import { resetOpsFlags } from "../src/ops-flags";
import { resetHlsServeCaches } from "../src/vod-hls-serve";
import { resetUserLimits } from "../src/user-limits";
import { makeRequest } from "./fakes";
import { BASE, VodSetup, nowSec, vodSetup } from "./vod-fixtures";

export { BASE, nowSec };
export const OWNER = "sess-owner";
export const OTHER = "sess-other";
export const SITE = "https://revu.lol";
export const SECRET = "test-play-secret-0123456789abcdef-0123";
export const MiB = 1024 * 1024;

const auth = (token: string) => ({ Authorization: `Bearer ${token}` });

/** Forget every per-isolate cache the HLS code keeps (ops flags, share state, inits, limiters). */
export function resetHlsState(): void {
  resetOpsFlags();
  resetHlsServeCaches();
  resetUserLimits();
}

export async function hlsSetup(opts: Parameters<typeof vodSetup>[0] = {}): Promise<VodSetup> {
  resetHlsState();
  return vodSetup({ ...opts, env: { VOD_HLS: "1", VOD_PLAY_SECRET: SECRET, ...(opts.env ?? {}) } });
}

export function call(env: Env, path: string, init: RequestInit = {}, ctx?: ExecutionContext): Promise<Response> {
  return worker.fetch(makeRequest(`${BASE}${path}`, init), env, ctx);
}

export async function json(res: Response): Promise<Record<string, unknown>> {
  return (await res.json()) as Record<string, unknown>;
}

export function hlsInit(
  env: Env,
  token = OWNER,
  q: { size?: number; duration?: number; quality?: string; fps?: string | null; format?: string | null } = {},
): Promise<Response> {
  const p = new URLSearchParams({ size: String(q.size ?? 50 * MiB), duration: String(q.duration ?? 1800), quality: q.quality ?? "1080p" });
  if (q.fps !== null) p.set("fps", q.fps ?? "60");
  if (q.format !== null) p.set("format", q.format ?? "hls");
  return call(env, `/vod-shares/uploads?${p}`, {
    method: "POST",
    headers: { ...auth(token), "Content-Type": "video/mp4", "Content-Length": "0" },
  });
}

/** An init segment shape: ftyp (iso5) then moov; `variant` changes the bytes (another encoder). */
export function initBytes(size = 1408, variant = 0): Uint8Array {
  const b = new Uint8Array(size);
  b.set([0, 0, 0, 0x1c, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6f, 0x35], 0);
  if (size > 36) b.set([0, 0, 0x05, 0x64, 0x6d, 0x6f, 0x6f, 0x76], 28);
  b[size - 1] = variant;
  return b;
}

/** A media segment shape: moof then filler, distinct per seq. */
export function segBytes(seq: number, size = 4096): Uint8Array {
  const b = new Uint8Array(size);
  b.set([0, 0, 0, 0x18, 0x6d, 0x6f, 0x6f, 0x66], 0);
  b[size - 1] = seq % 251;
  return b;
}

export function putRun(
  env: Env,
  id: string,
  k: number,
  o: { from?: number; quality?: string; fps?: string; bytes?: Uint8Array; token?: string; length?: string | null } = {},
): Promise<Response> {
  const bytes = o.bytes ?? initBytes();
  const p = new URLSearchParams({ from: String(o.from ?? 0), quality: o.quality ?? "1080p", fps: o.fps ?? "60" });
  const headers: Record<string, string> = { ...auth(o.token ?? OWNER), "Content-Type": "video/mp4" };
  if (o.length !== null) headers["Content-Length"] = o.length ?? String(bytes.byteLength);
  return call(env, `/vod-shares/${id}/hls/runs/${k}?${p}`, { method: "PUT", headers, body: bytes });
}

export function putSeg(
  env: Env,
  id: string,
  seq: number,
  o: { ms?: number; run?: number; bytes?: Uint8Array; token?: string; ctx?: ExecutionContext; length?: string | null; query?: string } = {},
): Promise<Response> {
  const bytes = o.bytes ?? segBytes(seq);
  const headers: Record<string, string> = { ...auth(o.token ?? OWNER), "Content-Type": "video/mp4" };
  if (o.length !== null) headers["Content-Length"] = o.length ?? String(bytes.byteLength);
  const q = o.query ?? `ms=${o.ms ?? 2000}&run=${o.run ?? 0}`;
  return call(env, `/vod-shares/${id}/hls/seg/${seq}?${q}`, { method: "PUT", headers, body: bytes }, o.ctx);
}

export function pause(env: Env, id: string, reason: unknown = "game", token = OWNER): Promise<Response> {
  const body = JSON.stringify({ reason });
  return call(env, `/vod-shares/${id}/hls/pause`, {
    method: "POST",
    headers: { ...auth(token), "Content-Type": "application/json", "Content-Length": String(body.length) },
    body,
  });
}

export function endShare(env: Env, id: string, segments: unknown, truncated: unknown = false, token = OWNER): Promise<Response> {
  const body = JSON.stringify({ segments, truncated });
  return call(env, `/vod-shares/${id}/hls/end`, {
    method: "POST",
    headers: { ...auth(token), "Content-Type": "application/json", "Content-Length": String(body.length) },
    body,
  });
}

export function play(env: Env, id: string, headers: Record<string, string> = { Origin: SITE }): Promise<Response> {
  return call(env, `/vod-play/${id}`, { method: "POST", headers });
}

export function media(env: Env, id: string, token: string, file: string, init: RequestInit = {}): Promise<Response> {
  return call(env, `/vod-hls/${id}/${token}/${file}`, init);
}

/** Init + run 0 + segments 0..n-1 (2 s each unless `ms` says otherwise). Returns the id. */
export async function liveShare(
  s: VodSetup,
  n: number,
  o: { duration?: number; size?: number; ms?: (seq: number) => number; segSize?: number } = {},
): Promise<string> {
  const res = await hlsInit(s.env, OWNER, { duration: o.duration ?? 1800, size: o.size ?? 50 * MiB });
  if (res.status !== 201) throw new Error(`init failed: ${res.status} ${await res.text()}`);
  const id = (await json(res)).id as string;
  const run = await putRun(s.env, id, 0);
  if (run.status !== 200) throw new Error(`run failed: ${run.status} ${await run.text()}`);
  for (let seq = 0; seq < n; seq++) {
    const put = await putSeg(s.env, id, seq, { ms: o.ms ? o.ms(seq) : 2000, bytes: segBytes(seq, o.segSize ?? 4096) });
    if (put.status !== 200) throw new Error(`segment ${seq} failed: ${put.status} ${await put.text()}`);
  }
  return id;
}

/** Mint a play token through POST /vod-play. */
export async function token(s: VodSetup, id: string): Promise<string> {
  const res = await play(s.env, id);
  if (res.status !== 200) throw new Error(`mint failed: ${res.status} ${await res.text()}`);
  return (await json(res)).token as string;
}

/** Wrap the fake bucket so every R2 call is counted (and list() throws: the code must never list). */
export function countR2(s: VodSetup): { calls: Record<string, number>; total(): number; deleted: string[][] } {
  const calls: Record<string, number> = {};
  const deleted: string[][] = [];
  const raw = s.r2.bucket as unknown as Record<string, (...a: unknown[]) => unknown>;
  const wrapped: Record<string, unknown> = {
    list: () => { throw new Error("R2 list() must never be called"); },
  };
  for (const op of ["put", "get", "head", "delete", "createMultipartUpload", "resumeMultipartUpload"]) {
    wrapped[op] = (...a: unknown[]) => {
      calls[op] = (calls[op] ?? 0) + 1;
      if (op === "delete") deleted.push(Array.isArray(a[0]) ? [...(a[0] as string[])] : [a[0] as string]);
      return raw[op](...a);
    };
  }
  s.env.CLIPS = wrapped as unknown as R2Bucket;
  return { calls, total: () => Object.values(calls).reduce((x, y) => x + y, 0), deleted };
}

/** A fake ExecutionContext that records what was handed to waitUntil. */
export function fakeCtx(): { ctx: ExecutionContext; waited: Promise<unknown>[] } {
  const waited: Promise<unknown>[] = [];
  const ctx = { waitUntil(p: Promise<unknown>) { waited.push(p); }, passThroughOnException() {}, props: {} };
  return { ctx: ctx as unknown as ExecutionContext, waited };
}

export function hlsRow(s: VodSetup, id: string): Record<string, unknown> | undefined {
  return s.db.q<Record<string, unknown>>("SELECT * FROM vod_hls WHERE clip_id = ?1", id)[0];
}

export function clipRow(s: VodSetup, id: string): Record<string, unknown> | undefined {
  return s.db.q<Record<string, unknown>>("SELECT * FROM clips WHERE id = ?1", id)[0];
}

export function segRows(s: VodSetup, id: string): { seq: number; duration_ms: number; bytes: number; committed: number }[] {
  return s.db.q("SELECT seq, duration_ms, bytes, committed FROM vod_hls_segments WHERE clip_id = ?1 ORDER BY seq", id);
}

/** Segment `seq` of run `run` (the key layout: vods/<id>/<k>/<seq>.m4s). */
export function segKey(id: string, seq: number, run = 0): string {
  return `vods/${id}/${run}/${seq}.m4s`;
}

/** The share's stored segment keys, by run then seq. */
export function storedKeys(s: VodSetup, id: string): string[] {
  return [...s.r2.store.keys()].filter((k) => k.startsWith(`vods/${id}/`)).sort((a, b) => order(a) - order(b));
}

function order(key: string): number {
  const m = /\/(\d+)\/(\d+)\.m4s$/.exec(key);
  return m ? Number(m[1]) * 1_000_000 + Number(m[2]) : -1;
}
