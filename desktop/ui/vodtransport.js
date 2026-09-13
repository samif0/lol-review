// Revu desktop — shared VOD transport core. Owns the player *transport* for ONE
// <video>: play/pause, ◀▶ step seek, the seek STEP model (Up/Down), speed, mute,
// and "enlarge" (in-app expand, not OS fullscreen), plus the time readout and the
// keyboard contract. Both the full VOD player (vodplayer.js) and the Patterns
// inline moment player (patterns.js) use this so they look + behave identically.
//
// Design rules (preserve the house style of both pages):
//   • This module attaches listeners ONLY to the <video> it owns (and, optionally,
//     ONE document keydown via attachKeyboard). Modal expansion temporarily adds
//     a focus guard. It NEVER adds a document *click*
//     listener — each page keeps its single delegated [data-action] handler and
//     forwards transport actions here via handleAction(). That keeps "one click
//     handler per page" intact.
//   • textContent-only writes (no innerHTML for server/data strings). The bar
//     markup emitted by renderTransportBar is a fixed, trusted template.
//   • Platform media resolution is shared with the full VOD player.

export { getMedia, resolveAssetUrl } from './platform/index.mjs';
import { gameToMedia, mediaToGame, timeOrigin } from './video-timeline.mjs';

// ── seek-step model (shared) ──────────────────────────────────────────────────
// The skip amount for the ◀▶ transport buttons + Left/Right arrows. Up/Down arrows
// cycle through STEP_CHOICES. Shown as "step Ns" in the time readout.
export const STEP_CHOICES = [1, 2, 5, 10, 30];
export const RATE_CHOICES = [0.25, 0.5, 1, 1.5, 2, 3];

