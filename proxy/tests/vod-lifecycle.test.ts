/**
 * Full-game (VOD) shares after init: parts (with the per-row PUT cap),
 * complete (3-day expiry from INIT), markers, kind isolation from clips,
 * owner delete with R2 failure, account-delete tombstones and the hourly cron.
 * Real SQLite (every migration applied) and the fake R2.
 */

import { beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { resetUserLimits } from "../src/user-limits";
import { resetClipViews } from "../src/clip-views";
import { deleteAccountRows, listClipObjectsForUser } from "../src/db";
import { storageUsage } from "../src/storage-budget";
import { utcDayOf, VOD_TTL_SECONDS } from "../src/share-limits";
import { makeRequest, runScheduled } from "./fakes";
import {
  BASE, MARKERS_DOC, MiB, body, del, get, nowSec, payload, shareVod, vodComplete, vodInitOk, vodMarkers, vodPart, vodSetup,
} from "./vod-fixtures";

beforeEach(() => {
  vi.restoreAllMocks();
  resetUserLimits();
  resetClipViews();
});

const OWNER = "sess-owner";
const OTHER = "sess-other";

function clipRow(db: { x(sql: string, ...a: unknown[]): void }, id: string, userId: number, status = "ready", extra = "") {
  const now = nowSec();
  db.x(
    `INSERT INTO clips (id, user_id, r2_key, content_type, size_bytes, created_at, expires_at, status, kind, upload_id, part_size, part_count, last_activity_at${extra ? ", title" : ""}) VALUES (?1, ?2, ?3, 'video/mp4', 100, ?4, ?5, ?6, 'clip', ?7, 16777216, 1, ?4${extra ? ", ?8" : ""})`,
    ...[id, userId, `clips/${id}.mp4`, now, now + 3600, status, status === "uploading" ? "upl-clip" : null, ...(extra ? [extra] : [])],
  );
}

describe("VOD parts and complete", () => {
  it("uploads 16 MiB parts, completes with a 3-day expiry counted from INIT, and replays idempotently", async () => {
    const s = await vodSetup();
    const t0 = Date.now();
    const clock = vi.spyOn(Date, "now").mockReturnValue(t0);
    const size = 16 * MiB + 100;
    const init = await body(await vodInitOk(s.env, OWNER, size));
    const id = init.id as string;
    expect(init.part_count).toBe(2);
    const created = s.db.q<{ created_at: number }>("SELECT created_at FROM clips WHERE id = ?1", id)[0].created_at;

    clock.mockReturnValue(t0 + 600_000); // ten minutes of uploading
    const bytes = payload(size);
    const p1 = await vodPart(s.env, OWNER, id, 1, bytes.slice(0, 16 * MiB));
    const p2 = await vodPart(s.env, OWNER, id, 2, bytes.slice(16 * MiB));
    expect([p1.status, p2.status]).toEqual([200, 200]);
    const parts = [await body(p1), await body(p2)];
    const done = await vodComplete(s.env, OWNER, id, parts);
    expect(done.status).toBe(201);
    const expected = { id, url: `https://revu.lol/v/${id}`, expires_at: created + VOD_TTL_SECONDS };
    expect(await body(done)).toEqual(expected);

    const row = s.db.q<Record<string, unknown>>("SELECT * FROM clips WHERE id = ?1", id)[0];
    expect(row).toMatchObject({ status: "ready", upload_id: null, created_at: created, expires_at: created + VOD_TTL_SECONDS, part_puts: 2 });
    expect(s.r2.store.get(`vods/${id}.mp4`)!.byteLength).toBe(size);

    const replay = await vodComplete(s.env, OWNER, id, parts);
    expect(replay.status).toBe(201);
    expect(await body(replay)).toEqual(expected);
    expect(s.r2.calls.complete).toBe(1);
  });

  it("caps part PUTs at 2 x part_count + 4 per row", async () => {
    const s = await vodSetup();
    const id = (await body(await vodInitOk(s.env, OWNER, 500))).id as string;
    const bytes = payload(500);
    for (let i = 0; i < 6; i++) expect((await vodPart(s.env, OWNER, id, 1, bytes)).status).toBe(200);
    const seventh = await vodPart(s.env, OWNER, id, 1, bytes);
    expect(seventh.status).toBe(429);
    expect(await body(seventh)).toEqual({
      error: "too_many_part_uploads", message: "This upload retried too many times. Start the share again.",
    });
    expect(s.r2.calls.uploadPart).toBe(6);
    // Someone else's id, or an unknown one, is still a plain 404.
    expect((await vodPart(s.env, OTHER, id, 1, bytes)).status).toBe(404);
    expect((await vodPart(s.env, OWNER, "Nope123", 1, bytes)).status).toBe(404);
  });

  it("validates part numbers and exact part lengths like clips", async () => {
    const s = await vodSetup();
    const id = (await body(await vodInitOk(s.env, OWNER, 500))).id as string;
    expect((await vodPart(s.env, OWNER, id, 2, payload(500))).status).toBe(400);
    const short = await vodPart(s.env, OWNER, id, 1, payload(499));
    expect(short.status).toBe(400);
    expect(await body(short)).toEqual({ error: "part_size_mismatch", expected: 500 });
    const noLength = await worker.fetch(
      new Request(`${BASE}/vod-shares/${id}/parts/1`, { method: "PUT", headers: { Authorization: `Bearer ${OWNER}` }, body: payload(500) }),
      s.env,
    );
    expect(noLength.status).toBe(411);
    // A refused part does not spend an attempt.
    expect(s.db.q("SELECT part_puts FROM clips WHERE id = ?1", id)).toEqual([{ part_puts: 0 }]);
  });

  it("the start kill switch never blocks parts or complete of an upload already started", async () => {
    const s = await vodSetup();
    const id = (await body(await vodInitOk(s.env, OWNER, 500))).id as string;
    const off = { ...s.env, VOD_SHARING: "off" };
    const part = await vodPart(off, OWNER, id, 1, payload(500));
    expect(part.status).toBe(200);
    expect((await vodComplete(off, OWNER, id, [await body(part)])).status).toBe(201);
  });

  it("discards the row when the completed object is not video", async () => {
    const s = await vodSetup();
    const id = (await body(await vodInitOk(s.env, OWNER, 500))).id as string;
    const part = await vodPart(s.env, OWNER, id, 1, new Uint8Array(500));
    const res = await vodComplete(s.env, OWNER, id, [await body(part)]);
    expect(res.status).toBe(415);
    expect((await body(res)).error).toBe("not_video");
    expect(s.db.q("SELECT id FROM clips")).toHaveLength(0);
    expect(s.r2.store.has(`vods/${id}.mp4`)).toBe(false);
  });
});

describe("PUT /vod-shares/:id/markers", () => {
  it("stores the normalized document, sets the public title and champion, and replays idempotently", async () => {
    const s = await vodSetup();
    const { id } = await shareVod(s);
    const res = await vodMarkers(s.env, OWNER, id, { ...MARKERS_DOC, extra: "dropped" });
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({ ok: true, markers: 2, notes: 0 });
    const meta = s.db.q<{ meta_json: string; has_notes: number }>("SELECT meta_json, has_notes FROM vod_share_meta WHERE clip_id = ?1", id)[0];
    expect(JSON.parse(meta.meta_json)).toEqual({
      version: 1,
      gameTimeAtVideoStart: 12.35,
      game: { champion: "Jinx", opponentChampion: "Caitlyn", role: "BOTTOM", result: "win", durationS: 1834, playedAt: "2026-10-12" },
      markers: [
        { t: 95, kind: "KILL", label: "First blood", tone: "win" },
        { t: 312, kind: "DRAGON", label: "Dragon", tone: "gold" },
      ],
      notes: null,
    });
    expect(meta.has_notes).toBe(0);
    expect(s.db.q("SELECT title, champion FROM clips WHERE id = ?1", id)).toEqual([{ title: "Jinx vs Caitlyn", champion: "Jinx" }]);

    const again = await vodMarkers(s.env, OWNER, id, { ...MARKERS_DOC, extra: "dropped" });
    expect(await body(again)).toEqual({ ok: true, markers: 2, notes: 0 });
    expect(s.db.q("SELECT meta_json FROM vod_share_meta")).toEqual([{ meta_json: meta.meta_json }]);

    const withNotes = await vodMarkers(s.env, OWNER, id, {
      ...MARKERS_DOC, game: { champion: "Jinx" }, notes: [{ t: 400, end: 430, kind: "clip", text: " Should have backed here " }],
    });
    expect(await body(withNotes)).toEqual({ ok: true, markers: 2, notes: 1 });
    expect(s.db.q("SELECT has_notes FROM vod_share_meta")).toEqual([{ has_notes: 1 }]);
    expect(s.db.q("SELECT title, champion FROM clips WHERE id = ?1", id)).toEqual([{ title: "Jinx game", champion: "Jinx" }]);
  });

  it("needs the caller's own ready, unexpired VOD", async () => {
    const s = await vodSetup();
    const pending = (await body(await vodInitOk(s.env, OWNER, 500))).id as string;
    expect((await vodMarkers(s.env, OWNER, pending, MARKERS_DOC)).status).toBe(404);
    s.db.x("DELETE FROM clips");
    resetUserLimits();
    const { id } = await shareVod(s);
    expect((await vodMarkers(s.env, OTHER, id, MARKERS_DOC)).status).toBe(404);
    clipRow(s.db, "Clip001", s.users[OWNER]);
    expect((await vodMarkers(s.env, OWNER, "Clip001", MARKERS_DOC)).status).toBe(404);
    s.db.x("UPDATE clips SET expires_at = ?1 WHERE id = ?2", nowSec() - 1, id);
    expect((await vodMarkers(s.env, OWNER, id, MARKERS_DOC)).status).toBe(404);
    expect(s.db.q("SELECT * FROM vod_share_meta")).toHaveLength(0);
  });

  it("refuses an oversized body and bad or oversized documents", async () => {
    const s = await vodSetup();
    const { id } = await shareVod(s);
    const huge = await vodMarkers(s.env, OWNER, id, "x".repeat(65537));
    expect(huge.status).toBe(413);
    expect((await body(huge)).error).toBe("payload_too_large");

    const bad = await vodMarkers(s.env, OWNER, id, { ...MARKERS_DOC, markers: [{ t: -1, kind: "KILL" }] });
    expect(bad.status).toBe(400);
    expect(await body(bad)).toEqual({ error: "bad_markers", message: "markers[0].t must be a number from 0 to 10800" });

    const many = Array.from({ length: 600 }, (_, i) => ({ t: i, kind: "OBJECTIVE_TAKEN", label: "L".repeat(40), tone: "neutral" }));
    const big = await vodMarkers(s.env, OWNER, id, { ...MARKERS_DOC, markers: many });
    expect(big.status).toBe(413);
    expect((await body(big)).error).toBe("markers_too_large");
    expect(s.db.q("SELECT * FROM vod_share_meta")).toHaveLength(0);
  });
});

describe("kinds stay isolated", () => {
  it("no clip route serves or changes a VOD row", async () => {
    const s = await vodSetup();
    const { id } = await shareVod(s);
    const pending = (await body(await vodInitOk(s.env, OWNER, 500))).id as string;
    const auth = { Authorization: `Bearer ${OWNER}` };
    expect((await get(s.env, `/clip-meta/${id}`)).status).toBe(404);
    expect((await get(s.env, `/clip-file/${id}`)).status).toBe(404);
    expect((await get(s.env, `/clip-transcript/${id}`)).status).toBe(404);
    expect((await get(s.env, `/clip-view/${id}`, { method: "POST" })).status).toBe(200);
    expect(s.db.q("SELECT view_count FROM clips WHERE id = ?1", id)).toEqual([{ view_count: 0 }]);
    const doc = JSON.stringify({ version: 1, segments: [{ start: 0, end: 1, text: "hi" }] });
    const transcript = await get(s.env, `/clips/${id}/transcript`, {
      method: "PUT", headers: { ...auth, "Content-Type": "application/json", "Content-Length": String(doc.length) }, body: doc,
    });
    expect(transcript.status).toBe(404);
    const part = await worker.fetch(makeRequest(`${BASE}/clips/${pending}/parts/1`, {
      method: "PUT", headers: { ...auth, "Content-Length": "500" }, body: payload(500),
    }), s.env);
    expect(part.status).toBe(404);
    const done = await worker.fetch(makeRequest(`${BASE}/clips/${pending}/complete`, {
      method: "POST", headers: { ...auth, "Content-Type": "application/json", "Content-Length": "11" }, body: '{"parts":[]}'.slice(0, 11),
    }), s.env);
    expect(done.status).toBe(404);

    // The bare slug goes straight to the watch page: no Open Graph video tags.
    const bare = await get(s.env, `/${id}`, { redirect: "manual" });
    expect(bare.status).toBe(302);
    expect(bare.headers.get("Location")).toBe(`https://revu.lol/v/${id}`);
    expect((await get(s.env, `/${pending}`, { redirect: "manual" })).headers.get("Location")).toBe(`https://revu.lol/clip.html?id=${pending}`);
  });

  it("no VOD route serves or changes a clip row", async () => {
    const s = await vodSetup();
    clipRow(s.db, "Clip001", s.users[OWNER]);
    clipRow(s.db, "Clip002", s.users[OWNER], "uploading");
    s.r2.store.set("clips/Clip001.mp4", payload(100));
    expect((await get(s.env, "/vod-meta/Clip001")).status).toBe(404);
    expect((await get(s.env, "/vod-file/Clip001")).status).toBe(404);
    expect((await vodPart(s.env, OWNER, "Clip002", 1, payload(100))).status).toBe(404);
    expect((await vodComplete(s.env, OWNER, "Clip002", [{ part_number: 1, etag: "x" }])).status).toBe(404);
    expect((await vodMarkers(s.env, OWNER, "Clip001", MARKERS_DOC)).status).toBe(404);
    expect(s.db.q("SELECT part_puts, status FROM clips WHERE id = 'Clip002'")).toEqual([{ part_puts: 0, status: "uploading" }]);
    // And the clip quota never counts a VOD.
    await shareVod(s);
    const mine = await body(await worker.fetch(makeRequest(`${BASE}/clips/mine`, { headers: { Authorization: `Bearer ${OWNER}` } }), s.env));
    const kinds = (mine.clips as { id: string; kind: string; url: string }[]).map((c) => [c.kind, c.url.startsWith("https://revu.lol/v/") || c.url.startsWith("https://clips.revu.lol/")]);
    expect(kinds.sort()).toEqual([["clip", true], ["clip", true], ["vod", true]]);
  });
});

describe("deleting VOD shares", () => {
  it("DELETE /clips/:id removes a VOD, its meta and its object", async () => {
    const s = await vodSetup();
    const { id } = await shareVod(s);
    await vodMarkers(s.env, OWNER, id, MARKERS_DOC);
    expect((await del(s.env, OTHER, id)).status).toBe(403);
    const res = await del(s.env, OWNER, id);
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({ ok: true });
    expect(s.db.q("SELECT * FROM clips")).toHaveLength(0);
    expect(s.db.q("SELECT * FROM vod_share_meta")).toHaveLength(0);
    expect(s.r2.store.has(`vods/${id}.mp4`)).toBe(false);
    expect((await get(s.env, `/vod-meta/${id}`)).status).toBe(404);
  });

  it("an R2 failure keeps an expired purging row in the budget; the hourly purge finishes it", async () => {
    const s = await vodSetup();
    const { id } = await shareVod(s);
    await vodMarkers(s.env, OWNER, id, MARKERS_DOC);
    const realDelete = s.r2.bucket.delete.bind(s.r2.bucket);
    s.r2.bucket.delete = (async () => { throw new Error("R2 down"); }) as typeof s.r2.bucket.delete;
    const logs: string[] = [];
    vi.spyOn(console, "error").mockImplementation((line: unknown) => { logs.push(String(line)); });
    const res = await del(s.env, OWNER, id);
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({ ok: true });
    expect(logs.join("\n")).toContain(`"scope":"clips.delete.r2","id":"${id}"`);
    const row = s.db.q<{ status: string; expires_at: number }>("SELECT status, expires_at FROM clips WHERE id = ?1", id)[0];
    expect(row.status).toBe("purging");
    expect(row.expires_at).toBeLessThanOrEqual(nowSec());
    expect(await storageUsage(s.db)).toEqual({ used: 500, vod_used: 500 });
    expect((await get(s.env, `/vod-meta/${id}`)).status).toBe(404);

    s.r2.bucket.delete = realDelete;
    vi.spyOn(console, "log").mockImplementation(() => {});
    await runScheduled(worker, s.env);
    expect(s.db.q("SELECT * FROM clips")).toHaveLength(0);
    expect(s.db.q("SELECT * FROM vod_share_meta")).toHaveLength(0);
    expect(s.r2.store.has(`vods/${id}.mp4`)).toBe(false);
    expect(await storageUsage(s.db)).toEqual({ used: 0, vod_used: 0 });
  });

  it("account delete keeps tombstones for failed R2 keys, removes every VOD row, and the cron retries them", async () => {
    const s = await vodSetup();
    const { id } = await shareVod(s);
    await vodMarkers(s.env, OWNER, id, MARKERS_DOC);
    const pending = (await body(await vodInitOk(s.env, OWNER, 700))).id as string;
    const userId = s.users[OWNER];
    const objects = await listClipObjectsForUser(s.db, userId);
    expect(objects.map((o) => [o.id, o.size_bytes, o.kind]).sort()).toEqual([[id, 500, "vod"], [pending, 700, "vod"]].sort());

    await deleteAccountRows(s.db, userId, "user1@example.com", objects.map((o) => o.id));
    expect(s.db.q("SELECT * FROM users WHERE id = ?1", userId)).toHaveLength(0);
    for (const table of ["clips", "vod_share_meta", "vod_share_usage", "vod_sharers WHERE user_id = " + userId]) {
      expect(s.db.q(`SELECT * FROM ${table}`), table).toHaveLength(0);
    }
    const tombs = s.db.q<{ r2_key: string; size_bytes: number; kind: string; upload_id: string | null }>(
      "SELECT r2_key, size_bytes, kind, upload_id FROM storage_tombstones ORDER BY size_bytes",
    );
    expect(tombs.map((t) => [t.r2_key, t.size_bytes, t.kind])).toEqual([[`vods/${id}.mp4`, 500, "vod"], [`vods/${pending}.mp4`, 700, "vod"]]);
    expect(tombs[1].upload_id).not.toBeNull();
    expect(await storageUsage(s.db)).toEqual({ used: 1200, vod_used: 1200 });

    // First cron: R2 still failing, the tombstones stay. Second: they go.
    const realDelete = s.r2.bucket.delete.bind(s.r2.bucket);
    s.r2.bucket.delete = (async () => { throw new Error("R2 down"); }) as typeof s.r2.bucket.delete;
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(console, "log").mockImplementation(() => {});
    await runScheduled(worker, s.env);
    expect(s.db.q("SELECT * FROM storage_tombstones")).toHaveLength(2);
    s.r2.bucket.delete = realDelete;
    await runScheduled(worker, s.env);
    expect(s.db.q("SELECT * FROM storage_tombstones")).toHaveLength(0);
    expect(s.r2.store.has(`vods/${id}.mp4`)).toBe(false);
    expect([...s.r2.uploads.values()].some((u) => u.key === `vods/${pending}.mp4` && u.state === "aborted")).toBe(true);
  });
});

describe("hourly cron", () => {
  it("purges expired VOD shares with their meta, and prunes start counts older than 7 days", async () => {
    const s = await vodSetup();
    const { id } = await shareVod(s);
    await vodMarkers(s.env, OWNER, id, MARKERS_DOC);
    s.db.x("UPDATE clips SET expires_at = ?1 WHERE id = ?2", nowSec() - 1, id);
    const now = nowSec();
    s.db.x("INSERT INTO vod_share_usage (user_id, day, starts) VALUES (?1, ?2, 2)", s.users[OWNER], utcDayOf(now - 8 * 86400));
    s.db.x("INSERT INTO vod_share_usage (user_id, day, starts) VALUES (?1, ?2, 2)", s.users[OWNER], utcDayOf(now - 6 * 86400));
    vi.spyOn(console, "log").mockImplementation(() => {});
    await runScheduled(worker, s.env);
    expect(s.db.q("SELECT * FROM clips")).toHaveLength(0);
    expect(s.db.q("SELECT * FROM vod_share_meta")).toHaveLength(0);
    expect(s.r2.store.has(`vods/${id}.mp4`)).toBe(false);
    expect(s.db.q<{ day: string }>("SELECT day FROM vod_share_usage ORDER BY day").map((r) => r.day)).toEqual([
      utcDayOf(now - 6 * 86400), utcDayOf(now),
    ]);
  });
});
