import assert from 'node:assert/strict';
import { mkdtemp, readdir, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { saveNarration } from '../narration.mjs';
import { backendRequest } from '../sidecar.mjs';

async function serve(handler) {
  const server = createServer(handler);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return { server, hs: { port: server.address().port, token: 'A'.repeat(64) } };
}

test('a rejected backend request carries its HTTP status and the sidecar message', async () => {
  const seen = [];
  const { server, hs } = await serve((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      seen.push({ method: req.method, url: req.url, auth: req.headers.authorization, body });
      res.writeHead(422, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ok: false, error: 'x' }));
    });
  });
  try {
    await assert.rejects(backendRequest(hs, '/api/clip/narration/save', { method: 'POST', body: { a: 1 } }),
      error => error.status === 422 && error.message === 'x');
    assert.deepEqual(seen, [{ method: 'POST', url: '/api/clip/narration/save', auth: `Bearer ${hs.token}`, body: '{"a":1}' }]);
  } finally { server.close(); }
});

test('a rejection without a message reports the status; success returns the parsed body', async () => {
  const { server, hs } = await serve((req, res) => {
    res.writeHead(req.url === '/api/ok' ? 200 : 503, { 'Content-Type': 'application/json' });
    res.end(req.url === '/api/ok' ? '{"ok":true}' : '{}');
  });
  try {
    assert.deepEqual(await backendRequest(hs, '/api/ok'), { ok: true });
    await assert.rejects(backendRequest(hs, '/api/busy'), error => error.status === 503 && error.message === 'Backend HTTP 503');
    await assert.rejects(backendRequest(hs, 'http://elsewhere/api'), error => error.status === undefined && /Invalid backend route/.test(error.message));
  } finally { server.close(); }
});

test('empty and non-JSON bodies keep their HTTP status instead of becoming a parse error', async () => {
  const { server, hs } = await serve((req, res) => {
    if (req.url === '/api/empty') { res.writeHead(400); res.end(); }
    else if (req.url === '/api/text') { res.writeHead(502, { 'Content-Type': 'text/plain' }); res.end('Bad gateway'); }
    else if (req.url === '/api/no-content') { res.writeHead(200); res.end(); }
    else { res.writeHead(200, { 'Content-Type': 'text/html' }); res.end('<html>'); }
  });
  try {
    await assert.rejects(backendRequest(hs, '/api/empty'), error => error.status === 400 && error.message === 'Backend HTTP 400');
    await assert.rejects(backendRequest(hs, '/api/text'), error => error.status === 502 && error.message === 'Backend HTTP 502');
    assert.equal(await backendRequest(hs, '/api/no-content'), null);
    await assert.rejects(backendRequest(hs, '/api/html'), error => error.status === 200 && /unreadable/.test(error.message));
  } finally { server.close(); }
});

test('an empty 400 is still a definitive refusal, so the rejected voice file is removed', async () => {
  const { server, hs } = await serve((req, res) => { req.resume(); req.on('end', () => { res.writeHead(400); res.end(); }); });
  const base = await mkdtemp(path.join(os.tmpdir(), 'Revu.TransportNarration.'));
  try {
    const root = path.join(base, 'Narration');
    const bytes = new Uint8Array(2048);
    bytes.set([0x1A, 0x45, 0xDF, 0xA3]);
    const meta = { gameId: 1, bookmarkId: 2, mimeType: 'audio/webm', offsetMs: 0, durationMs: 1500,
      gameVolume: 0.8, narrationVolume: 1, duck: true };
    const backend = { request: (route, options) => backendRequest(hs, route, options) };
    await assert.rejects(saveNarration({ root, backend, bytes, meta }), error => error.status === 400);
    assert.deepEqual(await readdir(root), []);
  } finally { server.close(); await rm(base, { recursive: true, force: true }); }
});
