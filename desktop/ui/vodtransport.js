// Revu desktop — shared VOD transport core. Owns the player *transport* for ONE
// <video>: play/pause, ◀▶ step seek, the seek STEP model (Up/Down), speed, mute,
// and "enlarge" (in-app expand, not OS fullscreen), plus the time readout and the
// keyboard contract. Both the full VOD player (vodplayer.js) and the Patterns
// inline moment player (patterns.js) use this so they look + behave identically.
//
// Design rules (preserve the house style of both pages):
//   • This module attaches listeners ONLY to the <video> it owns (and, optionally,
//     ONE document keydown via attachKeyboard). It NEVER adds a document *click*
//     listener — each page keeps its single delegated [data-action] handler and
//     forwards transport actions here via handleAction(). That keeps "one click
//     handler per page" intact.
//   • textContent-only writes (no innerHTML for server/data strings). The bar
//     markup emitted by renderTransportBar is a fixed, trusted template.
//   • The asset-URL path (tauriCore / resolveAssetUrl) is lifted VERBATIM from
//     vodplayer.js and must stay byte-for-byte identical — convertFileSrc first
//     (gives range-request streaming so seeking works), then the asset.localhost
//     fallback. Do not "improve" it.

// ── invoke + asset-url resolvers (lifted verbatim from vodplayer.js) ─────────
let _invoke = null;
let _convert = null;
export async function tauriCore() {
  if (_invoke && _convert) return { invoke: _invoke, convertFileSrc: _convert };
  try {
    const mod = await import('@tauri-apps/api/core');
    if (mod && typeof mod.invoke === 'function') {
      _invoke = mod.invoke;
      _convert = mod.convertFileSrc;
      return { invoke: _invoke, convertFileSrc: _convert };
    }
  } catch (_) { /* not in Tauri bundler */ }
  if (window.__TAURI__ && window.__TAURI__.core) {
    const core = window.__TAURI__.core;
    if (typeof core.invoke === 'function') {
      _invoke = core.invoke.bind(core);
      _convert = (core.convertFileSrc || ((p) => p)).bind(core);
      return { invoke: _invoke, convertFileSrc: _convert };
    }
  }
  return null;
}

// Build a usable asset URL for a local file. Tries convertFileSrc (the correct
// Tauri v2 path); falls back to manually constructing the asset:// URL shape if
// the helper isn't exposed. Returns null if neither is possible.
export function resolveAssetUrl(core, filePath) {
  if (!filePath) return null;
  if (core && typeof core.convertFileSrc === 'function') {
    try {
      const u = core.convertFileSrc(filePath);
      if (u) return u;
    } catch (_) { /* fall through */ }
  }
  // Manual fallback: Tauri v2 asset URLs are http://asset.localhost/<encoded path>
  // on Windows. Only used if convertFileSrc is missing.
  if (window.__TAURI__) {
    const enc = encodeURIComponent(filePath);
    return `http://asset.localhost/${enc}`;
  }
  return null;
}

// ── seek-step model (shared) ──────────────────────────────────────────────────
// The skip amount for the ◀▶ transport buttons + Left/Right arrows. Up/Down arrows
// cycle through STEP_CHOICES. Shown as "step Ns" in the time readout.
export const STEP_CHOICES = [1, 2, 5, 10, 30];

