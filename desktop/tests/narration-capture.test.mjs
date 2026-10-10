import test from 'node:test';
import assert from 'node:assert/strict';
import { clampOffset, computeOffsetMs, createStudioStateMachine, formatClock, formatSync, isActiveWork, isEndOfClipPause,
  micErrorMessage, MIC_COPY, pickMimeType, rmsLevel, STUDIO_STATES } from '../ui/narration-capture.mjs';

test('offset is the clip time of narration sample 0, negative when recording began before playback', () => {
  // Recorder started at perf 1000; the first frame (clip 0.040 s) displays at perf 1300.
  assert.equal(computeOffsetMs({ recorderStartPerfMs: 1000, frameMediaTimeS: 0.04, frameExpectedDisplayPerfMs: 1300 }), -260);
  // A frame later in the clip than the elapsed recording time gives a positive offset.
  assert.equal(computeOffsetMs({ recorderStartPerfMs: 1000, frameMediaTimeS: 0.5, frameExpectedDisplayPerfMs: 1100 }), 400);
  assert.equal(computeOffsetMs({ recorderStartPerfMs: 1000, frameMediaTimeS: 0, frameExpectedDisplayPerfMs: 1000 }), 0);
  assert.equal(computeOffsetMs({ recorderStartPerfMs: 1000.4, frameMediaTimeS: 0.0333, frameExpectedDisplayPerfMs: 1000 }), 34);
  assert.equal(computeOffsetMs({ recorderStartPerfMs: 0, frameMediaTimeS: 0, frameExpectedDisplayPerfMs: 25000 }), -10000);
  assert.equal(computeOffsetMs({ recorderStartPerfMs: 0, frameMediaTimeS: 30, frameExpectedDisplayPerfMs: 0 }), 10000);
  assert.equal(computeOffsetMs({ recorderStartPerfMs: 0, frameMediaTimeS: NaN, frameExpectedDisplayPerfMs: 0 }), 0);
  assert.equal(clampOffset(10050), 10000); assert.equal(clampOffset(-10050), -10000); assert.equal(clampOffset(120.4), 120);
});

test('a pause at the end of the clip is ignored while a mid-clip pause stops the take', () => {
  assert.equal(isEndOfClipPause({ ended: true, currentTime: 3, duration: 10 }), true);
  assert.equal(isEndOfClipPause({ ended: false, currentTime: 9.8, duration: 10 }), true);
  assert.equal(isEndOfClipPause({ ended: false, currentTime: 10, duration: 10 }), true);
  assert.equal(isEndOfClipPause({ ended: false, currentTime: 9.7, duration: 10 }), false);
  assert.equal(isEndOfClipPause({ ended: false, currentTime: 4, duration: 10 }), false);
  assert.equal(isEndOfClipPause({ ended: false, currentTime: 4, duration: NaN }), false);
  assert.equal(isEndOfClipPause(null), false);
});

test('every studio transition follows the take lifecycle and illegal events change nothing', () => {
  const legal = {
    setup: { record: 'countdown', fail: 'error' },
    countdown: { started: 'recording', discard: 'setup', fail: 'error' },
    recording: { stop: 'saving', tooShort: 'setup', discard: 'setup', fail: 'error' },
    saving: { saved: 'watching', fail: 'error' },
    watching: { record: 'countdown', reset: 'setup', fail: 'error' },
    error: { retry: 'setup' },
  };
  const events = ['record', 'started', 'stop', 'tooShort', 'discard', 'saved', 'fail', 'retry', 'reset', 'bogus'];
  for (const state of STUDIO_STATES) {
    for (const event of events) {
      const sm = createStudioStateMachine(state);
      const expected = legal[state][event] ?? null;
      assert.equal(sm.can(event), expected !== null, `${state}.${event}`);
      assert.equal(sm.send(event), expected, `${state}.${event}`);
      assert.equal(sm.state, expected ?? state, `${state}.${event}`);
    }
  }
  assert.throws(() => createStudioStateMachine('closed'), /Unknown studio state/);
});

