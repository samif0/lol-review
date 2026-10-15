/**
 * HLS shares: format isolation in both directions, every delete path with its
 * exact key list and prefix tombstone (discard, owner delete, hourly purge,
 * tombstone retry, account deletion, cron settle), and the cron settle rules.
 * Real SQLite + fake R2 (whose list() throws: nothing may list a prefix).
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { discardRow, sweepIdleUploads } from "../src/upload-cleanup";
import { purgeExpiredClips, purgeTombstones } from "../src/clip-purge";
import { settleHlsShare } from "../src/vod-hls-end";
import { storageUsage } from "../src/storage-budget";
import { HLS_PAUSE_MAX_SECONDS, HLS_TOMBSTONE_DELAY_SECONDS, IDLE_SWEEP_SECONDS, VOD_TTL_SECONDS } from "../src/share-limits";
import { runScheduled, sha256Hex } from "./fakes";
import { webReq } from "./web-fixtures";
import worker from "../src/index";
import {
  OTHER, OWNER, call, clipRow, countR2, endShare, hlsInit, hlsRow, hlsSetup, json, liveShare, nowSec, pause, putRun, putSeg,
  resetHlsState, segBytes, segRows, storedKeys,
} from "./hls-fixtures";
import { payload, shareVod, vodComplete, vodInitOk, vodPart } from "./vod-fixtures";

beforeEach(() => {
  vi.restoreAllMocks();
  resetHlsState();
  vi.spyOn(console, "log").mockImplementation(() => {});
});
afterEach(() => {
  vi.useRealTimers();
});

type Setup = Awaited<ReturnType<typeof hlsSetup>>;
const T0 = Date.UTC(2026, 9, 14, 12, 0, 0);
const keys = (id: string, from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => `vods/${id}/${from + i}.m4s`);
const tombstones = (s: Setup) => s.db.q("SELECT r2_key, upload_id, size_bytes, kind, key_count, not_before FROM storage_tombstones ORDER BY r2_key");

/** Every D1 row of a share, by table. */
function rowsOf(s: Setup, id: string): Record<string, number> {
  const out: Record<string, number> = {};
  for (const [t, col] of [["clips", "id"], ["vod_hls", "clip_id"], ["vod_hls_runs", "clip_id"], ["vod_hls_segments", "clip_id"], ["vod_share_meta", "clip_id"]]) {
    out[t] = s.db.q<{ n: number }>(`SELECT COUNT(*) AS n FROM ${t} WHERE ${col} = ?1`, id)[0].n;
  }
  return out;
}
const NO_ROWS = { clips: 0, vod_hls: 0, vod_hls_runs: 0, vod_hls_segments: 0, vod_share_meta: 0 };

/** A live share with 4 committed segments, one pending (seq 5, 333 bytes) and max_seq 5. */
async function busyShare(s: Setup): Promise<string> {
  const id = await liveShare(s, 4);
  s.db.x("INSERT INTO vod_hls_segments (clip_id, seq, duration_ms, bytes, committed) VALUES (?1, 5, 2000, 333, 0)", id);
  s.db.x("UPDATE vod_hls SET max_seq = 5 WHERE clip_id = ?1", id);
  s.db.x("INSERT INTO vod_share_meta (clip_id, meta_json, has_notes, created_at) VALUES (?1, '{}', 0, 1)", id);
  return id;
}

