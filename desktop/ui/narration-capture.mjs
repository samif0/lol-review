// Pure narration-studio logic: timing, recorder setup, microphone error copy and
// the studio state machine. No DOM access, so it runs under node:test.

export const NARRATION_OFFSET_LIMIT_MS = 10000;
export const MIN_TAKE_MS = 1000;
export const MAX_TAKE_MS = 625000;

export const MIC_COPY = Object.freeze({
  denied: 'Windows is blocking microphone access for desktop apps. Turn it on in Settings, then try again.',
  missing: 'No microphone found. Plug one in and try again.',
  busy: 'Your microphone is busy in another app, or Windows blocked it.',
});

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

// Clip time (ms) of narration sample 0. The recorder starts before playback, so
// the first presented frame shows clip time T at a later perf instant: sample 0
// sits at T minus the gap. A negative value means the voice began before the clip.
export function computeOffsetMs({ recorderStartPerfMs, frameMediaTimeS, frameExpectedDisplayPerfMs }) {
  const value = Math.round(frameMediaTimeS * 1000 - (frameExpectedDisplayPerfMs - recorderStartPerfMs));
  if (!Number.isFinite(value)) return 0;
  return clamp(value, -NARRATION_OFFSET_LIMIT_MS, NARRATION_OFFSET_LIMIT_MS);
}

// Reaching the end of the clip pauses the video before `ended`; that pause is not
// a stop-and-keep interruption.
export function isEndOfClipPause(video) {
  if (!video) return false;
  return !!video.ended || (Number.isFinite(video.duration) && video.currentTime >= video.duration - 0.25);
}

export function pickMimeType(MediaRecorderCtor) {
  for (const type of ['audio/webm;codecs=opus', 'audio/webm']) {
    try { if (MediaRecorderCtor?.isTypeSupported?.(type)) return type; } catch (_) { /* try the next type */ }
  }
  return '';
}

// null means "retry with the default device" (a saved device disappeared).
export function micErrorMessage(err, accessStatus) {
  if (accessStatus === 'denied') return MIC_COPY.denied;
  const name = err?.name || '';
  if (name === 'OverconstrainedError') return null;
  if (name === 'NotFoundError' || name === 'DevicesNotFoundError') return MIC_COPY.missing;
  return MIC_COPY.busy; // NotReadableError, NotAllowedError and anything unexpected
}

const TRANSITIONS = Object.freeze({
  record: { setup: 'countdown', watching: 'countdown' },
  started: { countdown: 'recording' },
  stop: { recording: 'saving' },
  tooShort: { recording: 'setup' },
  discard: { countdown: 'setup', recording: 'setup' },
  saved: { saving: 'watching' },
  fail: { setup: 'error', countdown: 'error', recording: 'error', saving: 'error', watching: 'error' },
  retry: { error: 'setup' },
  // Record again or Remove narration leave the narrated preview for the recorder.
  reset: { watching: 'setup' },
});
export const STUDIO_STATES = Object.freeze(['setup', 'countdown', 'recording', 'saving', 'watching', 'error']);

export function createStudioStateMachine(initial = 'setup') {
  if (!STUDIO_STATES.includes(initial)) throw new Error(`Unknown studio state: ${initial}`);
  let state = initial;
  return {
    get state() { return state; },
    can(event) { return !!TRANSITIONS[event]?.[state]; },
    // Returns the new state, or null (and changes nothing) for an illegal event.
    send(event) {
      const next = TRANSITIONS[event]?.[state];
      if (!next) return null;
      state = next;
      return state;
    },
  };
}

export function isActiveWork(state) {
  return state === 'countdown' || state === 'recording' || state === 'saving';
}

export function rmsLevel(samples) {
  if (!samples || !samples.length) return 0;
  let sum = 0;
  for (let i = 0; i < samples.length; i++) {
    const value = Number(samples[i]) || 0;
    sum += value * value;
  }
  const rms = Math.sqrt(sum / samples.length);
  return Number.isFinite(rms) ? clamp(rms, 0, 1) : 0;
}

export function formatClock(seconds) {
  const s = Math.max(0, Math.floor(Number(seconds) || 0));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

export function formatSync(offsetMs) {
  const value = Math.round(Number(offsetMs) || 0);
  return `Sync: ${value >= 0 ? '+' : ''}${value} ms`;
}

export function clampOffset(offsetMs) {
  return clamp(Math.round(Number(offsetMs) || 0), -NARRATION_OFFSET_LIMIT_MS, NARRATION_OFFSET_LIMIT_MS);
}
