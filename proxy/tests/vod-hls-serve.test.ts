/**
 * HLS shares, public side: the playlist text, play tokens and the play cap,
 * the host pin, fetch metadata, headers and caching, the kill switches, the
 * per-isolate caches, /vod-meta and /vod-file. Real SQLite + fake R2.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { buildPlaylist } from "../src/vod-hls-serve";
import { loggablePath } from "../src/http";
import { PLAY_TOKEN_TTL_SECONDS } from "../src/play-token";
import { VOD_DAILY_PLAY_CAP, nextUtcMidnight, utcDayOf } from "../src/share-limits";
import worker from "../src/index";
import { makeRequest } from "./fakes";
import {
  BASE, OWNER, SITE, call, clipRow, countR2, endShare, hlsInit, hlsSetup, initBytes, json, liveShare, media, nowSec, pause,
  play, putRun, putSeg, resetHlsState, segBytes, token,
} from "./hls-fixtures";
import { shareVod } from "./vod-fixtures";

beforeEach(() => {
  vi.restoreAllMocks();
  resetHlsState();
});
afterEach(() => {
  vi.useRealTimers();
});

const HEAD_LINES = [
  "#EXTM3U",
  "#EXT-X-VERSION:7",
  "#EXT-X-TARGETDURATION:6",
  "#EXT-X-MEDIA-SEQUENCE:0",
  "#EXT-X-PLAYLIST-TYPE:EVENT",
  "#EXT-X-INDEPENDENT-SEGMENTS",
  "#EXT-X-START:TIME-OFFSET=0,PRECISE=YES",
  '#EXT-X-MAP:URI="init-0.mp4"',
];
const seg = (seq: number, ms = 2000) => [`#EXTINF:${(ms / 1000).toFixed(3)},`, `${seq}.m4s`];
const text = (lines: string[]) => lines.join("\n") + "\n";

describe("the playlist text", () => {
  it("lists committed segments under run 0's init, with ENDLIST once ended", () => {
    const one = { durations: [2000, 2000, 1999, 1], runs: [{ k: 0, from: 0, sameInit: false }], ended: false };
    expect(buildPlaylist(one)).toBe(text([...HEAD_LINES, ...seg(0), ...seg(1), ...seg(2, 1999), "#EXTINF:0.001,", "3.m4s"]));
    expect(buildPlaylist({ ...one, ended: true })).toBe(text([...HEAD_LINES, ...seg(0), ...seg(1), ...seg(2, 1999), "#EXTINF:0.001,", "3.m4s", "#EXT-X-ENDLIST"]));
    expect(buildPlaylist({ durations: [], runs: [], ended: false })).toBe(text(HEAD_LINES));
  });

  it("marks every resume with a DISCONTINUITY and a new MAP only when the init changed", () => {
    const runs = [
      { k: 0, from: 0, sameInit: false },
      { k: 1, from: 2, sameInit: true },
      { k: 2, from: 3, sameInit: false },
      { k: 3, from: 9, sameInit: true }, // not committed yet: nothing listed
    ];
    const durations = [2000, 2000, 2000, 2000];
    expect(buildPlaylist({ durations, runs, ended: false })).toBe(text([
      ...HEAD_LINES, ...seg(0), ...seg(1),
      "#EXT-X-DISCONTINUITY", ...seg(2),
      "#EXT-X-DISCONTINUITY", '#EXT-X-MAP:URI="init-2.mp4"', ...seg(3),
    ]));
    // The hls.js variant gate G3 may pick: no DISCONTINUITY at a same-init resume.
    expect(buildPlaylist({ durations, runs, ended: true }, { discontinuityAtSameInit: false })).toBe(text([
      ...HEAD_LINES, ...seg(0), ...seg(1), ...seg(2),
      "#EXT-X-DISCONTINUITY", '#EXT-X-MAP:URI="init-2.mp4"', ...seg(3), "#EXT-X-ENDLIST",
    ]));
  });

  it("is served from the rows: a same-init resume and a 720p resume, then ENDLIST", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 3);
    expect((await putRun(s.env, id, 1, { from: 3 })).status).toBe(200); // same encoder: same init
    expect((await putSeg(s.env, id, 3, { run: 1 })).status).toBe(200);
    expect((await putRun(s.env, id, 2, { from: 4, quality: "720p", bytes: initBytes(1390, 7) })).status).toBe(200);
    expect((await putSeg(s.env, id, 4, { run: 2, ms: 1500 })).status).toBe(200);
    const t = await token(s, id);
    const res = await media(s.env, id, t, "index.m3u8");
    expect(res.status).toBe(200);
    expect(res.headers.get("Content-Type")).toBe("application/vnd.apple.mpegurl");
    const expected = [
      ...HEAD_LINES, ...seg(0), ...seg(1), ...seg(2),
      "#EXT-X-DISCONTINUITY", ...seg(3),
      "#EXT-X-DISCONTINUITY", '#EXT-X-MAP:URI="init-2.mp4"', ...seg(4, 1500),
    ];
    expect(await res.text()).toBe(text(expected));
    expect((await endShare(s.env, id, 5)).status).toBe(201);
    vi.useFakeTimers();
    vi.setSystemTime(Date.now() + 1500); // past the 1 s isolate cache
    expect(await (await media(s.env, id, t, "index.m3u8")).text()).toBe(text([...expected, "#EXT-X-ENDLIST"]));
  });

  it("is playable at exactly 8,000 ms of committed media", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4, { ms: (q) => (q === 3 ? 1999 : 2000) });
    const early = await play(s.env, id);
    expect(early.status).toBe(404);
    expect((await json(early)).error).toBe("not_ready");
    expect(clipRow(s, id)?.play_count).toBe(0);
    expect((await putSeg(s.env, id, 4, { ms: 1 })).status).toBe(200); // 7,999 + 1
    expect((await play(s.env, id)).status).toBe(200);
    // Ended with a single segment is playable too.
    const t = await hlsSetup();
    const short = await liveShare(t, 1);
    expect((await play(t.env, short)).status).toBe(404);
    expect((await endShare(t.env, short, 1)).status).toBe(201);
    expect((await play(t.env, short)).status).toBe(200);
  });
});

describe("POST /vod-play/:id", () => {
  it("counts one play and mints a 12 h token; this is the only place a play counts", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const res = await play(s.env, id);
    expect(res.status).toBe(200);
    expect(res.headers.get("Cache-Control")).toBe("no-store");
    expect(res.headers.get("Access-Control-Allow-Origin")).toBe(SITE);
    const out = await json(res);
    expect(out.token).toMatch(/^\d{10}\.[A-Za-z0-9_-]{22}$/);
    expect(Math.abs((out.expires_at as number) - (nowSec() + PLAY_TOKEN_TTL_SECONDS))).toBeLessThanOrEqual(2);
    expect((out.token as string).split(".")[0]).toBe(String(out.expires_at));
    expect(clipRow(s, id)).toMatchObject({ play_day: utcDayOf(nowSec()), play_count: 1 });
    const t = out.token as string;
    for (const file of ["index.m3u8", "init-0.mp4", "0.m4s", "3.m4s"]) expect((await media(s.env, id, t, file)).status, file).toBe(200);
    expect(clipRow(s, id)?.play_count).toBe(1);
  });

  it("refuses: 403 without an allowed Origin, 404 for an MP4 share, an unknown or stopped one", async () => {
    const s = await hlsSetup({ allow: ["sess-owner", "sess-other"] });
    const id = await liveShare(s, 4);
    for (const headers of [{}, { Origin: "https://evil.example" }, { Origin: "null" }]) {
      const res = await play(s.env, id, headers as Record<string, string>);
      expect(res.status, JSON.stringify(headers)).toBe(403);
      expect((await json(res)).error).toBe("forbidden_origin");
    }
    const mp4 = await shareVod(s, "sess-other", 500);
    expect((await play(s.env, mp4.id)).status).toBe(404);
    expect((await play(s.env, "Nope123")).status).toBe(404);
    expect((await call(s.env, `/vod-play/${id}`, { method: "GET", headers: { Origin: SITE } })).status).toBe(405);
    s.db.x("UPDATE clips SET status = 'purging' WHERE id = ?1", id);
    expect((await play(s.env, id)).status).toBe(404);
    expect(clipRow(s, id)?.play_count).toBe(0);
  });

  it("past the daily cap: 429 play_limit until UTC midnight, while existing token holders keep watching", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const t = await token(s, id);
    const now = nowSec();
    s.db.x("UPDATE clips SET play_day = ?1, play_count = ?2 WHERE id = ?3", utcDayOf(now), VOD_DAILY_PLAY_CAP, id);
    const res = await play(s.env, id);
    expect(res.status).toBe(429);
    expect(await json(res)).toEqual({ error: "play_limit", message: "This game has been watched a lot today. Try again tomorrow." });
    expect(Math.abs(Number(res.headers.get("Retry-After")) - (nextUtcMidnight(now) - now))).toBeLessThanOrEqual(2);
    for (const file of ["index.m3u8", "init-0.mp4", "1.m4s"]) expect((await media(s.env, id, t, file)).status, file).toBe(200);
    s.db.x("UPDATE clips SET play_day = ?1 WHERE id = ?2", utcDayOf(now - 86400), id);
    expect((await play(s.env, id)).status).toBe(200);
    expect(clipRow(s, id)?.play_count).toBe(1);
  });

  it("never counts past the cap under concurrency", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const results = await Promise.all(Array.from({ length: VOD_DAILY_PLAY_CAP + 10 }, () => play(s.env, id)));
    expect(results.filter((r) => r.status === 200)).toHaveLength(VOD_DAILY_PLAY_CAP);
    expect(results.filter((r) => r.status === 429)).toHaveLength(10);
    expect(clipRow(s, id)?.play_count).toBe(VOD_DAILY_PLAY_CAP);
  });
});

describe("media routes: checks before any D1 or R2 work", () => {
  /** Counts every D1 statement and R2 call made from here on. */
  function spies(s: Awaited<ReturnType<typeof hlsSetup>>) {
    const r2 = countR2(s);
    const prepare = vi.spyOn(s.db, "prepare");
    const batch = vi.spyOn(s.db, "batch");
    return () => ({ d1: prepare.mock.calls.length + batch.mock.calls.length, r2: r2.total() });
  }

  it("a bad, forged, expired or other share's token is 403 bad_token with no D1 or R2 call", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const other = await (async () => {
      const t = await hlsSetup();
      return token(t, await liveShare(t, 4));
    })();
    const good = await token(s, id);
    const [exp, sig] = good.split(".");
    const count = spies(s);
    const bad = [
      "nope",
      `${exp}.${"A".repeat(22)}`,
      `${exp}.${sig.slice(0, 21)}`,
      `${Number(exp) + 1}.${sig}`,
      other, // minted for another id
    ];
    for (const t of bad) {
      for (const file of ["index.m3u8", "init-0.mp4", "0.m4s"]) {
        const res = await media(s.env, id, t, file);
        expect(res.status, `${t} ${file}`).toBe(403);
        expect(await json(res)).toEqual({ error: "bad_token" });
        expect(res.headers.get("Cache-Control")).toBe("no-store");
      }
    }
    expect(count()).toEqual({ d1: 0, r2: 0 });
    // Expired: the same token 12 h later.
    vi.useFakeTimers();
    vi.setSystemTime((Number(exp) + 1) * 1000);
    expect((await media(s.env, id, good, "index.m3u8")).status).toBe(403);
    expect(count()).toEqual({ d1: 0, r2: 0 });
  });

  it("answers only on the host of PUBLIC_BASE: workers.dev gets 404 for /vod-hls, /vod-play and /vod-file", async () => {
    const s = await hlsSetup({ allow: ["sess-owner", "sess-other"] });
    const id = await liveShare(s, 4);
    const t = await token(s, id);
    const mp4 = await shareVod(s, "sess-other", 500);
    const count = spies(s);
    const off = "https://revu-proxy.lol-review.workers.dev";
    for (const [path, method] of [[`/vod-hls/${id}/${t}/index.m3u8`, "GET"], [`/vod-play/${id}`, "POST"], [`/vod-file/${mp4.id}`, "GET"]]) {
      const res = await worker.fetch(makeRequest(`${off}${path}`, { method, headers: { Origin: SITE } }), s.env);
      expect(res.status, path).toBe(404);
    }
    expect(count()).toEqual({ d1: 0, r2: 0 });
    expect((await call(s.env, `/vod-file/${mp4.id}`)).status).toBe(200); // the MP4 path is unchanged on the clips host
  });

  it("refuses a cross-site request (403) unless its Origin is allowed; same-site and no metadata pass", async () => {
    const s = await hlsSetup({ env: { ALLOWED_ORIGINS: "https://revu.lol,http://localhost:8788" } });
    const id = await liveShare(s, 4);
    const t = await token(s, id);
    const count = spies(s);
    for (const headers of [{ "Sec-Fetch-Site": "cross-site" }, { "Sec-Fetch-Site": "cross-site", Origin: "https://evil.example" }]) {
      const res = await media(s.env, id, t, "0.m4s", { headers });
      expect(res.status).toBe(403);
      expect((await json(res)).error).toBe("forbidden_origin");
    }
    expect(count()).toEqual({ d1: 0, r2: 0 });
    for (const headers of [{ "Sec-Fetch-Site": "same-site", Origin: SITE }, { "Sec-Fetch-Site": "cross-site", Origin: "http://localhost:8788" }, {}]) {
      expect((await media(s.env, id, t, "0.m4s", { headers })).status, JSON.stringify(headers)).toBe(200);
    }
  });

  it("503 vod_serving_disabled from the VOD_SERVING var and from the vod_serving ops flag", async () => {
    const off = await hlsSetup({ env: { VOD_SERVING: "0" } });
    const id = await liveShare(off, 4);
    expect((await play(off.env, id)).status).toBe(503);
    off.env.VOD_SERVING = "1";
    const t = await token(off, id);
    off.env.VOD_SERVING = "off";
    for (const file of ["index.m3u8", "init-0.mp4", "0.m4s"]) {
      const res = await media(off.env, id, t, file);
      expect(res.status).toBe(503);
      expect((await json(res)).error).toBe("vod_serving_disabled");
    }
    off.env.VOD_SERVING = "1";
    off.db.x("INSERT INTO ops_flags (name, value) VALUES ('vod_serving', '0')");
    resetHlsState(); // the flag is cached for 30 s per isolate
    expect((await media(off.env, id, t, "0.m4s")).status).toBe(503);
    expect((await play(off.env, id)).status).toBe(503);
    expect((await json(await call(off.env, `/vod-meta/${id}`))).serving).toBe(false);
    off.db.x("DELETE FROM ops_flags");
    resetHlsState();
    expect((await media(off.env, id, t, "0.m4s")).status).toBe(200);
    // No secret: nothing can be played, and no token can be minted.
    const nosecret = await hlsSetup({ env: { VOD_PLAY_SECRET: undefined } });
    expect((await media(nosecret.env, id, t, "0.m4s")).status).toBe(503);
    const mint = await play(nosecret.env, id);
    expect(mint.status).toBe(503);
    expect((await json(mint)).error).toBe("vod_serving_disabled");
    expect((await media(off.env, id, t, "index.m3u8", { method: "HEAD" })).status).toBe(200);
  });

  it("caches the ops flag for 30 s per isolate", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(Date.UTC(2026, 9, 14, 12, 0, 0));
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const t = await token(s, id);
    s.db.x("INSERT INTO ops_flags (name, value) VALUES ('vod_serving', '0')");
    expect((await media(s.env, id, t, "0.m4s")).status).toBe(200);
    vi.setSystemTime(Date.now() + 30_001);
    expect((await media(s.env, id, t, "0.m4s")).status).toBe(503);
  });
});

