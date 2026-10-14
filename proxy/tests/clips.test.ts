import { beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { MAX_ACTIVE_CLIPS_PER_USER, parseRange } from "../src/clips";
import { CLIP_UPLOAD_PER_MINUTE_PER_USER, resetUserLimits } from "../src/user-limits";
import { resetClipViews } from "../src/clip-views";
import { clip, json, makeEnv, makeFakeDb, makeFakeR2, makeRequest, mp4Bytes, runScheduled, sha256Hex } from "./fakes";

// Shared hardened fakes (D1, R2 with known-length stream enforcement, helpers).
// makeRequest registers a Content-Length request body as a known-length stream,
// the way Workers hands one to a handler.
const env = makeEnv;

/** Minimal bytes that pass the webm (EBML header) magic-byte sniff. */
function webmBytes(extra = 0): Uint8Array {
  const head = [0x1a, 0x45, 0xdf, 0xa3, 0x9f, 0x42, 0x86, 0x81, 0x01, 0x42, 0xf7, 0x81, 0x01, 0x42, 0xf2, 0x81];
  return new Uint8Array([...head, ...new Array(extra).fill(0)]);
}

/**
 * Headers for an honest upload: the desktop client always declares
 * Content-Length (ClipUploadService.cs), and the handler requires it.
 */
function uploadHeaders(token: string, body: Uint8Array, contentType = "video/mp4"): Record<string, string> {
  return {
    Authorization: `Bearer ${token}`,
    "Content-Type": contentType,
    "Content-Length": String(body.byteLength),
  };
}

describe("clip sharing", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    resetUserLimits();
  });

  it("rejects upload without a bearer token", async () => {
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", { method: "POST", headers: { "Content-Type": "video/mp4" }, body: "x" }),
      env(),
    );
    expect(res.status).toBe(401);
  });

  it("rejects upload from an unknown bearer (there is no static operator token path)", async () => {
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: { Authorization: "Bearer static-op-token", "Content-Type": "video/mp4" },
        body: new Uint8Array([1, 2, 3]),
      }),
      env(),
    );
    expect(res.status).toBe(401);
    expect((await json(res)).error).toBe("invalid_token");
  });

  it("legacy POST /clips never spends the Riot budget and has its own per-user limit", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.now());
    resetUserLimits();
    const token = "sess-budget";
    const db = makeFakeDb([], { [await sha256Hex(token)]: 42 });
    const { bucket } = makeFakeR2();
    let doCalls = 0;
    const limiter = {
      idFromName: () => ({}),
      get: () => ({ fetch: async () => { doCalls++; return new Response(JSON.stringify({ allowed: false })); } }),
    } as unknown as DurableObjectNamespace;
    const e = env({ DB: db, CLIPS: bucket, RATE_LIMITER: limiter, PER_TOKEN_RPS: "1" });
    const upload = () => {
      const body = mp4Bytes();
      return worker.fetch(
        makeRequest("https://proxy.example/clips?title=x&champion=Lux&duration=3", {
          method: "POST",
          headers: uploadHeaders(token, body),
          body,
        }),
        e,
      );
    };
    // A saturated Riot budget (and a per-token Riot cap of 1) doesn't touch uploads.
    expect((await upload()).status).toBe(201);
    expect((await upload()).status).toBe(201);
    expect(doCalls).toBe(0);
    for (let i = 2; i < CLIP_UPLOAD_PER_MINUTE_PER_USER; i++) await upload();
    const limited = await upload();
    expect(limited.status).toBe(429);
    expect((await json(limited)).error).toBe("rate_limited");
  });

  it("rejects a non-video content type", async () => {
    const token = "sess-png";
    const db = makeFakeDb([], { [await sha256Hex(token)]: 7 });
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "image/png" },
        body: new Uint8Array([1]),
      }),
      env({ DB: db }),
    );
    expect(res.status).toBe(415);
  });

  it("uploads with a valid session and returns a public url", async () => {
    const sessionToken = "session-xyz";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 42 });
    const { bucket, store } = makeFakeR2();
    const body = mp4Bytes();

    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips?title=Great%20gank&champion=LeeSin&duration=12", {
        method: "POST",
        headers: uploadHeaders(sessionToken, body),
        body,
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

  it("streams request.body directly to R2 on the known-length path (no per-byte JS)", async () => {
    // The upload must NOT route the body through any JS TransformStream / custom
    // ReadableStream on the Content-Length path — doing so runs a JS callback per
    // chunk = O(filesize) CPU and trips the Workers CPU limit ("exceededCpu" → 503).
    // Verified live 2026-06-30: even a "streaming" per-chunk relay burned cpuMs=1088
    // on a 30 MB clip. The handler now hands `request.body` straight to R2, which
    // consumes it natively using the request's Content-Length. A clean 201 with the
    // full bytes stored proves the direct path works.
    const sessionToken = "session-fixedlen";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 77 });
    const { bucket, store } = makeFakeR2();
    const body = mp4Bytes(64);

    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips?title=streamed&champion=Sett&duration=8", {
        method: "POST",
        headers: {
          Authorization: `Bearer ${sessionToken}`,
          "Content-Type": "video/mp4",
          "Content-Length": String(body.byteLength),
        },
        body,
      }),
      env({ DB: db, CLIPS: bucket }),
    );

    expect(res.status).toBe(201);
    expect(store.size).toBe(1);
    // The whole file landed (streamed straight through to R2).
    const stored = [...store.values()][0];
    expect(stored.byteLength).toBe(body.byteLength);
  });

  it("known-length path sniffs the stored object with one ranged read, not a JS stream", async () => {
    // The body still goes straight to R2 (the fake R2 refuses any stream the
    // Worker wrapped in JS, the exceededCpu lesson). The magic-byte check runs
    // AFTER the put, on a 16-byte ranged get, and a non-video body is deleted
    // and 415'd so clips.revu.lol can't host arbitrary files.
    const sessionToken = "session-fixedlen-badmagic";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 88 });
    const { bucket, store, calls } = makeFakeR2();
    const body = new Uint8Array(16).fill(0x41); // "AAAA...": neither ftyp nor EBML

    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: uploadHeaders(sessionToken, body),
        body,
      }),
      env({ DB: db, CLIPS: bucket }),
    );

    expect(res.status).toBe(415);
    const out = await json(res);
    expect(out.error).toBe("unsupported_media_type");
    expect(out.message).toBe("file content does not match the declared video type");
    expect(calls.put).toBe(1); // streamed straight to R2 first...
    expect(store.size).toBe(0); // ...then deleted, nothing left in R2
    expect(db._clips.size).toBe(0); // and no row was written
  });

  it("known-length path accepts a genuine webm (EBML) body", async () => {
    const sessionToken = "session-fixedlen-webm";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 89 });
    const { bucket, store } = makeFakeR2();
    const body = webmBytes(32);

    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: uploadHeaders(sessionToken, body, "video/webm"),
        body,
      }),
      env({ DB: db, CLIPS: bucket }),
    );

    expect(res.status).toBe(201);
    expect(store.size).toBe(1);
    expect([...store.keys()][0]).toMatch(/\.webm$/);
  });

  it("rejects an upload without Content-Length (411) before reading the body", async () => {
    // Without a declared length R2 can't stream the body natively, and the old
    // fallback materialised up to the 100 MB cap in isolate memory (chunk array +
    // merged copy, about 2x the cap, above the ~128 MB isolate limit). The desktop
    // client always sets Content-Length, so the handler refuses to read an
    // undeclared body at all: no bytes pulled, nothing stored.
    const sessionToken = "session-nolength";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 1 });
    const { bucket, store, calls } = makeFakeR2();

    // highWaterMark 0: a default stream primes its queue by calling pull() at
    // construction, which would flip the flag with no consumer. At 0 pull() only
    // runs when someone actually reads.
    let bodyRead = false;
    const body = new ReadableStream<Uint8Array>(
      {
        pull(controller) {
          bodyRead = true;
          controller.enqueue(mp4Bytes(32));
          controller.close();
        },
      },
      { highWaterMark: 0 },
    );
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: { Authorization: `Bearer ${sessionToken}`, "Content-Type": "video/mp4" },
        body,
        // undici requires this for a stream body; the Workers runtime ignores it.
        duplex: "half",
      } as RequestInit),
      env({ DB: db, CLIPS: bucket }),
    );

    expect(res.status).toBe(411);
    const out = await json(res);
    expect(out.error).toBe("length_required");
    expect(out.message).toBe("Content-Length is required");
    expect(bodyRead).toBe(false);
    expect(calls.put).toBe(0);
    expect(store.size).toBe(0);
  });

  it("rejects a malformed Content-Length (411) instead of parsing a prefix", async () => {
    // parseInt("1e9") is 1, so a lenient parse would accept a bogus header and
    // trust a length the body does not have.
    const sessionToken = "session-badlength";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 1 });
    const { bucket, store } = makeFakeR2();

    for (const value of ["1e9", "16.0", "-16", "abc"]) {
      const res = await worker.fetch(
        makeRequest("https://proxy.example/clips", {
          method: "POST",
          headers: { Authorization: `Bearer ${sessionToken}`, "Content-Type": "video/mp4", "Content-Length": value },
          body: mp4Bytes(),
        }),
        env({ DB: db, CLIPS: bucket }),
      );
      expect(res.status).toBe(411);
      expect((await json(res)).error).toBe("length_required");
    }
    expect(store.size).toBe(0);
  });

  it("treats an explicit Content-Length: 0 as an empty body (400)", async () => {
    const sessionToken = "session-zerolength";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 1 });
    const { bucket, store } = makeFakeR2();

    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: { Authorization: `Bearer ${sessionToken}`, "Content-Type": "video/mp4", "Content-Length": "0" },
        body: new Uint8Array(0),
      }),
      env({ DB: db, CLIPS: bucket }),
    );

    expect(res.status).toBe(400);
    expect((await json(res)).message).toBe("empty body");
    expect(store.size).toBe(0);
  });

  it("converts an R2 failure into a clean 502 clip_error (not an escaped 503)", async () => {
    // If R2.put throws, the exception used to escape the worker entirely and
    // Cloudflare returned a raw 503 — surfaced to the desktop as the unhelpful
    // "UPLOAD FAILED (503)". The clip dispatch is now wrapped, so the throw
    // becomes a clean, retryable 502 {clip_error}.
    const sessionToken = "session-r2-down";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 7 });
    const throwingBucket = {
      async put() { throw new Error("R2 unavailable"); },
      async get() { return null; },
      async delete() { /* noop */ },
    } as unknown as R2Bucket;
    const body = mp4Bytes();

    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: uploadHeaders(sessionToken, body),
        body,
      }),
      env({ DB: db, CLIPS: throwingBucket }),
    );

    expect(res.status).toBe(502);
    const out = await json(res);
    expect(out.error).toBe("clip_error");
  });

  it("rejects an oversized upload via Content-Length", async () => {
    const sessionToken = "session-big";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 1 });
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
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
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: { Authorization: `Bearer ${sessionToken}`, "Content-Type": "image/gif" },
        body: new Uint8Array([1]),
      }),
      env({ DB: db }),
    );
    expect(res.status).toBe(415);
  });

  it("rejects an upload whose bytes are not actually video (magic-byte sniff)", async () => {
    const sessionToken = "session-fakevid";
    const tokenHash = await sha256Hex(sessionToken);
    const db = makeFakeDb([], { [tokenHash]: 1 });
    const { bucket, store } = makeFakeR2();
    const body = new TextEncoder().encode("<html><script>alert(1)</script></html>");
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: uploadHeaders(sessionToken, body),
        body,
      }),
      env({ DB: db, CLIPS: bucket }),
    );
    expect(res.status).toBe(415);
    expect((await json(res)).error).toBe("unsupported_media_type");
    expect(store.size).toBe(0);
  });

  it("rejects an upload once the per-user active-clip quota is reached", async () => {
    const sessionToken = "session-quota";
    const tokenHash = await sha256Hex(sessionToken);
    const seeded = Array.from({ length: MAX_ACTIVE_CLIPS_PER_USER }, (_, i) =>
      clip({ id: `seed${String(i).padStart(3, "0")}`, user_id: 9 }),
    );
    const db = makeFakeDb(seeded, { [tokenHash]: 9 });
    const body = mp4Bytes();
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: uploadHeaders(sessionToken, body),
        body,
      }),
      env({ DB: db }),
    );
    expect(res.status).toBe(403);
    expect((await json(res)).error).toBe("quota_exceeded");
  });

  it("post-insert recount rolls back a row that tipped the user over quota", async () => {
    // Simulate the concurrency race: the pre-insert COUNT under-reports (as if a
    // sibling upload hadn't committed yet), so the upload passes the first gate
    // and inserts — but the post-insert recount sees the user over the cap and
    // must delete the just-written row + R2 object and 403. We model the race
    // with a DB whose FIRST quota read returns "under" and whose SECOND returns
    // "over".
    const sessionToken = "session-recount";
    const tokenHash = await sha256Hex(sessionToken);
    const base = makeFakeDb([], { [tokenHash]: 7 });
    const r2 = makeFakeR2();

    let quotaReads = 0;
    const insertedIds: string[] = [];
    const deletedIds: string[] = [];
    const racingDb = {
      prepare(sql: string) {
        const inner = base.prepare(sql);
        let boundArgs: unknown[] = [];
        return {
          bind(...a: unknown[]) { boundArgs = a; inner.bind(...a); return this; },
          async first<T>(): Promise<T | null> {
            if (sql.includes("COALESCE(SUM(size_bytes)")) {
              quotaReads += 1;
              // 1st read (pre-insert): under cap. 2nd read (post-insert): over
              // the active-clip cap, forcing the rollback path.
              return (quotaReads === 1
                ? { n: 10, total: 0 }
                : { n: MAX_ACTIVE_CLIPS_PER_USER + 1, total: 0 }) as unknown as T;
            }
            return inner.first<T>();
          },
          async run() {
            if (sql.startsWith("INSERT INTO clips")) insertedIds.push(boundArgs[0] as string);
            if (sql.startsWith("DELETE FROM clips WHERE id")) deletedIds.push(boundArgs[0] as string);
            return inner.run();
          },
          all: inner.all,
        };
      },
    } as unknown as D1Database;
    const body = mp4Bytes(32);

    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips", {
        method: "POST",
        headers: uploadHeaders(sessionToken, body),
        body,
      }),
      env({ DB: racingDb, CLIPS: r2.bucket }),
    );

    expect(res.status).toBe(403);
    expect((await json(res)).error).toBe("quota_exceeded");
    // The row was inserted then rolled back, and the R2 object cleaned up.
    expect(insertedIds.length).toBe(1);
    expect(deletedIds).toContain(insertedIds[0]);
    expect(r2.store.size).toBe(0);
  });

  it("serves clip files with anti-sniffing headers", async () => {
    const c = clip({ id: "Hdr1234", r2_key: "clips/Hdr1234.mp4", size_bytes: 5 });
    const { bucket, store } = makeFakeR2();
    store.set("clips/Hdr1234.mp4", new Uint8Array([10, 20, 30, 40, 50]));
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clip-file/Hdr1234"),
      env({ DB: makeFakeDb([c]), CLIPS: bucket }),
    );
    expect(res.status).toBe(200);
    expect(res.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(res.headers.get("Content-Security-Policy")).toBe("sandbox");
    expect(res.headers.get("Content-Disposition")).toContain("inline");
  });

  it("serves clip metadata publicly", async () => {
    const c = clip({ id: "Meta123" });
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clip-meta/Meta123"),
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
      makeRequest("https://proxy.example/clip-meta/Expir12"),
      env({ DB: makeFakeDb([c]) }),
    );
    expect(res.status).toBe(404);
  });

  it("streams a clip file and supports Range with 206", async () => {
    const c = clip({ id: "File123", size_bytes: 5, r2_key: "clips/File123.mp4" });
    const { bucket, store } = makeFakeR2();
    store.set("clips/File123.mp4", new Uint8Array([10, 20, 30, 40, 50]));

    const full = await worker.fetch(
      makeRequest("https://proxy.example/clip-file/File123"),
      env({ DB: makeFakeDb([c]), CLIPS: bucket }),
    );
    expect(full.status).toBe(200);
    expect(full.headers.get("Accept-Ranges")).toBe("bytes");
    expect(full.headers.get("Content-Length")).toBe("5");

    const ranged = await worker.fetch(
      makeRequest("https://proxy.example/clip-file/File123", { headers: { Range: "bytes=1-3" } }),
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
      makeRequest("https://proxy.example/discord", { headers: { Authorization: "Bearer static-op-token" } }),
      env(),
    );
    const loc = res.headers.get("Location") ?? "";
    expect(loc).not.toContain("clip.html");
    expect(res.status).not.toBe(302);
  });

  it("redirects a bare clip-shaped slug to the watch page when the clip is unknown", async () => {
    const res = await worker.fetch(makeRequest("https://proxy.example/Xy12Z9q"), env());
    expect(res.status).toBe(302);
    expect(res.headers.get("Location")).toBe("https://revu.lol/clip.html?id=Xy12Z9q");
  });

  it("serves per-clip Open Graph video tags for an existing slug (Discord embed)", async () => {
    const db = makeFakeDb([clip({ id: "Embed99", title: "baron steal", champion: "LeeSin", content_type: "video/mp4" })]);
    const res = await worker.fetch(makeRequest("https://proxy.example/Embed99"), env({ DB: db }));
    expect(res.status).toBe(200);
    expect(res.headers.get("Content-Type")).toContain("text/html");
    const body = await res.text();
    // The crucial bits Discord reads to render an inline player:
    expect(body).toContain('property="og:type" content="video.other"');
    expect(body).toContain('property="og:video" content="https://clips.revu.lol/clip-file/Embed99"');
    expect(body).toContain('property="og:video:type" content="video/mp4"');
    // Per-clip title (caption + champion), HTML-escaped, not a static placeholder.
    expect(body).toContain("baron steal · LeeSin");
    // User-facing copy never carries an em or en dash.
    expect(body).not.toMatch(/[–—]/);
    // Humans still get bounced to the watch page.
    expect(body).toContain("https://revu.lol/clip.html?id=Embed99");
  });

  it("uses video/webm in og:video:type for a webm clip", async () => {
    const db = makeFakeDb([clip({ id: "Webm123", r2_key: "clips/Webm123.webm", content_type: "video/webm" })]);
    const res = await worker.fetch(makeRequest("https://proxy.example/Webm123"), env({ DB: db }));
    const body = await res.text();
    expect(body).toContain('property="og:video:type" content="video/webm"');
  });

  it("escapes HTML in a clip title to prevent injection in OG tags", async () => {
    const db = makeFakeDb([clip({ id: "Xss1234", title: '"><script>alert(1)</script>', champion: null })]);
    const res = await worker.fetch(makeRequest("https://proxy.example/Xss1234"), env({ DB: db }));
    const body = await res.text();
    expect(body).not.toContain("<script>alert(1)</script>");
    expect(body).toContain("&lt;script&gt;");
  });

  it("does not redirect multi-segment paths", async () => {
    const res = await worker.fetch(makeRequest("https://proxy.example/Xy12Z9q/extra"), env());
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
      makeRequest("https://proxy.example/clips/Own1234", { method: "DELETE", headers: { Authorization: `Bearer ${other}` } }),
      env({ DB: dbA }),
    );
    expect(denied.status).toBe(403);

    // owner → 200
    const dbB = makeFakeDb([clip({ id: "Own1234", user_id: 100 })], { [ownerHash]: 100 });
    const ok = await worker.fetch(
      makeRequest("https://proxy.example/clips/Own1234", { method: "DELETE", headers: { Authorization: `Bearer ${owner}` } }),
      env({ DB: dbB }),
    );
    expect(ok.status).toBe(200);
  });

  it("an owner delete whose R2 delete fails keeps an expired purging row; the hourly purge finishes it", async () => {
    const token = "sess-del-r2";
    const hash = await sha256Hex(token);
    const db = makeFakeDb([clip({ id: "Del1234", user_id: 4, r2_key: "clips/Del1234.mp4", size_bytes: 77 })], { [hash]: 4 });
    db._transcripts.set("Del1234", { clip_id: "Del1234", language: "en", segments_json: "[]", vtt: "", segment_count: 0, created_at: 1 });
    const r2 = makeFakeR2();
    r2.store.set("clips/Del1234.mp4", new Uint8Array([1]));
    const realDelete = r2.bucket.delete.bind(r2.bucket);
    r2.bucket.delete = (async () => { throw new Error("R2 down"); }) as typeof r2.bucket.delete;
    const errors: string[] = [];
    vi.spyOn(console, "error").mockImplementation((line: unknown) => { errors.push(String(line)); });
    const e = env({ DB: db, CLIPS: r2.bucket });

    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips/Del1234", { method: "DELETE", headers: { Authorization: `Bearer ${token}` } }), e,
    );
    expect(res.status).toBe(200);
    expect(await json(res)).toEqual({ ok: true });
    expect(errors.join(" ")).toContain('"scope":"clips.delete.r2","id":"Del1234"');
    const row = db._clips.get("Del1234")!;
    expect(row.status).toBe("purging");
    expect(row.expires_at).toBeLessThanOrEqual(Math.floor(Date.now() / 1000));
    expect((await worker.fetch(makeRequest("https://proxy.example/clip-meta/Del1234"), e)).status).toBe(404);

    r2.bucket.delete = realDelete;
    vi.spyOn(console, "log").mockImplementation(() => {});
    await runScheduled(worker, e);
    expect(db._clips.has("Del1234")).toBe(false);
    expect(db._transcripts.has("Del1234")).toBe(false);
    expect(r2.store.has("clips/Del1234.mp4")).toBe(false);
  });

  it("lists VOD shares beside clips with kind, quality, has_notes and the watch-page url", async () => {
    const token = "sess-list-vod";
    const hash = await sha256Hex(token);
    const db = makeFakeDb([
      clip({ id: "Clip001", user_id: 5, created_at: 10 }),
      clip({ id: "Vod0001", user_id: 5, created_at: 20, kind: "vod", quality: "720p", r2_key: "vods/Vod0001.mp4", title: "Jinx game" }),
      clip({ id: "Vod0002", user_id: 5, created_at: 30, kind: "vod", quality: "1080p", r2_key: "vods/Vod0002.mp4", status: "uploading" }),
    ], { [hash]: 5 });
    db._vodMeta.set("Vod0001", { clip_id: "Vod0001", meta_json: "{}", has_notes: 1, created_at: 1 });
    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips/mine", { headers: { Authorization: `Bearer ${token}` } }), env({ DB: db }),
    );
    const out = (await json(res)) as { clips: Array<Record<string, unknown>> };
    expect(out.clips.map((c) => [c.id, c.kind, c.quality, c.has_notes, c.url, c.status])).toEqual([
      ["Vod0002", "vod", "1080p", false, "https://revu.lol/v/Vod0002", "uploading"],
      ["Vod0001", "vod", "720p", true, "https://revu.lol/v/Vod0001", "ready"],
      ["Clip001", "clip", null, false, "https://clips.revu.lol/Clip001", "ready"],
    ]);
  });

  it("lists the caller's own clips", async () => {
    const token = "sess-list";
    const hash = await sha256Hex(token);
    const mine = [clip({ id: "Mine001", user_id: 5 }), clip({ id: "Mine002", user_id: 5 })];
    const notMine = clip({ id: "Other01", user_id: 9 });
    const db = makeFakeDb([...mine, notMine], { [hash]: 5 });

    const res = await worker.fetch(
      makeRequest("https://proxy.example/clips/mine", { headers: { Authorization: `Bearer ${token}` } }),
      env({ DB: db }),
    );
    expect(res.status).toBe(200);
    const out = (await json(res)) as { clips: Array<{ id: string }> };
    expect(out.clips.map((c) => c.id).sort()).toEqual(["Mine001", "Mine002"]);
  });
});

