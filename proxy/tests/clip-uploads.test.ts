import { beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { Env } from "../src/types";
import {
  CLIP_TTL_SECONDS,
  MAX_ACTIVE_CLIP_BYTES_PER_USER,
  PART_SIZE_BYTES,
  QUOTA_EXCEEDED_MESSAGE,
  UPLOAD_WINDOW_SECONDS,
} from "../src/clips";
import { resetUserLimits } from "../src/user-limits";
import {
  FakeDb,
  clip,
  dbWithSessions,
  json,
  knownLengthStreams,
  makeEnv,
  makeFakeR2,
  makeRequest,
  mp4Bytes,
  nowSec,
  runScheduled,
} from "./fakes";

const TOKEN = "sess-uploader";
const OTHER = "sess-other";
const USER = 11;
const BASE = "https://clips.revu.lol";

type Setup = { db: FakeDb; r2: ReturnType<typeof makeFakeR2>; e: Env };

async function setup(seed: Parameters<typeof dbWithSessions>[1] = []): Promise<Setup> {
  const db = await dbWithSessions({ [TOKEN]: USER, [OTHER]: 22 }, seed);
  const r2 = makeFakeR2();
  return { db, r2, e: makeEnv({ DB: db, CLIPS: r2.bucket }) };
}

const auth = (token = TOKEN) => ({ Authorization: `Bearer ${token}` });

function init(e: Env, size: number, opts: { duration?: number; type?: string; narrated?: string; token?: string } = {}) {
  const q = new URLSearchParams({ title: "long fight", champion: "Ahri", duration: String(opts.duration ?? 300), size: String(size) });
  if (opts.narrated) q.set("narrated", opts.narrated);
  return worker.fetch(
    makeRequest(`${BASE}/clips/uploads?${q}`, {
      method: "POST",
      headers: { ...auth(opts.token), "Content-Type": opts.type ?? "video/mp4" },
    }),
    e,
  );
}

function putPart(e: Env, id: string, n: number, bytes: Uint8Array, opts: { token?: string; length?: number | null } = {}) {
  const headers: Record<string, string> = { ...auth(opts.token), "Content-Type": "application/octet-stream" };
  if (opts.length !== null) headers["Content-Length"] = String(opts.length ?? bytes.byteLength);
  return worker.fetch(makeRequest(`${BASE}/clips/${id}/parts/${n}`, { method: "PUT", headers, body: bytes }), e);
}

function complete(e: Env, id: string, parts: unknown[], token = TOKEN) {
  const body = JSON.stringify({ parts });
  return worker.fetch(
    makeRequest(`${BASE}/clips/${id}/complete`, {
      method: "POST",
      headers: { ...auth(token), "Content-Type": "application/json", "Content-Length": String(body.length) },
      body,
    }),
    e,
  );
}

/** A payload of `size` bytes that passes the mp4 sniff. */
function payload(size: number): Uint8Array {
  const bytes = new Uint8Array(size);
  bytes.set(mp4Bytes().slice(0, Math.min(16, size)), 0);
  for (let i = 16; i < size; i += 4096) bytes[i] = i % 251;
  return bytes;
}

async function uploadAll(e: Env, id: string, bytes: Uint8Array, partSize: number) {
  const parts: { part_number: number; etag: string }[] = [];
  for (let n = 1, off = 0; off < bytes.byteLength; n++, off += partSize) {
    const res = await putPart(e, id, n, bytes.slice(off, off + partSize));
    expect(res.status).toBe(200);
    const out = await json(res);
    parts.push({ part_number: out.part_number as number, etag: out.etag as string });
  }
  return parts;
}

async function startUpload(s: Setup, size: number, opts: Parameters<typeof init>[2] = {}) {
  const res = await init(s.e, size, opts);
  expect(res.status).toBe(201);
  return (await json(res)) as { id: string; part_size: number; part_count: number; expires_at: number };
}

/** Run `hook` once, right after the first all() whose SQL starts with `prefix` returns. */
function afterSelect(db: FakeDb, prefix: string, hook: () => void): void {
  const prepare = db.prepare.bind(db);
  let fired = false;
  (db as { prepare: typeof db.prepare }).prepare = (sql: string) => {
    const stmt = prepare(sql);
    if (!fired && sql.startsWith(prefix)) {
      const all = stmt.all.bind(stmt);
      stmt.all = (async () => {
        const res = await all();
        fired = true;
        hook();
        return res;
      }) as typeof stmt.all;
    }
    return stmt;
  };
}

describe("multipart clip uploads", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    resetUserLimits();
  });

  it("uploads 2 x 16 MiB parts plus a short last part, completes, and serves the clip with Range", async () => {
    const s = await setup();
    const size = 2 * PART_SIZE_BYTES + 1000;
    const bytes = payload(size);
    const up = await startUpload(s, size, { narrated: "1" });
    expect(up.part_size).toBe(PART_SIZE_BYTES);
    expect(up.part_count).toBe(3);
    expect(Math.abs(up.expires_at - (nowSec() + UPLOAD_WINDOW_SECONDS))).toBeLessThan(5);
    expect(Object.keys(up).sort()).toEqual(["expires_at", "id", "part_count", "part_size"]);

    const row = s.db._clips.get(up.id)!;
    expect(row.status).toBe("uploading");
    expect(row.narrated).toBe(1);
    expect(row.size_bytes).toBe(size);

    const parts = await uploadAll(s.e, up.id, bytes, up.part_size);
    // Re-sending a part overwrites it (retries are idempotent); use the new etag.
    const again = await json(await putPart(s.e, up.id, 2, bytes.slice(PART_SIZE_BYTES, 2 * PART_SIZE_BYTES)));
    parts[1] = { part_number: 2, etag: again.etag as string };

    const done = await complete(s.e, up.id, [parts[2], parts[0], parts[1]]); // any order; server sorts
    expect(done.status).toBe(201);
    const out = await json(done);
    expect(Object.keys(out).sort()).toEqual(["expires_at", "id", "url"]);
    expect(out.url).toBe(`${BASE}/${up.id}`);
    expect(Math.abs((out.expires_at as number) - (nowSec() + CLIP_TTL_SECONDS))).toBeLessThan(5);
    expect(row.status).toBe("ready");
    expect(row.upload_id).toBeNull();
    expect(s.r2.store.get(row.r2_key)!.byteLength).toBe(size);

    const meta = await worker.fetch(makeRequest(`${BASE}/clip-meta/${up.id}`), s.e);
    expect(meta.status).toBe(200);
    const m = await json(meta);
    expect(m.narrated).toBe(true);
    expect(m.has_transcript).toBe(false);
    expect(m.transcript_language).toBeNull();
    for (const hidden of ["status", "upload_id", "last_activity_at", "user_id"]) expect(m[hidden]).toBeUndefined();

    const ranged = await worker.fetch(
      makeRequest(`${BASE}/clip-file/${up.id}`, { headers: { Range: `bytes=${PART_SIZE_BYTES - 2}-${PART_SIZE_BYTES + 1}` } }),
      s.e,
    );
    expect(ranged.status).toBe(206);
    expect(ranged.headers.get("Content-Range")).toBe(`bytes ${PART_SIZE_BYTES - 2}-${PART_SIZE_BYTES + 1}/${size}`);
    expect(new Uint8Array(await ranged.arrayBuffer())).toEqual(bytes.slice(PART_SIZE_BYTES - 2, PART_SIZE_BYTES + 2));
  });

  it("hides a pending clip from every public route", async () => {
    const s = await setup();
    const up = await startUpload(s, 1000);
    const putT = await worker.fetch(
      makeRequest(`${BASE}/clips/${up.id}/transcript`, {
        method: "PUT",
        headers: auth(),
        body: JSON.stringify({ version: 1, language: "en", segments: [{ start: 0, end: 1, text: "hi" }] }),
      }),
      s.e,
    );
    expect(putT.status).toBe(200); // owners may attach a transcript before complete

    for (const path of [`/clip-meta/${up.id}`, `/clip-file/${up.id}`, `/clip-transcript/${up.id}`]) {
      expect((await worker.fetch(makeRequest(`${BASE}${path}`), s.e)).status).toBe(404);
    }
    // The OG page falls back to the plain watch-page redirect (no tags for a pending clip).
    const og = await worker.fetch(makeRequest(`${BASE}/${up.id}`), s.e);
    expect(og.status).toBe(302);
    // clip-view is always a cheap 200, but it must not count a view on a pending clip.
    const view = await worker.fetch(makeRequest(`${BASE}/clip-view/${up.id}`, { method: "POST" }), s.e);
    expect(view.status).toBe(200);
    expect(s.db._clips.get(up.id)!.view_count).toBe(0);
    expect(s.db._sql.some((q) => q.startsWith("UPDATE clips SET view_count"))).toBe(false);
  });

  it("validates part numbers and exact part lengths", async () => {
    const s = await setup();
    const up = await startUpload(s, PART_SIZE_BYTES + 10);
    const wrong = await putPart(s.e, up.id, 2, new Uint8Array(9));
    expect(wrong.status).toBe(400);
    expect(await json(wrong)).toEqual({ error: "part_size_mismatch", expected: 10 });
    const missing = await putPart(s.e, up.id, 2, new Uint8Array(10), { length: null });
    expect(missing.status).toBe(411);
    expect((await json(missing)).error).toBe("length_required");
    for (const n of [0, 3]) {
      const bad = await putPart(s.e, up.id, n, new Uint8Array(10));
      expect(bad.status).toBe(400);
      expect((await json(bad)).error).toBe("bad_part_number");
    }
    expect((await putPart(s.e, up.id, 2, new Uint8Array(10))).status).toBe(200);
  });

  it("enforces ownership and requires a real account session", async () => {
    const s = await setup();
    const up = await startUpload(s, 100);
    expect((await putPart(s.e, up.id, 1, payload(100), { token: OTHER })).status).toBe(404);
    expect((await complete(s.e, up.id, [{ part_number: 1, etag: "x" }], OTHER)).status).toBe(404);

    const staticInit = await init(s.e, 100, { token: "static-op-token" });
    expect(staticInit.status).toBe(403);
    expect((await json(staticInit)).error).toBe("login_required");
    const staticPart = await putPart(s.e, up.id, 1, payload(100), { token: "static-op-token" });
    expect((await json(staticPart)).error).toBe("login_required");
    const badInit = await init(s.e, 100, { token: "nope" });
    expect(badInit.status).toBe(401);
    expect(await json(badInit)).toEqual({ error: "unauthorized" });
    const badPart = await putPart(s.e, up.id, 1, payload(100), { token: "nope" });
    expect(badPart.status).toBe(401);
    expect((await json(badPart)).error).toBe("unauthorized");
    const badComplete = await complete(s.e, up.id, [{ part_number: 1, etag: "x" }], "nope");
    expect((await json(badComplete)).error).toBe("unauthorized");
    // Legacy routes keep their historical 401 codes.
    const legacyDelete = await worker.fetch(
      makeRequest(`${BASE}/clips/${up.id}`, { method: "DELETE", headers: auth("nope") }),
      s.e,
    );
    expect(legacyDelete.status).toBe(401);
    expect((await json(legacyDelete)).error).toBe("invalid_token");
  });

  it("validates init parameters", async () => {
    const s = await setup();
    expect((await init(s.e, 0)).status).toBe(400);
    expect((await init(s.e, 2 * 1024 * 1024 * 1024 + 1)).status).toBe(413);
    expect((await init(s.e, 100, { duration: 611 })).status).toBe(400);
    expect((await init(s.e, 100, { type: "image/png" })).status).toBe(415);
    expect((await init(s.e, 100, { type: "video/webm; codecs=vp9" })).status).toBe(201);
  });

  it("rate limits init at 6 per minute per user", async () => {
    const s = await setup();
    const statuses: number[] = [];
    for (let i = 0; i < 7; i++) statuses.push((await init(s.e, 100)).status);
    expect(statuses.slice(0, 2)).toEqual([201, 201]);
    const last = await init(s.e, 100);
    expect(last.status).toBe(429);
    expect((await json(last)).error).toBe("rate_limited");
    expect(last.headers.get("Retry-After")).toBe("60");
  });

  it("rejects incomplete or duplicate part lists, and a size mismatch deletes the row and object", async () => {
    const s = await setup();
    const up = await startUpload(s, PART_SIZE_BYTES + 50);
    const parts = await uploadAll(s.e, up.id, payload(PART_SIZE_BYTES + 50), PART_SIZE_BYTES);
    expect((await complete(s.e, up.id, [parts[0]])).status).toBe(400);
    const dup = await complete(s.e, up.id, [parts[0], { ...parts[0] }]);
    expect((await json(dup)).error).toBe("bad_parts");

    // Simulate R2 holding a short last part (a part that was cut off upstream).
    const row = s.db._clips.get(up.id)!;
    const stored = s.r2.uploads.get(row.upload_id!)!.parts.get(2)!;
    stored.bytes = stored.bytes.slice(0, 40);
    const res = await complete(s.e, up.id, parts);
    expect(res.status).toBe(400);
    expect((await json(res)).error).toBe("size_mismatch");
    expect(s.db._clips.has(up.id)).toBe(false);
    expect(s.r2.store.has(row.r2_key)).toBe(false);
  });

  it("rejects a completed object that is not video (415 not_video) and deletes it", async () => {
    const s = await setup();
    const up = await startUpload(s, 64);
    const row = s.db._clips.get(up.id)!;
    const parts = await uploadAll(s.e, up.id, new TextEncoder().encode("<html>".padEnd(64, "x")), PART_SIZE_BYTES);
    const res = await complete(s.e, up.id, parts);
    expect(res.status).toBe(415);
    expect((await json(res)).error).toBe("not_video");
    expect(s.db._clips.has(up.id)).toBe(false);
    expect(s.r2.store.has(row.r2_key)).toBe(false);
  });

  it("is idempotent when finalize fails after R2 completed the object", async () => {
    const s = await setup();
    const up = await startUpload(s, 500);
    const parts = await uploadAll(s.e, up.id, payload(500), PART_SIZE_BYTES);
    s.db._failNext("UPDATE clips SET status = 'ready'");
    const failed = await complete(s.e, up.id, parts);
    expect(failed.status).toBe(502);
    expect((await json(failed)).error).toBe("clip_error");
    expect(s.db._clips.get(up.id)!.status).toBe("uploading");
    expect(s.r2.calls.complete).toBe(1);

    const retried = await complete(s.e, up.id, parts);
    expect(retried.status).toBe(201);
    expect(s.r2.calls.complete).toBe(1); // complete() was NOT called a second time
    expect(s.db._clips.get(up.id)!.status).toBe("ready");
    // And once ready, another retry returns the same 201.
    const third = await complete(s.e, up.id, parts);
    expect(third.status).toBe(201);
    expect((await json(third)).url).toBe(`${BASE}/${up.id}`);
  });

  it("DELETE after a completed-but-unfinalized upload removes the R2 object", async () => {
    const s = await setup();
    const up = await startUpload(s, 500);
    const key = s.db._clips.get(up.id)!.r2_key;
    const parts = await uploadAll(s.e, up.id, payload(500), PART_SIZE_BYTES);
    s.db._failNext("UPDATE clips SET status = 'ready'");
    expect((await complete(s.e, up.id, parts)).status).toBe(502);
    expect(s.r2.store.has(key)).toBe(true);

    const del = await worker.fetch(makeRequest(`${BASE}/clips/${up.id}`, { method: "DELETE", headers: auth() }), s.e);
    expect(del.status).toBe(200);
    expect(await json(del)).toEqual({ ok: true });
    expect(s.r2.store.has(key)).toBe(false);
    expect(s.db._clips.has(up.id)).toBe(false);
  });

  it("DELETE during an upload aborts it and deletes the key", async () => {
    const s = await setup();
    const up = await startUpload(s, PART_SIZE_BYTES + 5);
    const row = s.db._clips.get(up.id)!;
    const uploadId = row.upload_id!;
    await putPart(s.e, up.id, 1, payload(PART_SIZE_BYTES));
    const del = await worker.fetch(makeRequest(`${BASE}/clips/${up.id}`, { method: "DELETE", headers: auth() }), s.e);
    expect(del.status).toBe(200);
    expect(s.r2.uploads.get(uploadId)!.state).toBe("aborted");
    expect(s.r2.calls.deleted).toContain(row.r2_key);
    expect(s.db._clips.has(up.id)).toBe(false);
    // Parts for a deleted upload are now 404.
    expect((await putPart(s.e, up.id, 2, new Uint8Array(5))).status).toBe(404);
  });

  it("allows at most 2 pending uploads per user", async () => {
    const s = await setup();
    await startUpload(s, 100);
    await startUpload(s, 100);
    const third = await init(s.e, 100);
    expect(third.status).toBe(429);
    expect(await json(third)).toEqual({
      error: "too_many_pending_uploads",
      message: "Another long clip is still uploading. Try again when it finishes.",
    });
  });

  it("auto-aborts an idle pending upload on the next init", async () => {
    const s = await setup();
    const a = await startUpload(s, 100);
    await startUpload(s, 100);
    const rowA = s.db._clips.get(a.id)!;
    const uploadA = rowA.upload_id!;
    rowA.last_activity_at = nowSec() - 901;

    const c = await init(s.e, 100);
    expect(c.status).toBe(201);
    expect(s.db._clips.has(a.id)).toBe(false);
    expect(s.r2.uploads.get(uploadA)!.state).toBe("aborted");
    expect(s.r2.calls.deleted).toContain(rowA.r2_key);
  });

  it("the idle sweep never deletes a clip that a concurrent complete just finalized", async () => {
    const s = await setup();
    const a = await startUpload(s, 100);
    const rowA = s.db._clips.get(a.id)!;
    rowA.last_activity_at = nowSec() - 901;
    const uploadA = rowA.upload_id!;
    // A retried complete finalizes the row right after the sweep selected it.
    afterSelect(s.db, "SELECT id FROM clips WHERE user_id = ?1 AND status = 'uploading'", () => {
      s.r2.store.set(rowA.r2_key, mp4Bytes(84));
      Object.assign(rowA, { status: "ready", upload_id: null, expires_at: nowSec() + CLIP_TTL_SECONDS });
    });

    expect((await init(s.e, 100)).status).toBe(201);
    expect(s.db._clips.get(a.id)!.status).toBe("ready");
    expect(s.r2.store.has(rowA.r2_key)).toBe(true);
    expect(s.r2.calls.deleted).not.toContain(rowA.r2_key);
    expect(s.r2.uploads.get(uploadA)!.state).not.toBe("aborted");
    expect((await worker.fetch(makeRequest(`${BASE}/clip-meta/${a.id}`), s.e)).status).toBe(200);
  });

  it("a discard whose R2 delete fails leaves an expired row that the hourly purge finishes", async () => {
    const s = await setup();
    const a = await startUpload(s, 100);
    const rowA = s.db._clips.get(a.id)!;
    rowA.last_activity_at = nowSec() - 901;
    const realDelete = s.r2.bucket.delete.bind(s.r2.bucket);
    let failed = false;
    s.r2.bucket.delete = (async (keys: string | string[]) => {
      if (!failed) { failed = true; throw new Error("R2 down"); }
      return realDelete(keys);
    }) as typeof s.r2.bucket.delete;

    expect((await init(s.e, 100)).status).toBe(201);
    const stuck = s.db._clips.get(a.id)!;
    expect(stuck.status).toBe("purging");
    expect(stuck.expires_at).toBeLessThanOrEqual(nowSec());
    // Hidden from the owner's list and quota at once.
    const mine = await json(await worker.fetch(makeRequest(`${BASE}/clips/mine`, { headers: auth() }), s.e));
    expect((mine.clips as { id: string }[]).map((c) => c.id)).not.toContain(a.id);

    await runScheduled(worker, s.e);
    expect(s.db._clips.has(a.id)).toBe(false);
    expect(s.r2.calls.deleted).toContain(rowA.r2_key);
  });

  it("cron purge never deletes a pending row that complete finalized after the select", async () => {
    const now = nowSec();
    const s = await setup([clip({ id: "Old0001", r2_key: "clips/Old0001.mp4", expires_at: now - 10 })]);
    s.r2.store.set("clips/Old0001.mp4", new Uint8Array(1));
    const up = await startUpload(s, 100);
    const pending = s.db._clips.get(up.id)!;
    const uploadId = pending.upload_id!;
    pending.expires_at = now; // at the upload-window boundary
    afterSelect(s.db, "SELECT id, r2_key, upload_id FROM clips WHERE expires_at <= ?1", () => {
      s.r2.store.set(pending.r2_key, mp4Bytes(84));
      Object.assign(pending, { status: "ready", upload_id: null, expires_at: now + CLIP_TTL_SECONDS });
    });

    await runScheduled(worker, s.e);

    expect(s.db._clips.has("Old0001")).toBe(false);
    expect(s.r2.store.has("clips/Old0001.mp4")).toBe(false);
    expect(s.db._clips.get(up.id)!.status).toBe("ready");
    expect(s.r2.store.has(pending.r2_key)).toBe(true);
    expect(s.r2.uploads.get(uploadId)!.state).not.toBe("aborted");
  });

  it("counts pending reservations toward the byte quota", async () => {
    const GiB = 1024 * 1024 * 1024;
    const s = await setup([clip({ id: "Big0001", user_id: USER, size_bytes: MAX_ACTIVE_CLIP_BYTES_PER_USER - 3 * GiB })]);
    await startUpload(s, 2 * GiB); // 11 GiB used incl. this reservation
    const over = await init(s.e, 2 * GiB);
    expect(over.status).toBe(403);
    expect(await json(over)).toEqual({ error: "quota_exceeded", message: QUOTA_EXCEEDED_MESSAGE });
    expect((await init(s.e, GiB)).status).toBe(201); // exactly at the cap is fine
  });

  it("cron purge aborts expired pending uploads, deletes keys, and deletes only the selected ids", async () => {
    const now = nowSec();
    const expired = Array.from({ length: 2100 }, (_, i) =>
      clip({ id: `x${String(i).padStart(6, "0")}`, r2_key: `clips/x${i}.mp4`, expires_at: now - 10 }));
    const s = await setup([...expired, clip({ id: "Keep001", r2_key: "clips/Keep001.mp4" })]);
    for (const c of expired) s.r2.store.set(c.r2_key, new Uint8Array(1));
    s.r2.store.set("clips/Keep001.mp4", new Uint8Array(1));

    const up = await startUpload(s, 100);
    const pending = s.db._clips.get(up.id)!;
    const uploadId = pending.upload_id!;
    pending.expires_at = now - 1;
    // Move the pending row to the front of the scan so batch 1 selects it.
    s.db._clips.delete(up.id);
    const reordered = new Map([[up.id, pending], ...s.db._clips]);
    s.db._clips.clear();
    for (const [k, v] of reordered) s.db._clips.set(k, v);

    await runScheduled(worker, s.e);

    expect(s.r2.uploads.get(uploadId)!.state).toBe("aborted");
    expect(s.db._clips.has(up.id)).toBe(false);
    expect(s.db._clips.has("Keep001")).toBe(true);
    // 4 batches of 500 rows: 2000 purged; the 101 left keep their R2 objects.
    const left = [...s.db._clips.values()].filter((c) => c.expires_at <= now);
    expect(left.length).toBe(2101 - 2000);
    for (const c of left) expect(s.r2.store.has(c.r2_key)).toBe(true);
    expect(s.r2.store.has("clips/Keep001.mp4")).toBe(true);
    const inDeletes = s.db._sql.filter((q) => q.startsWith("DELETE FROM clips WHERE id IN ("));
    expect(inDeletes.length).toBeGreaterThan(0);
    for (const q of inDeletes) expect((q.match(/\?\d+/g) ?? []).length).toBeLessThanOrEqual(90);
    expect(s.db._sql.some((q) => q.startsWith("DELETE FROM clips WHERE expires_at"))).toBe(false);
  });

  it("legacy POST /clips keeps its 201 shape, stores narrated, and uses the new quota copy", async () => {
    const s = await setup();
    const res = await worker.fetch(
      makeRequest(`${BASE}/clips?title=t&champion=Lux&duration=30&narrated=1`, {
        method: "POST",
        headers: { ...auth(), "Content-Type": "video/mp4", "Content-Length": "48" },
        body: mp4Bytes(32),
      }),
      s.e,
    );
    expect(res.status).toBe(201);
    const out = await json(res);
    expect(Object.keys(out).sort()).toEqual(["expires_at", "id", "url"]);
    const row = s.db._clips.get(out.id as string)!;
    expect(row.narrated).toBe(1);
    expect(row.status).toBe("ready");

    const full = await setup([clip({ id: "Full001", user_id: USER, size_bytes: MAX_ACTIVE_CLIP_BYTES_PER_USER })]);
    const denied = await worker.fetch(
      makeRequest(`${BASE}/clips`, { method: "POST", headers: { ...auth(), "Content-Type": "video/mp4" }, body: mp4Bytes() }),
      full.e,
    );
    expect(denied.status).toBe(403);
    expect((await json(denied)).message).toBe("Active clip quota reached. Delete old clips or let them expire.");
  });

  it("the fake R2 rejects a stream without a known length (JS-wrapped bodies)", async () => {
    const { bucket } = makeFakeR2();
    const req = makeRequest(`${BASE}/x`, { method: "PUT", headers: { "Content-Length": "3" }, body: new Uint8Array([1, 2, 3]) });
    expect(knownLengthStreams.has(req.body!)).toBe(true);
    const wrapped = req.body!.pipeThrough(new TransformStream());
    await expect(bucket.put("k", wrapped)).rejects.toThrow("stream must have a known length");
    const mpu = await bucket.createMultipartUpload("k2");
    await expect(mpu.uploadPart(1, new ReadableStream())).rejects.toThrow("stream must have a known length");
    await expect(bucket.put("k3", new Uint8Array([1]).buffer)).resolves.toBeTruthy();
  });
});
