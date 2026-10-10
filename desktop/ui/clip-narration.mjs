import { clampOffset, computeOffsetMs, createStudioStateMachine, formatClock, formatSync, isActiveWork, isEndOfClipPause,
  MAX_TAKE_MS, micErrorMessage, MIC_COPY, MIN_TAKE_MS, pickMimeType, rmsLevel } from './narration-capture.mjs';

// Narration studio: a modal <dialog> on the VOD page. The user talks over the saved
// clip file at 1x; stopping a take saves it at once (the host renders the narrated
// MP4), then the studio plays that real render. Pure timing/state logic lives in
// narration-capture.mjs; this module owns the dialog, microphone and recorder.

export const STUDIO_COPY = Object.freeze({
  shared: 'This clip is shared. Saving a narration removes the current link. Share again afterwards to get a new one.',
  replace: 'Record a new take? It replaces the current narration when you stop.',
  remove: 'Remove the narration from this clip?',
  leave: 'Leave without saving your narration?',
  rendering: 'Rendering narrated clip...',
  leaveOk: 'You can leave this page. Revu keeps rendering.',
  renderFailed: 'Revu could not render the narrated clip. Your recording was not saved.',
  missing: 'Clip file is missing. Save the clip again.',
  tooShort: 'Recording was too short. Record at least one second.',
  shareCleared: 'Narration changed. Share again to update the link.',
});
const MIC_KEY = 'revu.narration.mic';

// Either saved-clip row kind (VodEvidenceDto or VodBookmarkDto) -> studio context.
export function clipRowNarrationContext(row) {
  if (!row || typeof row !== 'object') return null;
  const evidence = Object.hasOwn(row, 'shareBookmarkId');
  const bookmarkId = Number(evidence ? row.shareBookmarkId : row.id) || 0;
  const start = Number(evidence ? row.startTimeSeconds : (row.clipStartSeconds ?? row.gameTimeSeconds));
  return {
    bookmarkId,
    clipPath: typeof row.clipPath === 'string' && row.clipPath ? row.clipPath : null,
    narration: row.narration && typeof row.narration === 'object' ? row.narration : null,
    clipStartSeconds: Number.isFinite(start) && start >= 0 ? start : 0,
    shareUrl: typeof row.shareUrl === 'string' ? row.shareUrl : '',
  };
}

