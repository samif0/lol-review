import { beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { Env } from "../src/types";

// ── In-memory fakes for D1 + R2 ─────────────────────────────────────────────
//
// The clip handlers actually read/write D1 and R2 (unlike the Riot passthrough
// tests, which stub global fetch), so we need working fakes. These implement
// just the surface the handlers touch.

interface FakeClip {
  id: string;
  user_id: number;
  r2_key: string;
  content_type: string;
  size_bytes: number;
  duration_s: number | null;
  title: string | null;
  champion: string | null;
  created_at: number;
  expires_at: number;
  view_count: number;
  status: string;
}

function makeFakeDb(seedClips: FakeClip[] = [], sessions: Record<string, number> = {}) {
  const clips = new Map<string, FakeClip>();
  for (const c of seedClips) clips.set(c.id, c);

  function prepare(sql: string) {
    let args: unknown[] = [];
    const api = {
      bind(...a: unknown[]) {
        args = a;
        return api;
      },
      async first<T>(): Promise<T | null> {
        // session lookup (auth Path B)
        if (sql.includes("FROM sessions")) {
          const tokenHash = args[0] as string;
          const userId = sessions[tokenHash];
          if (userId === undefined) return null;
          return { token_hash: tokenHash, user_id: userId, created_at: 0, expires_at: 9e9 } as T;
        }
        // unique-slug existence probe
        if (sql.startsWith("SELECT id FROM clips WHERE id")) {
          const id = args[0] as string;
          return (clips.has(id) ? ({ id } as T) : null);
        }
        // clip lookup with expiry filter
        if (sql.includes("FROM clips WHERE id = ?1 AND expires_at >")) {
          const id = args[0] as string;
          const now = args[1] as number;
          const c = clips.get(id);
          if (!c || c.expires_at <= now) return null;
          return c as T;
        }
        // clip lookup without expiry (delete path)
        if (sql.startsWith("SELECT * FROM clips WHERE id = ?1 LIMIT")) {
          const id = args[0] as string;
          return (clips.get(id) as T) ?? null;
        }
        return null;
      },
      async run() {
        if (sql.startsWith("INSERT INTO clips")) {
          const [
            id, user_id, r2_key, content_type, size_bytes, duration_s,
            title, champion, created_at, expires_at, view_count, status,
          ] = args as [string, number, string, string, number, number | null, string | null, string | null, number, number, number, string];
          clips.set(id, { id, user_id, r2_key, content_type, size_bytes, duration_s, title, champion, created_at, expires_at, view_count, status });
          return { meta: { changes: 1 } };
        }
        if (sql.startsWith("UPDATE clips SET view_count")) {
          const id = args[0] as string;
          const c = clips.get(id);
          if (c) c.view_count += 1;
          return { meta: { changes: c ? 1 : 0 } };
        }
        if (sql.startsWith("DELETE FROM clips WHERE id")) {
          const id = args[0] as string;
          const had = clips.delete(id);
          return { meta: { changes: had ? 1 : 0 } };
        }
        if (sql.startsWith("DELETE FROM clips WHERE expires_at")) {
          const now = args[0] as number;
          let n = 0;
          for (const [id, c] of clips) if (c.expires_at <= now) { clips.delete(id); n++; }
          return { meta: { changes: n } };
        }
        return { meta: { changes: 0 } };
      },
      async all<T>(): Promise<{ results: T[] }> {
        if (sql.includes("FROM clips WHERE user_id")) {
          const userId = args[0] as number;
          const now = args[1] as number;
          const results = [...clips.values()].filter((c) => c.user_id === userId && c.expires_at > now);
          return { results: results as T[] };
        }
        if (sql.includes("FROM clips WHERE expires_at")) {
          const now = args[0] as number;
          const results = [...clips.values()].filter((c) => c.expires_at <= now).map((c) => ({ id: c.id, r2_key: c.r2_key }));
          return { results: results as T[] };
        }
        return { results: [] };
      },
    };
    return api;
  }

  return { prepare, _clips: clips } as unknown as D1Database & { _clips: Map<string, FakeClip> };
}

function makeFakeR2() {
  const store = new Map<string, Uint8Array>();
  const bucket = {
    async put(key: string, value: ArrayBuffer | ArrayBufferView) {
      const bytes = value instanceof ArrayBuffer ? new Uint8Array(value) : new Uint8Array((value as ArrayBufferView).buffer);
      store.set(key, bytes);
      return {};
    },
    async get(key: string, opts?: { range?: { offset: number; length: number } }) {
      const bytes = store.get(key);
      if (!bytes) return null;
      const slice = opts?.range ? bytes.slice(opts.range.offset, opts.range.offset + opts.range.length) : bytes;
      return {
        body: new Blob([slice]).stream(),
        httpEtag: `"etag-${key}"`,
        size: bytes.length,
      };
    },
    async delete(key: string) {
      store.delete(key);
    },
  };
  return { bucket: bucket as unknown as R2Bucket, store };
}

function env(overrides: Partial<Env> = {}): Env {
  return {
    RIOT_API_KEY: "riot-key",
    ALLOWED_TOKENS: "static-op-token",
    RESEND_API_KEY: "",
    AGGREGATE_RPS: "100",
    PER_TOKEN_RPS: "20",
    MAGIC_LINK_FROM: "noreply@example.com",
    APP_NAME: "Revu",
    PUBLIC_BASE: "https://clips.revu.lol",
    WATCH_BASE: "https://revu.lol",
    DB: makeFakeDb(),
    CLIPS: makeFakeR2().bucket,
    ...overrides,
  };
}

async function json(response: Response): Promise<Record<string, unknown>> {
  return (await response.json()) as Record<string, unknown>;
}

function clip(over: Partial<FakeClip> = {}): FakeClip {
  const now = Math.floor(Date.now() / 1000);
  return {
    id: "abc1234",
    user_id: 1,
    r2_key: "clips/abc1234.mp4",
    content_type: "video/mp4",
    size_bytes: 10,
    duration_s: 5,
    title: "nice play",
    champion: "Ahri",
    created_at: now,
    expires_at: now + 1000,
    view_count: 0,
    status: "ready",
    ...over,
  };
}

describe("clip sharing", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("rejects upload without a bearer token", async () => {
    const res = await worker.fetch(
      new Request("https://proxy.example/clips", { method: "POST", headers: { "Content-Type": "video/mp4" }, body: "x" }),
      env(),
    );
    expect(res.status).toBe(401);
  });

  it("rejects upload from a static operator token (no account)", async () => {
    // Path A static token authenticates but has no user id → can't own clips.
    const res = await worker.fetch(
      new Request("https://proxy.example/clips", {
        method: "POST",
        headers: { Authorization: "Bearer static-op-token", "Content-Type": "video/mp4" },
        body: new Uint8Array([1, 2, 3]),
      }),
      env(),
    );
    expect(res.status).toBe(403);
    expect((await json(res)).error).toBe("login_required");
  });

  it("rejects a non-video content type", async () => {
    const db = makeFakeDb([], { ["hash-sess"]: 7 });
    // sha256("sess-token") must map in the fake; we instead seed by token hash.
    // Easier: use a session token whose hash we precompute below.
    const res = await worker.fetch(
      new Request("https://proxy.example/clips", {
        method: "POST",
        headers: { Authorization: "Bearer static-op-token", "Content-Type": "image/png" },
        body: new Uint8Array([1]),
      }),
      env({ DB: db }),
    );
    // static token path returns 403 before type check; assert type check via session test below.
    expect([403, 415]).toContain(res.status);
  });

  it("uploads with a valid session and returns a public url", async () => {
    const sessionToken = "session-xyz";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 42 });
    const { bucket, store } = makeFakeR2();

    const res = await worker.fetch(
      new Request("https://proxy.example/clips?title=Great%20gank&champion=LeeSin&duration=12", {
        method: "POST",
        headers: { Authorization: `Bearer ${sessionToken}`, "Content-Type": "video/mp4" },
        body: new Uint8Array([1, 2, 3, 4, 5]),
      }),
      env({ DB: db, CLIPS: bucket }),
    );

    expect(res.status).toBe(201);
    const out = await json(res);
    expect(typeof out.id).toBe("string");
    expect((out.id as string).length).toBe(7);
    expect(out.url).toBe(`https://clips.revu.lol/${out.id}`);
    expect(typeof out.expires_at).toBe("number");
    // bytes actually landed in R2
    expect(store.size).toBe(1);
  });

  it("rejects an oversized upload via Content-Length", async () => {
    const sessionToken = "session-big";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 1 });
    const res = await worker.fetch(
      new Request("https://proxy.example/clips", {
        method: "POST",
        headers: {
          Authorization: `Bearer ${sessionToken}`,
          "Content-Type": "video/mp4",
          "Content-Length": String(201 * 1024 * 1024),
        },
        body: new Uint8Array([1]),
      }),
      env({ DB: db }),
    );
    expect(res.status).toBe(413);
  });

  it("rejects a non-video type with a valid session", async () => {
    const sessionToken = "session-img";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 1 });
    const res = await worker.fetch(
      new Request("https://proxy.example/clips", {
        method: "POST",
        headers: { Authorization: `Bearer ${sessionToken}`, "Content-Type": "image/gif" },
        body: new Uint8Array([1]),
      }),
      env({ DB: db }),
    );
    expect(res.status).toBe(415);
  });

  it("serves clip metadata publicly", async () => {
    const c = clip({ id: "Meta123" });
    const res = await worker.fetch(
      new Request("https://proxy.example/clip-meta/Meta123"),
      env({ DB: makeFakeDb([c]) }),
    );
    expect(res.status).toBe(200);
    const out = await json(res);
    expect(out.id).toBe("Meta123");
    expect(out.title).toBe("nice play");
    expect(out.champion).toBe("Ahri");
    // must NOT leak owner identity
    expect(out.user_id).toBeUndefined();
  });

  it("404s metadata for an expired clip", async () => {
    const now = Math.floor(Date.now() / 1000);
    const c = clip({ id: "Expir12", expires_at: now - 10 });
    const res = await worker.fetch(
      new Request("https://proxy.example/clip-meta/Expir12"),
      env({ DB: makeFakeDb([c]) }),
    );
    expect(res.status).toBe(404);
  });

  it("streams a clip file and supports Range with 206", async () => {
    const c = clip({ id: "File123", size_bytes: 5, r2_key: "clips/File123.mp4" });
    const { bucket, store } = makeFakeR2();
    store.set("clips/File123.mp4", new Uint8Array([10, 20, 30, 40, 50]));

    const full = await worker.fetch(
      new Request("https://proxy.example/clip-file/File123"),
      env({ DB: makeFakeDb([c]), CLIPS: bucket }),
    );
    expect(full.status).toBe(200);
    expect(full.headers.get("Accept-Ranges")).toBe("bytes");
    expect(full.headers.get("Content-Length")).toBe("5");

    const ranged = await worker.fetch(
      new Request("https://proxy.example/clip-file/File123", { headers: { Range: "bytes=1-3" } }),
      env({ DB: makeFakeDb([c]), CLIPS: bucket }),
    );
    expect(ranged.status).toBe(206);
    expect(ranged.headers.get("Content-Range")).toBe("bytes 1-3/5");
    expect(ranged.headers.get("Content-Length")).toBe("3");
  });

  it("treats a reserved slug like /discord as NOT a clip (no redirect)", async () => {
    // 'discord' is 7 chars and base62-shaped, so without the reserved guard it
    // would wrongly redirect to the watch page. With auth present it falls
    // through to normal (non-clip) handling. Key assertion: never a 302 to
    // clip.html.
    const res = await worker.fetch(
      new Request("https://proxy.example/discord", { headers: { Authorization: "Bearer static-op-token" } }),
      env(),
    );
    const loc = res.headers.get("Location") ?? "";
    expect(loc).not.toContain("clip.html");
    expect(res.status).not.toBe(302);
  });

  it("redirects a bare clip-shaped slug to the watch page", async () => {
    const res = await worker.fetch(new Request("https://proxy.example/Xy12Z9q"), env());
    expect(res.status).toBe(302);
    expect(res.headers.get("Location")).toBe("https://revu.lol/clip.html?id=Xy12Z9q");
  });

  it("does not redirect multi-segment paths", async () => {
    const res = await worker.fetch(new Request("https://proxy.example/Xy12Z9q/extra"), env());
    expect(res.status).not.toBe(302);
  });

  it("deletes only when the caller owns the clip", async () => {
    const owner = "sess-owner";
    const other = "sess-other";
    const ownerHash = await sha256Hex(owner);
    const otherHash = await sha256Hex(other);
    const c = clip({ id: "Own1234", user_id: 100, r2_key: "clips/Own1234.mp4" });

    // non-owner → 403
    const dbA = makeFakeDb([c], { [ownerHash]: 100, [otherHash]: 200 });
    const denied = await worker.fetch(
      new Request("https://proxy.example/clips/Own1234", { method: "DELETE", headers: { Authorization: `Bearer ${other}` } }),
      env({ DB: dbA }),
    );
    expect(denied.status).toBe(403);

    // owner → 200
    const dbB = makeFakeDb([clip({ id: "Own1234", user_id: 100 })], { [ownerHash]: 100 });
    const ok = await worker.fetch(
      new Request("https://proxy.example/clips/Own1234", { method: "DELETE", headers: { Authorization: `Bearer ${owner}` } }),
      env({ DB: dbB }),
    );
    expect(ok.status).toBe(200);
  });

  it("lists the caller's own clips", async () => {
    const token = "sess-list";
    const hash = await sha256Hex(token);
    const mine = [clip({ id: "Mine001", user_id: 5 }), clip({ id: "Mine002", user_id: 5 })];
    const notMine = clip({ id: "Other01", user_id: 9 });
    const db = makeFakeDb([...mine, notMine], { [hash]: 5 });

    const res = await worker.fetch(
      new Request("https://proxy.example/clips/mine", { headers: { Authorization: `Bearer ${token}` } }),
      env({ DB: db }),
    );
    expect(res.status).toBe(200);
    const out = (await json(res)) as { clips: Array<{ id: string }> };
    expect(out.clips.map((c) => c.id).sort()).toEqual(["Mine001", "Mine002"]);
  });
});

// sha256 helper mirroring src/crypto.ts so tests can seed session hashes.
async function sha256Hex(input: string): Promise<string> {
  const data = new TextEncoder().encode(input);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}