describe("media responses", () => {
  it("carry nosniff, noindex, CORP same-site, Vary: Origin and CORS, and never public or s-maxage", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const minted = await play(s.env, id);
    const t = (await json(minted.clone())).token as string;
    const responses: Response[] = [minted];
    const init = { headers: { Origin: SITE } };
    for (const file of ["index.m3u8", "init-0.mp4", "0.m4s", "9.m4s", "init-5.mp4"]) responses.push(await media(s.env, id, t, file, init));
    responses.push(await media(s.env, id, t, "0.m4s", { headers: { Origin: SITE, Range: "bytes=0-99" } }));
    responses.push(await media(s.env, id, t, "0.m4s", { method: "HEAD", headers: { Origin: SITE } }));
    responses.push(await media(s.env, id, "bad", "0.m4s", init));
    const meta = await call(s.env, `/vod-meta/${id}`, init);
    expect(meta.headers.get("Cache-Control")).toBe("no-store");
    for (const [i, res] of responses.entries()) {
      expect(res.headers.get("Cache-Control") ?? "", String(i)).not.toMatch(/public|s-maxage/);
      expect(res.headers.get("X-Content-Type-Options"), String(i)).toBe("nosniff");
      expect(res.headers.get("X-Robots-Tag"), String(i)).toBe("noindex, nofollow");
      expect(res.headers.get("Cross-Origin-Resource-Policy"), String(i)).toBe("same-site");
      expect(res.headers.get("Vary"), String(i)).toBe("Origin");
      expect(res.headers.get("Access-Control-Allow-Origin"), String(i)).toBe(SITE);
    }
    const [, playlist, initGet, segment, missing, noRun, ranged, head] = responses;
    expect(playlist.headers.get("Cache-Control")).toBe("no-store");
    expect(playlist.headers.get("Server-Timing")).toMatch(/^d1;dur=\d+, r2;dur=\d+$/);
    expect(initGet.headers.get("Cache-Control")).toBe("private, max-age=3600");
    expect(initGet.headers.get("Content-Security-Policy")).toBe("sandbox");
    expect(new Uint8Array(await initGet.arrayBuffer())).toEqual(initBytes());
    expect(segment.headers.get("Cache-Control")).toBe("private, max-age=3600");
    expect(segment.headers.get("Content-Security-Policy")).toBe("sandbox");
    expect(segment.headers.get("Content-Type")).toBe("video/mp4");
    expect(segment.headers.get("Server-Timing")).toMatch(/^d1;dur=\d+, r2;dur=\d+$/);
    expect(new Uint8Array(await segment.arrayBuffer())).toEqual(segBytes(0));
    expect(missing.status).toBe(404); // past the committed edge
    expect(noRun.status).toBe(404);
    expect(ranged.status).toBe(206);
    expect(ranged.headers.get("Content-Range")).toBe("bytes 0-99/4096");
    expect(head.status).toBe(200);
    expect(head.headers.get("Content-Length")).toBe("4096");
  });

  it("serves conditional requests (304) and 404s an unknown shape", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const t = await token(s, id);
    const etag = (await media(s.env, id, t, "1.m4s")).headers.get("ETag")!;
    const again = await media(s.env, id, t, "1.m4s", { headers: { "If-None-Match": etag } });
    expect(again.status).toBe(304);
    for (const file of ["index.m3u", "1.ts", "init.mp4", "init-0.mp4/x", "123456.m4s"]) {
      expect((await media(s.env, id, t, file)).status, file).toBe(404);
    }
    expect((await media(s.env, id, t, "0.m4s", { method: "POST" })).status).toBe(405);
  });
});

