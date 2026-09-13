import { readFile, realpath } from 'node:fs/promises';
import path from 'node:path';
import { isAppUrl } from './routing.mjs';

const artworkHosts = ['raw.communitydragon.org', 'ddragon.leagueoflegends.com'];
const contentTypes = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css',
  '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg',
  '.webp': 'image/webp', '.woff2': 'font/woff2', '.woff': 'font/woff', '.ttf': 'font/ttf', '.otf': 'font/otf', '.ico': 'image/x-icon' };
const csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https://raw.communitydragon.org https://ddragon.leagueoflegends.com; font-src 'self' data:; media-src revu-media:; connect-src 'self'; frame-src 'self'; object-src 'none'; base-uri 'none'; form-action 'none'";

export function registerProtocols({ protocol, session, uiDirectory, media }) {
  session.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  session.setPermissionCheckHandler(() => false);
  session.webRequest.onBeforeRequest((details, callback) => {
    try {
      const url = new URL(details.url);
      callback({ cancel: !['revu-app:', 'revu-media:', 'data:', 'blob:'].includes(url.protocol)
        && !(url.protocol === 'https:' && artworkHosts.includes(url.hostname)) });
    } catch { callback({ cancel: true }); }
  });
  protocol.handle('revu-app', request => appResponse(request, uiDirectory));
  protocol.handle('revu-media', request => media.respond(request));
}

export async function appResponse(request, uiDirectory) {
  if (!isAppUrl(request.url) || !['GET', 'HEAD'].includes(request.method)) return new Response(null, { status: 403 });
  try {
    const url = new URL(request.url);
    const filename = path.resolve(uiDirectory, '.' + decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname));
    const relative = path.relative(uiDirectory, filename);
    if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) return new Response(null, { status: 403 });
    const resolved = await realpath(filename);
    if (!resolved.toLowerCase().startsWith((uiDirectory + path.sep).toLowerCase())) return new Response(null, { status: 403 });
    const contentType = contentTypes[path.extname(resolved).toLowerCase()];
    if (!contentType) return new Response(null, { status: 403 });
    return new Response(request.method === 'HEAD' ? null : await readFile(resolved), {
      headers: { 'Content-Type': contentType, 'Content-Security-Policy': csp, 'X-Content-Type-Options': 'nosniff' },
    });
  } catch { return new Response(null, { status: 404 }); }
}
