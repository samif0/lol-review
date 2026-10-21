import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { RecordingService } from '../recording-service.mjs';
import { registrationRejected } from '../recording-registration.mjs';
import { fixture, settle, start } from './recording-fixture.mjs';

const endMatch = async f => { f.context.lastEndedGameId = 501; await f.service.tick(); await settle(f.service); };
const failRegistration = (f, answer) => {
  const original = f.service.backend.request, attempts = [];
  f.service.backend.request = async (route, options) => {
    if (!route.endsWith('/register')) return original(route, options);
    attempts.push(options.body);
    throw Object.assign(new Error('rejected'), await answer(options.body));
  };
  return attempts;
};

test('a failed process check before recording is retried for 30 s, then only that match is skipped', async t => {
  const f = await fixture(t);
  let checks = 0;
  f.driver.prepareStart = async () => {
    checks++; throw Object.assign(new Error('inspection'), { code: 'game-process-inspection-unavailable' });
  };
  await start(f);
  assert.equal(f.service.state, 'starting');
  f.advance(29_000); await f.service.tick();
  assert.equal(f.service.state, 'starting');
  f.advance(2000); await f.service.tick();
  assert.equal(f.service.state, 'failed');
  const tried = checks;
  f.advance(5000); await f.service.tick();
  assert.equal(checks, tried, 'the rest of this match is not retried');
  assert.deepEqual(f.calls, []);
  assert.equal(f.service.settings.enabled, true, 'recording stays on for the next match');
});

test('a slow helper exit after its final result still attaches the recording', async t => {
  const f = await fixture(t); await start(f);
  f.driver.stop = async () => { f.callback(); throw Object.assign(new Error('teardown'), { code: 'recorder-stop-failed' }); };
  await endMatch(f);
  assert.equal(f.registrations.length, 1);
  assert.equal(f.registrations[0].complete, true);
  assert.equal(f.service.blocked, false);
  assert.equal(f.service.state, 'saved');
});

test('a helper fault reported after the final result leaves that recording complete', async t => {
  const f = await fixture(t); await start(f);
  const validate = f.driver.validate;
  f.driver.validate = async (value, signal) => { f.service.unavailable('recorder-process-exited'); return validate(value, signal); };
  await endMatch(f);
  assert.equal(f.registrations.length, 1);
  assert.equal(f.registrations[0].complete, true);
});

test('registration answers: 409 is final, a 400 only once the file is gone or resized or after five in a row', async () => {
  const session = { filePath: path.resolve('capture.mp4'), fileSize: 7 };
  const same = async () => ({ size: 7 });
  assert.equal(await registrationRejected({ status: 409 }, { ...session }, same), true);
  for (const error of [new Error('lost reply'), { status: 503 }, { status: 500 }])
    assert.equal(await registrationRejected(error, { ...session }, same), false);
  const settling = { ...session };
  for (let answer = 1; answer < 5; answer++)
    assert.equal(await registrationRejected({ status: 400 }, settling, same), false, `400 number ${answer}`);
  assert.equal(await registrationRejected({ status: 400 }, settling, same), true, 'the fifth 400 in a row is final');
  const failing = code => async () => { throw Object.assign(new Error(code), { code }); };
  assert.equal(await registrationRejected({ status: 400 }, { ...session }, failing('ENOENT')), true);
  assert.equal(await registrationRejected({ status: 400 }, { ...session }, async () => ({ size: 9 })), true);
  assert.equal(await registrationRejected({ status: 400 }, { ...session }, failing('EBUSY')), false);
});

test('a conflicting registration ends its retries and keeps the files', async t => {
  const f = await fixture(t); await start(f);
  const attempts = failRegistration(f, () => ({ status: 409 }));
  await endMatch(f);
  f.advance(120_000); await f.service.tick();
  assert.equal(attempts.length, 1);
  assert.equal(f.service.pending.size, 0);
  assert.equal(f.service.state, 'partial');
  const metadata = JSON.parse(await readFile(path.join(attempts[0].filePath, '../session.json'), 'utf8'));
  assert.equal(metadata.reason, 'registration-rejected'); assert.equal(metadata.registrationError, 409);
  assert.equal(await readFile(attempts[0].filePath, 'utf8'), 'fixture');
});

test('a 400 for a file that still exists is retried; once the file is gone it is final', async t => {
  const f = await fixture(t); await start(f);
  let gone = false;
  const attempts = failRegistration(f, async body => { if (gone) await rm(body.filePath, { force: true }); return { status: 400 }; });
  await endMatch(f);
  assert.equal(f.service.pending.size, 1, 'a briefly locked or settling file is retried');
  gone = true; f.advance(5000); await f.service.tick();
  assert.equal(attempts.length, 2);
  assert.equal(f.service.pending.size, 0);
  f.advance(120_000); await f.service.tick();
  assert.equal(attempts.length, 2);
});

test('recording turned off after a fault says why, keeps the reason across a restart, and a deliberate save clears it', async t => {
  const f = await fixture(t); await start(f);
  await f.service.turnOffAfterFault('recorder-heartbeat-lost'); await settle(f.service);
  assert.equal(f.service.settings.enabled, false);
  assert.match(f.service.message, /turned off after an error/);
  assert.doesNotMatch(f.service.message, /preference is saved/i);
  const faultFile = path.join(f.service.profile, 'recording-fault.json');
  assert.equal(JSON.parse(await readFile(faultFile, 'utf8')).code, 'recorder-heartbeat-lost');
  const next = new RecordingService({ root: f.service.root, profile: f.service.profile, backend: f.service.backend,
    space: async () => 10 * 1024 ** 3 });
  t.after(() => next.shutdown());
  await next.initialize();
  assert.match(next.message, /turned off after an error in an earlier session/);
  await next.saveSettings({ enabled: true, preset: '720p30' });
  await assert.rejects(readFile(faultFile), { code: 'ENOENT' });
});
