// Audio device preferences shared by Settings, the narration studio and every
// player. They are renderer-local: every page loads from the same app origin, so
// one localStorage entry reaches all of them and the sidecar config is untouched.
// An absent or empty choice means the system default. Nothing here throws: a
// blocked storage, a missing mediaDevices or a failed setSinkId degrades to the
// system default.

export const AUDIO_PREF_KEYS = Object.freeze({ input: 'revu.narration.mic', output: 'revu.playback.output' });
export const AUDIO_COPY = Object.freeze({
  systemDefault: 'System default',
  missing: 'Saved device is not connected. Using the system default.',
});
// Chromium lists the Windows default endpoint as a virtual "default" device. It is
// the same choice as "System default", so it is never stored or listed twice.
export const DEFAULT_DEVICE_ID = 'default';
const KINDS = Object.freeze({ input: 'audioinput', output: 'audiooutput' });

const attempt = (fn, fallback = null) => { try { return fn(); } catch (_) { return fallback; } };
const whichOf = value => (value === 'output' || value === KINDS.output ? 'output' : 'input');
const isDefaultId = id => typeof id !== 'string' || !id || id === DEFAULT_DEVICE_ID;
const normalizeLabel = label => (typeof label === 'string' ? label.trim().toLowerCase() : '');

// Tests and Settings pass their own storage, mediaDevices and event target; pages
// use the browser globals, read lazily because reading localStorage can throw.
function io(options = {}) {
  const has = name => Object.prototype.hasOwnProperty.call(options, name);
  return {
    storage: has('storage') ? options.storage : attempt(() => globalThis.localStorage),
    mediaDevices: has('mediaDevices') ? options.mediaDevices : attempt(() => globalThis.navigator?.mediaDevices),
    target: has('target') ? options.target : attempt(() => globalThis.window),
  };
}

// ── preferences ──────────────────────────────────────────────────────────────
/** The saved device for 'input' or 'output', or null for the system default. */
export function readAudioPref(which, options) {
  const { storage } = io(options);
  const raw = attempt(() => storage?.getItem(AUDIO_PREF_KEYS[whichOf(which)]));
  if (typeof raw !== 'string' || !raw) return null;
  const value = attempt(() => JSON.parse(raw));
  if (!value || typeof value !== 'object' || isDefaultId(value.deviceId)) return null;
  return { deviceId: value.deviceId, label: typeof value.label === 'string' ? value.label : '' };
}

/** Save a choice ({deviceId,label}; null or '' = system default). Returns false if storage refused it. */
export function saveAudioPref(which, device, options) {
  const kind = whichOf(which);
  const { storage, target } = io(options);
  const deviceId = isDefaultId(device?.deviceId) ? '' : device.deviceId;
  const label = deviceId && typeof device.label === 'string' ? device.label : '';
  try { storage.setItem(AUDIO_PREF_KEYS[kind], JSON.stringify({ deviceId, label })); }
  catch (_) { return false; }
  // 'storage' events reach only other documents; tell this one directly.
  attempt(() => target?.dispatchEvent?.(changeEvent(kind)));
  return true;
}

// ── devices ──────────────────────────────────────────────────────────────────
/**
 * Concrete devices of one kind ('input'/'audioinput' or 'output'/'audiooutput').
 * labelsHidden: Chromium hides names and ids until the page may use a microphone.
 * available: false when this host cannot enumerate devices at all.
 */
export async function listAudioDevices(kind, options) {
  const type = KINDS[whichOf(kind)];
  const { mediaDevices } = io(options);
  const none = { devices: [], labelsHidden: false, available: false };
  if (typeof mediaDevices?.enumerateDevices !== 'function') return none;
  let all;
  try { all = await mediaDevices.enumerateDevices(); } catch (_) { return none; }
  const ofKind = (Array.isArray(all) ? all : []).filter(device => device?.kind === type);
  const seen = new Set(), devices = [];
  for (const device of ofKind) {
    if (isDefaultId(device.deviceId) || seen.has(device.deviceId)) continue;
    seen.add(device.deviceId);
    devices.push({ deviceId: device.deviceId, label: typeof device.label === 'string' ? device.label : '',
      groupId: typeof device.groupId === 'string' ? device.groupId : '' });
  }
  // The Windows communications endpoint is another virtual device; list it last.
  devices.sort((a, b) => (a.deviceId === 'communications') - (b.deviceId === 'communications'));
  return { devices, labelsHidden: ofKind.some(device => !device.label), available: true };
}

/**
 * Match a saved choice against the connected devices: by id, then by name (ids can
 * change), else the system default with missing=true. When the list is unknown or
 * names are hidden the saved id is used unverified, and the caller falls back if
 * the device refuses.
 */
export function resolveSavedDevice(saved, devices, { labelsHidden = false } = {}) {
  if (!saved || isDefaultId(saved.deviceId)) return { deviceId: '', label: '', matchedBy: 'default', missing: false };
  const list = Array.isArray(devices) ? devices : null;
  const byId = list?.find(device => device.deviceId === saved.deviceId);
  if (byId) return { deviceId: byId.deviceId, label: byId.label || saved.label || '', matchedBy: 'id', missing: false };
  const label = normalizeLabel(saved.label);
  const byLabel = label ? list?.find(device => normalizeLabel(device.label) === label) : null;
  if (byLabel) return { deviceId: byLabel.deviceId, label: byLabel.label, matchedBy: 'label', missing: false };
  if (!list || labelsHidden) return { deviceId: saved.deviceId, label: saved.label || '', matchedBy: 'unverified', missing: false };
  return { deviceId: '', label: '', matchedBy: 'default', missing: true };
}

