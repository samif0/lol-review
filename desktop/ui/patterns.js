// Revu desktop — Patterns page renderer for the glass-aurora layout.
// Renders the JSON returned by the Tauri command `get_patterns`
// (see Revu.Sidecar GET /api/patterns). Mirrors app.js / games.js conventions:
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.
//   • Outside Tauri it fetches ./sample-patterns.json so the page previews in a
//     plain browser.
//   • Every server string is written via textContent (never innerHTML) so the
//     surface stays XSS-free; colors arrive as *Hex strings applied to style
//     properties only.
//   • ONE delegated [data-action] click handler.
//
// READ-ONLY: this page surfaces the cross-game pattern playlists for review.
// "Mark pattern reviewed" + note writes are DEFERRED (no backend command yet);
// the reviewed state + carry-forward note are display-only. Selecting a pattern
// and stepping through its moments is pure client-side over the loaded snapshot.
//
// The inline moment clip plays on a SHARED transport (./vodtransport.js) — the
// same core the full VOD player uses — so the inline player looks + behaves
// identically (play/pause, ◀▶ step seek, speed, mute, enlarge, keyboard shortcuts).

import { createTransport, resolveAssetUrl, tauriCore, renderTransportBar } from './vodtransport.js';

// ── invoke resolver (writes) ─────────────────────────────────────────────────
// getInvoke() keeps serving the WRITE path (save_pattern_moment_note,
// mark_pattern_reviewed) exactly as before. The asset-url path + convertFileSrc are
// now obtained from the shared module's tauriCore() and cached in _core (see boot).
let _invoke = null;
async function getInvoke() {
  if (_invoke) return _invoke;
  try {
    const mod = await import('@tauri-apps/api/core');
    if (mod && typeof mod.invoke === 'function') {
      _invoke = mod.invoke;
      return _invoke;
    }
  } catch (_) {
    // module not resolvable outside the Tauri bundler — fall through
  }
  if (window.__TAURI__ && window.__TAURI__.core && typeof window.__TAURI__.core.invoke === 'function') {
    _invoke = window.__TAURI__.core.invoke.bind(window.__TAURI__.core);
    return _invoke;
  }
  return null;
}

const isTauri = () => typeof window.__TAURI__ !== 'undefined';

// ── small DOM helpers ───────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }
function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }
function tpl(id) {
  const t = $(id);
  return t.content.firstElementChild.cloneNode(true);
}

// ── module state: the loaded snapshot + the active pattern / moment indices ──
let _data = null;
let _patIdx = 0;  // index into _data.patterns
let _momIdx = 0;  // index into the active pattern's moments

// ── note-autosave state ──────────────────────────────────────────────────────
// Mirrors PatternReviewViewModel: a short pause after typing flushes the note
// (and clips the moment's window once). _suppressNoteSave gates the programmatic
// value-set when a moment loads. We capture the moment being edited so leaving it
// flushes the RIGHT moment (navigation changes activeMoment under us).
let _suppressNoteSave = false;
let _noteTimer = null;
let _editingMoment = null;   // the moment object whose note is in the textarea
const NOTE_DEBOUNCE_MS = 900;

// Shared transport core (play/seek/step/rate/mute/enlarge + keyboard) and the
// cached Tauri core ({invoke, convertFileSrc}) used to resolve the asset URL.
let _T = null;
let _core = null;

function patterns() { return Array.isArray(_data?.patterns) ? _data.patterns : []; }
function activePattern() { return patterns()[_patIdx] || null; }
function activeMoments() {
  const p = activePattern();
  return Array.isArray(p?.moments) ? p.moments : [];
}
function activeMoment() { return activeMoments()[_momIdx] || null; }

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchPatterns() {
  // Prefer the REAL backend (Tauri invoke → sidecar → your DB); fall back to the
  // bundled sample only when invoke is genuinely unavailable (browser preview).
  const invoke = await getInvoke();
  if (invoke) {
    return invoke('get_patterns');
  }
  const res = await fetch('./sample-patterns.json');
  if (!res.ok) throw new Error(`sample-patterns.json ${res.status}`);
  return res.json();
}

// ── render: header status line ──────────────────────────────────────────────
function renderHeader(d) {
  const parts = [];
  const total = patterns().length;
  parts.push(total === 0 ? 'No patterns yet' : `${total} pattern${total === 1 ? '' : 's'}`);
  const pending = d.pendingCount ?? 0;
  parts.push(pending === 0 ? 'all reviewed' : `${pending} to review`);
  if (d.reviewedPatternCount != null) parts.push(`${d.reviewedPatternCount} reviewed all-time`);
  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = parts.join(' · ');
}

