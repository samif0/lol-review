import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import worker from "../src/index";
import { Env } from "../src/types";
import { LOL_PROMPT, TRANSCRIPT_USER_MAX_BYTES, WHISPER_MODEL } from "../src/transcripts";
import { resetUserLimits } from "../src/user-limits";
import { FakeDb, clip, dbWithSessions, json, makeEnv, makeFakeAi, makeFakeR2, makeRequest, nowSec, runScheduled } from "./fakes";

const TOKEN = "sess-narrator";
const OTHER = "sess-stranger";
const USER = 31;
const BASE = "https://clips.revu.lol";
const AUDIO = "SUQzBAAAAAAAI1RTU0UAAAAPAAADTGF2ZjYwLjMuMTAwAAAAAAAAAAAAAAD/+0DAAAAAAAAAAAAAAAAAAAAAAABJbmZvAAAADwAAAAMAAAGw";

type AiReply = Record<string, unknown>;

async function setup(reply: AiReply | (() => never) = { text: "", segments: [] }, seed: Parameters<typeof dbWithSessions>[1] = []) {
  const db = await dbWithSessions({ [TOKEN]: USER, [OTHER]: 32 }, seed);
  const r2 = makeFakeR2();
  const fake = makeFakeAi(() => (typeof reply === "function" ? reply() : reply));
  return { db, r2, ai: fake, e: makeEnv({ DB: db, CLIPS: r2.bucket, AI: fake.ai }) };
}

const auth = (token = TOKEN) => ({ Authorization: `Bearer ${token}` });

function transcribe(e: Env, query: string, opts: { body?: string; type?: string; length?: string | null; token?: string } = {}) {
  const body = opts.body ?? AUDIO;
  const headers: Record<string, string> = { ...auth(opts.token), "Content-Type": opts.type ?? "text/plain; charset=utf-8" };
  if (opts.length !== null) headers["Content-Length"] = opts.length ?? String(body.length);
  return worker.fetch(makeRequest(`${BASE}/transcribe?${query}`, { method: "POST", headers, body }), e);
}

function putTranscript(e: Env, id: string, doc: unknown, token = TOKEN) {
  const body = typeof doc === "string" ? doc : JSON.stringify(doc);
  return worker.fetch(
    makeRequest(`${BASE}/clips/${id}/transcript`, {
      method: "PUT",
      headers: { ...auth(token), "Content-Type": "application/json; charset=utf-8" },
      body,
    }),
    e,
  );
}

const get = (e: Env, path: string, init: RequestInit = {}) => worker.fetch(makeRequest(`${BASE}${path}`, init), e);
const usageOf = (db: FakeDb, userId: number, day: string) => db._usage.get(`${userId}|${day}`);

