import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { addLoginRequest, addUser, FakeAuthState } from "./fake-auth";
import { clip, makeRequest, nowSec, runScheduled, sha256Hex } from "./fakes";
import worker from "../src/index";
import {
  AUTH, CLEAR_COOKIE, body, call, resetWebLimiters, seedDesktopSession, seedWebUser, stubFetch, webReq, webSetup,
} from "./web-fixtures";

beforeEach(() => {
  vi.restoreAllMocks();
  resetWebLimiters();
});
afterEach(() => vi.unstubAllGlobals());

let ipSeq = 0;
/** Desktop /auth/* is limited 5/min per IP by index.ts, so each call gets its own IP. */
function desktopReq(path: string, init: { method?: string; body?: unknown; bearer?: string } = {}): Request {
  const headers: Record<string, string> = { "CF-Connecting-IP": `10.0.0.${++ipSeq}` };
  if (init.body !== undefined) headers["Content-Type"] = "application/json";
  if (init.bearer) headers.Authorization = `Bearer ${init.bearer}`;
  return makeRequest(`${AUTH}${path}`, {
    method: init.method ?? "GET", headers, body: init.body === undefined ? undefined : JSON.stringify(init.body),
  });
}

describe("GET /auth/web/clips", () => {
  it("lists only the caller's unexpired clips, newest first, in the desktop shape", async () => {
    const now = nowSec();
    const { env, state } = webSetup({}, [
      clip({ id: "Mine001", user_id: 1, created_at: now - 50, narrated: 1, has_transcript: 1 }),
      clip({ id: "Mine002", user_id: 1, created_at: now - 10, status: "uploading", title: null, champion: null, duration_s: null }),
      clip({ id: "Gone001", user_id: 1, expires_at: now - 1 }),
      clip({ id: "Their01", user_id: 2 }),
    ]);
    const { token } = await seedWebUser(state, "lister@example.com");
    addUser(state, "other@example.com");
    const res = await call(env, webReq("/auth/web/clips", { cookie: token }));
    expect(res.status).toBe(200);
    expect(res.headers.get("Access-Control-Allow-Credentials")).toBe("true");
    const clips = (await body(res)).clips as Array<Record<string, unknown>>;
    expect(clips.map((c) => c.id)).toEqual(["Mine002", "Mine001"]);
    // 4.2 adds kind, quality and has_notes (full-game shares list here too).
    expect(Object.keys(clips[1]).sort()).toEqual([
      "champion", "created_at", "duration_s", "expires_at", "has_notes", "has_transcript", "id", "kind", "narrated", "quality",
      "size_bytes", "status", "title", "url", "view_count",
    ]);
    expect(clips[1]).toMatchObject({
      url: "https://clips.revu.lol/Mine001", narrated: true, has_transcript: true, status: "ready", kind: "clip", quality: null, has_notes: false,
    });
    expect(clips[0]).toMatchObject({ status: "uploading", title: null, champion: null, duration_s: null, narrated: false });
  });

  it("lists full-game shares with kind, quality, has_notes and the revu.lol/v/ link", async () => {
    const now = nowSec();
    const { env, state } = webSetup({}, [
      clip({ id: "Vod0001", user_id: 1, created_at: now - 5, kind: "vod", quality: "1080p", r2_key: "vods/Vod0001.mp4", title: "Jinx vs Caitlyn" }),
      clip({ id: "Clip001", user_id: 1, created_at: now - 50 }),
    ]);
    state.vodShareMeta.set("Vod0001", { clip_id: "Vod0001", meta_json: "{}", has_notes: 1, created_at: now });
    const { token } = await seedWebUser(state, "lister@example.com");
    const clips = (await body(await call(env, webReq("/auth/web/clips", { cookie: token })))).clips as Array<Record<string, unknown>>;
    expect(clips[0]).toMatchObject({
      id: "Vod0001", kind: "vod", quality: "1080p", has_notes: true, url: "https://revu.lol/v/Vod0001", title: "Jinx vs Caitlyn",
    });
    expect(clips[1]).toMatchObject({ id: "Clip001", kind: "clip", quality: null, has_notes: false, url: "https://clips.revu.lol/Clip001" });
  });

  it("401s without a session", async () => {
    const { env } = webSetup();
    const res = await call(env, webReq("/auth/web/clips"));
    expect(res.status).toBe(401);
    expect(await body(res)).toEqual({ error: "signed_out" });
  });
});