// ── render: pattern selector cards ──────────────────────────────────────────
// The whole card selects the pattern (select_pattern). Reuses the .gamerow hover
// hype; the left edge bar carries the pattern's severity color. Reviewed patterns
// read calm (dimmed) so pending ones lead the eye.
function buildPatCard(p, idx) {
  const el = tpl('tpl-patcard');
  const sev = el.querySelector('.pat-sev');
  const title = el.querySelector('.pat-card-title');
  const detail = el.querySelector('.pat-card-detail');
  const sub = el.querySelector('.pat-card-sub');
  const state = el.querySelector('.pat-card-state');
  const cue = el.querySelector('.gamerow-cue');

  sev.textContent = p.severityLabel || (p.severity || '').toUpperCase();
  if (p.severityHex) {
    sev.style.color = p.severityHex;
    sev.style.borderColor = p.severityHex;
  }

  title.textContent = p.title || '';
  detail.textContent = p.detail || '';
  sub.textContent = p.subtitle || '';

  // Reviewed vs pending state chip.
  if (p.isReviewed) {
    state.textContent = 'REVIEWED';
    state.classList.add('good');
    el.classList.add('pat-card-viewed');
  } else {
    state.textContent = 'TO REVIEW';
    state.classList.add('warn');
  }

  // Active card reads loud (accent rim) so the selection is obvious.
  if (idx === _patIdx) el.classList.add('pat-card-active');

  if (cue) cue.firstChild.textContent = (idx === _patIdx ? 'OPEN' : 'OPEN') + ' ';

  // Left edge bar rests in the severity color, energizes to accent on hover.
  if (p.severityHex) el.style.setProperty('--wl', p.severityHex);

  el.dataset.patIdx = String(idx);
  return el;
}

function renderPicker() {
  const host = $('pat-pick');
  clear(host);
  const list = patterns();

  if (list.length === 0) {
    show($('pat-label'), false);
    return;
  }
  show($('pat-label'), true);

  const sub = $('pat-sub');
  if (sub) {
    const pending = _data?.pendingCount ?? 0;
    sub.textContent = pending === 0 ? 'all reviewed' : `${pending} pending`;
  }

  list.forEach((p, i) => host.appendChild(buildPatCard(p, i)));
}

// ── render: active-moment player (strip + VOD surface + note) ───────────────
function renderPlayer() {
  const p = activePattern();
  const m = activeMoment();
  if (!p || !m) return;

  // Active-moment strip: WIN/LOSS tag + moment title + champion·time.
  const rtag = $('m-rtag');
  rtag.textContent = m.resultLabel || '';
  rtag.classList.toggle('win', !!m.win);
  rtag.classList.toggle('loss', !m.win);
  if (m.resultHex) {
    rtag.style.color = m.resultHex;
    rtag.style.borderColor = m.resultHex;
  }
  $('m-title').textContent = m.title || '';
  $('m-glabel').textContent = [m.championLabel, m.timeLabel].filter(Boolean).join(' · ');

  // VOD surface — header text + scrub endpoints; degrade gracefully with no VOD.
  $('m-vhead').textContent = (m.videoHeaderText || '') + (m.hasVod ? ': MOMENT CLIP' : '');
  show($('m-novod'), !m.hasVod);
  show($('m-play'), !!m.hasVod);
  const surface = $('m-surface');
  surface.classList.toggle('pat-surface-novod', !m.hasVod);
  if (m.gameId != null) surface.dataset.gameId = String(m.gameId);
  // Stamp the moment's start time so the VOD player can jump straight to it.
  if (m.startTimeSeconds != null) surface.dataset.startSeconds = String(m.startTimeSeconds);
  else delete surface.dataset.startSeconds;
  $('m-tstart').textContent = m.timeLabel || '';
  $('m-tend').textContent = m.timeLabel || '';

  // Note panel — editable, autosaves on pause/blur. Load WITHOUT triggering a
  // save (the programmatic value-set must not look like a user edit). Bind this
  // moment as the one the textarea is now editing.
  const note = $('m-note');
  _editingMoment = m;
  _suppressNoteSave = true;
  note.value = m.note || '';
  _suppressNoteSave = false;
  setMomentStatus('');
  // CLIP KEPT badge only once the moment actually has a saved clip.
  show($('m-clipt'), !!m._clipped || m.sourceKind === 'clip');

  // Prev / next bounds.
  const moms = activeMoments();
  $('m-prev').disabled = _momIdx <= 0;
  $('m-next').disabled = _momIdx >= moms.length - 1;
}

