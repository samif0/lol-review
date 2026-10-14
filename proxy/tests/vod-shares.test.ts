/**
 * Full-game (VOD) shares: GET /vod-shares/status and POST /vod-shares/uploads,
 * against real SQLite (every migration applied) and the fake R2.
 */

import { beforeEach, describe, expect, it, vi } from "vitest";
import { resetUserLimits } from "../src/user-limits";
import { nextHour } from "../src/storage-budget";
import { nextUtcMidnight, utcDayOf, VOD_PART_SIZE_BYTES, VOD_TTL_SECONDS, vodUploadWindowSeconds } from "../src/share-limits";
import { makeRequest } from "./fakes";
import { BASE, body, nowSec, shareVod, vodInit, vodInitOk, vodSetup, vodStatus } from "./vod-fixtures";
import worker from "../src/index";

beforeEach(() => {
  vi.restoreAllMocks();
  resetUserLimits();
});

const OWNER = "sess-owner";
const OTHER = "sess-other";

describe("GET /vod-shares/status", () => {
  it("answers an account that is not allowlisted with the short shape only", async () => {
    const s = await vodSetup();
    const res = await vodStatus(s.env, OTHER);
    expect(res.status).toBe(200);
    const out = await body(res);
    expect(Object.keys(out).sort()).toEqual(["allowed", "now", "serving", "shares", "sharing"]);
    expect(out).toMatchObject({ allowed: false, sharing: true, serving: true, shares: [] });
    expect(Math.abs((out.now as number) - nowSec())).toBeLessThanOrEqual(2);
  });

  it("answers an allowed account with limits, usage, pool and its own shares", async () => {
    const s = await vodSetup();
    const { id } = await shareVod(s, OWNER, 500);
    // Another account's clip counts in the pool but never in this account's shares.
    s.db.x(
      "INSERT INTO clips (id, user_id, r2_key, content_type, size_bytes, created_at, expires_at, status, kind) VALUES ('Clip001', ?1, 'clips/Clip001.mp4', 'video/mp4', 220, ?2, ?3, 'ready', 'clip')",
      s.users[OTHER], nowSec(), nowSec() + 3600,
    );
    const out = await body(await vodStatus(s.env, OWNER));
    const row = s.db.q<{ created_at: number; expires_at: number }>("SELECT created_at, expires_at FROM clips WHERE id = ?1", id)[0];
    const now = out.now as number;
    expect(out).toMatchObject({ allowed: true, sharing: true, serving: true });
    expect(out.limits).toEqual({
      max_bytes: 2147483648, max_seconds: 7200, ttl_seconds: 259200, max_live: 3, max_pending: 1, max_starts_per_day: 6,
      part_size: 16777216, markers_max_bytes: 49152, notes_max_bytes: 14336, daily_play_cap: 200,
    });
    expect(out.usage).toEqual({ live: 1, pending: 0, starts_today: 1, day_resets_at: nextUtcMidnight(now) });
    const pool = out.pool as Record<string, unknown>;
    expect(pool).toMatchObject({
      total_budget_bytes: 9e9, vod_budget_bytes: 7e9, used_bytes: 720, vod_used_bytes: 500, free_bytes: 7e9 - 500,
    });
    expect(pool.releases).toEqual([
      { at: nextHour(now + 3600), bytes: 220, vod_bytes: 0 },
      { at: nextHour(row.expires_at), bytes: 500, vod_bytes: 500 },
    ]);
    expect(out.shares).toEqual([
      { id, status: "ready", quality: "1080p", size_bytes: 500, created_at: row.created_at, expires_at: row.created_at + VOD_TTL_SECONDS },
    ]);
  });

  it("does not count an idle upload as pending or live", async () => {
    const s = await vodSetup();
    const init = await body(await vodInitOk(s.env, OWNER));
    expect((await body(await vodStatus(s.env, OWNER))).usage).toMatchObject({ live: 1, pending: 1 });
    s.db.x("UPDATE clips SET last_activity_at = ?1 WHERE id = ?2", nowSec() - 901, init.id);
    const out = await body(await vodStatus(s.env, OWNER));
    expect(out.usage).toMatchObject({ live: 0, pending: 0 });
    expect((out.shares as { id: string; status: string }[]).map((x) => [x.id, x.status])).toEqual([[init.id, "uploading"]]);
  });

  it("reports both kill switches", async () => {
    const s = await vodSetup({ env: { VOD_SHARING: " OFF ", VOD_SERVING: "0" } });
    expect(await body(await vodStatus(s.env, OWNER))).toMatchObject({ allowed: true, sharing: false, serving: false });
    expect(await body(await vodStatus(s.env, OTHER))).toMatchObject({ allowed: false, sharing: false, serving: false });
  });

  it("needs a session and is rate limited at 30 a minute", async () => {
    const s = await vodSetup();
    const anon = await worker.fetch(makeRequest(`${BASE}/vod-shares/status`), s.env);
    expect(anon.status).toBe(401);
    expect(await body(anon)).toEqual({ error: "unauthorized" });
    for (let i = 0; i < 30; i++) expect((await vodStatus(s.env, OWNER)).status).toBe(200);
    const limited = await vodStatus(s.env, OWNER);
    expect(limited.status).toBe(429);
    expect((await body(limited)).error).toBe("rate_limited");
  });
});