describe("POST /transcribe", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    resetUserLimits();
  });
  afterEach(() => vi.restoreAllMocks());

  it("passes the base64 string unchanged to the exact Whisper model and accepts charset params", async () => {
    const s = await setup({ text: "gank top", segments: [{ start: 0.2, end: 1.4, text: " gank top " }], transcription_info: { language: "en" } });
    const res = await transcribe(s.e, "offset_ms=0&duration_ms=3000&language=en");
    expect(res.status).toBe(200);
    expect(s.ai.calls).toHaveLength(1);
    const { model, input } = s.ai.calls[0];
    expect(model).toBe(WHISPER_MODEL);
    expect(model).toBe("@cf/openai/whisper-large-v3-turbo");
    expect(input.audio).toBe(AUDIO);
    expect(input).toEqual({
      audio: AUDIO,
      task: "transcribe",
      language: "en",
      vad_filter: true,
      condition_on_previous_text: false,
      initial_prompt: LOL_PROMPT,
    });
    const out = await json(res);
    expect(out).toEqual({
      language: "en",
      duration_s: 3,
      text: "gank top",
      segments: [{ start: 0.2, end: 1.4, text: "gank top" }],
      usage: { audio_seconds_today: 3, cap_seconds: 7200 },
    });
  });

  it("omits language for auto and reports the detected one", async () => {
    const s = await setup({ text: "hola", segments: [{ start: 0, end: 1, text: "hola" }], transcription_info: { language: "es" } });
    const out = await json(await transcribe(s.e, "duration_ms=1000&language=auto"));
    expect(s.ai.calls[0].input.language).toBeUndefined();
    expect(out.language).toBe("es");
  });

  it("shifts by offset_ms, rounds to 3 decimals, clamps start at 0 and drops inverted segments", async () => {
    const s = await setup({
      text: "x",
      segments: [
        { start: 0.5004, end: 2.2506, text: "first   gank\n comes" },
        { start: 3, end: 4, text: "late" },
      ],
    });
    const out = await json(await transcribe(s.e, "offset_ms=10000&duration_ms=5000"));
    expect(out.segments).toEqual([
      { start: 10.5, end: 12.251, text: "first gank comes" },
      { start: 13, end: 14, text: "late" },
    ]);

    const neg = await setup({ text: "x", segments: [{ start: 0.4, end: 2, text: "kept" }, { start: 0, end: 0.8, text: "gone" }] });
    const negOut = await json(await transcribe(neg.e, "offset_ms=-1000&duration_ms=5000"));
    expect(negOut.segments).toEqual([{ start: 0, end: 1, text: "kept" }]);
  });

  it("drops no-speech segments, known hallucinations and empty text", async () => {
    const s = await setup({
      text: "x",
      segments: [
        { start: 0, end: 1, text: "silence", no_speech_prob: 0.7, avg_logprob: -1.5 },
        { start: 1, end: 2, text: "quiet but real", no_speech_prob: 0.7, avg_logprob: -0.5 },
        { start: 2, end: 3, text: "Thanks for watching!" },
        { start: 3, end: 4, text: "You." },
        { start: 4, end: 5, text: "Please subscribe" },
        { start: 5, end: 6, text: "   " },
        { start: 6, end: 7, text: "you missed flash" },
      ],
    });
    const out = await json(await transcribe(s.e, "duration_ms=8000"));
    expect((out.segments as { text: string }[]).map((x) => x.text)).toEqual(["quiet but real", "you missed flash"]);
    expect(out.text).toBe("quiet but real you missed flash");
  });

  it("falls back to one segment from text when there are no segments", async () => {
    const s = await setup({ text: "  push the wave  " });
    const out = await json(await transcribe(s.e, "offset_ms=5000&duration_ms=3000"));
    expect(out.segments).toEqual([{ start: 5, end: 8, text: "push the wave" }]);
    const h = await setup({ text: "Thanks for watching." });
    expect((await json(await transcribe(h.e, "duration_ms=3000"))).segments).toEqual([]);
  });

  it("does not revive text from out.text when the filters dropped every segment", async () => {
    const noSpeech = await setup({
      text: " Okay.",
      segments: [{ start: 0, end: 1, text: " Okay.", no_speech_prob: 0.9, avg_logprob: -1.5 }],
    });
    expect((await json(await transcribe(noSpeech.e, "duration_ms=3000"))).segments).toEqual([]);
    const joined = await setup({
      text: " Thanks for watching. Please subscribe.",
      segments: [
        { start: 0, end: 1, text: " Thanks for watching." },
        { start: 1, end: 2, text: " Please subscribe." },
      ],
    });
    const out = await json(await transcribe(joined.e, "duration_ms=3000"));
    expect(out.segments).toEqual([]);
    expect(out.text).toBe("");
  });

  it("charges the audio the body implies at 24 kbps and refuses a body far longer than duration_ms", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-10T10:00:00Z"));
    const s = await setup({ text: "ok", segments: [{ start: 0, end: 1, text: "ok" }] });
    // 40000 base64 chars = 30000 bytes = 10 s at 3000 B/s; declared 8 s is within the slack.
    const fair = await transcribe(s.e, "duration_ms=8000", { body: "A".repeat(40000) });
    expect(fair.status).toBe(200);
    expect(((await json(fair)).usage as Record<string, number>).audio_seconds_today).toBe(10);
    expect(usageOf(s.db, 0, "2026-10-10")).toEqual({ audio_seconds: 10, requests: 1 });

    // About 260 s of audio declared as 1 ms: refused before any reservation or model call.
    const smuggled = await transcribe(s.e, "duration_ms=1", { body: "A".repeat(1_040_000) });
    expect(smuggled.status).toBe(400);
    expect((await json(smuggled)).error).toBe("bad_request");
    expect(usageOf(s.db, USER, "2026-10-10")).toEqual({ audio_seconds: 10, requests: 1 });
    expect(s.ai.calls).toHaveLength(1);
  });

  it("enforces the 7200 s user cap with a refund and Retry-After to UTC midnight", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-10T23:00:00Z"));
    const s = await setup({ text: "ok", segments: [{ start: 0, end: 1, text: "ok" }] });
    s.db._usage.set(`${USER}|2026-10-10`, { audio_seconds: 7190, requests: 5 });
    const atCap = await transcribe(s.e, "duration_ms=10000");
    expect(atCap.status).toBe(200);
    expect(((await json(atCap)).usage as Record<string, number>).audio_seconds_today).toBe(7200);

    const over = await transcribe(s.e, "duration_ms=1");
    expect(over.status).toBe(429);
    expect(await json(over)).toEqual({ error: "transcribe_quota", message: "Daily transcript limit reached. Try again tomorrow." });
    expect(over.headers.get("Retry-After")).toBe("3600");
    expect(usageOf(s.db, USER, "2026-10-10")).toEqual({ audio_seconds: 7200, requests: 6 });
    expect(usageOf(s.db, 0, "2026-10-10")).toEqual({ audio_seconds: 10, requests: 1 });
    expect(s.ai.calls).toHaveLength(1);
  });

  it("enforces the 10800 s global cap", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-10T12:00:00Z"));
    const s = await setup({ text: "ok" });
    s.db._usage.set("0|2026-10-10", { audio_seconds: 10795, requests: 900 });
    const res = await transcribe(s.e, "duration_ms=10000");
    expect(res.status).toBe(429);
    expect((await json(res)).error).toBe("transcribe_quota");
    expect(usageOf(s.db, 0, "2026-10-10")).toEqual({ audio_seconds: 10795, requests: 900 });
    expect(usageOf(s.db, USER, "2026-10-10")).toEqual({ audio_seconds: 0, requests: 0 });
    expect(s.ai.calls).toHaveLength(0);
  });

  it("returns 503 without the AI binding and 502 with a refund when the model throws", async () => {
    const s = await setup();
    const noAi = await transcribe({ ...s.e, AI: undefined }, "duration_ms=1000");
    expect(noAi.status).toBe(503);
    expect((await json(noAi)).error).toBe("transcribe_unavailable");

    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-10T08:00:00Z"));
    const errors = vi.spyOn(console, "error").mockImplementation(() => {});
    const boom = await setup(() => { throw new Error("model overloaded"); });
    const res = await transcribe(boom.e, "duration_ms=4000");
    expect(res.status).toBe(502);
    expect((await json(res)).error).toBe("transcribe_error");
    expect(usageOf(boom.db, USER, "2026-10-10")).toEqual({ audio_seconds: 0, requests: 0 });
    expect(usageOf(boom.db, 0, "2026-10-10")).toEqual({ audio_seconds: 0, requests: 0 });
    expect(errors).toHaveBeenCalled();
    for (const call of errors.mock.calls) expect(String(call[0])).not.toContain(AUDIO);
  });

  it("gives the user reservation back when the global reservation fails", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-10T09:00:00Z"));
    vi.spyOn(console, "error").mockImplementation(() => {});
    const s = await setup({ text: "ok" });
    s.db._failNext("INSERT INTO transcribe_usage", 1); // user upsert passes, global throws
    const res = await transcribe(s.e, "duration_ms=5000");
    expect(res.status).toBe(502);
    expect(usageOf(s.db, USER, "2026-10-10")).toEqual({ audio_seconds: 0, requests: 0 });
    expect(usageOf(s.db, 0, "2026-10-10")).toBeUndefined();
    expect(s.ai.calls).toHaveLength(0);
  });

  it("validates auth, params, media type and length", async () => {
    const s = await setup();
    expect((await json(await transcribe(s.e, "duration_ms=1000", { token: "static-op-token" }))).error).toBe("login_required");
    const bad = await transcribe(s.e, "duration_ms=1000", { token: "nope" });
    expect(bad.status).toBe(401);
    expect(await json(bad)).toEqual({ error: "unauthorized" });
    expect((await transcribe(s.e, "duration_ms=1000", { length: null })).status).toBe(411);
    expect((await transcribe(s.e, "duration_ms=1000", { length: "1048577" })).status).toBe(413);
    expect((await transcribe(s.e, "duration_ms=0")).status).toBe(400);
    expect((await transcribe(s.e, "duration_ms=120001")).status).toBe(400);
    expect((await transcribe(s.e, "duration_ms=1.5")).status).toBe(400);
    expect((await transcribe(s.e, "duration_ms=1000&offset_ms=-60001")).status).toBe(400);
    expect((await transcribe(s.e, "duration_ms=1000&offset_ms=1200001")).status).toBe(400);
    expect((await transcribe(s.e, "duration_ms=1000&language=EN")).status).toBe(400);
    expect((await transcribe(s.e, "duration_ms=1000", { type: "application/json" })).status).toBe(400);
    expect((await transcribe(s.e, "duration_ms=1000", { body: "", length: "0" })).status).toBe(400);
    expect(s.ai.calls).toHaveLength(0);
  });

  it("rate limits at 30 per minute per user", async () => {
    const s = await setup({ text: "" });
    for (let i = 0; i < 30; i++) expect((await transcribe(s.e, "duration_ms=1")).status).toBe(200);
    const res = await transcribe(s.e, "duration_ms=1");
    expect(res.status).toBe(429);
    expect((await json(res)).error).toBe("rate_limited");
  });
});

