import { getInvoke as platformInvoke } from './platform/index.mjs';
import { applyOutputSink, AUDIO_COPY, listAudioDevices, onAudioPrefsChange, readAudioPref, resolveAudioDevice,
  resolveSavedDevice, saveAudioPref } from './audio-devices.mjs';
import { MIC_COPY, micErrorMessage, rmsLevel } from './narration-capture.mjs';

// Settings card "Audio devices". Choices are renderer-local preferences shared with
// the narration studio and the players (audio-devices.mjs). They save on change, so
// the card never joins the sidecar-backed dirty state or the leave guard. Opening
// Settings never opens the microphone: only "Test microphone" and "Show device
// names" may, and the names request closes its stream at once.

export const MIC_TEST_LIMIT_MS = 30000;
export const AUDIO_SETTINGS_COPY = Object.freeze({
  inputSaved: 'Narration microphone saved.',
  outputSaved: 'Playback output saved.',
  notSaved: 'Revu could not save this choice on this computer.',
  unavailable: 'Audio devices are not available here.',
  testStart: 'Test microphone',
  testStop: 'Stop test',
  testing: 'Speak to see the level. The test stops after 30 seconds.',
  testTimedOut: 'Microphone test stopped after 30 seconds.',
  tonePlayed: 'Test sound played.',
  toneFallback: 'The chosen output is not available. The test sound played on the system default.',
  toneFailed: 'Revu could not play a test sound.',
  namesShown: 'Device names are shown. The microphone is off.',
  notConnected: label => `${label || 'Saved device'} (not connected)`,
  fallbackName: (which, index) => `${which === 'input' ? 'Microphone' : 'Speaker'} ${index + 1}`,
});

const attempt = (fn, fallback = null) => { try { return fn(); } catch (_) { return fallback; } };
const stopTracks = stream => { for (const track of attempt(() => stream?.getTracks?.(), []) || []) attempt(() => track.stop()); };