test('a full take, a stop-and-keep take and a too-short take walk the expected states', () => {
  const sm = createStudioStateMachine();
  assert.deepEqual(['record', 'started', 'stop', 'saved'].map(event => sm.send(event)), ['countdown', 'recording', 'saving', 'watching']);
  // Stop-and-keep (pause, seek, stall, hidden) uses the same stop event and keeps the audio.
  assert.deepEqual(['record', 'started', 'stop'].map(event => sm.send(event)), ['countdown', 'recording', 'saving']);
  assert.equal(sm.send('record'), null, 'a second take cannot start while saving');
  assert.equal(sm.send('fail'), 'error');
  assert.equal(sm.send('retry'), 'setup');
  assert.deepEqual(['record', 'started', 'tooShort'].map(event => sm.send(event)), ['countdown', 'recording', 'setup']);
  assert.deepEqual(['record', 'discard'].map(event => sm.send(event)), ['countdown', 'setup']);
});

test('only countdown, recording and saving count as active work', () => {
  assert.deepEqual(STUDIO_STATES.filter(isActiveWork), ['countdown', 'recording', 'saving']);
  assert.equal(isActiveWork('closed'), false);
});

test('microphone errors map to the documented copy, and a missing saved device retries the default', () => {
  assert.equal(micErrorMessage({ name: 'NotAllowedError' }, 'denied'), MIC_COPY.denied);
  assert.equal(micErrorMessage(null, 'denied'), 'Windows is blocking microphone access for desktop apps. Turn it on in Settings, then try again.');
  assert.equal(micErrorMessage({ name: 'NotFoundError' }, 'granted'), 'No microphone found. Plug one in and try again.');
  assert.equal(micErrorMessage({ name: 'NotReadableError' }, 'granted'), 'Your microphone is busy in another app, or Windows blocked it.');
  assert.equal(micErrorMessage({ name: 'NotAllowedError' }, 'unknown'), MIC_COPY.busy);
  assert.equal(micErrorMessage({ name: 'OverconstrainedError' }, 'granted'), null);
  assert.equal(micErrorMessage(new Error('anything'), 'granted'), MIC_COPY.busy);
  for (const copy of Object.values(MIC_COPY)) assert.doesNotMatch(copy, /—/);
});

test('recorder format prefers Opus WebM, then plain WebM, then the browser default', () => {
  const recorder = supported => ({ isTypeSupported: type => supported.includes(type) });
  assert.equal(pickMimeType(recorder(['audio/webm', 'audio/webm;codecs=opus'])), 'audio/webm;codecs=opus');
  assert.equal(pickMimeType(recorder(['audio/webm'])), 'audio/webm');
  assert.equal(pickMimeType(recorder([])), '');
  assert.equal(pickMimeType(undefined), '');
  assert.equal(pickMimeType({ isTypeSupported() { throw new Error('unsupported'); } }), '');
});

test('level, clock and sync readouts', () => {
  assert.equal(rmsLevel(new Float32Array(1024)), 0);
  assert.equal(rmsLevel(new Float32Array(1024).fill(0.5)), 0.5);
  assert.equal(rmsLevel(Float32Array.from([1, -1, 1, -1])), 1);
  assert.equal(rmsLevel(new Float32Array(8).fill(3)), 1);
  assert.equal(rmsLevel(Float32Array.from([NaN, 0])), 0);
  assert.equal(rmsLevel(null), 0); assert.equal(rmsLevel([]), 0);
  assert.equal(formatClock(0), '0:00'); assert.equal(formatClock(252.9), '4:12');
  assert.equal(formatClock(600), '10:00'); assert.equal(formatClock(-3), '0:00'); assert.equal(formatClock(NaN), '0:00');
  assert.equal(formatSync(120), 'Sync: +120 ms'); assert.equal(formatSync(-50), 'Sync: -50 ms'); assert.equal(formatSync(0), 'Sync: +0 ms');
});
