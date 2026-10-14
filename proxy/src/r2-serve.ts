/**
 * Shared R2 object serving for /clip-file and /vod-file: single byte ranges,
 * conditional requests and HEAD, with the bulk bytes passed through untouched
 * (`obj.body` straight into the Response, no JS stream work).
 *
 *   - Range: a single `bytes=` range is a 206; one that starts past the end is a
 *     416 whose Content-Range is the unsatisfied form (an asterisk, then the
 *     size); anything unparseable is ignored (full 200), as RFC 9110 allows.
 *   - If-None-Match matching the ETag is a 304 with no body.
 *   - If-Range that no longer matches the ETag ignores the Range (full 200).
 *   - HEAD reads only the object's metadata (R2 head), never its body.
 * Conditional requests and HEAD cost one R2 head; a plain GET costs one get.
 */

import { jsonResponse } from "./http";

export type ByteRange = { offset: number; length: number };

/**
 * Parse a single-range `Range: bytes=start-end` header into an R2-friendly
 * { offset, length }. Returns "unsatisfiable" for a well-formed range that
 * starts at or past the end (or a zero-length suffix), and null for an absent,
 * malformed, inverted or multi-range header (caller serves the whole object).
 */
export function parseRange(header: string | null, size: number): ByteRange | "unsatisfiable" | null {
  if (!header) return null;
  const m = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
  if (!m) return null;
  const startStr = m[1];
  const endStr = m[2];

  if (startStr === "" && endStr === "") return null;

  let offset: number;
  let end: number;
  if (startStr === "") {
    // suffix range: last N bytes
    const suffix = parseInt(endStr, 10);
    if (!Number.isFinite(suffix)) return null;
    if (suffix <= 0 || size <= 0) return "unsatisfiable";
    offset = Math.max(0, size - suffix);
    end = size - 1;
  } else {
    offset = parseInt(startStr, 10);
    end = endStr === "" ? size - 1 : parseInt(endStr, 10);
  }

  if (!Number.isFinite(offset) || !Number.isFinite(end)) return null;
  if (endStr !== "" && startStr !== "" && end < offset) return null;
  if (offset >= size) return "unsatisfiable";
  if (end >= size) end = size - 1;

  return { offset, length: end - offset + 1 };
}

/** True when an If-None-Match / If-Range value names `etag` (weak tags compare equal). */
function etagMatches(header: string, etag: string): boolean {
  if (header.trim() === "*") return true;
  const strip = (t: string) => t.trim().replace(/^W\//, "");
  return header.split(",").some((t) => strip(t) === strip(etag));
}

/**
 * Answer a GET or HEAD for `r2Key` (a `size`-byte object). `headers` carries the
 * route's fixed response headers (Content-Type first); this adds ETag,
 * Content-Length and Content-Range as needed.
 */
export async function serveR2Object(
  request: Request,
  bucket: R2Bucket,
  r2Key: string,
  size: number,
  headers: Headers,
): Promise<Response> {
  const isHead = request.method === "HEAD";
  const ifNoneMatch = request.headers.get("If-None-Match");
  const ifRange = request.headers.get("If-Range");
  let range = parseRange(request.headers.get("Range"), size);

  // Metadata first when the answer depends on the ETag, or no body is wanted.
  let etag: string | null = null;
  if (isHead || ifNoneMatch !== null || (range !== null && ifRange !== null)) {
    const meta = await bucket.head(r2Key);
    if (!meta) return jsonResponse({ error: "not_found" }, 404);
    etag = meta.httpEtag || null;
    if (etag) headers.set("ETag", etag);
    if (etag && ifNoneMatch !== null && etagMatches(ifNoneMatch, etag)) {
      headers.delete("Content-Type");
      return new Response(null, { status: 304, headers });
    }
    // If-Range carries an ETag here (we send no Last-Modified); a stale one
    // means "send the whole current file".
    if (range !== null && ifRange !== null && !(etag && etagMatches(ifRange, etag))) range = null;
  }

  if (range === "unsatisfiable") {
    headers.set("Content-Range", `bytes */${size}`);
    headers.delete("Content-Type");
    return new Response(null, { status: 416, headers });
  }

  if (range) {
    headers.set("Content-Length", String(range.length));
    headers.set("Content-Range", `bytes ${range.offset}-${range.offset + range.length - 1}/${size}`);
  } else {
    headers.set("Content-Length", String(size));
  }
  const status = range ? 206 : 200;
  if (isHead) return new Response(null, { status, headers });

  const obj = await bucket.get(r2Key, range ? { range: { offset: range.offset, length: range.length } } : undefined);
  if (!obj) return jsonResponse({ error: "not_found" }, 404);
  if (obj.httpEtag) headers.set("ETag", obj.httpEtag);
  return new Response(obj.body, { status, headers });
}