// ── render: moment rail ─────────────────────────────────────────────────────
function buildMomRow(m, idx) {
  const el = tpl('tpl-momrow');
  const rg = el.querySelector('.pat-rg');
  const clip = el.querySelector('.pat-rclip');
  const pol = el.querySelector('.pat-rpol');
  const title = el.querySelector('.pat-rtitle');
  const noteEl = el.querySelector('.pat-rnote');

  rg.textContent = [m.championLabel, m.timeLabel].filter(Boolean).join(' · ');

  // CLIP badge only when this moment kept a clip / has a matched VOD.
  show(clip, m.sourceKind === 'clip' || !!m.hasVod);

  const polarity = m.polarity || 'neutral';
  pol.textContent = m.polarityLabel || polarity.toUpperCase();
  pol.classList.add(polarity);
  if (m.accentHex) pol.style.color = m.accentHex;

  title.textContent = m.title || '';

  if (m.hasNote && m.note) {
    noteEl.textContent = m.note;
    show(noteEl, true);
  }

  // Polarity-colored left border; active / viewed states.
  el.classList.add(polarity);
  if (m.accentHex) el.style.setProperty('--pol', m.accentHex);
  if (idx === _momIdx) el.classList.add('active');
  else if (idx < _momIdx) el.classList.add('viewed');

  el.dataset.momIdx = String(idx);
  return el;
}

function renderRail() {
  const host = $('pat-rail');
  clear(host);
  const moms = activeMoments();

  const sub = $('rail-sub');
  if (sub) sub.textContent = `${moms.length} moment${moms.length === 1 ? '' : 's'}`;

  moms.forEach((m, i) => host.appendChild(buildMomRow(m, i)));
}

// ── render: closure / pending state (read-only) ─────────────────────────────
function renderClosure() {
  const p = activePattern();
  if (!p) return;
  const closed = $('pat-closed');
  const pending = $('pat-pending');

  if (p.isReviewed) {
    const moms = activeMoments();
    const noteCount = moms.filter((m) => m.hasNote).length;
    $('pat-csum').textContent =
      `Worked through ${p.title}: ${moms.length} moment${moms.length === 1 ? '' : 's'} reviewed` +
      (noteCount ? `, ${noteCount} note${noteCount === 1 ? '' : 's'} saved.` : '.');
    show(closed, true);
    show(pending, false);
  } else {
    $('pat-pending-text').textContent =
      'Step through every moment, note what you see, then mark the pattern reviewed.';
    const btn = $('pat-markrev');
    if (btn) btn.disabled = false;
    show(pending, true);
    show(closed, false);
  }
}

// ── render: the active-pattern panel (player + rail + closure) ──────────────
function renderActive() {
  const p = activePattern();
  if (!p) {
    show($('pat-main'), false);
    return;
  }
  show($('pat-main'), true);
  renderPlayer();
  renderRail();
  renderClosure();
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
}
function clearError() { show($('errpanel'), false); }

// ── entrance: stagger the main sections rising in on load ───────────────────
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  const order = [
    $('pat-pick'),
    $('pat-main'),
  ].filter(Boolean);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  _data = d;
  clearError();

  // Seed each moment's local clip flag from the snapshot so Save never re-clips a
  // moment that's already a saved clip (mirrors the VM's HasClip seed at load).
  for (const p of patterns()) {
    for (const m of (Array.isArray(p.moments) ? p.moments : [])) {
      if (m._clipped == null) m._clipped = m.sourceKind === 'clip';
    }
  }

  const list = patterns();
  if (list.length === 0) {
    renderHeader(d);
    renderPicker();
    show($('pat-main'), false);
    const empty = $('pat-empty');
    show(empty, true);
    if (d.emptyText) $('pat-empty-h').textContent = d.emptyText;
    playEntrance();
    return;
  }
  show($('pat-empty'), false);

  // Clamp indices in case the snapshot shrank between loads.
  if (_patIdx >= list.length) _patIdx = 0;
  if (_momIdx >= activeMoments().length) _momIdx = 0;

  renderHeader(d);
  renderPicker();
  renderActive();
  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