describe("format isolation", () => {
  it("the MP4 part and complete routes never touch an HLS share", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    expect((await vodPart(s.env, OWNER, id, 1, payload(100))).status).toBe(404);
    expect((await vodComplete(s.env, OWNER, id, [{ part_number: 1, etag: "x" }])).status).toBe(404);
    expect(clipRow(s, id)).toMatchObject({ status: "uploading", format: "hls" });
    const clipPart = await call(s.env, `/clips/${id}/parts/1`, {
      method: "PUT", headers: { Authorization: `Bearer ${OWNER}`, "Content-Length": "100" }, body: payload(100),
    });
    expect(clipPart.status).toBe(404);
  });

  it("the HLS routes never touch an MP4 share or a clip", async () => {
    const s = await hlsSetup();
    const mp4 = (await json(await vodInitOk(s.env, OWNER, 500))).id as string;
    expect((await putRun(s.env, mp4, 0)).status).toBe(404);
    expect((await putSeg(s.env, mp4, 0)).status).toBe(404);
    expect((await pause(s.env, mp4)).status).toBe(404);
    expect((await endShare(s.env, mp4, 1)).status).toBe(404);
    const part = await json(await vodPart(s.env, OWNER, mp4, 1, payload(500)));
    expect((await vodComplete(s.env, OWNER, mp4, [part])).status).toBe(201);
    expect((await putSeg(s.env, mp4, 0)).status).toBe(404);
    expect(s.db.q("SELECT * FROM vod_hls")).toEqual([]);
    expect(s.db.q("SELECT * FROM vod_hls_segments")).toEqual([]);
  });

  it("the storage budget counts an HLS share's reservation and its tombstones", async () => {
    const s = await hlsSetup();
    await hlsInit(s.env, OWNER, { size: 12345 });
    s.db.x("INSERT INTO storage_tombstones (r2_key, size_bytes, kind, created_at, key_count, not_before) VALUES ('vods/Gone001/', 55, 'vod', 1, 9, 2)");
    expect(await storageUsage(s.db)).toEqual({ used: 12345 + 55, vod_used: 12345 + 55 });
  });
});

