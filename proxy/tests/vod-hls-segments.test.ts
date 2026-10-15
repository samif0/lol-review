/**
 * HLS shares, desktop side: segment PUTs (claim, pending rows, reservation
 * growth, commit in waitUntil, contiguous edge, stop during a PUT), pause and
 * end. Real SQLite (every migration) + fake R2.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { HLS_SEGMENT_MAX_BYTES, VOD_MAX_BYTES, VOD_TTL_SECONDS } from "../src/share-limits";
import { knownLengthStreams } from "./fake-r2";
import {
  MiB, OTHER, OWNER, call, clipRow, countR2, endShare, fakeCtx, hlsInit, hlsRow, hlsSetup, json, liveShare,
  pause, putRun, putSeg, resetHlsState, segBytes, segRows, storedKeys,
} from "./hls-fixtures";

beforeEach(() => {
  vi.restoreAllMocks();
  resetHlsState();
});
afterEach(() => {
  vi.useRealTimers();
});

describe("PUT /vod-shares/:id/hls/seg/:seq", () => {
  async function share(opts: Parameters<typeof hlsSetup>[0] = {}, init: Parameters<typeof hlsInit>[2] = {}) {
    const s = await hlsSetup(opts);
    const id = (await json(await hlsInit(s.env, OWNER, init))).id as string;
    expect((await putRun(s.env, id, 0)).status).toBe(200);
    return { s, id };
  }

  it("stores the body in R2 as the request's own known-length stream, then commits", async () => {
    const { s, id } = await share();
    const seen: unknown[] = [];
    const put = s.r2.bucket.put.bind(s.r2.bucket);
    s.r2.bucket.put = (async (key: string, value: ReadableStream, o?: unknown) => {
      seen.push(value);
      return put(key, value, o as R2PutOptions);
    }) as typeof s.r2.bucket.put;
    const res = await putSeg(s.env, id, 0, { bytes: segBytes(0, 5000) });
    expect(res.status).toBe(200);
    expect(await json(res)).toEqual({ seq: 0, committed: 1, media_ms: 2000, playable: false });
    expect(res.headers.get("Server-Timing")).toMatch(/^d1;dur=\d+, r2;dur=\d+$/);
    expect(seen).toHaveLength(1);
    expect(seen[0]).toBeInstanceOf(ReadableStream);
    expect(knownLengthStreams.has(seen[0] as ReadableStream)).toBe(true);
    expect(s.r2.store.get(`vods/${id}/0.m4s`)).toEqual(segBytes(0, 5000));
    expect(segRows(s, id)).toEqual([{ seq: 0, duration_ms: 2000, bytes: 5000, committed: 1 }]);
    expect(clipRow(s, id)).toMatchObject({ hls_bytes: 1408 + 5000, part_puts: 2 });
    expect(hlsRow(s, id)).toMatchObject({ max_seq: 0, segments: 1, media_ms: 2000 });
  });

  it("validates before any D1 or R2 work: seq, ms (1 to 6499), run, Content-Length and size", async () => {
    const { s, id } = await share();
    const r2 = countR2(s);
    const before = s.db._sql.length;
    const cases: [Parameters<typeof putSeg>[3], number, string][] = [
      [{ query: "ms=0&run=0" }, 400, "bad_duration"],
      [{ query: "ms=6500&run=0" }, 400, "bad_duration"],
      [{ query: "ms=2.5&run=0" }, 400, "bad_duration"],
      [{ query: "ms=2000" }, 400, "bad_request"],
      [{ query: "ms=2000&run=8" }, 409, "run_mismatch"],
      [{ length: null }, 411, "length_required"],
      [{ bytes: new Uint8Array(0) }, 400, "bad_request"],
      [{ bytes: new Uint8Array(HLS_SEGMENT_MAX_BYTES + 1) }, 413, "payload_too_large"],
    ];
    for (const [o, status, error] of cases) {
      const res = await putSeg(s.env, id, 0, o);
      expect(res.status, JSON.stringify(o.query ?? o.length)).toBe(status);
      expect((await json(res)).error).toBe(error);
    }
    expect(s.db._sql.length).toBe(before);
    expect(r2.total()).toBe(0);
    expect((await putSeg(s.env, id, 0, { ms: 6499 })).status).toBe(200);
  });

  it("refuses by state: 401 without a session, 404 not the owner's, 400 past the slots, 409 run_mismatch", async () => {
    const { s, id } = await share({}, { duration: 1 }); // 13 slots
    expect((await putSeg(s.env, id, 0, { token: "sess-nobody" })).status).toBe(401);
    const anon = await putSeg(s.env, id, 0, { token: "web:abc" });
    expect(anon.status).toBe(401);
    expect(await json(anon)).toEqual({ error: "unauthorized" });
    expect((await putSeg(s.env, id, 0, { token: OTHER })).status).toBe(404);
    expect((await putSeg(s.env, id, 13)).status).toBe(400);
    expect((await json(await putSeg(s.env, id, 13))).error).toBe("bad_segment_number");
    expect((await putSeg(s.env, id, 0, { run: 1 })).status).toBe(409); // run 1 doesn't exist
    for (let seq = 0; seq < 3; seq++) expect((await putSeg(s.env, id, seq)).status).toBe(200);
    expect((await putRun(s.env, id, 1, { from: 5 })).status).toBe(200);
    const stale = await putSeg(s.env, id, 6, { run: 0 }); // run 1 covers seq 6 now
    expect(stale.status).toBe(409);
    expect((await json(stale)).error).toBe("run_mismatch");
    expect((await putSeg(s.env, id, 4, { run: 1 })).status).toBe(409); // before run 1's start
    expect((await putSeg(s.env, id, 4, { run: 0 })).status).toBe(200);
    expect((await putSeg(s.env, id, 6, { run: 1 })).status).toBe(200);
  });

  it("a committed seq answers 200 already and writes nothing", async () => {
    const { s, id } = await share();
    expect((await putSeg(s.env, id, 0)).status).toBe(200);
    const r2 = countR2(s);
    const puts = clipRow(s, id)?.part_puts;
    const res = await putSeg(s.env, id, 0, { bytes: segBytes(0, 9999) });
    expect(res.status).toBe(200);
    expect(await json(res)).toEqual({ seq: 0, already: true, committed: 1, media_ms: 2000, playable: false });
    expect(r2.calls.put ?? 0).toBe(0);
    expect(clipRow(s, id)?.part_puts).toBe(puts);
    expect(segRows(s, id)[0].bytes).toBe(4096);
  });

  it("counts an aborted PUT's bytes once: the pending row is replaced by the retry", async () => {
    const { s, id } = await share();
    const put = s.r2.bucket.put.bind(s.r2.bucket);
    let fail = true;
    s.r2.bucket.put = (async (key: string, value: ReadableStream, o?: unknown) => {
      if (fail) {
        fail = false;
        throw new Error("network connection lost");
      }
      return put(key, value, o as R2PutOptions);
    }) as typeof s.r2.bucket.put;
    vi.spyOn(console, "error").mockImplementation(() => {});
    expect((await putSeg(s.env, id, 0, { bytes: segBytes(0, 7000) })).status).toBe(502);
    expect(segRows(s, id)).toEqual([{ seq: 0, duration_ms: 2000, bytes: 7000, committed: 0 }]);
    expect(clipRow(s, id)?.hls_bytes).toBe(1408 + 7000);
    expect(hlsRow(s, id)).toMatchObject({ max_seq: 0, segments: 0 });
    const retry = await putSeg(s.env, id, 0, { bytes: segBytes(0, 6000) });
    expect(retry.status).toBe(200);
    expect(clipRow(s, id)).toMatchObject({ hls_bytes: 1408 + 6000, part_puts: 3 });
    expect(segRows(s, id)).toEqual([{ seq: 0, duration_ms: 2000, bytes: 6000, committed: 1 }]);
  });

  it("is exempt from the daily part budget, and caps attempts at 2 x part_count + 16 (runs included)", async () => {
    const { s, id } = await share({}, { duration: 1 }); // 13 slots: 42 attempts, the run used one
    // Every attempt that claims counts, whether or not its object lands: R2 fails them all.
    s.r2.bucket.put = (async () => { throw new Error("R2 internal error"); }) as typeof s.r2.bucket.put;
    vi.spyOn(console, "error").mockImplementation(() => {});
    for (let i = 0; i < 41; i++) expect((await putSeg(s.env, id, i % 13)).status).toBe(502);
    expect(clipRow(s, id)?.part_puts).toBe(42);
    const capped = await putSeg(s.env, id, 12);
    expect(capped.status).toBe(429);
    expect((await json(capped)).error).toBe("too_many_part_uploads");
    expect((await putRun(s.env, id, 1, { from: 5 })).status).toBe(429);
    expect(s.db.q("SELECT * FROM upload_part_usage")).toEqual([]);
    // 13 pending rows, each counted once in hls_bytes however often it was retried.
    expect(clipRow(s, id)?.hls_bytes).toBe(1408 + 13 * 4096);
  });

  it("grows a short reservation by max(64 MiB, 2%) while the pool has room, up to VOD_MAX_BYTES, then 413", async () => {
    const { s, id } = await share({ env: { TOTAL_BUDGET_BYTES: String(VOD_MAX_BYTES + 200 * MiB), VOD_BUDGET_BYTES: String(VOD_MAX_BYTES + 200 * MiB) } }, { size: 10_000 });
    // The reservation is 10,000 bytes; one 16 MiB slack covers the first segments.
    expect((await putSeg(s.env, id, 0, { bytes: segBytes(0, 8 * MiB) })).status).toBe(200);
    expect(clipRow(s, id)?.size_bytes).toBe(10_000);
    expect((await putSeg(s.env, id, 1, { bytes: segBytes(1, 9 * MiB) })).status).toBe(200);
    expect(clipRow(s, id)?.size_bytes).toBe(10_000 + 64 * MiB);
    // It grows again whenever it runs out: 64 MiB at a time while that is more than 2%.
    for (let seq = 2; seq < 14; seq++) expect((await putSeg(s.env, id, seq, { bytes: segBytes(seq, 15 * MiB) })).status).toBe(200);
    const row = clipRow(s, id)!;
    expect(row.hls_bytes).toBe(1408 + 17 * MiB + 12 * 15 * MiB);
    expect(row.size_bytes).toBe(10_000 + 3 * 64 * MiB);
    expect((row.hls_bytes as number) <= (row.size_bytes as number) + 16 * MiB).toBe(true);
    // Near the cap a grow stops at VOD_MAX_BYTES; past it every PUT is 413.
    s.db.x("UPDATE clips SET size_bytes = ?1, hls_bytes = ?2 WHERE id = ?3", VOD_MAX_BYTES - MiB, VOD_MAX_BYTES - MiB + 16 * MiB, id);
    expect((await putSeg(s.env, id, 20, { bytes: segBytes(20, MiB) })).status).toBe(200);
    expect(clipRow(s, id)?.size_bytes).toBe(VOD_MAX_BYTES);
    const over = await putSeg(s.env, id, 21, { bytes: segBytes(21, MiB) });
    expect(over.status).toBe(413);
    expect((await json(over)).error).toBe("over_reservation");
    expect(clipRow(s, id)?.size_bytes).toBe(VOD_MAX_BYTES);
  });

  it("does not grow past the storage pool", async () => {
    const { s, id } = await share({ env: { TOTAL_BUDGET_BYTES: String(40 * MiB), VOD_BUDGET_BYTES: String(40 * MiB) } }, { size: 10_000 });
    expect((await putSeg(s.env, id, 0, { bytes: segBytes(0, 16 * MiB) })).status).toBe(200);
    expect((await putSeg(s.env, id, 1, { bytes: segBytes(1, MiB) })).status).toBe(413);
    expect(clipRow(s, id)?.size_bytes).toBe(10_000);
  });

  it("advances the contiguous edge across out-of-order commits, summing EXTINF", async () => {
    const { s, id } = await share();
    const order = [2, 0, 3, 1, 6, 4, 7, 5];
    const ms = (seq: number) => 1990 + seq;
    const committed: number[] = [];
    for (const seq of order) {
      const out = await json(await putSeg(s.env, id, seq, { ms: ms(seq) }));
      committed.push(out.committed as number);
    }
    expect(committed).toEqual([0, 1, 1, 4, 4, 5, 5, 8]);
    expect(hlsRow(s, id)).toMatchObject({ segments: 8, media_ms: [0, 1, 2, 3, 4, 5, 6, 7].reduce((a, q) => a + ms(q), 0), max_seq: 7 });
    expect((await json(await putSeg(s.env, id, 8))).playable).toBe(true); // 9 x ~2 s past 8,000 ms
  });

  it("keeps an EWMA of the ingest rate and the last commit time", async () => {
    vi.useFakeTimers();
    const t0 = Date.UTC(2026, 9, 14, 12, 0, 0);
    vi.setSystemTime(t0);
    const { s, id } = await share();
    expect((await putSeg(s.env, id, 0)).status).toBe(200);
    expect(hlsRow(s, id)).toMatchObject({ last_commit_ms: t0, ingest_milli: null });
    vi.setSystemTime(t0 + 500); // 2 s of media in 0.5 s: 4x
    expect((await putSeg(s.env, id, 1)).status).toBe(200);
    expect(hlsRow(s, id)).toMatchObject({ last_commit_ms: t0 + 500, ingest_milli: 4000 });
    vi.setSystemTime(t0 + 1500); // 2 s in 1 s: (4000 x 30000 + 2000 x 1000) / (1000 + 30000)
    expect((await putSeg(s.env, id, 2)).status).toBe(200);
    expect(hlsRow(s, id)?.ingest_milli).toBe(Math.floor((4000 * 30000 + 2000 * 1000) / 31000));
    vi.setSystemTime(t0 + 3_600_000); // a long gap counts as 30 s at most
    expect((await putSeg(s.env, id, 3)).status).toBe(200);
    const prev = Math.floor((4000 * 30000 + 2000 * 1000) / 31000);
    expect(hlsRow(s, id)?.ingest_milli).toBe(Math.floor((prev * 30000 + 2000 * 1000) / 60000));
  });

  it("runs the store and the commit as one task handed to ctx.waitUntil", async () => {
    const { s, id } = await share();
    const { ctx, waited } = fakeCtx();
    const res = await putSeg(s.env, id, 0, { ctx });
    expect(res.status).toBe(200);
    expect(waited).toHaveLength(1);
    await expect(waited[0]).resolves.toBeUndefined();
    expect(segRows(s, id)[0].committed).toBe(1);

    // The task still commits when nobody awaits the response (a client that hung up).
    const slow = fakeCtx();
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const put = s.r2.bucket.put.bind(s.r2.bucket);
    s.r2.bucket.put = (async (key: string, value: ReadableStream, o?: unknown) => {
      const stored = await put(key, value, o as R2PutOptions);
      await gate;
      return stored;
    }) as typeof s.r2.bucket.put;
    void putSeg(s.env, id, 1, { ctx: slow.ctx });
    await vi.waitFor(() => expect(slow.waited).toHaveLength(1));
    release();
    await slow.waited[0];
    expect(segRows(s, id)[1]).toMatchObject({ seq: 1, committed: 1 });
  });

  /** Stop sharing (the real DELETE /clips/:id) while a PUT's body is still on the wire. */
  function stopDuringPut(s: Awaited<ReturnType<typeof hlsSetup>>, id: string) {
    const put = s.r2.bucket.put.bind(s.r2.bucket);
    s.r2.bucket.put = (async (key: string, value: ReadableStream, o?: unknown) => {
      s.r2.bucket.put = put;
      const stop = await call(s.env, `/clips/${id}`, { method: "DELETE", headers: { Authorization: `Bearer ${OWNER}` } });
      expect(stop.status).toBe(200);
      return put(key, value, o as R2PutOptions);
    }) as typeof s.r2.bucket.put;
  }

  it("deletes what it stored when the share was stopped while the body was on the wire", async () => {
    const { s, id } = await share();
    expect((await putSeg(s.env, id, 0)).status).toBe(200);
    stopDuringPut(s, id);
    const res = await putSeg(s.env, id, 1);
    expect(res.status).toBe(404);
    expect(await json(res)).toEqual({ error: "not_found" });
    expect(storedKeys(s, id)).toEqual([]); // seq 0 by the stop, seq 1 by the PUT itself
    expect(clipRow(s, id)).toBeUndefined();
  });

  it("tombstones that object when R2 refuses its delete", async () => {
    const { s, id } = await share();
    stopDuringPut(s, id);
    const del = s.r2.bucket.delete.bind(s.r2.bucket);
    let deletes = 0;
    s.r2.bucket.delete = (async (keys: string | string[]) => {
      if (++deletes > 1) throw new Error("R2 internal error"); // the stop's own delete works
      return del(keys);
    }) as typeof s.r2.bucket.delete;
    expect((await putSeg(s.env, id, 0, { bytes: segBytes(0, 3000) })).status).toBe(404);
    expect(s.db.q("SELECT r2_key, size_bytes, kind, key_count FROM storage_tombstones ORDER BY r2_key")).toEqual([
      { r2_key: `vods/${id}/`, size_bytes: 3000, kind: "vod", key_count: 9 },
      { r2_key: `vods/${id}/0.m4s`, size_bytes: 3000, kind: "vod", key_count: null },
    ]);
  });
});