describe("clip-file range and conditional requests", () => {
  const c = clip({ id: "Rng1234", size_bytes: 5, r2_key: "clips/Rng1234.mp4" });

  function setup() {
    const r2 = makeFakeR2();
    r2.store.set("clips/Rng1234.mp4", new Uint8Array([10, 20, 30, 40, 50]));
    const gets: unknown[] = [];
    const heads: string[] = [];
    const bucket = {
      ...r2.bucket,
      get: async (key: string, opts?: unknown) => { gets.push(opts ?? null); return (r2.bucket as unknown as { get: (k: string, o?: unknown) => Promise<unknown> }).get(key, opts); },
      head: async (key: string) => { heads.push(key); return (r2.bucket as unknown as { head: (k: string) => Promise<unknown> }).head(key); },
    } as unknown as R2Bucket;
    const e = env({ DB: makeFakeDb([c]), CLIPS: bucket });
    const file = (init: RequestInit = {}) => worker.fetch(makeRequest("https://proxy.example/clip-file/Rng1234", init), e);
    return { file, gets, heads };
  }

  it("parses ranges: satisfiable, suffix, open-ended, unsatisfiable and ignorable", () => {
    expect(parseRange("bytes=1-3", 5)).toEqual({ offset: 1, length: 3 });
    expect(parseRange("bytes=-2", 5)).toEqual({ offset: 3, length: 2 });
    expect(parseRange("bytes=2-", 5)).toEqual({ offset: 2, length: 3 });
    expect(parseRange("bytes=3-99", 5)).toEqual({ offset: 3, length: 2 });
    expect(parseRange("bytes=5-", 5)).toBe("unsatisfiable");
    expect(parseRange("bytes=9-12", 5)).toBe("unsatisfiable");
    expect(parseRange("bytes=-0", 5)).toBe("unsatisfiable");
    expect(parseRange("bytes=3-1", 5)).toBeNull();
    expect(parseRange("bytes=0-1,3-4", 5)).toBeNull();
    expect(parseRange("items=0-1", 5)).toBeNull();
    expect(parseRange(null, 5)).toBeNull();
  });

  it("answers an unsatisfiable range with 416 and never reads the object", async () => {
    const s = setup();
    const res = await s.file({ headers: { Range: "bytes=5-" } });
    expect(res.status).toBe(416);
    expect(res.headers.get("Content-Range")).toBe("bytes */5");
    expect(s.gets).toHaveLength(0);
  });

  it("HEAD reads only metadata, never the body", async () => {
    const s = setup();
    const res = await s.file({ method: "HEAD" });
    expect(res.status).toBe(200);
    expect(res.headers.get("Content-Length")).toBe("5");
    expect(res.headers.get("ETag")).toBe('"etag-clips/Rng1234.mp4"');
    expect(s.gets).toHaveLength(0);
    expect(s.heads).toEqual(["clips/Rng1234.mp4"]);
    const ranged = await s.file({ method: "HEAD", headers: { Range: "bytes=0-1" } });
    expect(ranged.status).toBe(206);
    expect(ranged.headers.get("Content-Range")).toBe("bytes 0-1/5");
  });

  it("answers a matching If-None-Match with 304 and no body read", async () => {
    const s = setup();
    const res = await s.file({ headers: { "If-None-Match": 'W/"etag-clips/Rng1234.mp4"' } });
    expect(res.status).toBe(304);
    expect(res.headers.get("ETag")).toBe('"etag-clips/Rng1234.mp4"');
    expect(s.gets).toHaveLength(0);
    const stale = await s.file({ headers: { "If-None-Match": '"old"' } });
    expect(stale.status).toBe(200);
  });

  it("If-Range: a current ETag keeps the range, a stale one gets the whole file", async () => {
    const s = setup();
    const current = await s.file({ headers: { Range: "bytes=1-2", "If-Range": '"etag-clips/Rng1234.mp4"' } });
    expect(current.status).toBe(206);
    expect(new Uint8Array(await current.arrayBuffer())).toEqual(new Uint8Array([20, 30]));
    const stale = await s.file({ headers: { Range: "bytes=1-2", "If-Range": '"old"' } });
    expect(stale.status).toBe(200);
    expect(stale.headers.get("Content-Length")).toBe("5");
  });
});