export function initializeAudioSettings({
  document = globalThis.document,
  scope = globalThis.window,
  getInvoke = platformInvoke,
  storage = attempt(() => globalThis.localStorage),
  mediaDevices = attempt(() => globalThis.navigator?.mediaDevices),
  AudioContextCtor = attempt(() => scope?.AudioContext || globalThis.AudioContext),
  requestFrame = callback => scope.requestAnimationFrame(callback),
  cancelFrame = id => scope.cancelAnimationFrame(id),
  setTimer = (callback, ms) => scope.setTimeout(callback, ms),
  clearTimer = id => scope.clearTimeout(id),
  testLimitMs = MIC_TEST_LIMIT_MS,
} = {}) {
  const el = id => document?.getElementById(id) || null;
  const card = el('audio-settings');
  if (!card) return null;
  const io = { storage, mediaDevices, target: scope };
  const ui = { input: el('audioInput'), output: el('audioOutput'), test: el('audio-mic-test'), meter: el('audio-mic-meter'),
    level: el('audio-mic-level'), tone: el('audio-output-test'), names: el('audio-names'), showNames: el('audio-show-names'),
    status: el('audio-status'), missing: { input: el('audio-input-missing'), output: el('audio-output-missing') } };
  const labels = { input: new Map(), output: new Map() };
  let disposed = false, available = true, toneReady = false, refreshing = null, refreshAgain = false, statusTimer = null;
  let test = null, testOn = false, testToken = 0, toneBusy = false, namesBusy = false;

  function setStatus(text, tone = '', autoClear = false) {
    const status = ui.status;
    if (!status) return;
    if (statusTimer !== null) { clearTimer(statusTimer); statusTimer = null; }
    status.textContent = text || '';
    status.classList?.remove('good', 'bad');
    if (tone) status.classList?.add(tone);
    if (text && autoClear) statusTimer = setTimer(() => { statusTimer = null; setStatus(''); }, 2400);
  }
  function option(value, text) {
    const node = document.createElement('option');
    node.value = value;
    node.textContent = text;
    return node;
  }

  // ── device lists ──────────────────────────────────────────────────────────
  function fill(which, list) {
    const select = ui[which];
    if (!select) return;
    const saved = readAudioPref(which, io);
    const resolved = resolveSavedDevice(saved, list.devices, list);
    const names = labels[which];
    names.clear();
    const options = [option('', AUDIO_COPY.systemDefault)];
    list.devices.forEach((device, index) => {
      const text = device.label || AUDIO_SETTINGS_COPY.fallbackName(which, index);
      names.set(device.deviceId, device.label);
      options.push(option(device.deviceId, text));
    });
    const matched = resolved.matchedBy === 'id' || resolved.matchedBy === 'label';
    // Keep a saved device that is not connected selectable, so the choice survives
    // until it is plugged in again or the player picks another one.
    if (saved && !matched) {
      names.set(saved.deviceId, saved.label);
      options.push(option(saved.deviceId, resolved.missing ? AUDIO_SETTINGS_COPY.notConnected(saved.label) : saved.label
        || AUDIO_SETTINGS_COPY.fallbackName(which, list.devices.length)));
    }
    select.replaceChildren(...options);
    select.value = !saved ? '' : matched ? resolved.deviceId : saved.deviceId;
    select.disabled = !list.available;
    if (ui.missing[which]) ui.missing[which].hidden = !resolved.missing;
    // A hidden notice must not be read out, so it joins the description only when shown.
    select.setAttribute?.('aria-describedby', [resolved.missing && ui.missing[which]?.id, `audio-${which}-help`].filter(Boolean).join(' '));
  }

  async function refreshOnce() {
    const [inputs, outputs] = await Promise.all([listAudioDevices('input', io), listAudioDevices('output', io)]);
    if (disposed) return;
    available = inputs.available || outputs.available;
    fill('input', inputs);
    fill('output', outputs);
    if (ui.names) ui.names.hidden = !available || !(inputs.labelsHidden || outputs.labelsHidden);
    if (ui.test) ui.test.disabled = !inputs.available || typeof mediaDevices?.getUserMedia !== 'function';
    toneReady = outputs.available && typeof AudioContextCtor === 'function';
    if (ui.tone) ui.tone.disabled = toneBusy || !toneReady;
    if (!available) setStatus(AUDIO_SETTINGS_COPY.unavailable);
  }
  // A change that lands while the lists load runs one more pass, so the card never
  // keeps a list older than the last change.
  function refresh() {
    if (refreshing) { refreshAgain = true; return refreshing; }
    refreshing = (async () => {
      do { refreshAgain = false; await refreshOnce(); } while (refreshAgain && !disposed);
    })().finally(() => { refreshing = null; });
    return refreshing;
  }

  async function microphoneAccess() {
    try {
      const invoke = await getInvoke();
      return invoke ? (await invoke('get_microphone_access'))?.status || 'unknown' : 'unknown';
    } catch (_) { return 'unknown'; }
  }
  const constraints = deviceId => ({ audio: { deviceId: deviceId ? { exact: deviceId } : undefined, echoCancellation: true,
    noiseSuppression: true, autoGainControl: true }, video: false });

  // ── microphone test ───────────────────────────────────────────────────────
  function setTesting(on) {
    testOn = on;
    if (ui.test) ui.test.textContent = on ? AUDIO_SETTINGS_COPY.testStop : AUDIO_SETTINGS_COPY.testStart;
    if (ui.meter) ui.meter.hidden = !on;
    if (!on) setLevel(0);
  }
  function setLevel(fraction) {
    const percent = Math.round(Math.min(1, Math.max(0, fraction)) * 100);
    if (ui.level) ui.level.style.width = `${percent}%`;
    ui.meter?.setAttribute?.('aria-valuenow', String(percent));
  }
  function stopTest(message = '', tone = '') {
    testToken++;
    const current = test;
    test = null;
    if (current) {
      if (current.frame) attempt(() => cancelFrame(current.frame));
      if (current.timer !== null) attempt(() => clearTimer(current.timer));
      stopTracks(current.stream);
      attempt(() => current.source?.disconnect());
      attempt(() => current.context?.close()?.catch?.(() => {}));
    }
    setTesting(false);
    if (message) setStatus(message, tone);
    else if (ui.status?.textContent === AUDIO_SETTINGS_COPY.testing) setStatus('');
  }
  async function startTest() {
    const token = ++testToken;
    setTesting(true);
    const access = await microphoneAccess();
    if (token !== testToken || disposed) return;
    if (access === 'denied') { stopTest(MIC_COPY.denied, 'bad'); return; }
    const resolved = await resolveAudioDevice('input', io);
    if (token !== testToken || disposed) return;
    let stream;
    try { stream = await mediaDevices.getUserMedia(constraints(resolved.deviceId)); }
    catch (error) {
      if (resolved.deviceId && micErrorMessage(error, access) === null) {
        try { stream = await mediaDevices.getUserMedia(constraints('')); }
        catch (retry) { if (token === testToken) stopTest(micErrorMessage(retry, access) || MIC_COPY.busy, 'bad'); return; }
      } else { if (token === testToken) stopTest(micErrorMessage(error, access) || MIC_COPY.busy, 'bad'); return; }
    }
    // Stopped, hidden or replaced while the microphone opened: release it at once.
    if (token !== testToken || disposed) { stopTracks(stream); return; }
    const current = { stream, context: null, source: null, frame: 0, timer: null };
    test = current;
    current.timer = setTimer(() => { if (test === current) stopTest(AUDIO_SETTINGS_COPY.testTimedOut); }, testLimitMs);
    try {
      current.context = new AudioContextCtor();
      const analyser = current.context.createAnalyser();
      analyser.fftSize = 1024;
      current.source = current.context.createMediaStreamSource(stream);
      current.source.connect(analyser);
      const samples = new Float32Array(analyser.fftSize);
      const tick = () => {
        if (test !== current) return;
        analyser.getFloatTimeDomainData(samples);
        setLevel(rmsLevel(samples) * 4);
        current.frame = requestFrame(tick);
      };
      current.frame = requestFrame(tick);
    } catch (_) { /* no level meter; the test still proves the microphone opens */ }
    setStatus(AUDIO_SETTINGS_COPY.testing);
    // Opening a microphone also reveals device names when they were hidden.
    if (ui.names && !ui.names.hidden) void refresh();
  }

  // ── device names ──────────────────────────────────────────────────────────
  async function showNames() {
    if (namesBusy || typeof mediaDevices?.getUserMedia !== 'function') return;
    namesBusy = true;
    if (ui.showNames) ui.showNames.disabled = true;
    try {
      const access = await microphoneAccess();
      if (disposed) return;
      if (access === 'denied') { setStatus(MIC_COPY.denied, 'bad'); return; }
      let stream;
      try { stream = await mediaDevices.getUserMedia({ audio: true, video: false }); }
      catch (error) { if (!disposed) setStatus(micErrorMessage(error, access) || MIC_COPY.busy, 'bad'); return; }
      stopTracks(stream); // only the names were needed
      if (disposed) return;
      await refresh();
      if (!disposed) setStatus(AUDIO_SETTINGS_COPY.namesShown, 'good', true);
    } finally {
      namesBusy = false;
      if (ui.showNames) ui.showNames.disabled = false;
    }
  }

  // ── test sound ────────────────────────────────────────────────────────────
  async function playTone() {
    if (toneBusy || typeof AudioContextCtor !== 'function') return;
    toneBusy = true;
    if (ui.tone) ui.tone.disabled = true;
    let context = null;
    try {
      context = new AudioContextCtor();
      const routed = await applyOutputSink(context, io);
      if (disposed) return;
      const oscillator = context.createOscillator(), gain = context.createGain();
      oscillator.type = 'sine';
      oscillator.frequency.value = 660;
      const start = context.currentTime || 0;
      // A short, quiet tone with soft edges.
      gain.gain.setValueAtTime(0, start);
      gain.gain.linearRampToValueAtTime(0.08, start + 0.03);
      gain.gain.setValueAtTime(0.08, start + 0.42);
      gain.gain.linearRampToValueAtTime(0, start + 0.5);
      oscillator.connect(gain);
      gain.connect(context.destination);
      if (context.state === 'suspended') await context.resume?.();
      await new Promise(resolve => {
        const timer = setTimer(resolve, 1500);
        oscillator.onended = () => { attempt(() => clearTimer(timer)); resolve(); };
        oscillator.start(start);
        oscillator.stop(start + 0.52);
      });
      // A context that cannot switch outputs plays on the system default.
      const fellBack = routed.fallback || (!routed.ok && !!readAudioPref('output', io));
      if (!disposed) setStatus(fellBack ? AUDIO_SETTINGS_COPY.toneFallback : AUDIO_SETTINGS_COPY.tonePlayed, '', true);
    } catch (_) {
      if (!disposed) setStatus(AUDIO_SETTINGS_COPY.toneFailed, 'bad');
    } finally {
      toneBusy = false;
      attempt(() => context?.close()?.catch?.(() => {}));
      if (ui.tone && !disposed) ui.tone.disabled = !toneReady;
    }
  }

  // ── choices ───────────────────────────────────────────────────────────────
  function choose(which) {
    const select = ui[which];
    const deviceId = select?.value || '';
    const saved = saveAudioPref(which, { deviceId, label: labels[which].get(deviceId) || '' }, io);
    if (!saved) { setStatus(AUDIO_SETTINGS_COPY.notSaved, 'bad'); return; }
    setStatus(which === 'input' ? AUDIO_SETTINGS_COPY.inputSaved : AUDIO_SETTINGS_COPY.outputSaved, 'good', true);
    // A running test switches to the newly chosen microphone.
    if (which === 'input' && testOn) { stopTest(); void startTest(); }
  }

  const life = new AbortController();
  const on = (target, type, listener) => target?.addEventListener?.(type, listener, { signal: life.signal });
  on(ui.input, 'change', () => choose('input'));
  on(ui.output, 'change', () => choose('output'));
  on(ui.test, 'click', () => { if (testOn) stopTest(); else void startTest(); });
  on(ui.showNames, 'click', () => void showNames());
  on(ui.tone, 'click', () => void playTone());
  on(document, 'visibilitychange', () => { if (document.hidden || document.visibilityState === 'hidden') stopTest(); });
  const stopWatching = onAudioPrefsChange(() => { if (!disposed) void refresh(); }, io);
  function dispose() {
    if (disposed) return;
    disposed = true;
    stopTest();
    stopWatching();
    life.abort();
    if (statusTimer !== null) attempt(() => clearTimer(statusTimer));
  }
  scope?.addEventListener?.('pagehide', dispose, { once: true });

  void refresh();
  return { refresh, stopTest, dispose, get testing() { return testOn; } };
}