describe("POST /vod-shares/uploads refusals, in order", () => {
  it("401 without a session, then the kill switch before the allowlist", async () => {
    const s = await vodSetup({ env: { VOD_SHARING: "off" } });
    const anon = await worker.fetch(makeRequest(`${BASE}/vod-shares/uploads?size=1&duration=1&quality=720p`, { method: "POST" }), s.env);
    expect(anon.status).toBe(401);
    expect(await body(anon)).toEqual({ error: "unauthorized" });
    for (const token of [OWNER, OTHER]) {
      const res = await vodInitOk(s.env, token);
      expect(res.status).toBe(503);
      expect(await body(res)).toEqual({ error: "vod_sharing_disabled", message: "Full-game sharing is paused right now." });
    }
  });

  it("403 not_allowed without a vod_sharers row, before any validation", async () => {
    const s = await vodSetup();
    const res = await vodInit(s.env, OTHER, { size: "nope" });
    expect(res.status).toBe(403);
    expect((await body(res)).error).toBe("not_allowed");
    expect(s.db.q("SELECT * FROM clips")).toHaveLength(0);
    expect(s.db.q("SELECT * FROM vod_share_usage")).toHaveLength(0);
  });

  it("429 rate_limited on the 7th init in a minute, separate from clip init", async () => {
    const s = await vodSetup();
    for (let i = 0; i < 6; i++) expect((await vodInit(s.env, OWNER, { type: "video/webm" })).status).toBe(415);
    const res = await vodInit(s.env, OWNER, { type: "video/webm" });
    expect(res.status).toBe(429);
    expect((await body(res)).error).toBe("rate_limited");
  });

  it("validates media type, size, duration and quality", async () => {
    const s = await vodSetup();
    const cases: Array<[Parameters<typeof vodInit>[2], number, string]> = [
      [{ size: 500, duration: 60, quality: "1080p", type: "video/webm" }, 415, "unsupported_media_type"],
      [{ duration: 60, quality: "1080p" }, 400, "bad_request"],
      [{ size: 0, duration: 60, quality: "1080p" }, 400, "bad_request"],
      [{ size: "1e3", duration: 60, quality: "1080p" }, 400, "bad_request"],
      [{ size: 2147483649, duration: 60, quality: "1080p" }, 413, "payload_too_large"],
      [{ size: 2147483648, duration: 0, quality: "1080p" }, 400, "bad_request"],
      [{ size: 500, duration: 7201, quality: "1080p" }, 400, "bad_request"],
      [{ size: 500, quality: "1080p" }, 400, "bad_request"],
      [{ size: 500, duration: 60, quality: "4k" }, 400, "bad_request"],
      [{ size: 500, duration: 60 }, 400, "bad_request"],
      [{ size: 500, duration: 60, quality: "1080p60" }, 400, "bad_request"],
      [{ size: 500, duration: 60, quality: "1080p", fps: 144 }, 400, "bad_request"],
      [{ size: 500, duration: 60, quality: "1080p", fps: "" }, 400, "bad_request"],
    ];
    for (const [q, status, error] of cases) {
      resetUserLimits();
      const res = await vodInit(s.env, OWNER, q);
      expect(res.status, JSON.stringify(q)).toBe(status);
      expect((await body(res)).error).toBe(error);
    }
    expect(s.db.q("SELECT * FROM clips")).toHaveLength(0);
    resetUserLimits();
    expect((await vodInit(s.env, OWNER, { size: 2147483648, duration: 7200, quality: "720p", type: "video/mp4; foo=1" })).status).toBe(201);
  });

  it("discards the caller's idle VOD uploads first, so they do not block the next one", async () => {
    const s = await vodSetup();
    const first = await body(await vodInitOk(s.env, OWNER));
    const row = s.db.q<{ r2_key: string; upload_id: string }>("SELECT r2_key, upload_id FROM clips WHERE id = ?1", first.id)[0];
    expect((await vodInitOk(s.env, OWNER)).status).toBe(429);
    s.db.x("UPDATE clips SET last_activity_at = ?1 WHERE id = ?2", nowSec() - 900, first.id);
    expect((await vodInitOk(s.env, OWNER)).status).toBe(201);
    expect(s.db.q("SELECT id FROM clips WHERE id = ?1", first.id)).toHaveLength(0);
    expect(s.r2.uploads.get(row.upload_id)!.state).toBe("aborted");
    expect(s.r2.calls.deleted).toContain(row.r2_key);
  });

  it("429 too_many_pending_uploads with one live upload, 409 live_limit at 3 live shares", async () => {
    const s = await vodSetup();
    await vodInitOk(s.env, OWNER);
    const pending = await vodInitOk(s.env, OWNER);
    expect(pending.status).toBe(429);
    expect((await body(pending)).error).toBe("too_many_pending_uploads");

    const t = await vodSetup();
    for (let i = 0; i < 3; i++) await shareVod(t, OWNER);
    resetUserLimits();
    const live = await vodInitOk(t.env, OWNER);
    expect(live.status).toBe(409);
    expect((await body(live)).error).toBe("live_limit");
    expect(t.db.q("SELECT starts FROM vod_share_usage")).toEqual([{ starts: 3 }]);
  });

  it("409 pool_full with retry_at and free_bytes, without spending a daily start", async () => {
    const s = await vodSetup({ env: { TOTAL_BUDGET_BYTES: "1000", VOD_BUDGET_BYTES: "1000" } });
    const { id } = await shareVod(s, OWNER, 600);
    const exp = s.db.q<{ expires_at: number }>("SELECT expires_at FROM clips WHERE id = ?1", id)[0].expires_at;
    // A second allowlisted account so the per-account limits stay out of the way.
    s.db.x("INSERT INTO vod_sharers (user_id, added_at) VALUES (?1, 1)", s.users[OTHER]);
    const res = await vodInit(s.env, OTHER, { size: 500, duration: 60, quality: "720p" });
    expect(res.status).toBe(409);
    expect(await body(res)).toEqual({ error: "pool_full", message: "Sharing space is full right now.", retry_at: nextHour(exp), free_bytes: 400 });
    const never = await vodInit(s.env, OTHER, { size: 1001, duration: 60, quality: "720p" });
    expect((await body(never)).retry_at).toBeNull();
    expect(s.db.q("SELECT * FROM vod_share_usage WHERE user_id = ?1", s.users[OTHER])).toHaveLength(0);
  });

  it("checks the VOD subset as well as the total", async () => {
    const s = await vodSetup({ env: { TOTAL_BUDGET_BYTES: "100000", VOD_BUDGET_BYTES: "400" } });
    const vodShort = await vodInitOk(s.env, OWNER, 500);
    expect(vodShort.status).toBe(409);
    expect((await body(vodShort)).free_bytes).toBe(400);
    // Clips (here a clip tombstone) can fill the total and crowd VODs out.
    s.db.x("INSERT INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at) VALUES ('clips/x.mp4', NULL, 99800, 'clip', 1)");
    const totalShort = await vodInitOk(s.env, OWNER, 300);
    expect(totalShort.status).toBe(409);
    expect((await body(totalShort)).free_bytes).toBe(200);
    expect((await vodInitOk(s.env, OWNER, 200)).status).toBe(201);
  });

  it("429 daily_limit after 6 starts in a UTC day, with retry_at and Retry-After", async () => {
    const s = await vodSetup();
    const now = nowSec();
    s.db.x("INSERT INTO vod_share_usage (user_id, day, starts) VALUES (?1, ?2, 6)", s.users[OWNER], utcDayOf(now));
    const res = await vodInitOk(s.env, OWNER);
    expect(res.status).toBe(429);
    const out = await body(res);
    expect(out).toMatchObject({ error: "daily_limit", retry_at: nextUtcMidnight(now) });
    const after = Number(res.headers.get("Retry-After"));
    expect(after).toBeGreaterThan(0);
    expect(Math.abs(after - (nextUtcMidnight(now) - now))).toBeLessThanOrEqual(2);
    // Yesterday's count does not carry over.
    s.db.x("UPDATE vod_share_usage SET day = ?1", utcDayOf(now - 86400));
    expect((await vodInitOk(s.env, OWNER)).status).toBe(201);
  });

  it("the daily start counter is one atomic statement that stops at 6", async () => {
    const s = await vodSetup();
    const sql = "INSERT INTO vod_share_usage (user_id, day, starts) VALUES (?1, ?2, 1) ON CONFLICT (user_id, day) DO UPDATE SET starts = starts + 1 WHERE starts < ?3 RETURNING starts";
    const results = await Promise.all(
      Array.from({ length: 9 }, () => s.env.DB.prepare(sql).bind(s.users[OWNER], "2026-10-14", 6).first<{ starts: number }>()),
    );
    expect(results.filter((r) => r !== null).map((r) => r!.starts)).toEqual([1, 2, 3, 4, 5, 6]);
    expect(s.db.q("SELECT starts FROM vod_share_usage")).toEqual([{ starts: 6 }]);
  });

  it("does not refund a start when R2 fails after it", async () => {
    const s = await vodSetup();
    s.r2.bucket.createMultipartUpload = (async () => { throw new Error("R2 down"); }) as typeof s.r2.bucket.createMultipartUpload;
    vi.spyOn(console, "error").mockImplementation(() => {});
    const res = await vodInitOk(s.env, OWNER);
    expect(res.status).toBe(502);
    expect((await body(res)).error).toBe("clip_error");
    expect(s.db.q("SELECT starts FROM vod_share_usage")).toEqual([{ starts: 1 }]);
    expect(s.db.q("SELECT * FROM clips")).toHaveLength(0);
  });

  it("reserves the declared size in an uploading row and returns the 201 shape", async () => {
    const s = await vodSetup();
    const size = 1_315_583_333;
    const now = nowSec();
    const res = await vodInit(s.env, OWNER, { size, duration: 2255, quality: "720p" });
    expect(res.status).toBe(201);
    const out = await body(res);
    const id = out.id as string;
    expect(id).toMatch(/^[0-9A-Za-z]{7}$/);
    expect(Object.keys(out).sort()).toEqual(["expires_at", "id", "part_count", "part_size", "share_expires_at", "url"]);
    const row = s.db.q<Record<string, unknown>>("SELECT * FROM clips WHERE id = ?1", id)[0];
    expect(out).toEqual({
      id, part_size: VOD_PART_SIZE_BYTES, part_count: Math.ceil(size / VOD_PART_SIZE_BYTES),
      expires_at: (row.created_at as number) + vodUploadWindowSeconds(size), share_expires_at: (row.created_at as number) + VOD_TTL_SECONDS,
      url: `https://revu.lol/v/${id}`,
    });
    expect(Math.abs((row.created_at as number) - now)).toBeLessThanOrEqual(2);
    expect(row).toMatchObject({
      kind: "vod", status: "uploading", quality: "720p", r2_key: `vods/${id}.mp4`, content_type: "video/mp4",
      size_bytes: size, duration_s: 2255, part_size: VOD_PART_SIZE_BYTES, part_count: 79, part_puts: 0,
      last_activity_at: row.created_at, title: null, champion: null, view_count: 0, narrated: 0, has_transcript: 0,
    });
    const upload = s.r2.uploads.get(row.upload_id as string)!;
    expect(upload.key).toBe(`vods/${id}.mp4`);
    expect(upload.state).toBe("live");
    expect(s.db.q("SELECT starts FROM vod_share_usage")).toEqual([{ starts: 1 }]);
  });

  it("keeps the copy's frame rate with its quality when the desktop sends one", async () => {
    const s = await vodSetup();
    const res = await vodInit(s.env, OWNER, { size: 1_517_117_364, duration: 2256, quality: "1080p", fps: 60 });
    expect(res.status).toBe(201);
    const id = (await body(res)).id as string;
    expect(s.db.q<{ quality: string }>("SELECT quality FROM clips WHERE id = ?1", id)[0].quality).toBe("1080p60");
    const status = await body(await vodStatus(s.env, OWNER));
    expect((status.shares as Array<{ id: string; quality: string }>).find(x => x.id === id)?.quality).toBe("1080p60");
  });

  it("the post-insert recount rolls back the loser of a pending race", async () => {
    const s = await vodSetup();
    const now = nowSec();
    // A concurrent init commits its row right after ours.
    s.db._after("INSERT INTO clips", () => {
      s.db.x(
        "INSERT INTO clips (id, user_id, r2_key, content_type, size_bytes, created_at, expires_at, status, kind, last_activity_at, part_count, part_size) VALUES ('Racer01', ?1, 'vods/Racer01.mp4', 'video/mp4', 10, ?2, ?3, 'uploading', 'vod', ?2, 1, 16777216)",
        s.users[OWNER], now, now + 21600,
      );
    });
    const res = await vodInitOk(s.env, OWNER);
    expect(res.status).toBe(429);
    expect((await body(res)).error).toBe("too_many_pending_uploads");
    expect(s.db.q<{ id: string }>("SELECT id FROM clips").map((r) => r.id)).toEqual(["Racer01"]);
    expect([...s.r2.uploads.values()].map((u) => u.state)).toEqual(["aborted"]);
  });

  it("the post-insert recount rolls back a budget overrun", async () => {
    const s = await vodSetup({ env: { TOTAL_BUDGET_BYTES: "1000", VOD_BUDGET_BYTES: "1000" } });
    s.db._after("INSERT INTO clips", () => {
      s.db.x("INSERT INTO storage_tombstones (r2_key, upload_id, size_bytes, kind, created_at) VALUES ('vods/Gone001.mp4', NULL, 600, 'vod', 1)");
    });
    const res = await vodInitOk(s.env, OWNER, 500);
    expect(res.status).toBe(409);
    const out = await body(res);
    expect(out).toMatchObject({ error: "pool_full", free_bytes: 400 });
    expect(out.retry_at).toBe(nextHour(nowSec()));
    expect(s.db.q("SELECT id FROM clips")).toHaveLength(0);
  });
});