let _loading = false;
async function loadPatterns() {
  if (_loading) return;
  _loading = true;
  try {
    const data = await fetchPatterns();
    render(data);
  } catch (err) {
    renderError(err);
    console.error('[patterns] load failed:', err);
  } finally {
    _loading = false;
  }
}

// ── selection helpers (pure client-side over the loaded snapshot) ────────────
function selectPattern(idx) {
  const list = patterns();
  if (idx < 0 || idx >= list.length || idx === _patIdx) {
    if (idx === _patIdx) { renderActive(); }
    return;
  }
  // Flush the moment we're leaving before switching patterns.
  flushOutgoingNote();
  resetInlineVideo();   // stop any inline clip from the previous pattern
  _patIdx = idx;
  _momIdx = 0;
  renderPicker();   // refresh active-card highlight
  renderActive();
}

function gotoMoment(idx) {
  const moms = activeMoments();
  if (idx < 0 || idx >= moms.length) return;
  // Flush the outgoing moment's note (background) before the index changes.
  flushOutgoingNote();
  // If a clip is already up, KEEP PLAYING through the playlist: switch to the new
  // moment and auto-load + play its clip (no drop back to the poster / play icon).
  // Otherwise just show the new moment's poster (first play is a click as before).
  const keepPlaying = _inlineActive;
  _momIdx = idx;
  renderPlayer();
  renderRail();
  if (keepPlaying) playMoment();   // loads the new moment's clip via _T.load (swaps src)
  else resetInlineVideo();         // not playing → poster state for the new moment
}

// Flush the note for the moment currently bound to the textarea (the one we're
// leaving), capturing it so the async save targets the right moment.
function flushOutgoingNote() {
  if (_noteTimer) { clearTimeout(_noteTimer); _noteTimer = null; }
  const moment = _editingMoment;
  if (!moment) return;
  const text = $('m-note') ? $('m-note').value : '';
  // Fire-and-forget; navigation stays snappy (mirrors the VM).
  flushMomentNote(moment, text);
}

// ── note autosave + clip (WRITE) ─────────────────────────────────────────────
// Mirrors PatternReviewViewModel.FlushNoteAsync: persist the note via the sidecar
// (UpdateNoteAsync), which — first time, when the moment has a VOD + a non-empty
// note — silently clips the moment's padded window and attaches it as evidence.
// We pass the moment's clip fields from the loaded snapshot (the server has the
// repo methods but not the VM's in-memory state). No-ops in browser preview.

function setMomentStatus(msg) {
  const el = $('m-nstatus');
  if (!el) return;
  el.textContent = msg || '';
  show(el, !!msg);
}

// Flush a SPECIFIC moment's note (captured so navigation flushes the right one).
async function flushMomentNote(moment, text) {
  if (!moment) return;
  const trimmed = (text || '').trim();
  const prev = (moment.note || '').trim();
  // Nothing changed and no clip pending → don't churn.
  if (trimmed === prev && (trimmed.length === 0 || moment._clipped)) return;

  const invoke = await getInvoke();
  if (!invoke) {
    // Preview: reflect locally so the UI feels right; no backend to persist to.
    moment.note = trimmed;
    moment.hasNote = trimmed.length > 0;
    return;
  }
  try {
    const payload = {
      evidenceId: moment.evidenceId,
      text: trimmed,
      gameId: moment.gameId,
      championName: moment.championName || '',
      vodPath: moment.vodPath || '',
      title: moment.title || '',
      polarity: moment.polarity || 'neutral',
      startTimeS: moment.startTimeSeconds != null ? moment.startTimeSeconds : 0,
      endTimeS: moment.endTimeSeconds != null ? moment.endTimeSeconds : 0,
      alreadyClipped: !!moment._clipped,
    };
    const res = await invoke('save_pattern_moment_note', { payload });
    // Reflect the saved state on the in-memory moment so re-renders are correct.
    moment.note = trimmed;
    moment.hasNote = trimmed.length > 0;
    if (res && res.clipped) moment._clipped = true;

    if (ReferenceEquals(moment, activeMoment())) {
      setMomentStatus(res && res.clipped ? 'Saved · clip kept' : 'Saved');
      show($('m-clipt'), !!moment._clipped || moment.sourceKind === 'clip');
    }
    // Keep the rail note preview + closure note-count in sync.
    renderRail();
    renderClosure();
  } catch (err) {
    if (ReferenceEquals(moment, activeMoment())) setMomentStatus("Couldn't save");
    console.error('[patterns] save_pattern_moment_note failed:', err);
  }
}

