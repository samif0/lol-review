import { RATE_CHOICES, STEP_CHOICES } from './vodtransport.js';
import { hasExplicitVodTarget } from './match-navigation.mjs';
export { hasExplicitVodTarget } from './match-navigation.mjs';

const bounded = (value, min, max, fallback) => typeof value === 'number' && Number.isFinite(value)
  ? Math.max(min, Math.min(max, value)) : fallback;
const text = value => typeof value === 'string' ? value.slice(0, 32768) : '';

// Restore as soon as the fresh view renders. Later metadata and media errors must
// not restore again over edits made while the recording was still loading.
export function createVodViewRestorer(restore) {
  let completion = null, ready = false;
  return {
    get ready() { return ready; },
    finish() {
      completion ??= Promise.resolve().then(restore).then(result => { ready = true; return result; });
      return completion;
    },
  };
}

// Track the whole save, including the resulting draft cleanup and UI refresh.
// Repeated keyboard submissions share the same operation instead of adding rows.
export function createVodWriteBarrier() {
  const pending = new Map();
  return {
    get pending() { return pending.size > 0; },
    run(key, operation) {
      if (pending.has(key)) return pending.get(key);
      const job = Promise.resolve().then(operation);
      pending.set(key, job);
      const finished = () => { if (pending.get(key) === job) pending.delete(key); };
      job.then(finished, finished);
      return job;
    },
    async flush() {
      while (pending.size) await Promise.allSettled([...pending.values()]);
    },
  };
}

export function sameVodDraft(current, submitted) {
  return JSON.stringify(current) === JSON.stringify(submitted);
}

// Session data is a UI buffer, never a write request. Match and asset identity
// are checked again against the newly loaded snapshot before resuming playback.
export function vodRestorePlan(state, { gameId, filePath, mediaDuration, gameDuration, objectiveIds = [], search = '' }) {
  if (!state || state.schema !== 1 || !(gameId > 0) || state.gameId !== gameId) return null;
  const explicit = hasExplicitVodTarget(search);
  const sameAsset = typeof filePath === 'string' && filePath.length > 0 && state.filePath === filePath;
  const validDimension = value => Number.isInteger(value) && value >= 16 && value <= 16384;
  const maxMediaTime = Number.isFinite(mediaDuration) && mediaDuration > 0 ? mediaDuration : 86400;
  const maxGameTime = Number.isFinite(gameDuration) && gameDuration > 0 ? gameDuration : 86400;
  const clipPoint = value => typeof value === 'number' && value >= 0
    ? bounded(value, 0, maxGameTime, -1) : -1;
  return {
    explicit,
    videoSize: sameAsset && validDimension(state.videoWidth) && validDimension(state.videoHeight)
      ? { width: state.videoWidth, height: state.videoHeight } : null,
    mediaTime: sameAsset && !explicit ? bounded(state.mediaTime, 0, maxMediaTime, null) : null,
    step: STEP_CHOICES.includes(state.step) ? state.step : 5,
    rate: RATE_CHOICES.includes(state.rate) ? state.rate : 1,
    muted: state.muted === true,
    volume: bounded(state.volume, 0, 1, 1),
    focusedObjectiveId: !explicit && objectiveIds.includes(state.focusedObjectiveId) ? state.focusedObjectiveId : null,
    filter: !explicit && ['auto', 'clips', 'bm'].includes(state.filter) ? state.filter : null,
    zoom: !explicit ? bounded(state.zoom, 1, 12, 1) : 1,
    pan: !explicit ? bounded(state.pan, -1000000, 0, 0) : 0,
    clip: {
      start: clipPoint(state.clip?.start), end: clipPoint(state.clip?.end),
      quality: ['good', 'neutral', 'bad'].includes(state.clip?.quality) ? state.clip.quality : '',
      note: text(state.clip?.note), picker: text(state.clip?.picker), userSet: state.clip?.userSet === true,
    },
    bookmark: { time: bounded(state.bookmark?.time, 0, maxGameTime, null),
      note: text(state.bookmark?.note), picker: text(state.bookmark?.picker), userSet: state.bookmark?.userSet === true },
    corrections: !explicit ? state.corrections : null,
  };
}
