import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import test from 'node:test';
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