describe("clip transcripts", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    resetUserLimits();
  });

  const doc = {
    version: 1,
    language: "en",
    segments: [
      { start: 5.12345, end: 6, text: "b &  <tag>  --> next" },
      { start: -1, end: 2, text: "  a  " },
      { start: 3, end: 3, text: "zero length" },
      { start: 8, end: 99, text: "long" },
      { start: 1, end: 2, text: "   " },
    ],
  };
  const EXPECTED_VTT =
    "WEBVTT\n\n" +
    "1\n00:00:00.000 --> 00:00:02.000\na\n\n" +
    // C3: "-->" collapses to "->", then every ">" is entity-escaped.
    "2\n00:00:05.123 --> 00:00:06.000\nb &amp; &lt;tag&gt; -&gt; next\n\n" +
    "3\n00:00:08.000 --> 00:00:15.000\nlong\n\n";

  async function withClip() {
    const s = await setup(undefined, [clip({ id: "Narr001", user_id: USER, duration_s: 10, narrated: 1 })]);
    return s;
  }

  it("normalizes a PUT document, builds escaped VTT, and serves JSON + VTT with safe headers and CORS", async () => {
    const s = await withClip();
    const res = await putTranscript(s.e, "Narr001", doc);
    expect(res.status).toBe(200);
    expect(await json(res)).toEqual({ ok: true, segment_count: 3 });
    expect(s.db._clips.get("Narr001")!.has_transcript).toBe(1);
    // The VTT is generated on GET, never stored (escaping inflates it about 5x).
    expect(s.db._transcripts.get("Narr001")!.vtt).toBe("");

    const origin = { headers: { Origin: "https://revu.lol" } };
    const j = await get(s.e, "/clip-transcript/Narr001", origin);
    expect(j.status).toBe(200);
    expect(j.headers.get("Content-Type")).toBe("application/json; charset=utf-8");
    expect(j.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(j.headers.get("Content-Security-Policy")).toBe("sandbox");
    expect(j.headers.get("Cache-Control")).toBe("public, max-age=300");
    expect(j.headers.get("Access-Control-Allow-Origin")).toBe("https://revu.lol");
    expect(j.headers.get("Access-Control-Allow-Methods")).toBe("GET, HEAD, POST, PUT, DELETE, OPTIONS");
    expect(await j.json()).toEqual({
      id: "Narr001",
      language: "en",
      segments: [
        { start: 0, end: 2, text: "a" },
        { start: 5.123, end: 6, text: "b & <tag> --> next" },
        { start: 8, end: 15, text: "long" },
      ],
    });

    const v = await get(s.e, "/clip-transcript/Narr001?format=vtt", origin);
    expect(v.headers.get("Content-Type")).toBe("text/vtt; charset=utf-8");
    expect(v.headers.get("Access-Control-Allow-Origin")).toBe("https://revu.lol");
    expect(v.headers.get("Content-Security-Policy")).toBe("sandbox");
    expect(await v.text()).toBe(EXPECTED_VTT);

    const h = await get(s.e, "/clip-transcript/Narr001?format=vtt", { method: "HEAD" });
    expect(h.status).toBe(200);
    expect(h.headers.get("Content-Type")).toBe("text/vtt; charset=utf-8");
    expect(h.headers.get("Cache-Control")).toBe("public, max-age=300");
    expect(await h.text()).toBe("");

    const meta = await json(await get(s.e, "/clip-meta/Narr001"));
    expect(meta).toMatchObject({ narrated: true, has_transcript: true, transcript_language: "en" });
    const mine = await json(await get(s.e, "/clips/mine", { headers: auth() }));
    expect((mine.clips as Record<string, unknown>[])[0]).toMatchObject({
      id: "Narr001", status: "ready", narrated: true, has_transcript: true,
    });
  });

  it("an empty PUT (after normalization) removes the transcript and clears has_transcript", async () => {
    const s = await withClip();
    await putTranscript(s.e, "Narr001", doc);
    const res = await putTranscript(s.e, "Narr001", { version: 1, language: "en", segments: [{ start: 2, end: 1, text: "x" }] });
    expect(await json(res)).toEqual({ ok: true, segment_count: 0 });
    expect(s.db._clips.get("Narr001")!.has_transcript).toBe(0);
    expect(s.db._transcripts.has("Narr001")).toBe(false);
    expect((await get(s.e, "/clip-transcript/Narr001")).status).toBe(404);
    expect((await json(await get(s.e, "/clip-meta/Narr001"))).transcript_language).toBeNull();
  });

  it("rejects malformed documents, oversized bodies, and non-owners", async () => {
    const s = await withClip();
    const bad = [
      { version: 2, segments: [] },
      { version: 1, segments: "nope" },
      { version: 1, segments: [{ start: 0, end: 1, text: 5 }] },
      { version: 1, segments: [{ start: "0", end: 1, text: "a" }] },
      { version: 1, language: "toolonglang", segments: [] },
      { version: 1, segments: Array.from({ length: 3001 }, (_, i) => ({ start: i, end: i + 1, text: "a" })) },
      "{not json",
    ];
    for (const b of bad) {
      const res = await putTranscript(s.e, "Narr001", b);
      expect(res.status).toBe(400);
      expect((await json(res)).error).toBe("bad_transcript");
    }
    const huge = { version: 1, segments: [{ start: 0, end: 1, text: "a".repeat(300_000) }] };
    expect((await putTranscript(s.e, "Narr001", huge)).status).toBe(413);
    expect((await putTranscript(s.e, "Narr001", doc, OTHER)).status).toBe(404);
    expect((await putTranscript(s.e, "Nope123", doc)).status).toBe(404);
    expect((await json(await putTranscript(s.e, "Narr001", doc, "static-op-token"))).error).toBe("login_required");
    const badPut = await putTranscript(s.e, "Narr001", doc, "nope");
    expect(badPut.status).toBe(401);
    expect((await json(badPut)).error).toBe("unauthorized");
  });

  it("caps the transcript text one account keeps across its live clips", async () => {
    const s = await setup(undefined, [
      clip({ id: "Narr001", user_id: USER, duration_s: 10 }),
      clip({ id: "Narr002", user_id: USER }),
      clip({ id: "Gone003", user_id: USER, expires_at: nowSec() - 5 }),
      clip({ id: "Else004", user_id: 32 }),
    ]);
    const filler = (id: string, chars: number) =>
      s.db._transcripts.set(id, { clip_id: id, language: "en", segments_json: "x".repeat(chars), vtt: "", segment_count: 1, created_at: 0 });
    filler("Narr002", TRANSCRIPT_USER_MAX_BYTES - 100);
    filler("Gone003", TRANSCRIPT_USER_MAX_BYTES); // expired: not counted
    filler("Else004", TRANSCRIPT_USER_MAX_BYTES); // another account: not counted

    const over = await putTranscript(s.e, "Narr001", doc);
    expect(over.status).toBe(413);
    expect((await json(over)).error).toBe("payload_too_large");
    expect(s.db._transcripts.has("Narr001")).toBe(false);

    filler("Narr002", TRANSCRIPT_USER_MAX_BYTES - 1000);
    expect((await putTranscript(s.e, "Narr001", doc)).status).toBe(200);
    // Replacing a clip's own transcript does not count the old copy.
    filler("Narr002", TRANSCRIPT_USER_MAX_BYTES - s.db._transcripts.get("Narr001")!.segments_json.length);
    expect((await putTranscript(s.e, "Narr001", doc)).status).toBe(200);
  });

  it("DELETE /clips/:id/transcript is owner-only and idempotent", async () => {
    const s = await withClip();
    await putTranscript(s.e, "Narr001", doc);
    const del = (token = TOKEN) =>
      worker.fetch(makeRequest(`${BASE}/clips/Narr001/transcript`, { method: "DELETE", headers: auth(token) }), s.e);
    expect((await del(OTHER)).status).toBe(404);
    expect(s.db._transcripts.has("Narr001")).toBe(true);
    for (let i = 0; i < 2; i++) {
      const res = await del();
      expect(res.status).toBe(200);
      expect(await json(res)).toEqual({ ok: true });
    }
    expect(s.db._clips.get("Narr001")!.has_transcript).toBe(0);
    expect(s.db._clips.has("Narr001")).toBe(true); // the clip itself stays
  });

  it("404s the transcript after the clip is deleted and after the purge", async () => {
    const s = await withClip();
    await putTranscript(s.e, "Narr001", doc);
    const del = await worker.fetch(makeRequest(`${BASE}/clips/Narr001`, { method: "DELETE", headers: auth() }), s.e);
    expect(del.status).toBe(200);
    expect(s.db._transcripts.has("Narr001")).toBe(false);
    expect((await get(s.e, "/clip-transcript/Narr001")).status).toBe(404);

    const p = await withClip();
    await putTranscript(p.e, "Narr001", doc);
    expect((await get(p.e, "/clip-transcript/Narr001")).status).toBe(200);
    p.db._clips.get("Narr001")!.expires_at = nowSec() - 1;
    expect((await get(p.e, "/clip-transcript/Narr001")).status).toBe(404); // expired: hidden at once
    await runScheduled(worker, p.e);
    expect(p.db._clips.has("Narr001")).toBe(false);
    expect(p.db._transcripts.has("Narr001")).toBe(false);
    const nf = await get(p.e, "/clip-transcript/Narr001");
    expect(nf.status).toBe(404);
    expect(await json(nf)).toEqual({ error: "not_found" });
  });

  it("the hourly cron prunes transcribe_usage rows older than 7 days", async () => {
    vi.spyOn(Date, "now").mockReturnValue(Date.parse("2026-10-10T05:00:00Z"));
    const s = await setup();
    s.db._usage.set(`${USER}|2026-10-02`, { audio_seconds: 50, requests: 1 });
    s.db._usage.set("0|2026-10-02", { audio_seconds: 50, requests: 1 });
    s.db._usage.set(`${USER}|2026-10-03`, { audio_seconds: 60, requests: 1 });
    s.db._usage.set(`${USER}|2026-10-10`, { audio_seconds: 70, requests: 1 });
    await runScheduled(worker, s.e);
    expect([...s.db._usage.keys()].sort()).toEqual([`${USER}|2026-10-03`, `${USER}|2026-10-10`]);
  });
});