describe("every delete path: keys 0..max_seq, rows, then a delayed prefix tombstone", () => {
  /** The tombstone every path leaves: keys 0..max_seq+8, pending bytes, 10 minutes out. */
  const expectTombstone = (s: Setup, id: string, maxSeq: number, bytes: number, now: number) =>
    expect(tombstones(s)).toEqual([{
      r2_key: `vods/${id}/`, upload_id: null, size_bytes: bytes, kind: "vod", key_count: maxSeq + 9, not_before: now + HLS_TOMBSTONE_DELAY_SECONDS,
    }]);

  it("discardRow", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup();
    const id = await busyShare(s);
    const r2 = countR2(s);
    await discardRow(s.env, id);
    expect(r2.deleted).toEqual([keys(id, 0, 5)]);
    expect(rowsOf(s, id)).toEqual(NO_ROWS);
    expectTombstone(s, id, 5, 333, T0 / 1000);
    expect(storedKeys(s, id)).toEqual([]);
  });

  it("the owner's DELETE /clips/:id (Stop sharing)", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup();
    const id = await busyShare(s);
    const r2 = countR2(s);
    const res = await call(s.env, `/clips/${id}`, { method: "DELETE", headers: { Authorization: `Bearer ${OWNER}` } });
    expect(res.status).toBe(200);
    expect(r2.deleted).toEqual([keys(id, 0, 5)]);
    expect(rowsOf(s, id)).toEqual(NO_ROWS);
    expectTombstone(s, id, 5, 333, T0 / 1000);
    expect((await call(s.env, `/clips/${id}`, { method: "DELETE", headers: { Authorization: `Bearer ${OTHER}` } })).status).toBe(404);
  });

  it("a share no PUT ever claimed has no object: rows only, no R2 call, no tombstone", async () => {
    const s = await hlsSetup();
    const id = (await json(await hlsInit(s.env))).id as string;
    const r2 = countR2(s);
    expect((await call(s.env, `/clips/${id}`, { method: "DELETE", headers: { Authorization: `Bearer ${OWNER}` } })).status).toBe(200);
    expect(r2.total()).toBe(0);
    expect(rowsOf(s, id)).toEqual(NO_ROWS);
    expect(tombstones(s)).toEqual([]);
  });

  it("deletes at most 1000 keys per call, never lists, and keeps the claimed row counted when R2 fails", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup();
    const id = await liveShare(s, 2);
    s.db.x("UPDATE vod_hls SET max_seq = 2500 WHERE clip_id = ?1", id);
    const r2 = countR2(s);
    const del = s.r2.bucket.delete.bind(s.r2.bucket);
    s.r2.bucket.delete = (async () => { throw new Error("R2 internal error"); }) as typeof s.r2.bucket.delete;
    vi.spyOn(console, "error").mockImplementation(() => {});
    expect((await call(s.env, `/clips/${id}`, { method: "DELETE", headers: { Authorization: `Bearer ${OWNER}` } })).status).toBe(200);
    expect(clipRow(s, id)).toMatchObject({ status: "purging", expires_at: T0 / 1000 }); // still counted
    expect(rowsOf(s, id)).toMatchObject({ clips: 1, vod_hls: 1, vod_hls_segments: 2 });
    expect(tombstones(s)).toEqual([]);
    // The hourly purge retries it: three calls of 1000, 1000 and 501 keys.
    s.r2.bucket.delete = del;
    vi.setSystemTime(T0 + 1000);
    r2.deleted.length = 0;
    await runScheduled(worker, s.env);
    expect(r2.deleted.slice(0, 3)).toEqual([keys(id, 0, 999), keys(id, 1000, 1999), keys(id, 2000, 2500)]);
    expect(rowsOf(s, id)).toEqual(NO_ROWS);
    expect(tombstones(s)).toEqual([expect.objectContaining({ r2_key: `vods/${id}/`, key_count: 2509 })]);
  });

  it("the hourly purge of an expired share, inside the run's budget of R2 calls", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup({ allow: ["sess-owner", "sess-other"] });
    const a = await busyShare(s);
    expect((await endShare(s.env, a, 4)).status).toBe(201);
    // A second, bigger share of another account: 40 R2 calls of keys, over what is left.
    const b = (await json(await hlsInit(s.env, OTHER))).id as string;
    s.db.x("UPDATE vod_hls SET max_seq = 39999 WHERE clip_id = ?1", b);
    s.db.x("UPDATE clips SET status = 'ready', expires_at = ?2 WHERE id = ?1", b, T0 / 1000 - 1);
    s.db.x("UPDATE clips SET expires_at = ?2 WHERE id = ?1", a, T0 / 1000 - 1);
    const r2 = countR2(s);
    await runScheduled(worker, s.env);
    // a went (its keys 0..5: /end left max_seq 5); b doesn't fit 40 - 1 calls this run.
    expect(r2.deleted[0]).toEqual(keys(a, 0, 5));
    expect(rowsOf(s, a)).toEqual(NO_ROWS);
    expect(clipRow(s, b)).toMatchObject({ status: "purging" });
    expect(tombstones(s)).toEqual([expect.objectContaining({ r2_key: `vods/${a}/`, key_count: 14, size_bytes: 0 })]);
  });

  it("the tombstone retry deletes keys 0..key_count-1 once not_before has passed", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup();
    const id = await busyShare(s);
    await discardRow(s.env, id);
    s.r2.store.set(`vods/${id}/13.m4s`, segBytes(13)); // a PUT that landed after the delete
    const r2 = countR2(s);
    expect(await purgeTombstones(s.env)).toBe(0); // not yet
    expect(r2.total()).toBe(0);
    vi.setSystemTime(T0 + HLS_TOMBSTONE_DELAY_SECONDS * 1000);
    expect(await purgeTombstones(s.env)).toBe(1);
    expect(r2.deleted).toEqual([keys(id, 0, 13)]);
    expect(storedKeys(s, id)).toEqual([]);
    expect(tombstones(s)).toEqual([]);
    // A failed delete keeps it for the next run.
    s.db.x("INSERT INTO storage_tombstones (r2_key, size_bytes, kind, created_at, key_count, not_before) VALUES ('vods/Gone001/', 5, 'vod', 1, 3, 2)");
    s.r2.bucket.delete = (async () => { throw new Error("R2 internal error"); }) as typeof s.r2.bucket.delete;
    vi.spyOn(console, "error").mockImplementation(() => {});
    expect(await purgeTombstones(s.env)).toBe(0);
    expect(tombstones(s)).toHaveLength(1);
  });

  it("account deletion", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup({ env: { AUTH_BASE: "https://clips.revu.lol", CREDENTIALED_ORIGINS: "https://revu.lol" } });
    const live = await busyShare(s);
    const now = T0 / 1000;
    const cookie = "a".repeat(48);
    s.db.x("INSERT INTO sessions (token_hash, user_id, created_at, expires_at, kind) VALUES (?1, ?2, ?3, ?4, 'web')", await sha256Hex("web:" + cookie), s.users[OWNER], now, now + 3600);
    const r2 = countR2(s);
    const res = await worker.fetch(webReq("/auth/web/account/delete", { method: "POST", body: { email: "user1@example.com" }, cookie }), s.env);
    expect(res.status).toBe(200);
    expect(r2.deleted).toEqual([keys(live, 0, 5)]);
    expect(rowsOf(s, live)).toEqual(NO_ROWS);
    expect(tombstones(s)).toEqual([{ r2_key: `vods/${live}/`, upload_id: null, size_bytes: 333, kind: "vod", key_count: 14, not_before: now + 600 }]);
    expect(s.db.q("SELECT * FROM users WHERE email = 'user1@example.com'")).toEqual([]);
  });

  it("account deletion when R2 fails: the tombstone counts every stored byte", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup({ env: { AUTH_BASE: "https://clips.revu.lol", CREDENTIALED_ORIGINS: "https://revu.lol" } });
    const live = await busyShare(s);
    const now = T0 / 1000;
    const cookie = "b".repeat(48);
    s.db.x("INSERT INTO sessions (token_hash, user_id, created_at, expires_at, kind) VALUES (?1, ?2, ?3, ?4, 'web')", await sha256Hex("web:" + cookie), s.users[OWNER], now, now + 3600);
    s.r2.bucket.delete = (async () => { throw new Error("R2 internal error"); }) as typeof s.r2.bucket.delete;
    vi.spyOn(console, "error").mockImplementation(() => {});
    const before = clipRow(s, live)!;
    const res = await worker.fetch(webReq("/auth/web/account/delete", { method: "POST", body: { email: "user1@example.com" }, cookie }), s.env);
    expect(res.status).toBe(200);
    expect(rowsOf(s, live)).toEqual(NO_ROWS);
    expect(tombstones(s)).toEqual([{
      r2_key: `vods/${live}/`, upload_id: null, size_bytes: Math.max(before.size_bytes as number, before.hls_bytes as number), kind: "vod", key_count: 14, not_before: now + 600,
    }]);
  });
});