describe("clip view counter", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    resetClipViews();
  });

  const view = (e: ReturnType<typeof env>, ip: string, id = "View123") =>
    worker.fetch(makeRequest(`https://proxy.example/clip-view/${id}`, { method: "POST", headers: { "CF-Connecting-IP": ip } }), e);

  it("counts one view per viewer per clip per hour", async () => {
    const nowSpy = vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-12T10:00:00Z"));
    const db = makeFakeDb([clip({ id: "View123", expires_at: Math.floor(Date.parse("2026-10-14T00:00:00Z") / 1000) })]);
    const e = env({ DB: db });
    for (let i = 0; i < 5; i++) expect((await view(e, "198.51.100.1")).status).toBe(200);
    expect(db._clips.get("View123")!.view_count).toBe(1);
    await view(e, "198.51.100.2");
    expect(db._clips.get("View123")!.view_count).toBe(2);
    nowSpy.mockReturnValue(Date.parse("2026-10-12T11:00:01Z"));
    await view(e, "198.51.100.1");
    expect(db._clips.get("View123")!.view_count).toBe(3);
  });

  it("asks the DO when it is bound, and does not count when it can't confirm", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const db = makeFakeDb([clip({ id: "View123" })]);
    const asked: string[] = [];
    let answer: () => Promise<Response> = async () => new Response(JSON.stringify({ allowed: false }));
    const limiter = {
      idFromName: () => ({}),
      get: () => ({ fetch: async (u: string) => { asked.push(u); return answer(); } }),
    } as unknown as DurableObjectNamespace;
    const e = env({ DB: db, RATE_LIMITER: limiter });
    await view(e, "198.51.100.3");
    expect(db._clips.get("View123")!.view_count).toBe(0); // another isolate already counted it
    answer = async () => { throw new Error("DO down"); };
    await view(e, "198.51.100.4");
    expect(db._clips.get("View123")!.view_count).toBe(0); // fail closed
    answer = async () => new Response(JSON.stringify({ allowed: false, reason: "full" }));
    await view(e, "198.51.100.7");
    expect(db._clips.get("View123")!.view_count).toBe(0); // a full namespace is an outage: still closed
    answer = async () => new Response(JSON.stringify({ allowed: true }));
    await view(e, "198.51.100.5");
    expect(db._clips.get("View123")!.view_count).toBe(1);
    const u = new URL(asked[0]);
    expect(u.pathname).toBe("/key");
    expect(u.searchParams.get("ns")).toBe("view");
    expect(u.searchParams.get("limit")).toBe("1");
    expect(u.searchParams.get("key")).not.toContain("198.51.100");
  });

  it("is always 200 and never counts a missing clip", async () => {
    const db = makeFakeDb([]);
    const res = await view(env({ DB: db }), "198.51.100.6", "Nope123");
    expect(res.status).toBe(200);
    expect(await json(res)).toEqual({ ok: true });
  });
});
