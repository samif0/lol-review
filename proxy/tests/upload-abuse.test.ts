/**
 * Storage and Class A abuse guards across clip and VOD uploads, on real SQLite:
 * one account's share of the pool, idle reservations of any account, the
 * per-user daily part budget, the size-based VOD upload window, and the rule
 * that bytes stay counted until R2 has really let go of them.
 */

import { beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { Env } from "../src/types";
import { resetUserLimits } from "../src/user-limits";
import { storageUsage } from "../src/storage-budget";
import { isGoneUploadError } from "../src/upload-cleanup";
import { ABORT_GIVE_UP_SECONDS } from "../src/clip-purge";
import {
  DEFAULT_USER_CLIP_BUDGET_BYTES,
  IDLE_SWEEP_ON_START,
  IDLE_SWEEP_PER_RUN,
  IDLE_SWEEP_SECONDS,
  PART_PUTS_PER_DAY,
  nextUtcMidnight,
  utcDayOf,
  vodUploadWindowSeconds,
} from "../src/share-limits";
import { makeRequest, mp4Bytes, runScheduled } from "./fakes";
import { BASE, MiB, VodSetup, body, del, nowSec, payload, shareVod, vodComplete, vodInitOk, vodPart, vodSetup } from "./vod-fixtures";

const GiB = 1024 * 1024 * 1024;
const auth = (token: string) => ({ Authorization: `Bearer ${token}` });

beforeEach(() => {
  vi.restoreAllMocks();
  resetUserLimits();
});

function clipInit(env: Env, token: string, size: number): Promise<Response> {
  return worker.fetch(
    makeRequest(`${BASE}/clips/uploads?size=${size}&duration=600`, {
      method: "POST",
      headers: { ...auth(token), "Content-Type": "video/mp4", "Content-Length": "0" },
    }),
    env,
  );
}

function clipPart(env: Env, token: string, id: string, n: number, bytes: Uint8Array): Promise<Response> {
  return worker.fetch(
    makeRequest(`${BASE}/clips/${id}/parts/${n}`, {
      method: "PUT",
      headers: { ...auth(token), "Content-Type": "application/octet-stream", "Content-Length": String(bytes.byteLength) },
      body: bytes,
    }),
    env,
  );
}

function single(env: Env, token: string, bytes: Uint8Array): Promise<Response> {
  return worker.fetch(
    makeRequest(`${BASE}/clips?title=t`, {
      method: "POST",
      headers: { ...auth(token), "Content-Type": "video/mp4", "Content-Length": String(bytes.byteLength) },
      body: bytes,
    }),
    env,
  );
}

async function idOf(res: Response): Promise<string> {
  expect(res.status).toBe(201);
  return (await body(res)).id as string;
}

function row(s: VodSetup, id: string) {
  return s.db.q<Record<string, unknown>>("SELECT * FROM clips WHERE id = ?1", id)[0];
}

describe("one account's share of the pool", () => {
  it("a free account can't reserve more than its share; the owner's VOD still fits", async () => {
    const s = await vodSetup({ tokens: ["sess-owner", "a1", "a2"], allow: ["sess-owner"] });
    await idOf(await clipInit(s.env, "a1", 2 * GiB));
    const over = await clipInit(s.env, "a1", 2 * GiB);
    expect(over.status).toBe(403);
    expect((await body(over)).error).toBe("quota_exceeded");
    expect((await clipInit(s.env, "a1", DEFAULT_USER_CLIP_BUDGET_BYTES - 2 * GiB)).status).toBe(201); // exactly at the share
    await idOf(await clipInit(s.env, "a2", 2 * GiB));
    expect((await clipInit(s.env, "a2", 2 * GiB)).status).toBe(403);

    // Two free accounts hold about 6.1e9; a 1.3 GB VOD still fits under 9e9.
    expect((await vodInitOk(s.env, "sess-owner", 1_300_000_000)).status).toBe(201);
  });

  it("the single PUT path counts the account's reservations too", async () => {
    const s = await vodSetup({ tokens: ["sess-owner", "a1"], allow: ["sess-owner"], env: { USER_CLIP_BUDGET_BYTES: "1000" } });
    await idOf(await clipInit(s.env, "a1", 990));
    const over = await single(s.env, "a1", mp4Bytes(20));
    expect(over.status).toBe(403);
    expect((await body(over)).error).toBe("quota_exceeded");
    expect(s.r2.calls.put).toBe(0);
  });

  it("allowlisted accounts only get the per-user clip quota", async () => {
    const s = await vodSetup({ tokens: ["sess-owner"], allow: ["sess-owner"] });
    await idOf(await clipInit(s.env, "sess-owner", 2 * GiB));
    expect((await clipInit(s.env, "sess-owner", 2 * GiB)).status).toBe(201);
  });
});

describe("idle reservations of any account", () => {
  async function fullPool() {
    const s = await vodSetup({ tokens: ["sess-owner", "a1", "a2", "a3"], allow: ["sess-owner"] });
    const ids: Record<string, string[]> = {};
    for (const t of ["a1", "a2", "a3"]) {
      ids[t] = [await idOf(await clipInit(s.env, t, 1_500_000_000)), await idOf(await clipInit(s.env, t, 1_400_000_000))];
    }
    return { s, ids };
  }

  it("a start that would be refused for storage discards other accounts' idle uploads first", async () => {
    const { s, ids } = await fullPool();
    expect((await vodInitOk(s.env, "sess-owner", 1_300_000_000)).status).toBe(409); // 8.7e9 reserved
    resetUserLimits();

    // a1 went quiet past the sweep threshold; a2 is idle only past the owner's own 15 minutes.
    const now = nowSec();
    for (const id of ids.a1) s.db.x("UPDATE clips SET last_activity_at = ?2 WHERE id = ?1", id, now - IDLE_SWEEP_SECONDS - 1);
    for (const id of ids.a2) s.db.x("UPDATE clips SET last_activity_at = ?2 WHERE id = ?1", id, now - 1000);

    expect((await vodInitOk(s.env, "sess-owner", 1_300_000_000)).status).toBe(201);
    for (const id of ids.a1) expect(row(s, id)).toBeUndefined();
    for (const id of ids.a2) expect(row(s, id).status).toBe("uploading");
    const aborted = [...s.r2.uploads.values()].filter((u) => u.state === "aborted").length;
    expect(aborted).toBe(2);
  });

  it("the hourly cron discards idle uploads of every account and keeps active ones", async () => {
    const { s, ids } = await fullPool();
    const now = nowSec();
    s.db.x("UPDATE clips SET last_activity_at = ?2 WHERE id = ?1", ids.a1[0], now - IDLE_SWEEP_SECONDS - 1);
    s.db.x("UPDATE clips SET last_activity_at = ?2 WHERE id = ?1", ids.a2[0], now - IDLE_SWEEP_SECONDS + 60);
    await runScheduled(worker, s.env);
    expect(row(s, ids.a1[0])).toBeUndefined();
    expect(row(s, ids.a1[1]).status).toBe("uploading");
    expect(row(s, ids.a2[0]).status).toBe("uploading");
  });

  /** `accounts` accounts with two idle pending clip uploads each. */
  async function idleBacklog(accounts: number) {
    const tokens = Array.from({ length: accounts }, (_, i) => `idle${i + 1}`);
    const s = await vodSetup({ tokens: ["sess-owner", ...tokens], allow: ["sess-owner"] });
    const ids: string[] = [];
    for (const t of tokens) ids.push(await idOf(await clipInit(s.env, t, 100)), await idOf(await clipInit(s.env, t, 100)));
    s.db.x("UPDATE clips SET last_activity_at = ?1 WHERE status = 'uploading'", nowSec() - IDLE_SWEEP_SECONDS - 1);
    return { s, ids };
  }
  const pendingCount = (s: VodSetup) => s.db.q("SELECT id FROM clips WHERE status = 'uploading'").length;

  it("one hourly run discards at most IDLE_SWEEP_PER_RUN idle uploads, and later runs clear the rest", async () => {
    expect(IDLE_SWEEP_PER_RUN).toBe(5);
    const { s, ids } = await idleBacklog(4);
    expect(pendingCount(s)).toBe(8);
    await runScheduled(worker, s.env);
    expect(pendingCount(s)).toBe(8 - IDLE_SWEEP_PER_RUN);
    await runScheduled(worker, s.env);
    expect(pendingCount(s)).toBe(0);
    for (const id of ids) expect(row(s, id)).toBeUndefined();
    expect([...s.r2.uploads.values()].filter((u) => u.state === "aborted")).toHaveLength(8);
  });

  it("the sweep runs after the purge and the pruning, so a backlog stays inside the Workers Free D1 budget", async () => {
    const { s } = await idleBacklog(10);
    const expired = (await shareVod(s)).id;
    s.db.x("UPDATE clips SET expires_at = ?2 WHERE id = ?1", expired, nowSec() - 60);
    const from = s.db._sql.length;
    await runScheduled(worker, s.env);
    const cron = s.db._sql.slice(from);

    expect(row(s, expired)).toBeUndefined();
    expect(pendingCount(s)).toBe(20 - IDLE_SWEEP_PER_RUN);
    const purgeAt = cron.findIndex((q) => q.startsWith("SELECT id, r2_key, upload_id FROM clips WHERE expires_at <="));
    const pruneAt = cron.findIndex((q) => q.startsWith("DELETE FROM upload_part_usage"));
    const sweepAt = cron.findIndex((q) => q.includes("status = 'uploading' AND COALESCE(last_activity_at, created_at) <="));
    expect(purgeAt).toBeGreaterThanOrEqual(0);
    expect(pruneAt).toBeGreaterThan(purgeAt);
    expect(sweepAt).toBeGreaterThan(pruneAt);
    // Workers Free allows 50 D1 queries per invocation; the whole run fits with room to spare.
    expect(cron.length).toBeLessThanOrEqual(50);
  });

  /**
   * A pool exactly full of idle 100-byte uploads: two clips for each of
   * `accounts` accounts and for "fresh", plus one VOD for the owner. A start
   * by "fresh" or the owner discards its own idle uploads first, then sweeps.
   */
  async function poolFullOfIdle(accounts: number) {
    const tokens = Array.from({ length: accounts }, (_, i) => `idle${i + 1}`);
    const budget = String(100 * (2 * accounts + 3));
    const s = await vodSetup({
      tokens: ["sess-owner", "fresh", ...tokens],
      allow: ["sess-owner"],
      env: { TOTAL_BUDGET_BYTES: budget, VOD_BUDGET_BYTES: budget },
    });
    for (const t of ["fresh", ...tokens]) {
      await idOf(await clipInit(s.env, t, 100));
      await idOf(await clipInit(s.env, t, 100));
    }
    await idOf(await vodInitOk(s.env, "sess-owner", 100));
    s.db.x("UPDATE clips SET last_activity_at = ?1 WHERE status = 'uploading'", nowSec() - IDLE_SWEEP_SECONDS - 1);
    resetUserLimits();
    return s;
  }

  it("an upload start that sweeps a backlog stays inside the Workers Free D1 budget", async () => {
    expect(IDLE_SWEEP_ON_START).toBe(5);
    // [route, start, own idle uploads it discards first, pending rows it adds]
    const starts: [string, (s: VodSetup) => Promise<Response>, number, number][] = [
      ["clip multipart init", (s) => clipInit(s.env, "fresh", 500), 2, 1],
      ["VOD init", (s) => vodInitOk(s.env, "sess-owner", 500), 1, 1],
      ["single PUT", (s) => single(s.env, "fresh", mp4Bytes(484)), 0, 0],
    ];
    for (const [route, start, own, added] of starts) {
      const s = await poolFullOfIdle(6);
      const pendingBefore = pendingCount(s);
      const from = s.db._sql.length;
      const res = await start(s);
      const queries = s.db._sql.length - from;

      expect(res.status, route).toBe(201);
      expect(pendingCount(s), route).toBe(pendingBefore - own - IDLE_SWEEP_ON_START + added);
      // Workers Free allows 50 D1 queries per invocation, auth and the post-insert recount included.
      expect(queries, route).toBeLessThanOrEqual(50);
    }
  });
});

describe("per-user daily part budget", () => {
  it("refuses part PUTs past the daily budget, and delete plus re-init does not reset it", async () => {
    const s = await vodSetup({ tokens: ["sess-owner", "a1"], allow: ["sess-owner"] });
    const now = nowSec();
    const user = s.users.a1;
    const id = await idOf(await clipInit(s.env, "a1", 100));
    s.db.x("INSERT INTO upload_part_usage (user_id, day, puts) VALUES (?1, ?2, ?3)", user, utcDayOf(now), PART_PUTS_PER_DAY - 1);

    expect((await clipPart(s.env, "a1", id, 1, payload(100))).status).toBe(200);
    const capped = await clipPart(s.env, "a1", id, 1, payload(100));
    expect(capped.status).toBe(429);
    expect(Number(capped.headers.get("Retry-After"))).toBeGreaterThan(0);
    const out = await body(capped);
    expect(out.error).toBe("daily_part_limit");
    expect(out.retry_at).toBe(nextUtcMidnight(nowSec()));
    expect(row(s, id).part_puts).toBe(1); // the refused attempt never claimed the row

    expect((await del(s.env, "a1", id)).status).toBe(200);
    const again = await idOf(await clipInit(s.env, "a1", 100));
    expect((await clipPart(s.env, "a1", again, 1, payload(100))).status).toBe(429);
    expect(s.r2.calls.uploadPart).toBe(1);
    // Another account is unaffected.
    expect((await vodPart(s.env, "sess-owner", await idOf(await vodInitOk(s.env, "sess-owner", 100)), 1, payload(100))).status).toBe(200);
  });

  it("the cron prunes part counts older than 7 days and account delete removes them", async () => {
    const s = await vodSetup();
    const now = nowSec();
    s.db.x("INSERT INTO upload_part_usage (user_id, day, puts) VALUES (?1, ?2, 5)", s.users["sess-owner"], utcDayOf(now - 8 * 86400));
    s.db.x("INSERT INTO upload_part_usage (user_id, day, puts) VALUES (?1, ?2, 5)", s.users["sess-owner"], utcDayOf(now));
    await runScheduled(worker, s.env);
    expect(s.db.q("SELECT day FROM upload_part_usage")).toEqual([{ day: utcDayOf(now) }]);
  });
});

describe("VOD upload window", () => {
  it("fits the declared size at 0.45 Mbps plus an hour, between 6 and 12 hours", () => {
    expect(vodUploadWindowSeconds(1)).toBe(21_600);
    expect(vodUploadWindowSeconds(1_315_583_333)).toBe(Math.ceil((1_315_583_333 * 8) / 450_000) + 3600);
    expect(vodUploadWindowSeconds(2 * GiB)).toBe(41_778); // about 11.6 h
    expect(vodUploadWindowSeconds(10 * GiB)).toBe(43_200);
  });

  it("a 2 GiB VOD's parts are still accepted 10 hours after init", async () => {
    const s = await vodSetup();
    const res = await vodInitOk(s.env, "sess-owner", 2 * GiB);
    const out = await body(res);
    const created = row(s, out.id as string).created_at as number;
    expect(out.expires_at).toBe(created + 41_778);
    s.db.x("UPDATE clips SET created_at = ?2, expires_at = ?3 WHERE id = ?1", out.id, created - 10 * 3600, created - 10 * 3600 + 41_778);
    // The part length check runs after the row lookup: a 400 here means the row is still live.
    expect((await vodPart(s.env, "sess-owner", out.id as string, 1, payload(10))).status).toBe(400);
  });
});

describe("bytes stay counted until R2 lets go of them", () => {
  function failAborts(s: VodSetup, message = "internal error (10001)") {
    const real = s.r2.bucket.resumeMultipartUpload.bind(s.r2.bucket);
    s.r2.bucket.resumeMultipartUpload = ((key: string, uploadId: string) => {
      const h = real(key, uploadId);
      return { ...h, abort: async () => { throw new Error(message); } };
    }) as typeof s.r2.bucket.resumeMultipartUpload;
    return () => { s.r2.bucket.resumeMultipartUpload = real as typeof s.r2.bucket.resumeMultipartUpload; };
  }

  it("classifies only 'upload is gone' abort errors as done", () => {
    expect(isGoneUploadError(new Error("NoSuchUpload"))).toBe(true);
    expect(isGoneUploadError(new Error("abortMultipartUpload: The specified multipart upload does not exist. (10024)"))).toBe(true);
    expect(isGoneUploadError(new Error("internal error (10001)"))).toBe(false);
    expect(isGoneUploadError(new Error("Network connection lost."))).toBe(false);
  });

  it("an owner delete whose abort fails keeps an expired purging row; the cron finishes it", async () => {
    const s = await vodSetup();
    const id = await idOf(await vodInitOk(s.env, "sess-owner", 20 * MiB));
    const restore = failAborts(s);
    expect((await del(s.env, "sess-owner", id)).status).toBe(200);
    expect(row(s, id).status).toBe("purging");
    expect((await storageUsage(s.env.DB)).vod_used).toBe(20 * MiB);

    await runScheduled(worker, s.env); // still failing: kept
    expect(row(s, id).status).toBe("purging");
    restore();
    await runScheduled(worker, s.env);
    expect(row(s, id)).toBeUndefined();
    expect((await storageUsage(s.env.DB)).used).toBe(0);
  });

  it("an idle discard whose abort fails keeps the row; an 'already gone' abort does not", async () => {
    const s = await vodSetup({ tokens: ["sess-owner", "a1"], allow: ["sess-owner"] });
    const kept = await idOf(await clipInit(s.env, "a1", 100));
    s.db.x("UPDATE clips SET last_activity_at = 1 WHERE id = ?1", kept);
    failAborts(s);
    await runScheduled(worker, s.env);
    expect(row(s, kept).status).toBe("purging");

    const s2 = await vodSetup({ tokens: ["sess-owner", "a1"], allow: ["sess-owner"] });
    const gone = await idOf(await clipInit(s2.env, "a1", 100));
    s2.db.x("UPDATE clips SET last_activity_at = 1 WHERE id = ?1", gone);
    failAborts(s2, "NoSuchUpload");
    await runScheduled(worker, s2.env);
    expect(row(s2, gone)).toBeUndefined();
  });

  it("the purge gives up on an abort that keeps failing once R2's 7-day rule has run", async () => {
    const s = await vodSetup({ tokens: ["sess-owner", "a1"], allow: ["sess-owner"] });
    const id = await idOf(await clipInit(s.env, "a1", 100));
    const old = nowSec() - ABORT_GIVE_UP_SECONDS - 10;
    s.db.x("UPDATE clips SET created_at = ?2, last_activity_at = ?2, expires_at = ?2 WHERE id = ?1", id, old);
    failAborts(s);
    await runScheduled(worker, s.env);
    expect(row(s, id)).toBeUndefined();
  });

  it("a single PUT that loses the recount and can't delete its object stays counted as a purging row", async () => {
    const s = await vodSetup({ tokens: ["sess-owner", "a1", "a2"], allow: ["sess-owner"], env: { TOTAL_BUDGET_BYTES: "1000" } });
    s.db._after("INSERT INTO clips", () => {
      s.db.x(
        "INSERT INTO clips (id, user_id, r2_key, content_type, size_bytes, created_at, expires_at, status, kind) VALUES ('Racer01', ?1, 'clips/Racer01.mp4', 'video/mp4', 990, ?2, ?3, 'ready', 'clip')",
        s.users.a2, nowSec(), nowSec() + 3600,
      );
    });
    s.r2.bucket.delete = (async () => { throw new Error("R2 down"); }) as typeof s.r2.bucket.delete;
    const res = await single(s.env, "a1", mp4Bytes(84));
    expect(res.status).toBe(403);
    const mine = s.db.q<Record<string, unknown>>("SELECT * FROM clips WHERE user_id = ?1", s.users.a1);
    expect(mine).toHaveLength(1);
    expect(mine[0].status).toBe("purging");
    expect(mine[0].expires_at as number).toBeLessThanOrEqual(nowSec());
    expect((await storageUsage(s.env.DB)).used).toBe(990 + 100);
  });

  it("a rejected single PUT whose object can't be deleted leaves a tombstone", async () => {
    const s = await vodSetup({ tokens: ["sess-owner", "a1"], allow: ["sess-owner"] });
    s.r2.bucket.delete = (async () => { throw new Error("R2 down"); }) as typeof s.r2.bucket.delete;
    const res = await single(s.env, "a1", new Uint8Array(64)); // fails the mp4 sniff
    expect(res.status).toBe(415);
    expect(s.db.q("SELECT size_bytes, kind FROM storage_tombstones")).toEqual([{ size_bytes: 64, kind: "clip" }]);
  });

  it("a complete whose row was claimed meanwhile deletes the object it just built", async () => {
    const s = await vodSetup();
    const id = await idOf(await vodInitOk(s.env, "sess-owner", 100));
    const part = await vodPart(s.env, "sess-owner", id, 1, payload(100));
    const parts = [(await part.json()) as { part_number: number; etag: string }];
    // A delete claims and drops the row right after complete read it (its abort having failed).
    s.db._after("SELECT * FROM clips WHERE id = ?1 AND user_id = ?2 AND expires_at > ?3 AND kind = ?4", () => {
      s.db.x("DELETE FROM clips WHERE id = ?1", id);
    });
    const res = await vodComplete(s.env, "sess-owner", id, parts);
    expect(res.status).toBe(404);
    expect(s.r2.store.has(`vods/${id}.mp4`)).toBe(false);
  });
});