describe("per-isolate caches", () => {
  it("a segment committed after the cache was filled is served (one refresh), a later stop within 2 s", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(Date.UTC(2026, 9, 14, 12, 0, 0));
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const t = await token(s, id);
    expect((await media(s.env, id, t, "3.m4s")).status).toBe(200);
    expect((await putSeg(s.env, id, 4)).status).toBe(200);
    const count = vi.spyOn(s.db, "batch");
    expect((await media(s.env, id, t, "4.m4s")).status).toBe(404); // the state is under 250 ms old: no D1
    expect(count).toHaveBeenCalledTimes(0);
    vi.setSystemTime(Date.now() + 300);
    expect((await media(s.env, id, t, "4.m4s")).status).toBe(200); // past the cached edge: one refresh
    expect(count).toHaveBeenCalledTimes(1);
    expect((await media(s.env, id, t, "2.m4s")).status).toBe(200); // cached: no D1
    for (let i = 0; i < 5; i++) expect((await media(s.env, id, t, "5.m4s")).status).toBe(404); // not committed
    expect(count).toHaveBeenCalledTimes(1); // asking past the edge again and again costs no D1 read
    // Stop sharing: the objects are gone at once; the cached state lasts at most 2 s.
    expect((await call(s.env, `/clips/${id}`, { method: "DELETE", headers: { Authorization: `Bearer ${OWNER}` } })).status).toBe(200);
    expect((await media(s.env, id, t, "2.m4s")).status).toBe(404); // the object is gone
    vi.setSystemTime(Date.now() + 2001);
    for (const file of ["index.m3u8", "init-0.mp4", "1.m4s"]) expect((await media(s.env, id, t, file)).status, file).toBe(404);
  });

  it("the playlist is rebuilt at most once a second per isolate, and lists only committed rows", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(Date.UTC(2026, 9, 14, 12, 0, 0));
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const t = await token(s, id);
    const count = (body: string) => body.split("\n").filter((l) => l.endsWith(".m4s")).length;
    expect(count(await (await media(s.env, id, t, "index.m3u8")).text())).toBe(4);
    expect((await putSeg(s.env, id, 5)).status).toBe(200); // out of order: not contiguous
    expect((await putSeg(s.env, id, 4)).status).toBe(200);
    expect(count(await (await media(s.env, id, t, "index.m3u8")).text())).toBe(4); // cached
    vi.setSystemTime(Date.now() + 1001);
    expect(count(await (await media(s.env, id, t, "index.m3u8")).text())).toBe(6);
  });
});