describe("races around one segment PUT", () => {
  /** Run `during` while the PUT's body is on the wire (before R2 has it). */
  function whilePutting(s: Awaited<ReturnType<typeof hlsSetup>>, during: () => Promise<void>) {
    const put = s.r2.bucket.put.bind(s.r2.bucket);
    s.r2.bucket.put = (async (key: string, value: ReadableStream, o?: unknown) => {
      s.r2.bucket.put = put;
      await during();
      return put(key, value, o as R2PutOptions);
    }) as typeof s.r2.bucket.put;
  }

  it("a retry of the same seq while the first is in flight: one commit, one already, counted once", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 2);
    let retry: Response | undefined;
    whilePutting(s, async () => { retry = await putSeg(s.env, id, 2); });
    const first = await putSeg(s.env, id, 2);
    expect(retry?.status).toBe(200);
    expect(await json(retry!)).toMatchObject({ seq: 2, committed: 3 });
    expect(first.status).toBe(200);
    expect(await json(first)).toMatchObject({ seq: 2, already: true, committed: 3 });
    expect(segRows(s, id)[2]).toEqual({ seq: 2, duration_ms: 2000, bytes: 4096, committed: 1 });
    expect(clipRow(s, id)?.hls_bytes).toBe(1408 + 3 * 4096);
    expect(storedKeys(s, id)).toHaveLength(3);
  });

  it("a run created at or before the seq while it was in flight: 409 run_mismatch, nothing committed", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 5);
    whilePutting(s, async () => { expect((await putRun(s.env, id, 1, { from: 5 })).status).toBe(200); });
    const stale = await putSeg(s.env, id, 6, { run: 0 });
    expect(stale.status).toBe(409);
    expect((await json(stale)).error).toBe("run_mismatch");
    expect(segRows(s, id).find((r) => r.seq === 6)?.committed).toBe(0);
    expect(hlsRow(s, id)?.segments).toBe(5);
    // The new run's own PUT of that seq replaces it.
    expect((await putSeg(s.env, id, 5, { run: 1 })).status).toBe(200);
    expect(await json(await putSeg(s.env, id, 6, { run: 1 }))).toMatchObject({ committed: 7 });
  });

  it("an end that lands while a later seq is in flight: that PUT is 404 and its object goes", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    whilePutting(s, async () => { expect((await endShare(s.env, id, 4)).status).toBe(201); });
    const late = await putSeg(s.env, id, 4);
    expect(late.status).toBe(404);
    expect(storedKeys(s, id)).toEqual([0, 1, 2, 3].map((q) => `vods/${id}/${q}.m4s`));
    expect(clipRow(s, id)).toMatchObject({ status: "ready", size_bytes: 1408 + 4 * 4096 });
  });
});