describe("DELETE /auth/web/clips/:id", () => {
  const setup = async () => {
    const s = webSetup({}, [clip({ id: "Mine123", user_id: 1, r2_key: "clips/Mine123.mp4" }), clip({ id: "Their12", user_id: 2, r2_key: "clips/Their12.mp4" })]);
    s.r2.store.set("clips/Mine123.mp4", new Uint8Array([1]));
    s.r2.store.set("clips/Their12.mp4", new Uint8Array([2]));
    const mine = await seedWebUser(s.state, "owner@example.com");
    addUser(s.state, "stranger@example.com");
    return { ...s, token: mine.token };
  };

  it("deletes the caller's own clip", async () => {
    const { env, db, r2, token } = await setup();
    const res = await call(env, webReq("/auth/web/clips/Mine123", { method: "DELETE", cookie: token }));
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({ ok: true });
    expect(db._clips.has("Mine123")).toBe(false);
    expect(r2.store.has("clips/Mine123.mp4")).toBe(false);
  });

  it("stops sharing a full game: row, meta and object go", async () => {
    const s = webSetup({}, [clip({ id: "Vod0001", user_id: 1, kind: "vod", quality: "720p", r2_key: "vods/Vod0001.mp4" })]);
    s.r2.store.set("vods/Vod0001.mp4", new Uint8Array([1]));
    s.state.vodShareMeta.set("Vod0001", { clip_id: "Vod0001", meta_json: "{}", has_notes: 0, created_at: 1 });
    const { token } = await seedWebUser(s.state, "owner@example.com");
    const res = await call(s.env, webReq("/auth/web/clips/Vod0001", { method: "DELETE", cookie: token }));
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({ ok: true });
    expect(s.db._clips.has("Vod0001")).toBe(false);
    expect(s.state.vodShareMeta.has("Vod0001")).toBe(false);
    expect(s.r2.store.has("vods/Vod0001.mp4")).toBe(false);
  });

  it("404s another user's clip and leaves it intact", async () => {
    const { env, db, r2, token } = await setup();
    const res = await call(env, webReq("/auth/web/clips/Their12", { method: "DELETE", cookie: token }));
    expect(res.status).toBe(404);
    expect(await body(res)).toEqual({ error: "not_found" });
    expect(db._clips.has("Their12")).toBe(true);
    expect(r2.store.has("clips/Their12.mp4")).toBe(true);
  });

  it("404s a malformed or missing id", async () => {
    const { env, token } = await setup();
    for (const id of ["abc", "Mine1234", "Mine12_", "Zzzzzzz"]) {
      const res = await call(env, webReq(`/auth/web/clips/${id}`, { method: "DELETE", cookie: token }));
      expect(res.status, id).toBe(404);
      expect(await body(res)).toEqual({ error: "not_found" });
    }
  });

  it("requires the CSRF header and a session", async () => {
    const { env, db, token } = await setup();
    const noCsrf = await call(env, webReq("/auth/web/clips/Mine123", { method: "DELETE", cookie: token, csrf: false }));
    expect(noCsrf.status).toBe(403);
    const noSession = await call(env, webReq("/auth/web/clips/Mine123", { method: "DELETE" }));
    expect(noSession.status).toBe(401);
    expect(db._clips.has("Mine123")).toBe(true);
  });
});

describe("logout and logout-all", () => {
  it("logout removes this browser's session and clears the cookie", async () => {
    const { env, state } = webSetup();
    const { user, token } = await seedWebUser(state, "out@example.com");
    await seedDesktopSession(state, user.id, "desktop-out");
    const res = await call(env, webReq("/auth/web/logout", { method: "POST", cookie: token }));
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({ ok: true });
    expect(res.headers.getSetCookie()).toEqual([CLEAR_COOKIE]);
    expect(state.sessions.map((s) => s.kind)).toEqual(["desktop"]);
    expect((await call(env, webReq("/auth/web/me", { cookie: token }))).status).toBe(401);
  });

  it("logout works without a cookie", async () => {
    const { env } = webSetup();
    const res = await call(env, webReq("/auth/web/logout", { method: "POST" }));
    expect(res.status).toBe(200);
    expect(res.headers.getSetCookie()).toEqual([CLEAR_COOKIE]);
  });

  it("logout-all ends every web session of the user and keeps the desktop signed in", async () => {
    const { env, state } = webSetup({}, [clip({ id: "Desk123", user_id: 1 })]);
    const { user, token } = await seedWebUser(state, "all@example.com");
    const second = await seedWebUser(state, "", { user, seed: "second-browser" });
    await seedDesktopSession(state, user.id, "desktop-all");
    const other = await seedWebUser(state, "bystander@example.com");

    const res = await call(env, webReq("/auth/web/logout-all", { method: "POST", cookie: token }));
    expect(res.status).toBe(200);
    expect(res.headers.getSetCookie()).toEqual([CLEAR_COOKIE]);
    expect((await call(env, webReq("/auth/web/me", { cookie: second.token }))).status).toBe(401);
    expect((await call(env, webReq("/auth/web/me", { cookie: other.token }))).status).toBe(200);
    expect(state.sessions.filter((s) => s.user_id === user.id).map((s) => s.kind)).toEqual(["desktop"]);

    const desk = await call(env, desktopReq("/clips/mine", { bearer: "desktop-all" }));
    expect(desk.status).toBe(200);
    expect(((await body(desk)).clips as unknown[]).length).toBe(1);
  });

  it("logout-all 401s without a session", async () => {
    const { env } = webSetup();
    const res = await call(env, webReq("/auth/web/logout-all", { method: "POST" }));
    expect(res.status).toBe(401);
    expect(await body(res)).toEqual({ error: "signed_out" });
  });
});

