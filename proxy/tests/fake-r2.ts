/**
 * Fake R2 bucket (with multipart), fake Workers AI, and known-length stream
 * tracking. Re-exported from fakes.ts; see the notes there.
 */

import { WHISPER_MODEL } from "../src/transcripts";

// ── known-length streams ────────────────────────────────────────────────────

export const knownLengthStreams = new WeakSet<ReadableStream>();

if (typeof (globalThis as Record<string, unknown>).FixedLengthStream === "undefined") {
  (globalThis as Record<string, unknown>).FixedLengthStream = class {
    readable: ReadableStream<Uint8Array>;
    writable: WritableStream<Uint8Array>;
    constructor(_length: number) {
      const ts = new TransformStream<Uint8Array, Uint8Array>();
      this.writable = ts.writable;
      this.readable = ts.readable;
      knownLengthStreams.add(this.readable);
    }
  };
}

/**
 * Build a Request. When it carries a Content-Length header its body is a
 * known-length stream (what Workers gives a handler), so register it.
 */
export function makeRequest(url: string, init: RequestInit = {}): Request {
  const req = new Request(url, init);
  if (req.body && req.headers.get("Content-Length") !== null) knownLengthStreams.add(req.body);
  return req;
}

// ── R2 ──────────────────────────────────────────────────────────────────────

const MIN_PART_BYTES = 5 * 1024 * 1024;

type R2Value = ReadableStream | ArrayBuffer | ArrayBufferView | string | Blob;

async function consume(value: R2Value): Promise<Uint8Array> {
  if (typeof value === "string") return new TextEncoder().encode(value);
  if (value instanceof ArrayBuffer) return new Uint8Array(value.slice(0));
  if (ArrayBuffer.isView(value)) return new Uint8Array(value.buffer, value.byteOffset, value.byteLength).slice();
  if (value instanceof Blob) return new Uint8Array(await value.arrayBuffer());
  if (value instanceof ReadableStream) {
    if (!knownLengthStreams.has(value)) throw new TypeError("stream must have a known length");
    const reader = value.getReader();
    const chunks: Uint8Array[] = [];
    let total = 0;
    for (;;) {
      const { done, value: chunk } = await reader.read();
      if (done) break;
      if (chunk) { chunks.push(chunk); total += chunk.byteLength; }
    }
    const merged = new Uint8Array(total);
    let off = 0;
    for (const c of chunks) { merged.set(c, off); off += c.byteLength; }
    return merged;
  }
  throw new TypeError("unsupported R2 value");
}

interface FakeUpload {
  key: string;
  state: "live" | "completed" | "aborted";
  parts: Map<number, { etag: string; bytes: Uint8Array }>;
}

export function makeFakeR2() {
  const store = new Map<string, Uint8Array>();
  const uploads = new Map<string, FakeUpload>();
  const calls = { put: 0, uploadPart: 0, complete: 0, abort: 0, deleted: [] as string[] };
  let seq = 0;

  function objectFor(key: string, bytes: Uint8Array) {
    return { key, size: bytes.byteLength, etag: `etag-${key}`, httpEtag: `"etag-${key}"` };
  }

  function liveUpload(key: string, uploadId: string): FakeUpload {
    const u = uploads.get(uploadId);
    if (!u || u.key !== key) throw new Error("NoSuchUpload");
    if (u.state !== "live") throw new Error(`upload already ${u.state}`);
    return u;
  }

  function handle(key: string, uploadId: string) {
    return {
      key,
      uploadId,
      async uploadPart(partNumber: number, value: R2Value) {
        const u = liveUpload(key, uploadId);
        if (!Number.isInteger(partNumber) || partNumber < 1 || partNumber > 10000) throw new RangeError("bad part number");
        const bytes = await consume(value);
        calls.uploadPart++;
        const etag = `etag-${uploadId}-${partNumber}-${++seq}`;
        u.parts.set(partNumber, { etag, bytes });
        return { partNumber, etag };
      },
      async abort() {
        calls.abort++;
        const u = uploads.get(uploadId);
        if (!u || u.key !== key || u.state !== "live") throw new Error("NoSuchUpload");
        u.state = "aborted";
      },
      async complete(parts: { partNumber: number; etag: string }[]) {
        calls.complete++;
        const u = liveUpload(key, uploadId);
        if (parts.length === 0) throw new Error("no parts");
        const chosen: Uint8Array[] = [];
        parts.forEach((p, i) => {
          if (i > 0 && p.partNumber <= parts[i - 1].partNumber) throw new Error("parts must be ascending");
          const stored = u.parts.get(p.partNumber);
          if (!stored || stored.etag !== p.etag) throw new Error(`unknown etag for part ${p.partNumber}`);
          chosen.push(stored.bytes);
        });
        for (let i = 0; i < chosen.length - 1; i++) {
          if (chosen[i].byteLength < MIN_PART_BYTES) throw new Error("EntityTooSmall");
          if (chosen[i].byteLength !== chosen[0].byteLength) throw new Error("non-last parts differ in size");
        }
        const total = chosen.reduce((s, c) => s + c.byteLength, 0);
        const merged = new Uint8Array(total);
        let off = 0;
        for (const c of chosen) { merged.set(c, off); off += c.byteLength; }
        store.set(key, merged);
        u.state = "completed";
        return objectFor(key, merged);
      },
    };
  }

  const bucket = {
    async put(key: string, value: R2Value) {
      const bytes = await consume(value);
      calls.put++;
      store.set(key, bytes);
      return objectFor(key, bytes);
    },
    async head(key: string) {
      const bytes = store.get(key);
      return bytes ? objectFor(key, bytes) : null;
    },
    async get(key: string, opts?: { range?: { offset: number; length: number } }) {
      const bytes = store.get(key);
      if (!bytes) return null;
      const slice = opts?.range ? bytes.slice(opts.range.offset, opts.range.offset + opts.range.length) : bytes;
      return {
        ...objectFor(key, bytes),
        body: new Blob([slice]).stream(),
        async arrayBuffer() { return slice.slice().buffer; },
      };
    },
    async delete(keys: string | string[]) {
      const list = Array.isArray(keys) ? keys : [keys];
      if (list.length > 1000) throw new Error("R2 delete takes at most 1000 keys");
      for (const k of list) { store.delete(k); calls.deleted.push(k); }
    },
    async createMultipartUpload(key: string, _opts?: unknown) {
      const uploadId = `upl-${++seq}`;
      uploads.set(uploadId, { key, state: "live", parts: new Map() });
      return handle(key, uploadId);
    },
    resumeMultipartUpload(key: string, uploadId: string) {
      return handle(key, uploadId);
    },
  };
  return { bucket: bucket as unknown as R2Bucket, store, uploads, calls };
}

// ── Workers AI ──────────────────────────────────────────────────────────────

export function makeFakeAi(respond: (input: Record<string, unknown>) => unknown | Promise<unknown>) {
  const calls: Array<{ model: string; input: Record<string, unknown> }> = [];
  const ai = {
    async run(model: string, input: Record<string, unknown>) {
      if (model !== WHISPER_MODEL) throw new Error(`fake AI: unexpected model ${model}`);
      if (typeof input?.audio !== "string") throw new TypeError("fake AI: audio must be a string");
      calls.push({ model, input });
      return respond(input);
    },
  };
  return { ai: ai as unknown as Ai, calls };
}