describe("POST /vod-shares/:id/hls/pause", () => {
  it("accepts only reason game, is idempotent, and the next PUT clears it", async () => {
    vi.useFakeTimers();
    const t0 = Date.UTC(2026, 9, 14, 12, 0, 0);
    vi.setSystemTime(t0);
    const s = await hlsSetup();
    const id = await liveShare(s, 2);
    expect((await pause(s.env, id, "lunch")).status).toBe(400);
    expect((await pause(s.env, id, 5)).status).toBe(400);
    const res = await pause(s.env, id);
    expect(res.status).toBe(200);
    expect(await json(res)).toEqual({ paused: true, reason: "game" });
    vi.setSystemTime(t0 + 60_000);
    expect((await pause(s.env, id)).status).toBe(200);
    expect(hlsRow(s, id)).toMatchObject({ paused_at: t0 / 1000, paused_reason: "game" });
    expect((await pause(s.env, id, "game", OTHER)).status).toBe(404);
    expect((await putSeg(s.env, id, 2)).status).toBe(200);
    expect(hlsRow(s, id)).toMatchObject({ paused_at: null, paused_reason: null });
    s.db.x("UPDATE clips SET status = 'ready' WHERE id = ?1", id);
    expect((await pause(s.env, id)).status).toBe(404);
  });
});