// JS has no ReferenceEquals — tiny identity helper to read like the VM.
function ReferenceEquals(a, b) { return a === b; }

// Schedule the active moment's note flush after a typing pause.
function scheduleNoteSave() {
  if (_noteTimer) clearTimeout(_noteTimer);
  setMomentStatus('Saving…');
  const moment = _editingMoment;
  const text = $('m-note') ? $('m-note').value : '';
  _noteTimer = setTimeout(() => { _noteTimer = null; flushMomentNote(moment, text); }, NOTE_DEBOUNCE_MS);
}

// Flush immediately (blur / leaving a moment / before mark-reviewed).
async function commitPendingNote() {
  if (_noteTimer) { clearTimeout(_noteTimer); _noteTimer = null; }
  const moment = _editingMoment;
  const text = $('m-note') ? $('m-note').value : '';
  await flushMomentNote(moment, text);
}

// ── mark pattern reviewed (WRITE) ────────────────────────────────────────────
async function markReviewed() {
  const p = activePattern();
  if (!p) return;
  // Make sure the on-screen note is saved before closing the pattern out.
  await commitPendingNote();
  const invoke = await getInvoke();
  if (!invoke) {
    // Preview: flip locally so the closure panel shows.
    p.isReviewed = true;
    renderPicker(); renderClosure();
    return;
  }
  const btn = $('pat-markrev');
  if (btn) btn.disabled = true;
  try {
    await invoke('mark_pattern_reviewed', {
      payload: {
        patternKey: p.patternKey,
        kind: p.kind || '',
        momentCount: Array.isArray(p.moments) ? p.moments.length : 0,
      },
    });
    p.isReviewed = true;
    renderPicker();   // dim the now-reviewed card
    renderHeader(_data);
    renderClosure();  // swap pending → closed
  } catch (err) {
    if (btn) btn.disabled = false;
    console.error('[patterns] mark_pattern_reviewed failed:', err);
  }
}

// ── inline moment clip player ────────────────────────────────────────────────
// Plays the active moment's VOD IN PLACE on the pattern surface (no navigation to
// the VOD page). Streams the local file via the asset protocol and jumps straight
// to the moment's start time. Switching moments resets the surface back to its
// poster state; pressing play again loads the new moment's clip.
let _patVideoLoadedFor = null; // the moment object whose clip is loaded, or null
// True once a clip has been started inline. Drives "keep playing through the
// playlist": when the user steps to another moment WHILE a clip is up, the new
// moment auto-loads + plays instead of dropping back to the poster. Cleared only by
// a real reset (leaving the pattern, or a load error) — NOT by stepping moments.
let _inlineActive = false;

// Reset the inline player back to the poster. The media half (pause + clear src) is
// delegated to the transport core; this keeps the patterns-only chrome cleanup (hide
// video + transport bar, drop the playing state, restore the enlarge layout).
function resetInlineVideo() {
  if (_T) { _T.unload(); if (_T.isExpanded()) _T.toggleEnlarge(); }
  show($('m-video'), false);
  show($('m-transport'), false);
  _patVideoLoadedFor = null;
  _inlineActive = false;
  const surface = $('m-surface');
  if (surface) surface.classList.remove('pat-surface-playing');
}

// Load + play the active moment's clip inline via the shared transport. Resolves
// the asset URL from the moment's vodPath, reveals the <video> + transport bar, and
// lets the core jump to the moment's start + play. Clicking the poster the first
// time loads; once loaded, the transport bar (and clicking the video) controls it.
function playMoment() {
  const m = activeMoment();
  if (!m || !m.hasVod) return;
  const vid = $('m-video');
  const surface = $('m-surface');
  if (!vid || !surface || !_T) return;

  // Already loaded for this moment → just toggle play/pause.
  if (_patVideoLoadedFor === m && vid.getAttribute('src')) {
    _T.toggle();
    return;
  }

  const url = resolveAssetUrl(_core, m.vodPath);
  if (!url) {
    // Browser preview (or asset protocol unavailable): can't stream a local file.
    setMomentStatus('Video preview is only available in the app.');
    return;
  }

  surface.classList.add('pat-surface-playing');
  show(vid, true);
  show($('m-transport'), true);
  _patVideoLoadedFor = m;
  _inlineActive = true;   // we're now in "playing" mode → stepping moments keeps playing
  _T.load(url, {
    startSeconds: m.startTimeSeconds != null ? Number(m.startTimeSeconds) : 0,
    autoplay: true,
    onError: () => { setMomentStatus('Could not load this clip.'); resetInlineVideo(); },
  });
}

