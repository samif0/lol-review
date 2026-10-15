/**
 * HLS shares, desktop side: the status object, init with format=hls, and runs
 * (segments, pause and end are in vod-hls-segments.test.ts). Real SQLite
 * (every migration) + fake R2.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { HLS_INIT_MAX_BYTES, VOD_MAX_BYTES, VOD_TTL_SECONDS, hlsSlots, vodHlsAllows } from "../src/share-limits";
import {
  OTHER, OWNER, clipRow, countR2, hlsInit, hlsRow, hlsSetup, initBytes, json, liveShare, nowSec, pause, putRun, putSeg,
  resetHlsState,
} from "./hls-fixtures";
import { vodInitOk, vodStatus } from "./vod-fixtures";

beforeEach(() => {
  vi.restoreAllMocks();
  resetHlsState();
});
afterEach(() => {
  vi.useRealTimers();
});

const HLS_OBJECT = {
  enabled: true, segment_seconds: 2, target_duration: 6, max_segment_bytes: 16_777_216, init_max_bytes: 16_384,
  playable_ms: 8000, max_runs: 8,
};

describe("GET /vod-shares/status for HLS", () => {
  it("adds the hls object for allowed sharers only, and format, segments and paused to shares", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 3);
    expect((await pause(s.env, id)).status).toBe(200);
    const out = await json(await vodStatus(s.env, OWNER));
    expect(out.hls).toEqual(HLS_OBJECT);
    expect(out.shares).toEqual([expect.objectContaining({ id, status: "uploading", format: "hls", segments: 3, paused: true, quality: "1080p60" })]);
    const other = await json(await vodStatus(s.env, OTHER));
    expect(Object.keys(other).sort()).toEqual(["allowed", "now", "serving", "shares", "sharing"]);
  });

  it("enabled follows VOD_HLS (on, canary list, off), the play secret and the vod_hls ops flag", async () => {
    expect([vodHlsAllows("1", 7), vodHlsAllows(" ON ", 7), vodHlsAllows("0", 7), vodHlsAllows(undefined, 7), vodHlsAllows("yes", 7)])
      .toEqual([true, true, false, false, false]);
    expect([vodHlsAllows("users:3, 7", 7), vodHlsAllows("users:3,70", 7), vodHlsAllows("users:", 7)]).toEqual([true, false, false]);

    const enabled = async (env: Record<string, string | undefined>, flag?: string) => {
      const s = await hlsSetup({ env });
      if (flag !== undefined) s.db.x("INSERT INTO ops_flags (name, value) VALUES ('vod_hls', ?1)", flag);
      return ((await json(await vodStatus(s.env, OWNER))).hls as { enabled: boolean }).enabled;
    };
    expect(await enabled({})).toBe(true);
    expect(await enabled({ VOD_HLS: "0" })).toBe(false);
    expect(await enabled({ VOD_HLS: undefined })).toBe(false);
    expect(await enabled({ VOD_HLS: "users:1" })).toBe(true); // the owner is user 1
    expect(await enabled({ VOD_HLS: "users:2" })).toBe(false);
    expect(await enabled({ VOD_PLAY_SECRET: undefined })).toBe(false);
    expect(await enabled({ VOD_PLAY_SECRET: "short" })).toBe(false);
    expect(await enabled({}, "0")).toBe(false);
    expect(await enabled({}, "1")).toBe(true);
  });
});

describe("POST /vod-shares/uploads?format=hls", () => {
  it("reserves with one D1 batch and no R2 call, and answers the contract body", async () => {
    const s = await hlsSetup();
    const r2 = countR2(s);
    const before = s.db._sql.length;
    const res = await hlsInit(s.env, OWNER, { size: 1_600_000_000, duration: 2256 });
    expect(res.status).toBe(201);
    const out = await json(res);
    const row = clipRow(s, out.id as string)!;
    expect(out).toEqual({
      id: out.id, format: "hls", part_count: 1153, segment_seconds: 2, target_duration: 6, max_segment_bytes: 16777216,
      init_max_bytes: 16384, playable_ms: 8000, max_runs: 8, expires_at: row.expires_at,
      share_expires_at: (row.created_at as number) + VOD_TTL_SECONDS, url: `https://revu.lol/v/${out.id}`,
    });
    expect(row).toMatchObject({
      format: "hls", r2_key: `vods/${out.id}/`, size_bytes: 1_600_000_000, part_count: 1153, upload_id: null, status: "uploading",
      kind: "vod", quality: "1080p60", hls_bytes: 0, part_puts: 0,
    });
    expect(hlsRow(s, out.id as string)).toMatchObject({ segments: 0, max_seq: -1, runs: 0 });
    expect(r2.total()).toBe(0);
    const inserts = s.db._sql.slice(before).filter((q) => q.startsWith("INSERT INTO clips") || q.startsWith("INSERT INTO vod_hls"));
    expect(inserts).toHaveLength(2);
  });

  it("gives ceil(d/2) + 2 + max(10, ceil(0.01 d)) segment slots", () => {
    expect([hlsSlots(1), hlsSlots(1800), hlsSlots(2256), hlsSlots(7200)]).toEqual([13, 920, 1153, 3674]);
  });

  it("checks format with the request shape: 400 for an unknown one, 409 hls_unavailable before any start is spent", async () => {
    const s = await hlsSetup({ env: { VOD_HLS: "0" } });
    const bad = await hlsInit(s.env, OWNER, { format: "dash" });
    expect(bad.status).toBe(400);
    // An idle upload of the caller would be discarded by a start that gets that far.
    const idle = (await json(await vodInitOk(s.env, OWNER))).id as string;
    s.db.x("UPDATE clips SET last_activity_at = ?1 WHERE id = ?2", nowSec() - 3600, idle);
    const starts = () => s.db.q<{ starts: number }>("SELECT starts FROM vod_share_usage")[0]?.starts;
    const before = starts();
    const off = await hlsInit(s.env, OWNER);
    expect(off.status).toBe(409);
    expect(await json(off)).toEqual({ error: "hls_unavailable", message: expect.any(String) });
    expect(starts()).toBe(before);
    expect(clipRow(s, idle)?.status).toBe("uploading");
    // Refusals that come before the shape keep their order.
    const shut = await hlsSetup({ env: { VOD_SHARING: "0" } });
    expect((await hlsInit(shut.env, OWNER)).status).toBe(503);
    expect((await hlsInit(shut.env, OTHER)).status).toBe(503);
    const open = await hlsSetup();
    expect((await hlsInit(open.env, OTHER)).status).toBe(403);
    expect((await hlsInit(open.env, OWNER, { size: VOD_MAX_BYTES + 1 })).status).toBe(413);
  });

  it("an init without format is today's MP4 init (no format field, a multipart upload)", async () => {
    const s = await hlsSetup();
    const out = await json(await hlsInit(s.env, OWNER, { format: null }));
    expect(out).toHaveProperty("part_size");
    expect(out).not.toHaveProperty("format");
    expect(clipRow(s, out.id as string)?.format).toBeNull();
    expect(s.r2.uploads.size).toBe(1);
  });

  it("counts toward the pending limit; a paused share holds its slot, an idle one is settled by the next start", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    expect((await hlsInit(s.env, OWNER)).status).toBe(429);
    expect((await pause(s.env, id)).status).toBe(200);
    s.db.x("UPDATE clips SET last_activity_at = ?1 WHERE id = ?2", nowSec() - 3600, id);
    expect((await hlsInit(s.env, OWNER)).status).toBe(429); // paused: not idle
    s.db.x("UPDATE vod_hls SET paused_at = NULL, paused_reason = NULL WHERE clip_id = ?1", id);
    const next = await hlsInit(s.env, OWNER);
    expect(next.status).toBe(201);
    // 8 s committed: watchable, so ended at its edge as truncated, never deleted.
    expect(clipRow(s, id)).toMatchObject({ status: "ready" });
    expect(hlsRow(s, id)).toMatchObject({ ended: 1, truncated: 1, segments: 4 });
  });
});

describe("PUT /vod-shares/:id/hls/runs/:k", () => {
  async function share() {
    const s = await hlsSetup();
    const id = (await json(await hlsInit(s.env))).id as string;
    return { s, id };
  }

  it("stores run 0 from 0, sets the quality, counts an attempt and the init bytes", async () => {
    const { s, id } = await share();
    expect((await putRun(s.env, id, 0, { from: 1 })).status).toBe(409);
    const res = await putRun(s.env, id, 0, { quality: "720p", fps: "30", bytes: initBytes(1400) });
    expect(res.status).toBe(200);
    expect(await json(res)).toEqual({ k: 0, from: 0, quality: "720p30" });
    expect(res.headers.get("Server-Timing")).toMatch(/^d1;dur=\d+, r2;dur=\d+$/);
    expect(clipRow(s, id)).toMatchObject({ quality: "720p30", hls_bytes: 1400, part_puts: 1 });
    expect(hlsRow(s, id)).toMatchObject({ runs: 1 });
    const stored = s.db.q<{ init: number[]; from_seq: number; quality: string }>("SELECT init, from_seq, quality FROM vod_hls_runs WHERE clip_id = ?1", id)[0];
    expect(Uint8Array.from(stored.init)).toEqual(initBytes(1400));
  });

  it("a replay with identical bytes and from is 200 already; anything else that doesn't follow is 409 run_conflict", async () => {
    const { s, id } = await share();
    expect((await putRun(s.env, id, 0)).status).toBe(200);
    const again = await putRun(s.env, id, 0);
    expect(again.status).toBe(200);
    expect(await json(again)).toEqual({ k: 0, from: 0, quality: "1080p60", already: true });
    expect(clipRow(s, id)).toMatchObject({ hls_bytes: 1408, part_puts: 2 }); // the attempt counts, the bytes don't
    expect((await putRun(s.env, id, 0, { bytes: initBytes(1408, 9) })).status).toBe(409);
    expect((await putRun(s.env, id, 2, { from: 10 })).status).toBe(409); // skips run 1
    for (let seq = 0; seq < 5; seq++) expect((await putSeg(s.env, id, seq)).status).toBe(200);
    expect((await putRun(s.env, id, 1, { from: 0 })).status).toBe(409); // not past run 0's start
    expect((await putRun(s.env, id, 1, { from: 4 })).status).toBe(409); // a committed segment at or after it
    const conflict = await putRun(s.env, id, 1, { from: 3 });
    expect(conflict.status).toBe(409);
    expect((await json(conflict)).error).toBe("run_conflict");
    // A pending row (an aborted PUT) at or after S is fine: the new run's PUT replaces it.
    s.db.x("INSERT INTO vod_hls_segments (clip_id, seq, duration_ms, bytes, committed) VALUES (?1, 6, 2000, 10, 0)", id);
    const resumed = await putRun(s.env, id, 1, { from: 5, quality: "720p" });
    expect(resumed.status).toBe(200);
    expect(await json(resumed)).toEqual({ k: 1, from: 5, quality: "720p60" });
    expect((await putRun(s.env, id, 1, { from: 5, quality: "720p" })).status).toBe(200); // replay of the last run
    expect((await putRun(s.env, id, 0)).status).toBe(409); // an older run is never replayed
    expect(clipRow(s, id)?.quality).toBe("720p60");
  });

  it("refuses bodies by shape (411, 413, 415) and runs past 8 (429 too_many_runs)", async () => {
    const { s, id } = await share();
    expect((await putRun(s.env, id, 0, { length: null })).status).toBe(411);
    expect((await putRun(s.env, id, 0, { bytes: initBytes(HLS_INIT_MAX_BYTES + 1) })).status).toBe(413);
    const notInit = new Uint8Array(100);
    expect((await putRun(s.env, id, 0, { bytes: notInit })).status).toBe(415);
    expect((await putRun(s.env, id, 0, { quality: "4k" })).status).toBe(400);
    expect((await putRun(s.env, id, 0, { fps: "24" })).status).toBe(400);
    const tooMany = await putRun(s.env, id, 8, { from: 80 });
    expect(tooMany.status).toBe(429);
    expect((await json(tooMany)).error).toBe("too_many_runs");
    // Eight runs fill the cap.
    expect((await putRun(s.env, id, 0)).status).toBe(200);
    for (let k = 1; k < 8; k++) expect((await putRun(s.env, id, k, { from: k * 10 })).status, `run ${k}`).toBe(200);
    expect((await putRun(s.env, id, 8, { from: 90 })).status).toBe(429);
    expect(hlsRow(s, id)?.runs).toBe(8);
  });

  it("is 404 for another account's share, an MP4 share and a stopped one", async () => {
    const { s, id } = await share();
    expect((await putRun(s.env, id, 0, { token: OTHER })).status).toBe(404);
    const mp4 = (await json(await hlsInit(s.env, OTHER, { format: null }))).id as string;
    expect((await putRun(s.env, mp4, 0, { token: OTHER })).status).toBe(404);
    s.db.x("UPDATE clips SET status = 'purging' WHERE id = ?1", id);
    expect((await putRun(s.env, id, 0)).status).toBe(404);
  });
});
