/**
 * Public VOD serving: GET /vod-meta/:id and GET|HEAD /vod-file/:id, with the
 * per-VOD daily play cap and the VOD_SERVING kill switch. Real SQLite + fake R2.
 */

import { beforeEach, describe, expect, it, vi } from "vitest";
import { resetUserLimits } from "../src/user-limits";
import { nextUtcMidnight, utcDayOf, VOD_DAILY_PLAY_CAP } from "../src/share-limits";
import { MARKERS_DOC, body, get, nowSec, shareVod, vodInitOk, vodMarkers, vodSetup, VodSetup } from "./vod-fixtures";

beforeEach(() => {
  vi.restoreAllMocks();
  resetUserLimits();
});

const OWNER = "sess-owner";

async function ready(opts: Parameters<typeof vodSetup>[0] = {}): Promise<VodSetup & { id: string }> {
  const s = await vodSetup(opts);
  const { id } = await shareVod(s, OWNER, 500);
  return { ...s, id };
}

const plays = (s: VodSetup, id: string) =>
  s.db.q<{ play_day: string | null; play_count: number }>("SELECT play_day, play_count FROM clips WHERE id = ?1", id)[0];

describe("GET /vod-meta/:id", () => {
  it("returns the public shape with no-store and noindex, and never internal fields", async () => {
    const s = await ready();
    const row = s.db.q<Record<string, number>>("SELECT created_at, expires_at FROM clips WHERE id = ?1", s.id)[0];
    const before = await get(s.env, `/vod-meta/${s.id}`, { headers: { Origin: "https://revu.lol" } });
    expect(before.status).toBe(200);
    expect(before.headers.get("Cache-Control")).toBe("no-store");
    expect(before.headers.get("X-Robots-Tag")).toBe("noindex, nofollow");
    expect(before.headers.get("Access-Control-Allow-Origin")).toBe("https://revu.lol");
    expect(await body(before)).toEqual({
      id: s.id, kind: "vod", content_type: "video/mp4", quality: "1080p", duration_s: 1800, size_bytes: 500,
      created_at: row.created_at, expires_at: row.expires_at, serving: true, play_limited: false, meta: null,
    });

    await vodMarkers(s.env, OWNER, s.id, MARKERS_DOC);
    const after = await body(await get(s.env, `/vod-meta/${s.id}`));
    expect(after.meta).toMatchObject({ version: 1, gameTimeAtVideoStart: 12.35, notes: null });
    expect((after.meta as { markers: unknown[] }).markers).toHaveLength(2);
    for (const key of ["user_id", "upload_id", "r2_key", "status", "part_size", "part_count", "part_puts", "play_day", "play_count"]) {
      expect(after).not.toHaveProperty(key);
    }
  });

  it("reports serving off and the play limit", async () => {
    const s = await ready({ env: { VOD_SERVING: "off" } });
    s.db.x("UPDATE clips SET play_day = ?1, play_count = ?2 WHERE id = ?3", utcDayOf(nowSec()), VOD_DAILY_PLAY_CAP, s.id);
    expect(await body(await get(s.env, `/vod-meta/${s.id}`))).toMatchObject({ serving: false, play_limited: true });
    s.db.x("UPDATE clips SET play_day = ?1 WHERE id = ?2", utcDayOf(nowSec() - 86400), s.id);
    expect((await body(await get(s.env, `/vod-meta/${s.id}`))).play_limited).toBe(false);
  });

  it("404s a bad slug, an unknown id, a pending upload and an expired share", async () => {
    const s = await ready();
    const pendingId = (await body(await vodInitOk(s.env, OWNER, 500))).id as string;
    expect((await get(s.env, `/vod-meta/${pendingId}`)).status).toBe(404);
    expect((await get(s.env, `/vod-file/${pendingId}`)).status).toBe(404);
    for (const id of ["short", "Nope123", "abc12345"]) {
      const res = await get(s.env, `/vod-meta/${id}`);
      expect(res.status, id).toBe(404);
      expect(await body(res)).toEqual({ error: "not_found" });
    }
    s.db.x("UPDATE clips SET expires_at = ?1 WHERE id = ?2", nowSec(), s.id);
    expect((await get(s.env, `/vod-meta/${s.id}`)).status).toBe(404);
    expect((await get(s.env, `/vod-file/${s.id}`)).status).toBe(404);
  });
});