describe("POST /auth/web/account/delete", () => {
  async function seedAccount(state: FakeAuthState, email: string, seed: string) {
    const { user, token } = await seedWebUser(state, email, { seed });
    await seedDesktopSession(state, user.id, `desktop-${seed}`);
    state.identities.push({
      id: state.nextIdentityId++, user_id: user.id, provider: "email", subject: email.toLowerCase(), email,
      display_name: null, created_at: 1, last_used_at: null,
    });
    state.transcribeUsage.set(`${user.id}|2026-10-10`, { audio_seconds: 30, requests: 1 });
    addLoginRequest(state, { code: `code-${seed}`, email, purpose: "web" });
    return { user, token };
  }

  async function setup() {
    const s = webSetup({}, [
      clip({ id: "Dead001", user_id: 1, r2_key: "clips/Dead001.mp4" }),
      clip({ id: "Dead002", user_id: 1, r2_key: "clips/Dead002.mp4", status: "uploading" }),
      clip({ id: "Live001", user_id: 2, r2_key: "clips/Live001.mp4" }),
    ]);
    const upload = await s.r2.bucket.createMultipartUpload("clips/Dead002.mp4");
    s.db._clips.get("Dead002")!.upload_id = upload.uploadId;
    for (const key of ["clips/Dead001.mp4", "clips/Live001.mp4"]) s.r2.store.set(key, new Uint8Array([1]));
    for (const id of ["Dead001", "Live001"]) {
      s.state.clipTranscripts.set(id, { clip_id: id, language: "en", segments_json: "[]", vtt: "", segment_count: 0, created_at: 1 });
    }
    const victim = await seedAccount(s.state, "Leaving@Example.com", "victim");
    const keeper = await seedAccount(s.state, "stay@example.com", "keeper");
    return { ...s, victim, keeper, uploadId: upload.uploadId };
  }

  it("refuses a mismatched confirmation and deletes nothing", async () => {
    const { env, state, db, victim } = await setup();
    const before = JSON.stringify([state.users, state.sessions, state.identities, state.loginRequests]);
    for (const email of ["stay@example.com", "", 5]) {
      const res = await call(env, webReq("/auth/web/account/delete", { method: "POST", cookie: victim.token, body: { email } }));
      expect(res.status).toBe(400);
      expect(await body(res)).toEqual({ error: "confirm_mismatch" });
    }
    expect(JSON.stringify([state.users, state.sessions, state.identities, state.loginRequests])).toBe(before);
    expect(db._clips.size).toBe(3);
  });

  it("deletes the account, every session kind, clips, objects and usage, and nothing else", async () => {
    const { env, state, db, r2, victim, keeper, uploadId } = await setup();
    const logs: string[] = [];
    vi.spyOn(console, "log").mockImplementation((line: unknown) => { logs.push(String(line)); });
    const res = await call(env, webReq("/auth/web/account/delete", {
      method: "POST", cookie: victim.token, body: { email: "  leaving@example.COM " },
    }));
    expect(res.status).toBe(200);
    expect(await body(res)).toEqual({ ok: true });
    expect(res.headers.getSetCookie()).toEqual([CLEAR_COOKIE]);

    expect(state.users.map((u) => u.email)).toEqual(["stay@example.com"]);
    expect(state.sessions.every((s) => s.user_id === keeper.user.id)).toBe(true);
    expect(state.sessions).toHaveLength(2);
    expect(state.identities.map((i) => i.user_id)).toEqual([keeper.user.id]);
    expect([...state.transcribeUsage.keys()]).toEqual([`${keeper.user.id}|2026-10-10`]);
    expect(state.loginRequests.map((r) => r.email)).toEqual(["stay@example.com"]);
    expect([...db._clips.keys()]).toEqual(["Live001"]);
    expect([...state.clipTranscripts.keys()]).toEqual(["Live001"]);

    expect(r2.calls.deleted.sort()).toEqual(["clips/Dead001.mp4", "clips/Dead002.mp4"]);
    expect(r2.store.has("clips/Live001.mp4")).toBe(true);
    expect(r2.uploads.get(uploadId)?.state).toBe("aborted");

    expect(logs.join("\n")).toContain('"scope":"web.account_delete"');
    expect(logs.join("\n")).not.toMatch(/leaving@example\.com/i);

    // The desktop app is signed out server-side; the other account is untouched.
    expect((await call(env, desktopReq("/clips/mine", { bearer: "desktop-victim" }))).status).toBe(401);
    expect((await call(env, desktopReq("/clips/mine", { bearer: "desktop-keeper" }))).status).toBe(200);
    expect((await call(env, webReq("/auth/web/me", { cookie: keeper.token }))).status).toBe(200);
  });

  it("still deletes the rows when R2 fails", async () => {
    const { env, state, r2, victim } = await setup();
    r2.bucket.delete = async () => { throw new Error("r2 down"); };
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(console, "log").mockImplementation(() => {});
    const res = await call(env, webReq("/auth/web/account/delete", { method: "POST", cookie: victim.token, body: { email: "leaving@example.com" } }));
    expect(res.status).toBe(200);
    expect(state.users.map((u) => u.email)).toEqual(["stay@example.com"]);
  });

  it("keeps a storage tombstone for every object R2 failed to delete, removes the VOD rows, and the purge retries", async () => {
    const { env, state, db, r2, victim, keeper, uploadId } = await setup();
    db._clips.set("Vod0001", { ...clip({ id: "Vod0001", user_id: victim.user.id, r2_key: "vods/Vod0001.mp4", size_bytes: 900 }), kind: "vod", quality: "1080p" });
    r2.store.set("vods/Vod0001.mp4", new Uint8Array([9]));
    state.vodShareMeta.set("Vod0001", { clip_id: "Vod0001", meta_json: "{}", has_notes: 1, created_at: 1 });
    state.vodShareUsage.set(`${victim.user.id}|2026-10-13`, 2);
    state.vodShareUsage.set(`${keeper.user.id}|2026-10-13`, 1);
    state.vodSharers.set(victim.user.id, 1);
    state.vodSharers.set(keeper.user.id, 1);
    const realDelete = r2.bucket.delete.bind(r2.bucket);
    r2.bucket.delete = async () => { throw new Error("r2 down"); };
    vi.spyOn(console, "error").mockImplementation(() => {});
    const logs: string[] = [];
    vi.spyOn(console, "log").mockImplementation((line: unknown) => { logs.push(String(line)); });

    const res = await call(env, webReq("/auth/web/account/delete", { method: "POST", cookie: victim.token, body: { email: "leaving@example.com" } }));
    expect(res.status).toBe(200);
    expect(state.users.map((u) => u.email)).toEqual(["stay@example.com"]);
    expect([...db._clips.keys()]).toEqual(["Live001"]);
    expect([...state.tombstones.values()].map((t) => [t.r2_key, t.size_bytes, t.kind, t.upload_id]).sort()).toEqual([
      ["clips/Dead001.mp4", 10, "clip", null],
      ["clips/Dead002.mp4", 10, "clip", uploadId],
      ["vods/Vod0001.mp4", 900, "vod", null],
    ]);
    expect(state.vodShareMeta.size).toBe(0);
    expect([...state.vodShareUsage.keys()]).toEqual([`${keeper.user.id}|2026-10-13`]);
    expect([...state.vodSharers.keys()]).toEqual([keeper.user.id]);
    expect(logs.join(" ")).toContain('"tombstones":3');

    r2.bucket.delete = realDelete;
    await runScheduled(worker, env);
    expect(state.tombstones.size).toBe(0);
    expect(r2.store.has("vods/Vod0001.mp4")).toBe(false);
    expect(r2.calls.deleted).toEqual(expect.arrayContaining(["clips/Dead001.mp4", "clips/Dead002.mp4", "vods/Vod0001.mp4"]));
  });

  it("401s without a session and 403s without the CSRF header", async () => {
    const { env, state, victim } = await setup();
    const noCookie = await call(env, webReq("/auth/web/account/delete", { method: "POST", body: { email: "leaving@example.com" } }));
    expect(noCookie.status).toBe(401);
    const noCsrf = await call(env, webReq("/auth/web/account/delete", {
      method: "POST", csrf: false, cookie: victim.token, body: { email: "leaving@example.com" },
    }));
    expect(noCsrf.status).toBe(403);
    expect(state.users).toHaveLength(2);
  });
});

