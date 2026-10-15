/**
 * Small response helpers.
 */

export function jsonResponse(
  body: unknown,
  status: number,
  extraHeaders: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...extraHeaders },
  });
}

export function badRequest(message: string): Response {
  return jsonResponse({ error: "bad_request", message }, 400);
}

/**
 * Wall time one request spent waiting on D1 and on R2, for a
 * `Server-Timing: d1;dur=N, r2;dur=N` header (the desktop logs it per PUT and
 * the canary reads it per colo). Workers only advance the clock across I/O,
 * which is exactly what this measures.
 */
export class Timing {
  d1 = 0;
  r2 = 0;

  async db<T>(work: () => Promise<T>): Promise<T> {
    const t0 = Date.now();
    try {
      return await work();
    } finally {
      this.d1 += Date.now() - t0;
    }
  }

  async r2op<T>(work: () => Promise<T>): Promise<T> {
    const t0 = Date.now();
    try {
      return await work();
    } finally {
      this.r2 += Date.now() - t0;
    }
  }

  header(): string {
    return `d1;dur=${this.d1}, r2;dur=${this.r2}`;
  }
}

/** A path safe to log: the play token in /vod-hls/<id>/<token>/... is never written out. */
export function loggablePath(pathname: string): string {
  return pathname.replace(/^(\/vod-hls\/[^/]*\/)[^/]+/, "$1-");
}

/**
 * The request's media type only: everything before `;`, trimmed, lowercased.
 * .NET sends `text/plain; charset=utf-8`, so parameters must never matter.
 */
export function mediaTypeOf(request: Request): string {
  return (request.headers.get("Content-Type") || "").split(";")[0].trim().toLowerCase();
}

/**
 * Parse a header/query value that must be a plain base-10 integer (optional
 * leading minus). Returns null for anything else ("1.5", "1e3", " 7", "").
 */
export function parseStrictInt(value: string | null): number | null {
  if (value === null || !/^-?\d{1,16}$/.test(value)) return null;
  const n = Number(value);
  return Number.isSafeInteger(n) ? n : null;
}

/**
 * Read a SMALL request body (JSON control payloads, at most a few hundred KiB)
 * with a hard byte cap, without trusting Content-Length. Returns null when the
 * body exceeds `maxBytes`. Never use this for clip bytes: those go straight to
 * R2 as `request.body`.
 */
export async function readSmallBody(request: Request, maxBytes: number): Promise<string | null> {
  const bytes = await readSmallBytes(request, maxBytes);
  return bytes === null ? null : new TextDecoder().decode(bytes);
}

/** readSmallBody's bytes, undecoded (an HLS init: a few KiB of ftyp + moov). */
export async function readSmallBytes(request: Request, maxBytes: number): Promise<Uint8Array | null> {
  const declared = parseStrictInt(request.headers.get("Content-Length"));
  if (declared !== null && declared > maxBytes) return null;
  if (!request.body) return new Uint8Array(0);
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (!value) continue;
      total += value.byteLength;
      if (total > maxBytes) {
        try { await reader.cancel(); } catch { /* best effort */ }
        return null;
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const out = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) { out.set(chunk, offset); offset += chunk.byteLength; }
  return out;
}
