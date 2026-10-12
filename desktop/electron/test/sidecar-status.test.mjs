import assert from 'node:assert/strict';
import { mkdtemp, readdir, readFile, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createRollingLog } from '../logging.mjs';
import { saveNarration } from '../narration.mjs';
import { backendRequest, isStartingHandshake, readHealth, Sidecar } from '../sidecar.mjs';

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

test('a degraded health probe is answered with the authenticated diagnosis instead of a timeout', async () => {
  const diagnosis = "Revu couldn't open its database: the file revu.db-shm is marked read-only (C:\\data\\revu.db-shm).";
  const seen = [];
  const { server, hs } = await serve((req, res) => {
    seen.push([req.url, req.headers.authorization]);
    res.writeHead(req.url === '/api/health' ? 503 : 200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(req.url === '/api/health' ? { status: 'degraded' } : { status: 'degraded', error: diagnosis }));
  });
  try {
    assert.deepEqual(await readHealth(route => backendRequest(hs, route)), { status: 'degraded', error: diagnosis });
    assert.deepEqual(seen.map(([url]) => url), ['/api/health', '/api/host/health']);
    assert.equal(seen[1][1], `Bearer ${hs.token}`);
  } finally { server.close(); }
  // Ready needs no detail; a refused detail keeps a generic sentence; other failures keep polling.
  assert.deepEqual(await readHealth(async () => ({ status: 'ready' })), { status: 'ready' });
  const unavailable = Object.assign(new Error('Backend HTTP 503'), { status: 503 });
  assert.deepEqual(await readHealth(async route => { if (route === '/api/health') throw unavailable; throw new Error('Unauthorized'); }),
    { status: 'degraded', error: "Revu couldn't open its database." });
  await assert.rejects(readHealth(async () => { throw new Error('ECONNREFUSED'); }), /ECONNREFUSED/);
});

test('only this launch can announce that it is preparing storage', () => {
  const identity = { launchId: 'launch', processId: 41 };
  const marker = { state: 'starting', apiVersion: 1, launchId: 'launch', processId: 41 };
  assert.equal(isStartingHandshake(marker, identity), true);
  for (const change of [{ state: 'ready' }, { launchId: 'other' }, { processId: 42 }, { apiVersion: 2 }])
    assert.equal(isStartingHandshake({ ...marker, ...change }, identity), false);
  assert.equal(isStartingHandshake(null, identity), false);
});

test('a backend that exits during startup leaves its stderr and exit code in the logs', { skip: process.platform !== 'win32' }, async () => {
  // findstr without arguments reports a usage error on stderr and exits, like a crashing backend.
  const executable = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'findstr.exe');
  const base = await mkdtemp(path.join(os.tmpdir(), 'Revu.SidecarLogs.'));
  try {
    const hostLog = createRollingLog({ name: 'host' }), stderrLog = createRollingLog({ name: 'sidecar-stderr' });
    hostLog.open(path.join(base, 'logs'));
    stderrLog.open(path.join(base, 'logs'));
    const sidecar = new Sidecar({ dataRoot: base, executable, isolated: true, log: hostLog, stderrLog });
    await assert.rejects(sidecar.start(), /exited before becoming ready/);
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.match(await readFile(stderrLog.file, 'utf8'), /FINDSTR/i);
    const host = await readFile(hostLog.file, 'utf8');
    assert.match(host, /INFO  Sidecar started \(process \d+\)/);
    assert.match(host, /Sidecar exited \(code \d+, signal none\)/);
  } finally { await rm(base, { recursive: true, force: true }); }
});