function clock(s) { s = Math.max(0, Math.floor(s || 0)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; }

function setGlyph(button, glyph) {
  if (button) (button.querySelector?.('.tbtn-icon') || button).textContent = glyph;
}

function shortcutHints(keys) {
  const hints = document.createElement('span');
  hints.className = 'transport-shortcuts';
  hints.setAttribute('aria-hidden', 'true');
  for (const key of keys) {
    const cap = document.createElement('kbd');
    cap.textContent = key;
    hints.appendChild(cap);
  }
  return hints;
}

function stepReadout(seconds) {
  const step = document.createElement('span');
  step.className = 'step';
  step.textContent = `Step ${seconds}s`;
  step.appendChild(shortcutHints(['↑', '↓']));
  return step;
}

function editableTarget(target) {
  return target?.isContentEditable || /^(INPUT|TEXTAREA|SELECT|OPTION)$/.test(target?.tagName || '') || !!target?.closest?.('select');
}

// ── transport bar markup ─────────────────────────────────────────────────────
// Emit the transport bar into `host`, returning the element refs the factory
// needs. Reuses the existing .vp-transport / .tbtn / .ttime / .vp-rate-sel
// classes so both pages get the identical glass look. `idPrefix` scopes the ids
// so the bar can coexist with vodplayer's static one. Both layouts keep the Speed
// label and key hints; the host stylesheet wraps their groups on small screens.
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
  const buttonContent = (button, glyph, key) => {
    const icon = mk('span', 'tbtn-icon', { 'aria-hidden': 'true' });
    icon.textContent = glyph;
    button.appendChild(icon);
    if (key) {
      const hint = mk('kbd', null, { 'aria-hidden': 'true' });
      hint.textContent = key;
      button.appendChild(hint);
    }
  };

  const back = mk('button', 'tbtn', { id: `${idPrefix}-back`, title: 'Back one step (←). Up/Down arrows change the step.', 'aria-label': 'Back one step', 'aria-keyshortcuts': 'ArrowLeft', 'data-action': 'seek', 'data-delta': '-1', type: 'button' });
  buttonContent(back, '⏮', '←');
  const play = mk('button', 'tbtn play', { id: `${idPrefix}-play-btn`, title: 'Play / Pause', 'aria-label': 'Play or pause', 'data-action': 'playpause', type: 'button' });
  buttonContent(play, '▶');
  const fwd = mk('button', 'tbtn', { id: `${idPrefix}-fwd`, title: 'Forward one step (→). Up/Down arrows change the step.', 'aria-label': 'Forward one step', 'aria-keyshortcuts': 'ArrowRight', 'data-action': 'seek', 'data-delta': '1', type: 'button' });
  buttonContent(fwd, '⏭', '→');

  const time = mk('span', 'ttime', { id: `${idPrefix}-time` });
  // Seed the readout (textContent-only; setTimeReadout rebuilds it the same way).
  time.appendChild(document.createTextNode('0:00 / 0:00'));
  time.appendChild(stepReadout(5));

  const spring = mk('span', 'tspring');

  const rateSel = mk('select', 'vp-rate-sel', { id: `${idPrefix}-rate-sel`, 'aria-label': 'Playback speed', title: 'Playback speed (- slower, + faster)' });
  for (const rate of RATE_CHOICES) {
    const opt = document.createElement('option');
    opt.value = String(rate);
    opt.textContent = `${rate}×`;
    if (rate === 1) opt.selected = true;
    rateSel.appendChild(opt);
  }

  const mute = mk('button', 'tbtn', { id: `${idPrefix}-mute`, title: 'Mute (M)', 'aria-label': 'Mute', 'aria-keyshortcuts': 'M', 'data-action': 'mute', type: 'button' });
  buttonContent(mute, '🔊', 'M');
  const fs = mk('button', 'tbtn', { id: `${idPrefix}-fs`, title: 'Enlarge video (F)', 'aria-label': 'Enlarge video', 'aria-keyshortcuts': 'F', 'data-action': 'fullscreen', type: 'button' });
  buttonContent(fs, '⛶', 'F');

  const seekControls = mk('div', 'transport-seek-controls');
  seekControls.appendChild(back);
  seekControls.appendChild(play);
  seekControls.appendChild(fwd);
  bar.appendChild(seekControls);
  bar.appendChild(time);
  bar.appendChild(spring);
  const options = mk('div', 'transport-options');
  const rateGroup = mk('div', 'transport-rate');
  const lbl = mk('label', 'tlabel', { for: `${idPrefix}-rate-sel` });
  lbl.textContent = 'Speed';
  lbl.appendChild(shortcutHints(['-', '+']));
  rateGroup.appendChild(lbl);
  rateGroup.appendChild(rateSel);
  options.appendChild(rateGroup);
  options.appendChild(mute);
  options.appendChild(fs);
  bar.appendChild(options);
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
  modalExpand = false,  // true → contain focus and disable the page behind the player
  onExpandChange = null, // cb(expanded) for page extras (e.g. scrollIntoView)
  fullGlyphs = true,     // true → ⛶/🗗 enlarge glyphs; false leaves the glyph as-is
  durationFallback = () => 0, // returns a duration when video.duration isn't ready
} = {}) {
  let _step = 5;
  let _timeOrigin = 0;
  let _videoBound = false;
  let _kbHandler = null;
  let _rateHandler = null;
  let _modalState = null;
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

  function dur() { return Number.isFinite(video.duration) && video.duration > 0
    ? mediaToGame(video.duration, _timeOrigin) : durationFallback() || 0; }
  function current() { return mediaToGame(video.currentTime || 0, _timeOrigin); }

  // Render the mono time readout with the persistent "step Ns" tag (textContent
  // only). Mirrors vodplayer.js setTimeReadout byte-for-byte in shape.
  function setTimeReadout(cur, d) {
    if (!timeEl) return;
    while (timeEl.firstChild) timeEl.removeChild(timeEl.firstChild);
    timeEl.appendChild(document.createTextNode(`${clock(cur)} / ${clock(d)}`));
    timeEl.appendChild(stepReadout(_step));
  }

  function onTimeUpdate() {
    const d = dur() || 1;
    const pct = Math.max(0, Math.min(1, current() / d));
    if (seekFillEl) seekFillEl.style.width = `${pct * 100}%`;
    if (playheadEl) playheadEl.style.left = `${pct * 100}%`;
    setTimeReadout(current(), d);
  }
  function onPlay() { setGlyph(playBtn, '❚❚'); }
  function onPause() { setGlyph(playBtn, '▶'); }
  function onVolume() {
    if (!muteBtn) return;
    setGlyph(muteBtn, (video.muted || video.volume === 0) ? '🔇' : '🔊');
    const label = video.muted ? 'Unmute' : 'Mute';
    muteBtn.title = `${label} (M)`;
    muteBtn.setAttribute('aria-label', label);
    muteBtn.setAttribute('aria-pressed', String(!!video.muted));
    muteBtn.setAttribute('aria-keyshortcuts', 'M');
  }
  function syncRateSelection() { if (rateSel) rateSel.value = String(video.playbackRate || 1); }

  function modalFocusables() {
    return [...expandTarget.querySelectorAll('button, [href], input, select, textarea, [tabindex]')]
      .filter(el => !el.disabled && el.tabIndex >= 0 && !el.closest('[hidden], [inert]') && el.getClientRects().length > 0);
  }

  function enterModal(scrollPosition) {
    const doc = expandTarget.ownerDocument;
    const attributes = ['role', 'aria-modal', 'aria-label', 'tabindex'];
    const state = {
      doc, previousFocus: doc.activeElement, scrollPosition,
      attributes: attributes.map(name => [name, expandTarget.getAttribute(name)]),
      background: [], focusGuard: null,
    };
    // Disable siblings along the ancestor path, never the path containing the
    // player. This also preserves any pre-existing inert state on the page.
    for (let branch = expandTarget; branch.parentElement; branch = branch.parentElement) {
      for (const sibling of branch.parentElement.children) {
        if (sibling !== branch) {
          state.background.push([sibling, sibling.inert]);
          sibling.inert = true;
        }
      }
      if (branch.parentElement === doc.body) break;
    }
    expandTarget.setAttribute('role', 'dialog');
    expandTarget.setAttribute('aria-modal', 'true');
    if (!expandTarget.hasAttribute('aria-label') && !expandTarget.hasAttribute('aria-labelledby')) {
      expandTarget.setAttribute('aria-label', 'Expanded video player');
    }
    if (!expandTarget.hasAttribute('tabindex')) expandTarget.setAttribute('tabindex', '-1');
    const focusPlayer = () => {
      const controls = modalFocusables();
      (controls.includes(fsBtn) ? fsBtn : controls[0] || expandTarget).focus({ preventScroll: true });
    };
    state.focusGuard = ev => { if (!expandTarget.contains(ev.target)) focusPlayer(); };
    _modalState = state;
    doc.addEventListener('focusin', state.focusGuard);
    focusPlayer();
  }

  function leaveModal() {
    if (!_modalState) return;
    const state = _modalState;
    _modalState = null;
    state.doc.removeEventListener('focusin', state.focusGuard);
    for (const [el, inert] of state.background) el.inert = inert;
    for (const [name, value] of state.attributes) {
      if (value === null) expandTarget.removeAttribute(name);
      else expandTarget.setAttribute(name, value);
    }
    if (state.previousFocus?.isConnected) state.previousFocus.focus({ preventScroll: true });
    state.doc.defaultView?.scrollTo({ ...state.scrollPosition, behavior: 'instant' });
  }

  function containModalTab(ev) {
    const controls = modalFocusables();
    const active = _modalState.doc.activeElement;
    const index = controls.indexOf(active);
    if (!controls.length) {
      ev.preventDefault();
      expandTarget.focus({ preventScroll: true });
    } else if (index < 0 || (ev.shiftKey ? index === 0 : index === controls.length - 1)) {
      ev.preventDefault();
      controls[ev.shiftKey ? controls.length - 1 : 0].focus({ preventScroll: true });
    }
  }

  const api = {
    get stepSeconds() { return _step; },
    get currentTime() { return current(); },
    get duration() { return dur(); },
    setTimeOrigin(value) { _timeOrigin = timeOrigin(value); },

    // Snap to the nearest allowed choice and refresh the readout.
    setStep(seconds) {
      const i = STEP_CHOICES.indexOf(seconds);
      _step = i >= 0 ? STEP_CHOICES[i] : 5;
      setTimeReadout(current(), dur());
    },
    // Move the step up/down the choice list (Up = larger, Down = smaller).
    nudgeStep(direction) {
      let i = STEP_CHOICES.indexOf(_step);
      if (i < 0) i = 2; // default index (5s)
      i = Math.max(0, Math.min(STEP_CHOICES.length - 1, i + (direction > 0 ? 1 : -1)));
      api.setStep(STEP_CHOICES[i]);
    },

    seekTo(seconds) {
      if (Number.isFinite(seconds)) video.currentTime = gameToMedia(seconds, _timeOrigin);
    },
    seekByStep(direction) {
      api.seekTo(current() + (direction < 0 ? -1 : 1) * _step);
    },

    play() { video.play().catch(() => {}); },
    pause() { video.pause(); },
    toggle() { video.paused ? api.play() : api.pause(); },
    setRate(n) {
      const requested = Number(n);
      const rate = Number.isFinite(requested)
        ? RATE_CHOICES.reduce((best, candidate) => Math.abs(candidate - requested) < Math.abs(best - requested) ? candidate : best)
        : 1;
      video.playbackRate = rate;
      syncRateSelection();
    },
    nudgeRate(direction) {
      const currentRate = Number(video.playbackRate) || 1;
      const next = direction > 0
        ? RATE_CHOICES.find(rate => rate > currentRate) ?? RATE_CHOICES.at(-1)
        : RATE_CHOICES.findLast(rate => rate < currentRate) ?? RATE_CHOICES[0];
      api.setRate(next);
    },
    toggleMute() { video.muted = !video.muted; onVolume(); },

    isExpanded() { return !!(expandTarget && expandTarget.classList.contains(expandClass)); },
    // In-app enlarge (NOT OS fullscreen): toggle the expand class on the host. The
    // host's CSS does the layout change. Page extras run via onExpandChange.
    toggleEnlarge() {
      if (!expandTarget) return false;
      // Loading/empty players have no visible dialog or exit control yet.
      if (modalExpand && !api.isExpanded() && expandTarget.closest('[hidden]')) return false;
      // A fixed player leaves normal flow and can clamp a page scrolled near
      // its bottom. Capture before the class changes or any layout read occurs.
      const view = expandTarget.ownerDocument?.defaultView;
      const scrollPosition = modalExpand && !api.isExpanded()
        ? { left: view?.scrollX || 0, top: view?.scrollY || 0 } : null;
      const expanded = expandTarget.classList.toggle(expandClass);
      if (modalExpand) expanded ? enterModal(scrollPosition) : leaveModal();
      if (fullGlyphs && fsBtn) {
        setGlyph(fsBtn, expanded ? '🗗' : '⛶');
      }
      if (fsBtn) {
        fsBtn.title = expanded ? 'Restore layout (F or Esc)' : 'Enlarge video (F)';
        fsBtn.setAttribute('aria-label', expanded ? 'Exit expanded player' : 'Enlarge video');
        fsBtn.setAttribute('aria-expanded', String(expanded));
        fsBtn.setAttribute('aria-keyshortcuts', expanded ? 'F Escape' : 'F');
      }
      if (typeof onExpandChange === 'function') onExpandChange(expanded);
      return expanded;
    },

    // Load a local asset URL into the video; on metadata, jump to startSeconds and
    // (optionally) play. Mirrors vodplayer onMeta jump + patterns playMoment jump.
    // onError lets the page surface a message + reset its poster.
    load(assetUrl, { startSeconds = 0, gameTimeAtVideoStart = 0, autoplay = true, onError = null } = {}) {
      // Drop any still-pending listeners from a prior load that never fired, so they
      // can't fire late against this clip (or stack across clips).
      clearLoadListeners();
      api.setTimeOrigin(gameTimeAtVideoStart);
      video.src = assetUrl;
      const jump = () => {
        _lastJump = null; // fired → consumed
        try { api.seekTo(startSeconds); } catch (_) { /* ignore */ }
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
      if (modalExpand && api.isExpanded()) api.toggleEnlarge();
      clearLoadListeners();
      try { video.pause(); } catch (_) { /* ignore */ }
      video.removeAttribute('src');
      try { video.load(); } catch (_) { /* ignore */ }
      setGlyph(playBtn, '▶');
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

    // Both players use this contract after their page-specific write/correction
    // handlers. Native editing, native button activation and consumed keys win.
    handleShortcut(ev) {
      if (!ev || ev.defaultPrevented || ev.ctrlKey || ev.metaKey || ev.altKey || ev.isComposing) return false;
      if (_modalState) {
        if (ev.key === 'Escape') { ev.preventDefault(); api.toggleEnlarge(); return true; }
        if (ev.key === 'Tab') { containModalTab(ev); return true; }
      }
      if (editableTarget(ev.target)) return false;
      if ((ev.key === ' ' || ev.key === 'Enter') && ev.target?.closest?.('button')) return false;
      let action;
      switch (ev.key) {
        case 'ArrowUp': action = () => api.nudgeStep(1); break;
        case 'ArrowDown': action = () => api.nudgeStep(-1); break;
        case '-': action = () => api.nudgeRate(-1); break;
        case '+': case '=': action = () => api.nudgeRate(1); break;
        case 'm': case 'M': action = () => { if (!ev.repeat) api.toggleMute(); }; break;
        case 'f': case 'F':
          if (!expandTarget) return false;
          action = () => { if (!ev.repeat) api.toggleEnlarge(); }; break;
        case 'Escape':
          if (!api.isExpanded()) return false;
          action = () => api.toggleEnlarge(); break;
        case ' ': case 'ArrowLeft': case 'ArrowRight':
          if (!video || (!video.src && !video.srcObject)) return false;
          action = ev.key === ' ' ? () => api.toggle() : () => api.seekByStep(ev.key === 'ArrowLeft' ? -1 : 1);
          break;
        default: return false;
      }
      ev.preventDefault();
      action();
      return true;
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
      video.addEventListener('ratechange', syncRateSelection);
      onVolume();
      syncRateSelection();
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
    refreshReadout() { setTimeReadout(current(), dur()); },

    // Optional global keyboard for the transport keys. Adds ONE document keydown.
    // Write keys (B/I/O/S) stay in the page. onJumpRow, if given, handles
    // Enter/Space on a focused row before the shared transport keys.
    attachKeyboard({ onJumpRow = null } = {}) {
      if (_kbHandler) return;
      _kbHandler = (ev) => {
        if (ev.defaultPrevented || ev.ctrlKey || ev.metaKey || ev.altKey || ev.isComposing) return;
        if (!editableTarget(ev.target) && onJumpRow && (ev.key === 'Enter' || ev.key === ' ')) {
          if (onJumpRow(ev)) return;
        }
        api.handleShortcut(ev);
      };
      document.addEventListener('keydown', _kbHandler);
    },
    detachKeyboard() {
      if (modalExpand && api.isExpanded()) api.toggleEnlarge();
      if (_kbHandler) { document.removeEventListener('keydown', _kbHandler); _kbHandler = null; }
    },
  };

  return api;
}
