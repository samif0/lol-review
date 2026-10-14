/**
 * The global storage budget: limits parsing, fitsAt, release grouping, what
 * usage counts, and the refusal on BOTH clip upload paths (single PUT and
 * multipart init), including the post-insert rollback of a concurrent loser.
 */

import { beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { Env } from "../src/types";
import { resetUserLimits } from "../src/user-limits";
import { fitsAt, nextHour, readReleases, StoragePool, storageUsage, STORAGE_FULL_MESSAGE, STORAGE_USAGE_SQL } from "../src/storage-budget";
import { RELEASES_MAX, totalBudgetBytes, vodBudgetBytes, vodServingOn, vodSharingOn } from "../src/share-limits";
import { clip, dbWithSessions, FakeDb, json, makeEnv, makeFakeR2, makeRequest, mp4Bytes, nowSec } from "./fakes";
import { makeSqliteD1, migrationFiles } from "./sqlite-d1";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

beforeEach(() => {
  vi.restoreAllMocks();
  resetUserLimits();
});

const BASE = "https://clips.revu.lol";
const TOKEN = "sess-budget";
const USER = 31;
const STORAGE_FULL = { error: "quota_exceeded", reason: "storage_full", message: STORAGE_FULL_MESSAGE };

describe("migration 0006", () => {
  it("applies on top of 0001 to 0005 and turns existing rows into kind 'clip'", () => {
    const files = migrationFiles();
    expect(files.slice(-1)).toEqual(["0006_vod_shares.sql"]);
    const { DatabaseSync } = createRequire(import.meta.url)("node:sqlite");
    const raw = new DatabaseSync(":memory:");
    raw.exec("PRAGMA foreign_keys = ON");
    const read = (f: string) => readFileSync(fileURLToPath(new URL(`../migrations/${f}`, import.meta.url)), "utf8");
    for (const f of files.slice(0, -1)) raw.exec(read(f));
    raw.exec("INSERT INTO users (id, email, created_at) VALUES (1, 'a@example.com', 1)");
    raw.exec("INSERT INTO clips (id, user_id, r2_key, content_type, size_bytes, created_at, expires_at) VALUES ('Old0001', 1, 'clips/Old0001.mp4', 'video/mp4', 5, 1, 2)");
    raw.exec(read("0006_vod_shares.sql"));
    expect({ ...raw.prepare("SELECT kind, part_puts, quality, play_day, play_count FROM clips").get() }).toEqual({
      kind: "clip", part_puts: 0, quality: null, play_day: null, play_count: 0,
    });
    const tables = raw.prepare("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name").all().map((r: { name: string }) => r.name);
    expect(tables).toEqual(expect.arrayContaining(["vod_sharers", "vod_share_usage", "vod_share_meta", "storage_tombstones"]));
    // Deleting a user cascades to the allowlist; tombstones have no owner to cascade from.
    raw.exec("INSERT INTO vod_sharers (user_id, added_at) VALUES (1, 1)");
    raw.exec("INSERT INTO storage_tombstones (r2_key, size_bytes, created_at) VALUES ('k', 1, 1)");
    raw.exec("DELETE FROM users WHERE id = 1");
    expect(raw.prepare("SELECT COUNT(*) AS n FROM vod_sharers").get().n).toBe(0);
    expect({ ...raw.prepare("SELECT kind FROM storage_tombstones").get() }).toEqual({ kind: "clip" });
  });
});

describe("limits from the environment", () => {
  const env = (vars: Partial<Env>) => makeEnv({ TOTAL_BUDGET_BYTES: undefined, VOD_BUDGET_BYTES: undefined, ...vars });

  it("uses the defaults unless a budget is 1 to 13 plain digits above zero", () => {
    expect([totalBudgetBytes(env({})), vodBudgetBytes(env({}))]).toEqual([9e9, 7e9]);
    expect(totalBudgetBytes(env({ TOTAL_BUDGET_BYTES: "12000000000" }))).toBe(12e9);
    for (const bad of ["", "0", "-5", "9e9", "1.5", " 9000", "12345678901234", "abc"]) {
      expect(totalBudgetBytes(env({ TOTAL_BUDGET_BYTES: bad })), bad).toBe(9e9);
    }
    expect(vodBudgetBytes(env({ TOTAL_BUDGET_BYTES: "5000000000" }))).toBe(5e9); // clamped to total
    expect(vodBudgetBytes(env({ VOD_BUDGET_BYTES: "1000" }))).toBe(1000);
  });

  it("switches are off only for 0 or off", () => {
    for (const [value, on] of [[undefined, true], ["1", true], ["", true], ["yes", true], ["0", false], [" OFF ", false], ["off", false]] as const) {
      expect(vodSharingOn(env({ VOD_SHARING: value })), String(value)).toBe(on);
      expect(vodServingOn(env({ VOD_SERVING: value })), String(value)).toBe(on);
    }
  });
});

describe("fitsAt and releases", () => {
  const pool = (over: Partial<StoragePool> = {}): StoragePool => ({
    total_budget_bytes: 1000, vod_budget_bytes: 800, used_bytes: 900, vod_used_bytes: 500, free_bytes: 100, releases: [], ...over,
  });

  it("is now when it fits, the first sufficient release when it will, else null", () => {
    expect(fitsAt(pool(), 100, 50)).toBe(50);
    const releases = [
      { at: 7200, bytes: 300, vod_bytes: 0 }, // frees total only: VOD space still 300
      { at: 3600, bytes: 50, vod_bytes: 50 },
      { at: 10800, bytes: 400, vod_bytes: 400 },
    ];
    expect(fitsAt(pool({ releases }), 150, 50)).toBe(3600);
    expect(fitsAt(pool({ releases }), 350, 50)).toBe(7200);
    expect(fitsAt(pool({ releases }), 400, 50)).toBe(10800); // total fits at 7200, the VOD subset only at 10800
    expect(fitsAt(pool({ releases }), 5000, 50)).toBeNull();
  });

  it("groups by purge hour: purging and tombstones next hour, others after expiry", async () => {
    const db = makeSqliteD1();
    const now = 1_760_400_100;
    db.x("INSERT INTO users (id, email, created_at) VALUES (1, 'a@example.com', 1)");
    const add = (id: string, kind: string, size: number, status: string, exp: number) =>
      db.x("INSERT INTO clips (id, user_id, r2_key, content_type, size_bytes, created_at, expires_at, status, kind) VALUES (?1, 1, ?1, 'video/mp4', ?2, 1, ?3, ?4, ?5)", id, size, exp, status, kind);
    add("Purge01", "vod", 10, "purging", now + 99999);
    add("Expd001", "clip", 20, "ready", now - 50);
    add("Live001", "vod", 30, "ready", now + 7200);
    add("Upl0001", "clip", 40, "uploading", now + 7300);
    db.x("INSERT INTO storage_tombstones (r2_key, size_bytes, kind, created_at) VALUES ('t1', 5, 'vod', 1)");
    expect(await readReleases(db, now)).toEqual([
      { at: nextHour(now), bytes: 35, vod_bytes: 15 },
      { at: nextHour(now + 7200), bytes: 70, vod_bytes: 30 },
    ]);
    for (let i = 0; i < RELEASES_MAX + 5; i++) add(`Many${String(i).padStart(3, "0")}`, "clip", 1, "ready", now + (i + 3) * 3600);
    expect(await readReleases(db, now)).toHaveLength(RELEASES_MAX);
  });

  it("usage counts every row of every kind and status, expired or not, plus tombstones", async () => {
    const db = makeSqliteD1();
    db.x("INSERT INTO users (id, email, created_at) VALUES (1, 'a@example.com', 1)");
    let n = 0;
    for (const [kind, status, exp] of [["clip", "ready", 9e9], ["vod", "uploading", 9e9], ["clip", "purging", 1], ["vod", "ready", 1]] as const) {
      db.x("INSERT INTO clips (id, user_id, r2_key, content_type, size_bytes, created_at, expires_at, status, kind) VALUES (?1, 1, ?1, 'video/mp4', 100, 1, ?2, ?3, ?4)", `Row000${++n}`, exp, status, kind);
    }
    db.x("INSERT INTO storage_tombstones (r2_key, size_bytes, kind, created_at) VALUES ('t1', 7, 'clip', 1)");
    expect(await storageUsage(db)).toEqual({ used: 407, vod_used: 200 });
    expect(STORAGE_USAGE_SQL).not.toContain("COALESCE(SUM(size_bytes)");
  });
});

describe("clip uploads respect the global budget", () => {
  async function setup(budget: string, seed: Parameters<typeof dbWithSessions>[1] = []) {
    const db = await dbWithSessions({ [TOKEN]: USER }, seed);
    const r2 = makeFakeR2();
    return { db, r2, e: makeEnv({ DB: db, CLIPS: r2.bucket, TOTAL_BUDGET_BYTES: budget }) };
  }

  function single(e: Env, bytes: Uint8Array) {
    return worker.fetch(makeRequest(`${BASE}/clips?title=t`, {
      method: "POST",
      headers: { Authorization: `Bearer ${TOKEN}`, "Content-Type": "video/mp4", "Content-Length": String(bytes.byteLength) },
      body: bytes,
    }), e);
  }

  function init(e: Env, size: number) {
    return worker.fetch(makeRequest(`${BASE}/clips/uploads?size=${size}&duration=60`, {
      method: "POST", headers: { Authorization: `Bearer ${TOKEN}`, "Content-Type": "video/mp4" },
    }), e);
  }

  it("single PUT: 403 storage_full when used + size > TOTAL, counting other users, VODs, purging rows and tombstones", async () => {
    const s = await setup("1000", [
      clip({ id: "Other01", user_id: 99, size_bytes: 300 }),
      clip({ id: "Vod0001", user_id: 99, size_bytes: 300, kind: "vod" }),
      clip({ id: "Purge01", user_id: USER, size_bytes: 200, status: "purging", expires_at: nowSec() - 10 }),
    ]);
    s.db._tombstones.set("x", { r2_key: "x", upload_id: null, size_bytes: 150, kind: "clip", created_at: 1 });
    const over = await single(s.e, mp4Bytes(35)); // 950 + 51 > 1000
    expect(over.status).toBe(403);
    expect(await json(over)).toEqual(STORAGE_FULL);
    expect(s.r2.calls.put).toBe(0);
    const fits = await single(s.e, mp4Bytes(34)); // exactly 1000
    expect(fits.status).toBe(201);
  });

  it("multipart init: 403 storage_full before any R2 call, and an abort releases the bytes", async () => {
    const s = await setup("1000", [clip({ id: "Other01", user_id: 99, size_bytes: 600 })]);
    const first = await init(s.e, 400);
    expect(first.status).toBe(201);
    const over = await init(s.e, 1);
    expect(over.status).toBe(403);
    expect(await json(over)).toEqual(STORAGE_FULL);
    expect(s.r2.uploads.size).toBe(1);
    // Deleting the pending upload frees its reservation at once.
    const id = (await json(first)).id as string;
    const del = await worker.fetch(makeRequest(`${BASE}/clips/${id}`, { method: "DELETE", headers: { Authorization: `Bearer ${TOKEN}` } }), s.e);
    expect(del.status).toBe(200);
    expect((await init(s.e, 400)).status).toBe(201);
  });

  /** A DB whose budget reads see `racer` bytes appear after the first read. */
  function racing(db: FakeDb, racer: number): D1Database {
    let reads = 0;
    return {
      ...db,
      prepare(sql: string) {
        const stmt = db.prepare(sql);
        if (!sql.includes("FROM storage_tombstones) AS b")) return stmt;
        const first = stmt.first.bind(stmt);
        stmt.first = (async () => {
          const row = (await first()) as { used: number; vod_used: number };
          return (++reads > 1 ? { ...row, used: row.used + racer } : row) as never;
        }) as typeof stmt.first;
        return stmt;
      },
      batch: db.batch,
    } as unknown as D1Database;
  }

  it("single PUT: the post-insert recount rolls back a concurrent loser (object and row)", async () => {
    const s = await setup("1000");
    const res = await single({ ...s.e, DB: racing(s.db, 980) }, mp4Bytes(34));
    expect(res.status).toBe(403);
    expect(await json(res)).toEqual(STORAGE_FULL);
    expect(s.db._clips.size).toBe(0);
    expect(s.r2.store.size).toBe(0);
  });

  it("multipart init: the post-insert recount discards a concurrent loser", async () => {
    const s = await setup("1000");
    const res = await init({ ...s.e, DB: racing(s.db, 700) }, 400);
    expect(res.status).toBe(403);
    expect(await json(res)).toEqual(STORAGE_FULL);
    expect(s.db._clips.size).toBe(0);
    expect([...s.r2.uploads.values()].map((u) => u.state)).toEqual(["aborted"]);
  });
});
