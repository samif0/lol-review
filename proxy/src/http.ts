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
  const declared = parseStrictInt(request.headers.get("Content-Length"));
  if (declared !== null && declared > maxBytes) return null;
  if (!request.body) return "";
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
  return new TextDecoder().decode(out);
}