function clock(s) { s = Math.max(0, Math.floor(s || 0)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; }

// ── transport bar markup ─────────────────────────────────────────────────────
// Emit the transport bar into `host`, returning the element refs the factory
// needs. Reuses the existing .vp-transport / .tbtn / .ttime / .vp-rate-sel
// classes so both pages get the identical glass look. `idPrefix` scopes the ids
// so the bar can coexist with vodplayer's static one. `compact` drops the Speed
// label text (the dropdown stays) for the tighter inline pattern surface.
//
// The data-action names match vodplayer's static bar (playpause / seek with
// data-delta / mute / fullscreen), so each page's single delegated click handler
// can forward them to the core via handleAction().
export function renderTransportBar(host, { idPrefix = 'm', compact = false } = {}) {
  if (!host) return null;
  while (host.firstChild) host.removeChild(host.firstChild);
  host.classList.add('vp-transport-card');

  const bar = document.createElement('div');
  bar.className = 'vp-transport';

  const mk = (tag, cls, attrs) => {
    const el = document.createElement(tag);
    if (cls) el.className = cls;
    if (attrs) for (const k in attrs) el.setAttribute(k, attrs[k]);
    return el;
  };

  const back = mk('button', 'tbtn', { id: `${idPrefix}-back`, title: 'Back one step (←). Up/Down arrows change the step.', 'data-action': 'seek', 'data-delta': '-1', type: 'button' });
  back.textContent = '⏮';
  const play = mk('button', 'tbtn play', { id: `${idPrefix}-play-btn`, title: 'Play / Pause (space)', 'data-action': 'playpause', type: 'button' });
  play.textContent = '▶';
  const fwd = mk('button', 'tbtn', { id: `${idPrefix}-fwd`, title: 'Forward one step (→). Up/Down arrows change the step.', 'data-action': 'seek', 'data-delta': '1', type: 'button' });
  fwd.textContent = '⏭';

  const time = mk('span', 'ttime', { id: `${idPrefix}-time` });
  // Seed the readout (textContent-only; setTimeReadout rebuilds it the same way).
  time.appendChild(document.createTextNode('0:00 / 0:00  '));
  const stepSpan = mk('span', 'step');
  stepSpan.textContent = 'step 5s';
  time.appendChild(stepSpan);

  const spring = mk('span', 'tspring');

  const rateSel = mk('select', 'vp-rate-sel', { id: `${idPrefix}-rate-sel`, 'aria-label': 'Playback speed' });
  for (const [v, label] of [['0.25', '0.25×'], ['0.5', '0.5×'], ['1', '1×'], ['1.5', '1.5×'], ['2', '2×'], ['3', '3×']]) {
    const opt = document.createElement('option');
    opt.value = v;
    opt.textContent = label;
    if (v === '1') opt.selected = true;
    rateSel.appendChild(opt);
  }

  const mute = mk('button', 'tbtn', { id: `${idPrefix}-mute`, title: 'Mute', 'data-action': 'mute', type: 'button' });
  mute.textContent = '🔊';
  const fs = mk('button', 'tbtn', { id: `${idPrefix}-fs`, title: 'Enlarge video', 'aria-label': 'Enlarge', 'data-action': 'fullscreen', type: 'button' });
  fs.textContent = '⛶';

  bar.appendChild(back);
  bar.appendChild(play);
  bar.appendChild(fwd);
  bar.appendChild(time);
  bar.appendChild(spring);
  if (!compact) {
    const lbl = mk('label', 'tlabel', { for: `${idPrefix}-rate-sel` });
    lbl.textContent = 'Speed';
    bar.appendChild(lbl);
  }
  bar.appendChild(rateSel);
  bar.appendChild(mute);
  bar.appendChild(fs);
  host.appendChild(bar);

  return { time, play, mute, fs, rateSel, back, fwd, bar };
}

// ── the transport core ───────────────────────────────────────────────────────
// Owns ONE <video>. No document click listener; the page forwards via
// handleAction(). attachVideo() binds the per-video event listeners (glyphs +
// time readout); attachKeyboard() optionally adds ONE document keydown for the
// transport keys (used by patterns, which had none; vodplayer keeps its own
// keydown and calls these methods directly to preserve its write-key ordering).
export function createTransport({
  video,                 // the <video> element (REQUIRED)
  timeEl = null,         // element to receive the "m:ss / m:ss  step Ns" readout
  seekFillEl = null,     // optional progress fill (full-chrome only)
  playheadEl = null,     // optional playhead (full-chrome only)
  playBtn = null,        // play/pause button whose glyph flips on play/pause
  muteBtn = null,        // mute button whose glyph flips on volumechange
  fsBtn = null,          // enlarge button whose glyph/title flips on toggleEnlarge
  rateSel = null,        // speed <select>; change → playbackRate (wired here)
  expandTarget = null,   // element to toggle the enlarge class on
  expandClass = 'vp-expanded',
  onExpandChange = null, // cb(expanded) for page extras (e.g. scrollIntoView)
  fullGlyphs = true,     // true → ⛶/🗗 enlarge glyphs; false leaves the glyph as-is
  durationFallback = () => 0, // returns a duration when video.duration isn't ready
} = {}) {
  let _step = 5;
  let _videoBound = false;
  let _kbHandler = null;
  let _rateHandler = null;
  // The current load()'s metadata-jump + error listeners, tracked so a reload (or
  // unload) can remove a still-pending one. {once:true} only auto-removes a listener
  // when its event actually FIRES; on a successful load the 'error' listener (and, if
  // the user switches clips before metadata, the 'loadedmetadata' jump) would
  // otherwise linger on the <video> and stack across clips.
  let _lastJump = null;
  let _lastErr = null;
  function clearLoadListeners() {
    if (_lastJump) { video.removeEventListener('loadedmetadata', _lastJump); _lastJump = null; }
    if (_lastErr) { video.removeEventListener('error', _lastErr); _lastErr = null; }
  }

  function dur() { return video.duration || durationFallback() || 0; }

  // Render the mono time readout with the persistent "step Ns" tag (textContent
  // only). Mirrors vodplayer.js setTimeReadout byte-for-byte in shape.
  function setTimeReadout(cur, d) {
    if (!timeEl) return;
    while (timeEl.firstChild) timeEl.removeChild(timeEl.firstChild);
    timeEl.appendChild(document.createTextNode(`${clock(cur)} / ${clock(d)}  `));
    const step = document.createElement('span');
    step.className = 'step';
    step.textContent = `step ${_step}s`;
    timeEl.appendChild(step);
  }

  function onTimeUpdate() {
    const d = dur() || 1;
    const pct = Math.max(0, Math.min(1, video.currentTime / d));
    if (seekFillEl) seekFillEl.style.width = `${pct * 100}%`;
    if (playheadEl) playheadEl.style.left = `${pct * 100}%`;
    setTimeReadout(video.currentTime, d);
  }
  function onPlay() { if (playBtn) playBtn.textContent = '❚❚'; }
  function onPause() { if (playBtn) playBtn.textContent = '▶'; }
  function onVolume() { if (muteBtn) muteBtn.textContent = (video.muted || video.volume === 0) ? '🔇' : '🔊'; }

  const api = {
    get stepSeconds() { return _step; },

    // Snap to the nearest allowed choice and refresh the readout.
    setStep(seconds) {
      const i = STEP_CHOICES.indexOf(seconds);
      _step = i >= 0 ? STEP_CHOICES[i] : 5;
      setTimeReadout(video.currentTime || 0, dur());
    },
    // Move the step up/down the choice list (Up = larger, Down = smaller).
    nudgeStep(direction) {
      let i = STEP_CHOICES.indexOf(_step);
      if (i < 0) i = 2; // default index (5s)
      i = Math.max(0, Math.min(STEP_CHOICES.length - 1, i + (direction > 0 ? 1 : -1)));
      api.setStep(STEP_CHOICES[i]);
    },

    seekTo(seconds) {
      if (Number.isFinite(seconds)) video.currentTime = Math.max(0, seconds);
    },
    seekByStep(direction) {
      api.seekTo((video.currentTime || 0) + (direction < 0 ? -1 : 1) * _step);
    },

    play() { video.play().catch(() => {}); },
    pause() { video.pause(); },
    toggle() { video.paused ? api.play() : api.pause(); },
    setRate(n) { video.playbackRate = Number(n || 1); },
    toggleMute() { video.muted = !video.muted; },

    isExpanded() { return !!(expandTarget && expandTarget.classList.contains(expandClass)); },
    // In-app enlarge (NOT OS fullscreen): toggle the expand class on the host. The
    // host's CSS does the layout change. Page extras run via onExpandChange.
    toggleEnlarge() {
      if (!expandTarget) return false;
      const expanded = expandTarget.classList.toggle(expandClass);
      if (fullGlyphs && fsBtn) {
        fsBtn.textContent = expanded ? '🗗' : '⛶';
        fsBtn.title = expanded ? 'Restore layout' : 'Enlarge video';
      }
      if (typeof onExpandChange === 'function') onExpandChange(expanded);
      return expanded;
    },

    // Load a local asset URL into the video; on metadata, jump to startSeconds and
    // (optionally) play. Mirrors vodplayer onMeta jump + patterns playMoment jump.
    // onError lets the page surface a message + reset its poster.
    load(assetUrl, { startSeconds = 0, autoplay = true, onError = null } = {}) {
      // Drop any still-pending listeners from a prior load that never fired, so they
      // can't fire late against this clip (or stack across clips).
      clearLoadListeners();
      video.src = assetUrl;
      const jump = () => {
        _lastJump = null; // fired → consumed
        try { if (startSeconds > 0) video.currentTime = startSeconds; } catch (_) { /* ignore */ }
        if (autoplay) api.play();
      };
      const err = onError ? (ev) => { _lastErr = null; onError(ev); } : null;
      _lastJump = jump;
      video.addEventListener('loadedmetadata', jump, { once: true });
      if (err) { _lastErr = err; video.addEventListener('error', err, { once: true }); }
      video.load();
    },
    // Stop + detach the current source (the media half of a reset-to-poster). Also
    // drops any pending load listeners so they don't carry into the next clip.
    unload() {
      clearLoadListeners();
      try { video.pause(); } catch (_) { /* ignore */ }
      video.removeAttribute('src');
      try { video.load(); } catch (_) { /* ignore */ }
      if (playBtn) playBtn.textContent = '▶';
    },

    // CALLED FROM the page's existing single click handler. Returns true if it
    // handled a transport action (so the page can stop), false otherwise.
    handleAction(action, el) {
      switch (action) {
        case 'playpause': api.toggle(); return true;
        case 'seek': api.seekByStep(Number(el && el.dataset ? el.dataset.delta : 0) < 0 ? -1 : 1); return true;
        case 'mute': api.toggleMute(); return true;
        case 'fullscreen': api.toggleEnlarge(); return true;
        default: return false;
      }
    },

    // Bind the per-video event listeners (glyphs, time readout, click-to-toggle).
    // clickToToggle: clicking the video toggles play/pause; stopProp prevents the
    // click from bubbling to a surrounding surface (replaces patterns' old hack).
    attachVideo({ clickToToggle = true, stopProp = false } = {}) {
      if (_videoBound) return;
      _videoBound = true;
      video.addEventListener('timeupdate', onTimeUpdate);
      video.addEventListener('play', onPlay);
      video.addEventListener('pause', onPause);
      video.addEventListener('volumechange', onVolume);
      if (clickToToggle) {
        video.addEventListener('click', (ev) => {
          if (stopProp) ev.stopPropagation();
          api.toggle();
        });
      }
      if (rateSel && !_rateHandler) {
        _rateHandler = () => api.setRate(rateSel.value || 1);
        rateSel.addEventListener('change', _rateHandler);
      }
    },

    // Re-place the time readout now (e.g. after metadata loads in the host page).
    refreshReadout() { setTimeReadout(video.currentTime || 0, dur()); },

    // Optional global keyboard for the transport keys. Adds ONE document keydown.
    // Replicates vodplayer's transport-key ordering EXACTLY (Up/Down change the
    // step and work BEFORE the !src guard; Space/Left/Right need a loaded video).
    // It does NOT handle write keys (B/I/O/S) — those stay in the page. The
    // typing-guard exempts INPUT/TEXTAREA (covers patterns' #m-note). onJumpRow,
    // if given, handles Enter/Space on a focused row before the transport keys.
    attachKeyboard({ onJumpRow = null } = {}) {
      if (_kbHandler) return;
      _kbHandler = (ev) => {
        // Never hijack typing in a text field or the arrow/Space keys of a focused
        // <select> (e.g. the speed dropdown — Up/Down pick an option, Space opens it).
        const tag = ev.target.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
        // A focused row (role=button) handles Enter/Space first if the page wants.
        if (onJumpRow && (ev.key === 'Enter' || ev.key === ' ')) {
          if (onJumpRow(ev)) return;
        }
        // Up/Down change the seek STEP — work even before the video has a src.
        if (ev.key === 'ArrowUp') { ev.preventDefault(); api.nudgeStep(1); return; }
        if (ev.key === 'ArrowDown') { ev.preventDefault(); api.nudgeStep(-1); return; }
        // F — enlarge / restore.
        if (ev.key === 'f' || ev.key === 'F') { ev.preventDefault(); api.toggleEnlarge(); return; }
        // Esc — restore the layout if currently enlarged.
        if (ev.key === 'Escape') {
          if (api.isExpanded()) { ev.preventDefault(); api.toggleEnlarge(); }
          return;
        }
        // The rest need a loaded video.
        if (!video || !video.src) return;
        if (ev.key === ' ') { ev.preventDefault(); api.toggle(); }
        else if (ev.key === 'ArrowLeft') { ev.preventDefault(); api.seekByStep(-1); }
        else if (ev.key === 'ArrowRight') { ev.preventDefault(); api.seekByStep(1); }
      };
      document.addEventListener('keydown', _kbHandler);
    },
    detachKeyboard() {
      if (_kbHandler) { document.removeEventListener('keydown', _kbHandler); _kbHandler = null; }
    },
  };

  return api;
}
