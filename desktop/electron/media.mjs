import { randomUUID } from 'node:crypto';
import { open, realpath } from 'node:fs/promises';
import path from 'node:path';
import { Readable } from 'node:stream';

export function byteRange(header, size) {
  if (!Number.isSafeInteger(size) || size < 0) throw new Error('Unsupported file size');
  if (!header) return { start: 0, end: size - 1, partial: false };
  const match = /^bytes=(\d*)-(\d*)$/.exec(header);
  if (!match || (!match[1] && !match[2]) || size === 0) return null;
  let start, end;
  if (!match[1]) {
    const suffix = Number(match[2]);
    if (!Number.isSafeInteger(suffix) || suffix <= 0) return null;
    start = Math.max(0, size - suffix); end = size - 1;
  } else {
    start = Number(match[1]); end = match[2] ? Number(match[2]) : size - 1;
    if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start >= size || end < start) return null;
    end = Math.min(end, size - 1);
  }
  return { start, end, partial: true };
}

const mediaTypes = new Map([['.mp4', 'video/mp4'], ['.m4v', 'video/mp4'],
  ['.mkv', 'video/x-matroska'], ['.webm', 'video/webm'], ['.mov', 'video/quicktime']]);

// The renderer cannot grant a path. Only allowlisted backend snapshots populate
// this registry, and each page/review replacement revokes the previous grants.
export class MediaRegistry {
  #grants = new Map(); #streams = new Map(); #generation = 0;
  clear() {
    this.#generation++;
    this.#grants.clear();
    for (const stream of this.#streams.keys()) stream.destroy();
    this.#streams.clear();
  }
  async replace(paths) {
    const generation = ++this.#generation;
    const resolved = [];
    for (const file of [...new Set(paths)].slice(0, 512)) {
      if (typeof file !== 'string' || !path.isAbsolute(file) || !mediaTypes.has(path.extname(file).toLowerCase())) continue;
      try {
        const canonical = await realpath(file);
        resolved.push([file, canonical]);
      } catch { /* Missing media is unavailable, never a fallback arbitrary path. */ }
    }
    if (generation !== this.#generation) throw new Error('Media review changed');
    const desired = new Set(resolved.map(([, canonical]) => canonical));
    for (const [token, file] of this.#grants) if (!desired.has(file)) this.#grants.delete(token);
    for (const [stream, file] of this.#streams) if (!desired.has(file)) { stream.destroy(); this.#streams.delete(stream); }
    const tokens = new Map([...this.#grants].map(([token, file]) => [file, token]));
    const result = resolved.map(([original, canonical]) => {
      const token = tokens.get(canonical) || randomUUID();
      tokens.set(canonical, token); this.#grants.set(token, canonical);
      return [original, `revu-media://file/${token}`];
    });
    return result;
  }
  async respond(request) {
    const url = new URL(request.url);
    if (url.protocol !== 'revu-media:' || url.host !== 'file' || url.search || url.hash) return new Response(null, { status: 403 });
    const file = this.#grants.get(url.pathname.slice(1));
    if (!file) return new Response(null, { status: 403 });
    if (!['GET', 'HEAD'].includes(request.method)) return new Response(null, { status: 405 });
    const generation = this.#generation;
    let handle;
    try {
      if (await realpath(file) !== file) return new Response(null, { status: 403 });
      handle = await open(file, 'r');
      const stat = await handle.stat();
      if (this.#grants.get(url.pathname.slice(1)) !== file) return new Response(null, { status: 403 });
      if (!stat.isFile()) return new Response(null, { status: 404 });
      const range = byteRange(request.headers.get('Range'), stat.size);
      const headers = { 'Accept-Ranges': 'bytes', 'Cache-Control': 'no-store',
        'Content-Type': mediaTypes.get(path.extname(file).toLowerCase()), 'X-Content-Type-Options': 'nosniff' };
      if (!range) return new Response(null, { status: 416, headers: { ...headers, 'Content-Range': `bytes */${stat.size}` } });
      headers['Content-Length'] = String(Math.max(0, range.end - range.start + 1));
      if (range.partial) headers['Content-Range'] = `bytes ${range.start}-${range.end}/${stat.size}`;
      const status = range.partial ? 206 : 200;
      if (request.method === 'HEAD' || stat.size === 0) return new Response(null, { status, headers });
      const stream = handle.createReadStream({ start: range.start, end: range.end, autoClose: true });
      handle = null; // Stream now owns the descriptor.
      this.#streams.set(stream, file);
      stream.once('close', () => this.#streams.delete(stream));
      return new Response(Readable.toWeb(stream), { status, headers });
    } catch { return new Response(null, { status: 404 }); }
    finally { await handle?.close(); }
  }
}