function readSavedMic() {
  try { const value = JSON.parse(localStorage.getItem(MIC_KEY) || 'null'); return typeof value?.deviceId === 'string' ? value : null; }
  catch (_) { return null; }
}
function saveMic(deviceId, label) {
  try { localStorage.setItem(MIC_KEY, JSON.stringify({ deviceId, label: label || '' })); } catch (_) { /* best effort */ }
}
// Electron prefixes rejected IPC errors; show only the sidecar's sentence.
function saveErrorText(err) {
  const raw = String(err?.message || err || '').replace(/^Error invoking remote method '[^']+':\s*/, '').replace(/^(?:\w*Error:\s*)+/, '').trim();
  if (!raw || /HTTP \d|timeout|timed out|abort|fetch failed|ECONN|Sidecar|Backend|Untrusted|Invalid|capability|closing/i.test(raw)) return STUDIO_COPY.renderFailed;
  return raw;
}
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export function openNarrationStudio({ dialog, ctx: initialCtx, gameId, media, invoke, platform, pauseMainVod, onChanged,
  confirmFn = message => window.confirm(message), opener = document.activeElement,
  now = () => performance.now(), countdownMs = 1000 }) {
  let ctx = initialCtx;
  const sm = createStudioStateMachine(ctx?.narration?.narratedClipPath ? 'watching' : 'setup');
  const life = new AbortController();
  const on = (target, type, fn, options = {}) => target?.addEventListener(type, fn, { ...options, signal: life.signal });
  const q = name => dialog.querySelector(`[data-nr="${name}"]`);
  const video = q('video');
  let closed = false, stream = null, audio = null, analyser = null, source = null, levelFrame = 0, accessStatus = 'unknown';
  let takeId = 0, take = null, dispatched = Promise.resolve();
  const mix = { gameVolume: 0.8, narrationVolume: 1, duck: true, offsetMs: 0 };
  const finite = (value, fallback) => (Number.isFinite(Number(value)) && value !== null && value !== '' ? Number(value) : fallback);
  // The saved mix of the current narration (defaults before the first take).
  const stored = () => {
    const n = ctx?.narration;
    if (!n) return { ...mix };
    return { gameVolume: finite(n.gameVolume, 0.8), narrationVolume: finite(n.narrationVolume, 1),
      duck: n.duck !== false, offsetMs: clampOffset(finite(n.offsetMs, 0)) };
  };

  const setText = (name, text) => { const el = q(name); if (el) el.textContent = text || ''; };
  const status = text => setText('status', text);
  const show = (name, visible) => { const el = q(name); if (el) el.hidden = !visible; };

  function guard() {
    if (isActiveWork(sm.state) && !closed) {
      window.__revuActiveWork = 'narration';
      window.revuBeforeNavigate = beforeNavigate;
    } else clearGuard();
  }
  async function beforeNavigate() {
    if (sm.state === 'saving') { await dispatched; return true; } // the host finishes the render
    return confirmFn(STUDIO_COPY.leave);
  }
  function clearGuard() {
    if (window.__revuActiveWork === 'narration') window.__revuActiveWork = false;
    if (window.revuBeforeNavigate === beforeNavigate) delete window.revuBeforeNavigate;
  }
  const send = event => { const next = sm.send(event); if (next) render(); return next; };

  function render() {
    const state = sm.state, active = isActiveWork(state);
    dialog.dataset.state = state;
    show('shared', !!ctx?.shareUrl);
    show('mic-block', state === 'setup' && accessStatus !== 'denied');
    show('blocked', state === 'setup' && accessStatus === 'denied');
    show('countdown', state === 'countdown');
    show('rec', state === 'recording');
    show('sync-row', state === 'watching');
    show('record', state === 'setup');
    show('stop', state === 'countdown' || state === 'recording');
    for (const name of ['apply', 'remove']) show(name, state === 'watching');
    show('record-again', state === 'watching' || state === 'error');
    const record = q('record');
    if (record) record.disabled = !stream || !ctx?.clipPath;
    for (const name of ['mic', 'game', 'voice', 'duck']) { const el = q(name); if (el) el.disabled = active; }
    const done = q('done');
    if (done) done.hidden = state === 'countdown' || state === 'recording';
    if (video) video.controls = state === 'setup' || state === 'watching';
    syncMixInputs();
    guard();
    keepFocusInside(state === 'countdown' || state === 'recording');
  }
  // Hiding the focused control (Record when the countdown starts) drops focus to the
  // body, where keys would reach the VOD page's shortcuts; hand it to a visible control.
  function keepFocusInside(taking) {
    if (closed || !dialog.open) return;
    const focused = document.activeElement;
    const lost = !focused || focused === document.body || (dialog.contains && !dialog.contains(focused))
      || !!focused.closest?.('[hidden]');
    if (!lost) return;
    const target = q(taking ? 'stop' : 'done');
    if (target && !target.hidden) { try { target.focus({ preventScroll: true }); } catch (_) { /* not focusable */ } }
  }
  function syncMixInputs() {
    const game = q('game'), voice = q('voice'), duck = q('duck');
    if (game) game.value = String(mix.gameVolume);
    if (voice) voice.value = String(mix.narrationVolume);
    if (duck) duck.checked = mix.duck;
    setText('game-out', `${Math.round(mix.gameVolume * 100)}%`);
    setText('voice-out', `${Math.round(mix.narrationVolume * 100)}%`);
    setText('sync', formatSync(mix.offsetMs));
    const base = stored(), apply = q('apply');
    if (apply) apply.disabled = !ctx?.narration || (base.gameVolume === mix.gameVolume && base.narrationVolume === mix.narrationVolume
      && base.duck === mix.duck && base.offsetMs === mix.offsetMs);
    if (video && sm.state !== 'watching') video.volume = Math.min(1, mix.gameVolume);
  }

  function loadVideo(path) {
    if (!video) return false;
    const url = path ? media?.resolveMedia(path) : null;
    if (!url) { video.removeAttribute('src'); video.load?.(); return false; }
    if (video.getAttribute('src') !== url) { video.src = url; video.load?.(); }
    video.playbackRate = 1;
    video.volume = sm.state === 'watching' ? 1 : Math.min(1, mix.gameVolume);
    return true;
  }
  function showSourceVideo() {
    if (sm.state === 'watching') {
      if (!loadVideo(ctx?.narration?.narratedClipPath)) { loadVideo(ctx?.clipPath); status(STUDIO_COPY.missing); }
    } else if (!loadVideo(ctx?.clipPath)) status(STUDIO_COPY.missing);
  }

  // ── Microphone ──────────────────────────────────────────────────────────────
  function stopStream() {
    for (const track of stream?.getTracks?.() || []) { try { track.stop(); } catch (_) { /* already stopped */ } }
    stream = null;
    try { source?.disconnect(); } catch (_) { /* detached */ }
    source = null;
  }
  async function acquire(deviceId) {
    const constraints = deviceId => ({ audio: { deviceId: deviceId ? { exact: deviceId } : undefined, echoCancellation: true,
      noiseSuppression: true, autoGainControl: true, channelCount: { ideal: 1 }, sampleRate: { ideal: 48000 } }, video: false });
    try { return await navigator.mediaDevices.getUserMedia(constraints(deviceId)); }
    catch (err) {
      if (deviceId && micErrorMessage(err, accessStatus) === null) return navigator.mediaDevices.getUserMedia(constraints(undefined));
      throw err;
    }
  }
  async function startMic(deviceId = readSavedMic()?.deviceId) {
    try { accessStatus = (await invoke('get_microphone_access'))?.status || 'unknown'; } catch (_) { accessStatus = 'unknown'; }
    if (closed) return;
    if (accessStatus === 'denied') { stopStream(); render(); return; }
    let next;
    try { next = await acquire(deviceId); }
    catch (err) { stopStream(); render(); status(micErrorMessage(err, accessStatus) || MIC_COPY.busy); return; }
    if (closed || sm.state !== 'setup') { for (const track of next.getTracks()) track.stop(); return; }
    stopStream();
    stream = next;
    const track = stream.getAudioTracks()[0];
    const activeId = track?.getSettings?.().deviceId || deviceId || 'default';
    await fillDevices(activeId);
    if (closed) return; // close() already released the stream while the devices were listed
    saveMic(activeId, track?.label);
    if (Object.values(MIC_COPY).includes(q('status')?.textContent)) status('');
    startMeter();
    render();
  }
  async function fillDevices(activeId) {
    const select = q('mic');
    if (!select) return;
    let inputs = [];
    try { inputs = (await navigator.mediaDevices.enumerateDevices()).filter(device => device.kind === 'audioinput'); } catch (_) { /* keep empty */ }
    const rank = device => device.deviceId === 'default' ? 0 : device.deviceId === 'communications' ? 2 : 1;
    inputs.sort((a, b) => rank(a) - rank(b));
    select.replaceChildren(...inputs.map((device, index) => {
      const option = document.createElement('option');
      option.value = device.deviceId;
      option.textContent = device.label || `Microphone ${index + 1}`;
      return option;
    }));
    select.value = inputs.some(device => device.deviceId === activeId) ? activeId : (inputs[0]?.deviceId || '');
  }
  function startMeter() {
    if (closed || !stream) return;
    try {
      audio ??= new AudioContext();
      analyser ??= Object.assign(audio.createAnalyser(), { fftSize: 1024 });
      source = audio.createMediaStreamSource(stream);
      source.connect(analyser);
    } catch (_) { return; }
    const samples = new Float32Array(analyser.fftSize), bar = q('level');
    cancelAnimationFrame(levelFrame);
    const tick = () => {
      if (closed) return;
      analyser.getFloatTimeDomainData(samples);
      if (bar) bar.style.width = `${Math.round(Math.min(1, rmsLevel(samples) * 4) * 100)}%`;
      levelFrame = requestAnimationFrame(tick);
    };
    levelFrame = requestAnimationFrame(tick);
  }

  // ── Take ───────────────────────────────────────────────────────────────────
  async function seekToStart() {
    if (!video) return;
    video.pause();
    if (video.currentTime === 0 && video.readyState >= 1) return;
    await new Promise(resolve => {
      const timer = setTimeout(resolve, 3000);
      video.addEventListener('seeked', () => { clearTimeout(timer); resolve(); }, { once: true });
      video.currentTime = 0;
    });
  }
  async function record() {
    if (sm.state !== 'setup' || !stream || !ctx?.clipPath) return;
    if (ctx.shareUrl && !confirmFn(STUDIO_COPY.shared)) return;
    if (ctx.narration && !confirmFn(STUDIO_COPY.replace)) return;
    const id = ++takeId;
    send('record');
    status('');
    loadVideo(ctx.clipPath);
    await seekToStart();
    for (const n of [3, 2, 1]) {
      if (id !== takeId || sm.state !== 'countdown') return;
      setText('countdown', String(n));
      await sleep(countdownMs);
    }
    if (id !== takeId || sm.state !== 'countdown') return;
    beginTake(id);
  }
  function beginTake(id) {
    const mimeType = pickMimeType(globalThis.MediaRecorder);
    let recorder;
    try { recorder = new MediaRecorder(stream, { ...(mimeType ? { mimeType } : {}), audioBitsPerSecond: 96000 }); }
    catch (_) { send('fail'); status(MIC_COPY.busy); return; }
    take = { id, recorder, chunks: [], startPerf: null, stopPerf: null, frame: null, discarded: false, listeners: new AbortController(), timers: [] };
    const current = take;
    recorder.addEventListener('dataavailable', event => { if (event.data?.size) current.chunks.push(event.data); });
    recorder.addEventListener('start', () => { current.startPerf = now(); });
    recorder.addEventListener('stop', () => {
      // The recorder stopped by itself (the microphone was unplugged or failed): keep what it captured.
      if (current.stopPerf === null && !current.discarded && take === current) stopTake('mic');
      finishTake(current);
    });
    current.startPerf = now(); // refined by the recorder's start event
    recorder.start(1000);
    send('started');
    const listen = (target, type, fn) => target.addEventListener(type, fn, { signal: current.listeners.signal });
    listen(video, 'ended', () => stopTake('end'));
    const limitMs = ((Number.isFinite(video.duration) ? video.duration : MAX_TAKE_MS / 1000 - 5) + 5) * 1000;
    current.timers.push(setTimeout(() => stopTake('limit'), Math.min(limitMs, MAX_TAKE_MS)));
    const elapsed = setInterval(() => setText('rec', `Recording ${formatClock((now() - current.startPerf) / 1000)}`), 250);
    current.timers.push(elapsed);
    const firstFrame = (mediaTimeS, expectedPerf) => {
      if (current.frame || take !== current) return;
      current.frame = { mediaTimeS, expectedPerf };
      // Only a playing take can be interrupted; the countdown seek and start are ours.
      listen(video, 'pause', () => { if (!isEndOfClipPause(video)) stopTake('pause'); });
      listen(video, 'seeking', () => stopTake('seek'));
      let stall = 0;
      listen(video, 'waiting', () => { clearTimeout(stall); stall = setTimeout(() => stopTake('stall'), 500); current.timers.push(stall); });
      for (const type of ['playing', 'timeupdate']) listen(video, type, () => clearTimeout(stall));
      listen(document, 'visibilitychange', () => { if (document.visibilityState === 'hidden') stopTake('hidden'); });
    };
    if (typeof video.requestVideoFrameCallback === 'function') {
      video.requestVideoFrameCallback((_now, metadata) => firstFrame(metadata.mediaTime, metadata.expectedDisplayTime));
    } else listen(video, 'playing', () => firstFrame(video.currentTime, now()));
    video.play().catch(() => { if (take === current) { discardTake(); status(STUDIO_COPY.missing); } });
  }
  function endTimers(current) {
    current.listeners.abort();
    for (const timer of current.timers) { clearTimeout(timer); clearInterval(timer); }
  }
  function stopTake(cause) {
    const current = take;
    if (!current || sm.state !== 'recording' || current.stopPerf !== null) return;
    current.stopPerf = now();
    endTimers(current);
    const early = !['end', 'limit', 'stop'].includes(cause) && !video.ended ? video.currentTime : null;
    video.pause();
    const durationMs = Math.round(current.stopPerf - current.startPerf);
    if (durationMs < MIN_TAKE_MS) {
      current.discarded = true;
      send('tooShort');
      status(STUDIO_COPY.tooShort);
    } else {
      // The guard lets a navigation leave once the take reaches the host; until the
      // recorder's stop event hands it to saveTake, leaving must wait.
      dispatched = new Promise(resolve => { current.markDispatched = resolve; });
      send('stop');
      if (early !== null) status(`Recording stopped early at ${formatClock(early)}.`);
    }
    try { if (current.recorder.state !== 'inactive') current.recorder.stop(); } catch (_) { finishTake(current); }
  }
  function discardTake() {
    takeId++;
    const current = take;
    if (current) {
      current.discarded = true;
      endTimers(current);
      try { if (current.recorder.state !== 'inactive') current.recorder.stop(); } catch (_) { /* nothing recorded */ }
    }
    video?.pause();
    if (sm.can('discard')) send('discard');
  }
  async function finishTake(current) {
    if (current.finished) return;
    current.finished = true;
    if (take === current) take = null;
    if (current.discarded || current.stopPerf === null) { current.markDispatched?.(); return; }
    const durationMs = Math.min(MAX_TAKE_MS, Math.round(current.stopPerf - current.startPerf));
    const frame = current.frame;
    mix.offsetMs = frame ? computeOffsetMs({ recorderStartPerfMs: current.startPerf, frameMediaTimeS: frame.mediaTimeS,
      frameExpectedDisplayPerfMs: frame.expectedPerf }) : 0;
    await saveTake(new Blob(current.chunks, { type: 'audio/webm' }), durationMs, current.markDispatched);
  }
  async function saveTake(blob, durationMs, markDispatched) {
    const early = q('status')?.textContent || '';
    status([early, STUDIO_COPY.rendering, STUDIO_COPY.leaveOk].filter(Boolean).join(' '));
    if (!markDispatched) dispatched = new Promise(resolve => { markDispatched = resolve; });
    try {
      const bytes = new Uint8Array(await blob.arrayBuffer());
      const pending = platform.saveNarration(bytes, { gameId, bookmarkId: ctx.bookmarkId, mimeType: blob.type || 'audio/webm',
        offsetMs: clampOffset(mix.offsetMs), durationMs, gameVolume: mix.gameVolume, narrationVolume: mix.narrationVolume, duck: mix.duck });
      markDispatched();
      const res = await pending;
      if (res?.ok === false) throw new Error(res.error || STUDIO_COPY.renderFailed);
      await afterChange(res, 'saved');
    } catch (err) {
      markDispatched();
      if (closed) return;
      send('fail');
      status(saveErrorText(err));
    }
  }
  async function afterChange(res, event) {
    const fresh = await onChanged?.().catch(() => null);
    if (fresh) ctx = fresh;
    if (closed) return;
    if (event === 'saved') { cancelAnimationFrame(levelFrame); stopStream(); } // the mic is off while watching
    Object.assign(mix, stored());
    if (event) send(event);
    showSourceVideo();
    status(res?.shareCleared ? STUDIO_COPY.shareCleared : '');
    render();
  }

  // ── Watching ───────────────────────────────────────────────────────────────
  async function applyMix() {
    if (sm.state !== 'watching' || !ctx?.narration) return;
    if (ctx.shareUrl && !confirmFn(STUDIO_COPY.shared)) return;
    setBusy(true);
    status(STUDIO_COPY.rendering);
    try {
      const res = await invoke('mix_clip_narration', { payload: { gameId, bookmarkId: ctx.bookmarkId, offsetMs: clampOffset(mix.offsetMs),
        gameVolume: mix.gameVolume, narrationVolume: mix.narrationVolume, duck: mix.duck } });
      if (res?.ok === false) throw new Error(res.error || STUDIO_COPY.renderFailed);
      video?.removeAttribute('src');
      await afterChange(res, null);
    } catch (err) { if (!closed) status(saveErrorText(err)); }
    finally { setBusy(false); }
  }
  async function removeNarration() {
    if (sm.state !== 'watching' || !confirmFn(STUDIO_COPY.remove)) return;
    if (ctx.shareUrl && !confirmFn(STUDIO_COPY.shared)) return;
    setBusy(true);
    try {
      const res = await invoke('delete_clip_narration', { payload: { gameId, bookmarkId: ctx.bookmarkId } });
      if (res?.ok === false) throw new Error(res.error || STUDIO_COPY.renderFailed);
      await afterChange(res, 'reset');
      if (!closed) void startMic();
    } catch (err) { if (!closed) status(saveErrorText(err)); }
    finally { setBusy(false); }
  }
  function setBusy(busy) {
    for (const name of ['apply', 'record-again', 'remove', 'nudge-minus', 'nudge-plus', 'game', 'voice', 'duck']) {
      const el = q(name); if (el) el.disabled = busy;
    }
    if (!busy && !closed) render();
  }
  function recordAgain() {
    if (!send('reset') && !send('retry')) return;
    status('');
    showSourceVideo();
    void startMic();
  }

  // ── Dialog lifetime ────────────────────────────────────────────────────────
  function close() {
    if (closed) return;
    if (sm.state === 'countdown' || sm.state === 'recording') discardTake();
    closed = true;
    life.abort();
    cancelAnimationFrame(levelFrame);
    stopStream();
    try { audio?.close(); } catch (_) { /* already closed */ }
    audio = null; analyser = null;
    if (video) { video.pause(); video.removeAttribute('src'); video.load?.(); }
    clearGuard();
    if (dialog.open) dialog.close();
    try { opener?.focus?.({ preventScroll: true }); } catch (_) { /* opener left the page */ }
  }
  function requestClose() {
    if ((sm.state === 'countdown' || sm.state === 'recording') && !confirmFn(STUDIO_COPY.leave)) return;
    close();
  }

  // Escape is handled on keydown: a prevented keydown never becomes a close request, so a
  // second Escape cannot force the dialog shut past the confirm (Chromium's CloseWatcher
  // makes cancel non-cancelable without fresh user activation).
  on(dialog, 'keydown', event => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); if (!event.repeat) requestClose(); return; }
    if (event.key !== 'Tab') event.stopPropagation();
  }, { capture: true });
  on(dialog, 'cancel', event => { event.preventDefault(); requestClose(); });
  // A previous studio's queued close event can arrive after this one opened; ignore it.
  // If the browser closed the dialog mid-take anyway, keep the take instead of dropping it.
  on(dialog, 'close', () => { if (!dialog.open) { if (sm.state === 'recording') stopTake('stop'); close(); } });
  on(window, 'pagehide', close);
  on(q('record'), 'click', () => void record());
  on(q('stop'), 'click', () => (sm.state === 'countdown' ? discardTake() : stopTake('stop')));
  on(q('done'), 'click', requestClose);
  on(q('close'), 'click', requestClose);
  on(q('record-again'), 'click', recordAgain);
  on(q('apply'), 'click', () => void applyMix());
  on(q('remove'), 'click', () => void removeNarration());
  on(q('open-settings'), 'click', () => void invoke('open_microphone_settings').catch(() => {}));
  on(q('mic'), 'change', event => { if (sm.state === 'setup') void startMic(event.target.value); });
  on(q('game'), 'input', event => { mix.gameVolume = Math.min(1.5, Math.max(0, Number(event.target.value) || 0)); syncMixInputs(); });
  on(q('voice'), 'input', event => { mix.narrationVolume = Math.min(2, Math.max(0, Number(event.target.value) || 0)); syncMixInputs(); });
  on(q('duck'), 'change', event => { mix.duck = !!event.target.checked; syncMixInputs(); });
  on(q('nudge-minus'), 'click', () => { mix.offsetMs = clampOffset(mix.offsetMs - 50); syncMixInputs(); });
  on(q('nudge-plus'), 'click', () => { mix.offsetMs = clampOffset(mix.offsetMs + 50); syncMixInputs(); });

  pauseMainVod?.();
  Object.assign(mix, stored());
  status('');
  dialog.showModal();
  showSourceVideo();
  render();
  if (sm.state === 'setup') void startMic();
  return { get state() { return closed ? 'closed' : sm.state; }, close, requestClose };
}