// ── single delegated action handler ─────────────────────────────────────────
// select_pattern = clicking a pattern selector card (client-side, no backend).
// goto_moment    = clicking a moment in the rail (client-side).
// prev/next_moment = step the active playlist (client-side).
// play_moment    = the VOD surface → load + play the moment's clip INLINE.
// playpause/seek/mute/fullscreen = the shared transport bar, forwarded to _T.
const ACTIONS = new Set(['select_pattern', 'goto_moment', 'prev_moment', 'next_moment', 'play_moment', 'mark_reviewed', 'playpause', 'seek', 'mute', 'fullscreen']);

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // Transport bar actions (play/pause, ◀▶ step seek, mute, enlarge) are owned by
  // the shared core — forward them and stop. Keeps one delegated handler.
  if (_T && _T.handleAction(action, target)) return;

  if (action === 'select_pattern') {
    selectPattern(Number(target.dataset.patIdx));
    return;
  }
  if (action === 'goto_moment') {
    gotoMoment(Number(target.dataset.momIdx));
    return;
  }
  if (action === 'prev_moment') {
    gotoMoment(_momIdx - 1);
    return;
  }
  if (action === 'next_moment') {
    gotoMoment(_momIdx + 1);
    return;
  }
  if (action === 'mark_reviewed') {
    await markReviewed();
    return;
  }

  // The VOD surface → open the moment in the full VOD viewer (same as Review).
  if (action === 'play_moment') {
    playMoment();
    return;
  }
});

// Keyboard activation for the role="button" rows / surface (Enter / Space). A
// real <button> (prev/next) handles its own keys natively — don't double-fire.
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  // Don't hijack typing in the note textarea.
  if (ev.target && ev.target.id === 'm-note') return;
  if (ev.target.closest('button')) return;
  const target = ev.target.closest('[data-action][role="button"]');
  if (!target) return;
  ev.preventDefault();
  target.click();
});

// Note textarea: debounced autosave while typing, immediate flush on blur.
// #m-note is a static element, so direct listeners are safe (no per-render leak).
function wireNoteEditor() {
  const note = $('m-note');
  if (!note) return;
  note.addEventListener('input', () => {
    if (_suppressNoteSave) return;   // ignore the programmatic value-set on load
    scheduleNoteSave();
  });
  note.addEventListener('blur', () => {
    if (_suppressNoteSave) return;
    commitPendingNote();
  });
}

// Persist a pending note if the user navigates away / closes the page.
window.addEventListener('beforeunload', () => { flushOutgoingNote(); });

// ── boot ────────────────────────────────────────────────────────────────────
async function boot() {
  wireNoteEditor();

  // Build the shared transport over the inline <video>. It owns the play/pause +
  // mute glyphs, the time readout, the speed select, mute, and the in-app enlarge
  // (toggling .pat-expanded on the .pat-stage card). clickToToggle + stopProp means
  // clicking the playing video toggles playback WITHOUT re-firing the surface's
  // play_moment (this replaces the old standalone stopPropagation hack on #m-video).
  renderTransportBar($('m-transport'), { idPrefix: 'm', compact: true });
  _T = createTransport({
    video: $('m-video'),
    timeEl: $('m-time'),
    playBtn: $('m-play-btn'),
    muteBtn: $('m-mute'),
    fsBtn: $('m-fs'),
    rateSel: $('m-rate-sel'),
    expandTarget: document.querySelector('.pat-stage'),
    expandClass: 'pat-expanded',
    fullGlyphs: true,
  });
  _T.attachVideo({ clickToToggle: true, stopProp: true });
  // Transport keyboard (Space, ◀▶ seek, Up/Down step, F/Esc enlarge). #m-note typing
  // is exempt (INPUT/TEXTAREA guard). When a role=button row/surface is focused, let
  // patterns' own keydown (below) handle Enter/Space activation instead of toggling.
  _T.attachKeyboard({
    onJumpRow: (ev) => !!(ev.target.closest && ev.target.closest('[data-action][role="button"]')),
  });

  // Prime the asset/invoke paths under Tauri (no-op in browser preview).
  getInvoke();
  _core = await tauriCore();
  loadPatterns();
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