describe("GET /vod-meta/:id for an HLS share", () => {
  it("adds the HLS fields while uploading and after the end, never internal ones", async () => {
    vi.useFakeTimers();
    const t0 = Date.UTC(2026, 9, 14, 12, 0, 0);
    vi.setSystemTime(t0);
    const s = await hlsSetup();
    const created = await json(await hlsInit(s.env, OWNER, { duration: 1834 }));
    const id = created.id as string;
    const first = await json(await call(s.env, `/vod-meta/${id}`));
    expect(first).toMatchObject({
      status: "uploading", format: "hls", playable: false, media_seconds: 0, ingest_x: null, last_commit_age_s: null,
      paused_reason: null, ended: false, truncated: false, play_limited: false, progress: { parts: 0, part_count: 938 },
      duration_s: 1834, now: t0 / 1000,
    });
    expect((await putRun(s.env, id, 0)).status).toBe(200);
    for (let seq = 0; seq < 5; seq++) {
      vi.setSystemTime(t0 + 250 * (seq + 1));
      expect((await putSeg(s.env, id, seq, { ms: seq === 4 ? 1234 : 2000 })).status).toBe(200);
    }
    expect((await pause(s.env, id)).status).toBe(200);
    vi.setSystemTime(t0 + 1250 + 3000);
    const during = await json(await call(s.env, `/vod-meta/${id}`));
    expect(during).toMatchObject({
      status: "uploading", playable: true, media_seconds: 9.234, ingest_x: expect.any(Number), last_commit_age_s: 3,
      paused_reason: "game", stalled: false, progress: { parts: 5, part_count: 938 },
    });
    expect(during.ingest_x as number).toBeGreaterThan(1);
    for (const key of ["hls_bytes", "part_puts", "r2_key", "upload_id", "user_id", "max_seq", "token"]) expect(during).not.toHaveProperty(key);
    expect((await endShare(s.env, id, 5, true)).status).toBe(201);
    const after = await json(await call(s.env, `/vod-meta/${id}`));
    expect(after).toMatchObject({
      format: "hls", playable: true, ended: true, truncated: true, paused_reason: null, duration_s: 10, media_seconds: 9.234,
      size_bytes: 1408 + 5 * 4096, now: expect.any(Number),
    });
    expect(after).not.toHaveProperty("status");
  });

  it("MP4 shares are unchanged apart from now", async () => {
    const s = await hlsSetup();
    const { id } = await shareVod(s, OWNER, 500);
    const out = await json(await call(s.env, `/vod-meta/${id}`));
    expect(out).not.toHaveProperty("format");
    expect(out).not.toHaveProperty("progress");
    expect(Math.abs((out.now as number) - nowSec())).toBeLessThanOrEqual(2);
  });
});