describe("POST /vod-shares/:id/hls/end", () => {
  it("needs run 0 and N committed segments, else 409 segments_incomplete with the committed count", async () => {
    const s = await hlsSetup();
    const bare = (await json(await hlsInit(s.env))).id as string;
    const none = await endShare(s.env, bare, 1);
    expect(none.status).toBe(409);
    expect(await json(none)).toEqual({ error: "segments_incomplete", message: expect.any(String), committed: 0 });
    expect((await endShare(s.env, bare, 1.5)).status).toBe(400);
    expect((await endShare(s.env, bare, "3")).status).toBe(400);
    const zero = await endShare(s.env, bare, 0);
    expect(zero.status).toBe(409);
    expect((await json(zero)).error).toBe("segments_incomplete");
    expect((await endShare(s.env, bare, 1, "yes")).status).toBe(400);
    expect((await endShare(s.env, bare, 1, false, OTHER)).status).toBe(404);
  });

  it("ends at the committed edge: drops later rows and objects, recounts the bytes, and a replay gets the same 201", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(Date.UTC(2026, 9, 14, 12, 0, 0));
    const s = await hlsSetup();
    const id = await liveShare(s, 5, { ms: (q) => (q === 4 ? 1500 : 2000) });
    // Seq 7 committed out of order and seq 6 pending: both past the edge (5).
    expect((await putSeg(s.env, id, 7)).status).toBe(200);
    s.db.x("INSERT INTO vod_hls_segments (clip_id, seq, duration_ms, bytes, committed) VALUES (?1, 6, 2000, 123, 0)", id);
    s.db.x("UPDATE vod_hls SET max_seq = 7 WHERE clip_id = ?1", id);
    s.db.x("UPDATE clips SET hls_bytes = hls_bytes + 999999 WHERE id = ?1", id); // never trusted
    const r2 = countR2(s);
    const res = await endShare(s.env, id, 4, true);
    expect(res.status).toBe(201);
    const row = clipRow(s, id)!;
    const out = await json(res);
    const exact = 1408 + 5 * 4096;
    expect(out).toEqual({
      id, url: `https://revu.lol/v/${id}`, expires_at: (row.created_at as number) + VOD_TTL_SECONDS, size_bytes: exact,
      media_ms: 9500, segments: 5, truncated: true,
    });
    expect(row).toMatchObject({ status: "ready", size_bytes: exact, hls_bytes: exact, duration_s: 10, expires_at: (row.created_at as number) + VOD_TTL_SECONDS });
    expect(hlsRow(s, id)).toMatchObject({ ended: 1, truncated: 1, segments: 5 });
    expect(segRows(s, id).map((r) => r.seq)).toEqual([0, 1, 2, 3, 4]);
    expect(storedKeys(s, id)).toEqual([0, 1, 2, 3, 4].map((q) => `vods/${id}/${q}.m4s`));
    expect(r2.deleted).toEqual([[5, 6, 7].map((q) => `vods/${id}/${q}.m4s`)]);
    const replay = await endShare(s.env, id, 99, false);
    expect(replay.status).toBe(201);
    expect(await json(replay)).toEqual(out);
    expect((await putSeg(s.env, id, 5)).status).toBe(404); // ended: no more segments
  });

  it("keeps the bytes of objects R2 refused to delete as tombstones", async () => {
    const s = await hlsSetup();
    const id = await liveShare(s, 4);
    s.db.x("INSERT INTO vod_hls_segments (clip_id, seq, duration_ms, bytes, committed) VALUES (?1, 4, 2000, 777, 0)", id);
    s.db.x("UPDATE vod_hls SET max_seq = 4 WHERE clip_id = ?1", id);
    s.r2.bucket.delete = (async () => { throw new Error("R2 internal error"); }) as typeof s.r2.bucket.delete;
    vi.spyOn(console, "error").mockImplementation(() => {});
    expect((await endShare(s.env, id, 4)).status).toBe(201);
    expect(s.db.q("SELECT r2_key, size_bytes FROM storage_tombstones")).toEqual([{ r2_key: `vods/${id}/4.m4s`, size_bytes: 777 }]);
  });
});
