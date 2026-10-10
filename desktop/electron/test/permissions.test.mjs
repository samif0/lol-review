import assert from 'node:assert/strict';
import test from 'node:test';
import { allowMediaCheck, allowMediaRequest, registerProtocols } from '../protocols.mjs';

const top = { getURL: () => 'revu-app://ui/index.html' };
const request = (overrides = {}) => ({ mediaTypes: ['audio'], securityOrigin: 'revu-app://ui',
  requestingUrl: 'revu-app://ui/index.html', ...overrides });

test('microphone capture is granted to the app document and its same-origin page iframe', () => {
  assert.equal(allowMediaRequest(top, 'media', request()), true);
  assert.equal(allowMediaRequest(top, 'media', request({ requestingUrl: 'revu-app://ui/vodplayer.html?gameId=1',
    securityOrigin: 'revu-app://ui' })), true);
  assert.equal(allowMediaCheck(top, 'media', 'revu-app://ui', { mediaType: 'audio' }), true);
  assert.equal(allowMediaCheck(top, 'media', 'revu-app://ui', { mediaType: 'audio',
    securityOrigin: 'revu-app://ui', embeddingOrigin: 'revu-app://ui' }), true);
});

test('video, mixed or missing media types and foreign origins are denied', () => {
  for (const mediaTypes of [['video'], ['audio', 'video'], [], undefined, 'audio', [null]])
    assert.equal(allowMediaRequest(top, 'media', request({ mediaTypes })), false, String(mediaTypes));
  assert.equal(allowMediaRequest(top, 'media', request({ securityOrigin: 'https://evil.example' })), false);
  for (const requestingUrl of ['about:srcdoc', 'revu-app://evil/x', 'https://revu.lol/', undefined])
    assert.equal(allowMediaRequest(top, 'media', request({ requestingUrl })), false, requestingUrl);
  assert.equal(allowMediaRequest(null, 'media', request()), false);
  assert.equal(allowMediaRequest({ getURL: () => 'https://evil.example/' }, 'media', request()), false);
  assert.equal(allowMediaRequest(top, 'media', undefined), false);
  for (const mediaType of ['video', 'unknown', undefined])
    assert.equal(allowMediaCheck(top, 'media', 'revu-app://ui', { mediaType }), false, String(mediaType));
  assert.equal(allowMediaCheck(top, 'media', 'revu-app://ui', { mediaType: 'audio', embeddingOrigin: 'https://evil.example' }), false);
  assert.equal(allowMediaCheck(top, 'media', 'revu-app://ui', { mediaType: 'audio', securityOrigin: 'https://evil.example' }), false);
  assert.equal(allowMediaCheck(top, 'media', 'https://evil.example', { mediaType: 'audio' }), false);
  assert.equal(allowMediaCheck(null, 'media', 'revu-app://ui', { mediaType: 'audio' }), false);
});

test('every other renderer permission stays denied', () => {
  for (const permission of ['display-capture', 'speaker-selection', 'fullscreen', 'notifications', 'clipboard-read',
    'geolocation', 'openExternal', 'midi', 'pointerLock']) {
    assert.equal(allowMediaRequest(top, permission, request()), false, permission);
    assert.equal(allowMediaCheck(top, permission, 'revu-app://ui', { mediaType: 'audio' }), false, permission);
  }
});

test('the session handlers delegate to the same predicates and add no capture handlers', () => {
  const installed = {};
  const session = {
    setPermissionRequestHandler(fn) { installed.request = fn; },
    setPermissionCheckHandler(fn) { installed.check = fn; },
    setDisplayMediaRequestHandler() { assert.fail('display capture must not be configured'); },
    webRequest: { onBeforeRequest() {} },
  };
  registerProtocols({ protocol: { handle() {} }, session, uiDirectory: '.', media: {} });
  const answers = [];
  installed.request(top, 'media', allowed => answers.push(allowed), request());
  installed.request(top, 'media', allowed => answers.push(allowed), request({ mediaTypes: ['video'] }));
  installed.request(top, 'display-capture', allowed => answers.push(allowed), request());
  assert.deepEqual(answers, [true, false, false]);
  assert.equal(installed.check(top, 'media', 'revu-app://ui', { mediaType: 'audio' }), true);
  assert.equal(installed.check(top, 'notifications', 'revu-app://ui', {}), false);
});