describe("GET|HEAD /vod-file/:id", () => {
  it("never serves an HLS share, uploading or ended", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    expect((await call(s.env, `/vod-file/${id}`)).status).toBe(404);
    expect((await endShare(s.env, id, 4)).status).toBe(201);
    expect((await call(s.env, `/vod-file/${id}`)).status).toBe(404);
    expect((await call(s.env, `/vod-file/${id}`, { method: "HEAD" })).status).toBe(404);
    expect(clipRow(s, id)?.play_count).toBe(0);
  });
});

describe("routing and logs", () => {
  it("an unknown HLS media path or token on the clips host is 404 JSON with noindex", async () => {
    const s = await hlsSetup();
    const res = await call(s.env, "/vod-hls/abc");
    expect(res.status).toBe(404);
    expect(res.headers.get("X-Robots-Tag")).toBe("noindex, nofollow");
    expect(BASE).toBe("https://clips.revu.lol");
  });

  it("never writes a play token to the logs, even when a media request fails", async () => {
    expect(loggablePath("/vod-hls/abc1234/1792080693.GlQnv83UG4ypbpBklXEBIf/3.m4s")).toBe("/vod-hls/abc1234/-/3.m4s");
    expect(loggablePath("/vod-meta/abc1234")).toBe("/vod-meta/abc1234");
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    const t = await token(s, id);
    const logged: string[] = [];
    vi.spyOn(console, "error").mockImplementation((...a: unknown[]) => { logged.push(a.map(String).join(" ")); });
    s.db._failNext("FROM clips c JOIN vod_hls h");
    const res = await media(s.env, id, t, "index.m3u8");
    expect(res.status).toBe(502);
    expect(logged.join("\n")).toContain(`/vod-hls/${id}/-/index.m3u8`);
    expect(logged.join("\n")).not.toContain(t.split(".")[1]);
  });
});