describe("the cron settle (before the purge)", () => {
  async function quiet(s: Setup, n: number, set: string, ...args: unknown[]): Promise<string> {
    const id = await liveShare(s, n);
    s.db.x("INSERT INTO vod_hls_segments (clip_id, seq, duration_ms, bytes, committed) VALUES (?1, ?2, 2000, 99, 0)", id, n + 1);
    s.db.x("UPDATE vod_hls SET max_seq = ?2 WHERE clip_id = ?1", id, n + 1);
    s.db.x(set, id, ...args);
    return id;
  }

  it("ends a watchable share idle for 30 minutes at its edge, as truncated", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup();
    const id = await quiet(s, 4, "UPDATE clips SET last_activity_at = ?2 WHERE id = ?1", T0 / 1000 - IDLE_SWEEP_SECONDS);
    const r2 = countR2(s);
    await runScheduled(worker, s.env);
    expect(clipRow(s, id)).toMatchObject({ status: "ready", size_bytes: 1408 + 4 * 4096, duration_s: 8 });
    expect(clipRow(s, id)?.expires_at).toBe((clipRow(s, id)?.created_at as number) + VOD_TTL_SECONDS);
    expect(hlsRow(s, id)).toMatchObject({ ended: 1, truncated: 1, segments: 4 });
    expect(segRows(s, id).map((r) => r.seq)).toEqual([0, 1, 2, 3]);
    expect(r2.deleted[0]).toEqual(keys(id, 4, 5));
  });

  it("discards one that isn't watchable, and leaves an active or briefly paused one alone", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup({ tokens: ["sess-owner", "sess-other", "sess-third"], allow: ["sess-owner", "sess-other", "sess-third"] });
    const idle = await quiet(s, 3, "UPDATE clips SET last_activity_at = ?2 WHERE id = ?1", T0 / 1000 - IDLE_SWEEP_SECONDS); // 6 s: not watchable
    resetHlsState();
    // Others: active, or paused for 5 h with no activity.
    const activeRes = await hlsInit(s.env, OTHER);
    const active = (await json(activeRes)).id as string;
    s.db.x("UPDATE clips SET last_activity_at = ?2 WHERE id = ?1", active, T0 / 1000 - IDLE_SWEEP_SECONDS + 60);
    const pausedRes = await hlsInit(s.env, "sess-third");
    const paused = (await json(pausedRes)).id as string;
    s.db.x("UPDATE clips SET last_activity_at = 1 WHERE id = ?1", paused);
    s.db.x("UPDATE vod_hls SET paused_at = ?2, paused_reason = 'game' WHERE clip_id = ?1", paused, T0 / 1000 - HLS_PAUSE_MAX_SECONDS + 3600);
    await runScheduled(worker, s.env);
    expect(rowsOf(s, idle)).toEqual(NO_ROWS);
    expect(tombstones(s)).toEqual([expect.objectContaining({ r2_key: `vods/${idle}/`, key_count: 4 + 9 })]);
    expect(clipRow(s, active)?.status).toBe("uploading");
    expect(clipRow(s, paused)?.status).toBe("uploading");
  });

  it("ends a share paused for 6 hours, and one past its upload window", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup({ allow: ["sess-owner", "sess-other"] });
    const paused = await quiet(s, 5, "UPDATE vod_hls SET paused_at = ?2, paused_reason = 'game' WHERE clip_id = ?1", T0 / 1000 - HLS_PAUSE_MAX_SECONDS);
    resetHlsState();
    const late = await (async () => {
      const res = await hlsInit(s.env, OTHER);
      const id = (await json(res)).id as string;
      expect((await putRun(s.env, id, 0, { token: OTHER })).status).toBe(200);
      for (let seq = 0; seq < 4; seq++) expect((await putSeg(s.env, id, seq, { token: OTHER })).status).toBe(200);
      s.db.x("UPDATE clips SET expires_at = ?2 WHERE id = ?1", id, T0 / 1000);
      return id;
    })();
    await runScheduled(worker, s.env);
    expect(clipRow(s, paused)).toMatchObject({ status: "ready" });
    expect(hlsRow(s, paused)).toMatchObject({ ended: 1, truncated: 1, paused_at: null, paused_reason: null });
    expect(clipRow(s, late)).toMatchObject({ status: "ready" });
    expect(clipRow(s, late)?.expires_at).toBe((clipRow(s, late)?.created_at as number) + VOD_TTL_SECONDS);
    expect((await json(await call(s.env, `/vod-meta/${late}`))).truncated).toBe(true);
  });

  it("re-checks the share is still quiet when it acts", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    expect(await settleHlsShare(s.env, id, T0 / 1000, IDLE_SWEEP_SECONDS)).toBe("kept");
    expect(clipRow(s, id)?.status).toBe("uploading");
  });

  it("neither the purge nor the idle sweep ever deletes an HLS share that is still uploading", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    s.db.x("UPDATE clips SET last_activity_at = 1, expires_at = ?2 WHERE id = ?1", id, T0 / 1000 - 1);
    expect(await purgeExpiredClips(s.env)).toBe(0);
    expect(await sweepIdleUploads(s.env, T0 / 1000, 5)).toBe(0);
    expect(clipRow(s, id)?.status).toBe("uploading");
  });
});

describe("a MP4 share is cleaned up exactly as before", () => {
  it("one key, no prefix tombstone", async () => {
    const s = await hlsSetup();
    const { id } = await shareVod(s, OWNER, 500);
    const r2 = countR2(s);
    expect((await call(s.env, `/clips/${id}`, { method: "DELETE", headers: { Authorization: `Bearer ${OWNER}` } })).status).toBe(200);
    expect(r2.deleted).toEqual([[`vods/${id}.mp4`]]);
    expect(tombstones(s)).toEqual([]);
    expect(nowSec()).toBeGreaterThan(0);
  });
});