describe("GET|HEAD /vod-file/:id", () => {
  it("serves with the VOD headers, ranges and 416, passing R2's body through", async () => {
    const s = await ready();
    const full = await get(s.env, `/vod-file/${s.id}`);
    expect(full.status).toBe(200);
    for (const [k, v] of Object.entries({
      "Content-Type": "video/mp4",
      "Accept-Ranges": "bytes",
      "Cache-Control": "private, max-age=3600",
      "X-Content-Type-Options": "nosniff",
      "Content-Security-Policy": "sandbox",
      "X-Robots-Tag": "noindex, nofollow",
      "Content-Disposition": `inline; filename="revu-game-${s.id}.mp4"`,
      "Content-Length": "500",
    })) expect(full.headers.get(k), k).toBe(v);
    expect((await full.arrayBuffer()).byteLength).toBe(500);

    const part = await get(s.env, `/vod-file/${s.id}`, { headers: { Range: "bytes=100-199" } });
    expect(part.status).toBe(206);
    expect(part.headers.get("Content-Range")).toBe("bytes 100-199/500");
    expect((await part.arrayBuffer()).byteLength).toBe(100);
    const past = await get(s.env, `/vod-file/${s.id}`, { headers: { Range: "bytes=500-" } });
    expect(past.status).toBe(416);
    expect(past.headers.get("Content-Range")).toBe("bytes */500");
    const head = await get(s.env, `/vod-file/${s.id}`, { method: "HEAD" });
    expect(head.status).toBe(200);
    expect(head.headers.get("Content-Length")).toBe("500");
  });

  it("counts a play only for a GET with no Range, an ignored Range, or a Range from byte 0", async () => {
    const s = await ready();
    const today = utcDayOf(nowSec());
    const cases: Array<[RequestInit, number]> = [
      [{}, 1],
      [{ headers: { Range: "bytes=0-" } }, 2],
      [{ headers: { Range: "bytes=0-99" } }, 3],
      [{ headers: { Range: "items=5-9" } }, 4], // ignored: the whole file
      [{ headers: { Range: "bytes=100-" } }, 4],
      [{ headers: { Range: "bytes=-50" } }, 4],
      [{ headers: { Range: "bytes=500-" } }, 4], // unsatisfiable
      [{ method: "HEAD" }, 4],
      [{ method: "HEAD", headers: { Range: "bytes=0-" } }, 4],
    ];
    for (const [init, expected] of cases) {
      await get(s.env, `/vod-file/${s.id}`, init);
      expect(plays(s, s.id), JSON.stringify(init)).toEqual({ play_day: today, play_count: expected });
    }
  });

  it("refuses every request once the day's cap is reached, until the next UTC day", async () => {
    const s = await ready();
    const now = nowSec();
    s.db.x("UPDATE clips SET play_day = ?1, play_count = ?2 WHERE id = ?3", utcDayOf(now), VOD_DAILY_PLAY_CAP - 1, s.id);
    expect((await get(s.env, `/vod-file/${s.id}`)).status).toBe(200);
    expect(plays(s, s.id).play_count).toBe(VOD_DAILY_PLAY_CAP);

    for (const init of [{}, { headers: { Range: "bytes=0-" } }, { headers: { Range: "bytes=100-" } }, { method: "HEAD" }] as RequestInit[]) {
      const res = await get(s.env, `/vod-file/${s.id}`, init);
      expect(res.status, JSON.stringify(init)).toBe(429);
      expect(res.headers.get("X-Robots-Tag")).toBe("noindex, nofollow");
      const after = Number(res.headers.get("Retry-After"));
      expect(Math.abs(after - (nextUtcMidnight(now) - now))).toBeLessThanOrEqual(2);
      if (init.method !== "HEAD") {
        expect(await body(res)).toEqual({ error: "play_limit", message: "This game has been watched a lot today. Try again tomorrow." });
      }
    }
    expect(plays(s, s.id).play_count).toBe(VOD_DAILY_PLAY_CAP);
    expect((await body(await get(s.env, `/vod-meta/${s.id}`))).play_limited).toBe(true);

    // Day rollover: yesterday's full count starts over at 1.
    s.db.x("UPDATE clips SET play_day = ?1 WHERE id = ?2", utcDayOf(now - 86400), s.id);
    expect((await get(s.env, `/vod-file/${s.id}`, { headers: { Range: "bytes=0-1" } })).status).toBe(206);
    expect(plays(s, s.id)).toEqual({ play_day: utcDayOf(now), play_count: 1 });
  });

  it("the play counter UPDATE never passes the cap", async () => {
    const s = await ready();
    const results = await Promise.all(Array.from({ length: VOD_DAILY_PLAY_CAP + 25 }, () => get(s.env, `/vod-file/${s.id}`)));
    const statuses = results.map((r) => r.status);
    expect(statuses.filter((x) => x === 200)).toHaveLength(VOD_DAILY_PLAY_CAP);
    expect(statuses.filter((x) => x === 429)).toHaveLength(25);
    expect(plays(s, s.id).play_count).toBe(VOD_DAILY_PLAY_CAP);
  });

  it("answers 503 when VOD_SERVING is off, and 404 before that for an unknown id", async () => {
    const s = await ready({ env: { VOD_SERVING: "0" } });
    const res = await get(s.env, `/vod-file/${s.id}`);
    expect(res.status).toBe(503);
    expect(await body(res)).toEqual({ error: "vod_serving_disabled", message: "Playback is paused right now. Try again later." });
    expect(plays(s, s.id).play_count).toBe(0);
    expect((await get(s.env, "/vod-file/Nope123")).status).toBe(404);
  });
});
