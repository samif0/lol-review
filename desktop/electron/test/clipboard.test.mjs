import assert from 'node:assert/strict';
import test from 'node:test';
import { nativeCommands } from '../native.mjs';
import { commandRequest, validateSender } from '../routing.mjs';

test('native copy writes exact review markdown through the injected clipboard and reports completion', async () => {
  const writes = [];
  const native = nativeCommands({ clipboard: { writeText: text => writes.push(text) } });
  const text = '# Ahri review\n\nWait for the next wave.\n한글 · 25% → repeat\n';
  const request = commandRequest('copy_text_to_clipboard', { text });
  assert.equal(request.method, 'NATIVE');
  assert.deepEqual(await native('copy_text_to_clipboard', { text }), { ok: true });
  assert.deepEqual(writes, [text]);
});

test('a failed clipboard write rejects instead of returning a copied acknowledgement', async () => {
  const failure = new Error('Synthetic clipboard failure');
  const native = nativeCommands({ clipboard: { writeText: () => { throw failure; } } });
  await assert.rejects(native('copy_text_to_clipboard', { text: '# Review' }), error => error === failure);
});

test('copy retains trusted main-frame sender validation and accepts only a text argument', () => {
  const mainFrame = { url: 'revu-app://ui/index.html' };
  const contents = { mainFrame };
  validateSender({ sender: contents, senderFrame: mainFrame }, contents);
  for (const event of [
    { sender: {}, senderFrame: mainFrame },
    { sender: contents, senderFrame: { url: 'revu-app://ui/review.html' } },
    { sender: contents, senderFrame: { url: 'https://example.invalid/' } },
  ]) assert.throws(() => validateSender(event, contents), /Untrusted desktop sender/);
  for (const args of [{}, { text: null }, { text: 1 }, { text: [] }, { text: 'Review', html: '<b>Review</b>' }])
    assert.throws(() => commandRequest('copy_text_to_clipboard', args));
  assert.throws(() => commandRequest('read_clipboard'), /Unsupported desktop command/);
});

test('copy supports large review exports but rejects serialized payloads over 4 MiB', () => {
  assert.equal(commandRequest('copy_text_to_clipboard', { text: 'x'.repeat(64 * 1024) }).method, 'NATIVE');
  const limit = 4 * 1024 * 1024;
  const envelopeBytes = Buffer.byteLength(JSON.stringify({ text: '' }));
  assert.equal(commandRequest('copy_text_to_clipboard', { text: 'x'.repeat(limit - envelopeBytes) }).method, 'NATIVE');
  assert.throws(() => commandRequest('copy_text_to_clipboard', { text: 'x'.repeat(limit - envelopeBytes + 1) }), /payload limit/);
  assert.throws(() => commandRequest('copy_text_to_clipboard', { text: '한'.repeat(limit / 2) }), /payload limit/);
  assert.throws(() => commandRequest('save_config', { payload: { text: 'x'.repeat(64 * 1024) } }), /payload limit/);
});