describe("desktop auth regression", () => {
  const LOGIN_HTML = (code: string) => `
    <div style="font-family: -apple-system, Segoe UI, sans-serif; color: #222; max-width: 480px; margin: 0 auto;">
      <h2 style="color: #111;">Revu</h2>
      <p>Paste this code into the Revu desktop app to finish logging in:</p>
      <p style="font-family: ui-monospace, Consolas, monospace; font-size: 28px; font-weight: bold; letter-spacing: 4px; background: #f4f4f8; padding: 14px 18px; border-radius: 6px; text-align: center;">${code}</p>
      <p style="color: #666; font-size: 13px;">This code expires in 10 minutes and can only be used once. If you did not request it, you can safely ignore this email.</p>
    </div>
  `;

  it("POST /auth/login keeps its response and desktop email copy", async () => {
    const { env, state } = webSetup();
    const stub = stubFetch();
    const res = await call(env, desktopReq("/auth/login", { method: "POST", body: { email: "Desk@Example.com" } }));
    expect(res.status).toBe(200);
    expect(await res.text()).toBe('{"sent":true}');
    const mail = stub.resend[0];
    expect(mail.subject).toBe("Your Revu login code");
    const m = /^Paste this code into the Revu desktop app to finish logging in:\n\n([A-Z0-9]{8})\n\nThis code expires in 10 minutes\.$/.exec(String(mail.text));
    expect(m).not.toBeNull();
    expect(mail.html).toBe(LOGIN_HTML(m![1]));
    expect(state.loginRequests[0]).toMatchObject({ email: "desk@example.com", purpose: "login" });
  });

  it("POST /auth/verify returns exactly {session_token, expires_at} and records a desktop session", async () => {
    const { env, state } = webSetup();
    addLoginRequest(state, { code: await sha256Hex("DESKCODE"), email: "fresh@example.com", purpose: "login" });
    const res = await call(env, desktopReq("/auth/verify", { method: "POST", body: { code: "deskcode" } }));
    expect(res.status).toBe(200);
    const json = await body(res);
    expect(Object.keys(json).sort()).toEqual(["expires_at", "session_token"]);
    expect(json.session_token).toMatch(/^[0-9a-f]{48}$/);
    expect(res.headers.getSetCookie()).toEqual([]);
    expect(state.sessions).toHaveLength(1);
    expect(state.sessions[0]).toMatchObject({ kind: "desktop", token_hash: await sha256Hex(json.session_token as string), expires_at: json.expires_at });
    expect(state.identities).toEqual([
      expect.objectContaining({ user_id: state.users[0].id, provider: "email", subject: "fresh@example.com", email: "fresh@example.com" }),
    ]);
    const mine = await call(env, desktopReq("/clips/mine", { bearer: json.session_token as string }));
    expect(mine.status).toBe(200);
  });

  it("POST /auth/verify still succeeds when the identity upsert throws", async () => {
    const { env, state, db } = webSetup();
    addLoginRequest(state, { code: await sha256Hex("IDFAIL22"), email: "idfail@example.com", purpose: "login" });
    db._failNext("INSERT INTO identities");
    const errors: string[] = [];
    vi.spyOn(console, "error").mockImplementation((line: unknown) => { errors.push(String(line)); });
    const res = await call(env, desktopReq("/auth/verify", { method: "POST", body: { code: "IDFAIL22" } }));
    expect(res.status).toBe(200);
    expect(Object.keys(await body(res)).sort()).toEqual(["expires_at", "session_token"]);
    expect(state.identities).toHaveLength(0);
    expect(errors.some((e) => e.includes("auth.identity"))).toBe(true);
  });

  it("an identity upsert on a later sign-in only refreshes last_used_at", async () => {
    const { env, state } = webSetup();
    const user = addUser(state, "Again@Example.com");
    state.identities.push({
      id: state.nextIdentityId++, user_id: user.id, provider: "email", subject: "again@example.com", email: "Again@Example.com",
      display_name: null, created_at: 5, last_used_at: null,
    });
    addLoginRequest(state, { code: await sha256Hex("AGAIN222"), email: "again@example.com", purpose: "login" });
    expect((await call(env, desktopReq("/auth/verify", { method: "POST", body: { code: "AGAIN222" } }))).status).toBe(200);
    expect(state.identities).toHaveLength(1);
    expect(state.identities[0].created_at).toBe(5);
    expect(state.identities[0].last_used_at).toBeGreaterThan(0);
  });
});