/** Resolve the saved choice for 'input' or 'output' against the devices connected now. */
export async function resolveAudioDevice(which, options) {
  const saved = readAudioPref(which, options);
  if (!saved) return { ...resolveSavedDevice(null), saved: null };
  const list = await listAudioDevices(which, options);
  return { ...resolveSavedDevice(saved, list.available ? list.devices : null, list), saved };
}

// ── output routing ───────────────────────────────────────────────────────────
let warned = false;
function warnOnce(error) {
  if (warned) return;
  warned = true;
  attempt(() => console.warn('[audio] Could not use the chosen output device. Playing on the system default.',
    error?.name || String(error)));
}
const currentSink = target => (typeof target.sinkId === 'string' ? target.sinkId : target.sinkId == null ? '' : null);
const queues = new WeakMap();

/**
 * Route a media element or AudioContext to the saved output device (or
 * options.resolved). Resolves { ok, sinkId, fallback }: ok=false when the target
 * cannot switch outputs, fallback=true when the system default plays instead of
 * the chosen device. Calls on one target run in order; this never rejects.
 */
export function applyOutputSink(target, options = {}) {
  if (!target || typeof target.setSinkId !== 'function') return Promise.resolve({ ok: false, sinkId: null, fallback: false });
  const run = async () => {
    const resolved = options.resolved || await resolveAudioDevice('output', options);
    const wanted = resolved.deviceId || '';
    if (currentSink(target) === wanted) return { ok: true, sinkId: wanted, fallback: !!resolved.missing };
    try {
      await target.setSinkId(wanted);
      return { ok: true, sinkId: wanted, fallback: !!resolved.missing };
    } catch (error) {
      warnOnce(error);
      if (wanted && currentSink(target) !== '') { try { await target.setSinkId(''); } catch (_) { /* keeps its current output */ } }
      return { ok: false, sinkId: currentSink(target), fallback: !!wanted };
    }
  };
  const next = (queues.get(target) || Promise.resolve()).then(run, run)
    .catch(() => ({ ok: false, sinkId: null, fallback: false }));
  queues.set(target, next);
  return next;
}

/**
 * Keep media elements on the saved output: apply now and again whenever the
 * preference or the connected devices change. A vanished device falls back to the
 * system default. Returns a function that stops following changes.
 */
export function bindOutputSink(targets, options = {}) {
  const list = [targets].flat().filter(target => target && typeof target.setSinkId === 'function');
  if (!list.length) return () => {};
  let generation = 0, disposed = false;
  const apply = async () => {
    const current = ++generation;
    try {
      const resolved = await resolveAudioDevice('output', options);
      if (disposed || current !== generation) return;
      await Promise.all(list.map(target => applyOutputSink(target, { ...options, resolved })));
    } catch (_) { /* each target keeps its current output */ }
  };
  void apply();
  const stop = onAudioPrefsChange(change => { if (change.which !== 'input') void apply(); }, options);
  return () => { disposed = true; stop(); };
}

// ── change notifications ─────────────────────────────────────────────────────
// 'storage' events reach other documents (Settings and a player in another
// window); a save in this document dispatches SAVED_EVENT on its own window.
const SAVED_EVENT = 'revu:audio-prefs';
function changeEvent(which) {
  const detail = { which };
  return typeof globalThis.CustomEvent === 'function' ? new globalThis.CustomEvent(SAVED_EVENT, { detail })
    : Object.assign(new Event(SAVED_EVENT), { detail });
}

/**
 * callback({ which: 'input'|'output'|'both', reason: 'saved'|'storage'|'devicechange' })
 * on a preference change in this or another page, or when devices are plugged in or
 * removed. Returns an unsubscribe function.
 */
export function onAudioPrefsChange(callback, options) {
  if (typeof callback !== 'function') return () => {};
  const { target, mediaDevices } = io(options);
  const call = change => attempt(() => callback(change));
  const onStorage = event => {
    if (event?.key === null) { call({ which: 'both', reason: 'storage' }); return; } // storage.clear()
    const hit = Object.entries(AUDIO_PREF_KEYS).find(([, key]) => key === event?.key);
    if (hit) call({ which: hit[0], reason: 'storage' });
  };
  const onDevices = () => call({ which: 'both', reason: 'devicechange' });
  const onSaved = event => call({ which: event?.detail?.which === 'output' ? 'output' : 'input', reason: 'saved' });
  attempt(() => target?.addEventListener?.(SAVED_EVENT, onSaved));
  attempt(() => target?.addEventListener?.('storage', onStorage));
  attempt(() => mediaDevices?.addEventListener?.('devicechange', onDevices));
  let active = true;
  return () => {
    if (!active) return;
    active = false;
    attempt(() => target?.removeEventListener?.(SAVED_EVENT, onSaved));
    attempt(() => target?.removeEventListener?.('storage', onStorage));
    attempt(() => mediaDevices?.removeEventListener?.('devicechange', onDevices));
  };
}
