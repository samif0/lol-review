import { $, show, clear, tpl } from './dom.mjs';
import { readSnapshot } from './data.mjs';
import { getInvoke } from './platform/index.mjs';

// Revu desktop — Patterns page renderer for the glass-aurora layout.
// Renders the JSON returned by the Electron command `get_patterns`
// (see Revu.Sidecar GET /api/patterns). Mirrors app.js / games.js conventions:
//   • getInvoke() uses the shared platform boundary and detects browser previews.
//   • Outside Electron it fetches ./sample-patterns.json so the page previews in a
//     plain browser.
//   • Every server string is written via textContent (never innerHTML) so the
//     surface stays XSS-free; colors arrive as *Hex strings applied to style
//     properties only.
//   • ONE delegated [data-action] click handler.
//
// This page surfaces the cross-game pattern playlists for review. Two WRITES
// are live: "Mark pattern reviewed" (mark_pattern_reviewed) and the per-moment
// note autosave (save_pattern_moment_note) — see markReviewed() and
// commitPendingNote() below. Selecting a pattern and stepping through its
// moments is pure client-side over the loaded snapshot.
//
// The inline moment clip plays on a SHARED transport (./vodtransport.js) — the
// same core the full VOD player uses — so the inline player looks + behaves
// identically (play/pause, ◀▶ step seek, speed, mute, enlarge, keyboard shortcuts).

import { createTransport, resolveAssetUrl, getMedia, renderTransportBar } from './vodtransport.js';

// ── invoke resolver (writes) ─────────────────────────────────────────────────
// getInvoke() keeps serving the WRITE path (save_pattern_moment_note,
// mark_pattern_reviewed) exactly as before. Media URLs are
// now obtained from the shared module's getMedia() and cached in _core (see boot).



// ── small DOM helpers ───────────────────────────────────────────────────────

// ── module state: the loaded snapshot + the active pattern / moment indices ──
let _data = null;
let _patIdx = 0;  // index into _data.patterns
let _momIdx = 0;  // index into the active pattern's moments
let _reviewSaving = false;
let _qualitySaving = false;
let _trendRefreshNeeded = false;
let _view = 'trend';
let _viewInitialized = false;

// ── note-autosave state ──────────────────────────────────────────────────────
// Mirrors PatternReviewViewModel: a short pause after typing flushes the note
// without extracting a clip. _suppressNoteSave gates the programmatic
// value-set when a moment loads. We capture the moment being edited so leaving it
// flushes the RIGHT moment (navigation changes activeMoment under us).
let _suppressNoteSave = false;
let _noteTimer = null;
let _editingMoment = null;   // the moment object whose note is in the textarea
const NOTE_DEBOUNCE_MS = 900;
const MOMENT_BATCH_SIZE = 8;

// Shared transport core (play/seek/step/rate/mute/enlarge + keyboard) and the
// cached platform media interface ({invoke, resolveMedia}) used to resolve the asset URL.
let _T = null;
let _core = null;

function reviewMode(p) { return p?.reviewMode === 'saved' ? 'saved' : 'trend'; }
function patterns() {
  return Array.isArray(_data?.patterns)
    ? _data.patterns.filter(p => reviewMode(p) === _view)
      .sort((a, b) => Number(!!a.isReviewed) - Number(!!b.isReviewed))
    : [];
}
function activePattern() { return patterns()[_patIdx] || null; }
function activeMoments() {
  const p = activePattern();
  return Array.isArray(p?.moments) ? p.moments : [];
}
function activeMoment() { return activeMoments()[_momIdx] || null; }

// Only display labels are normalized; player-written titles and notes stay intact.
function displayLabel(value) {
  const labels = { HIGH: 'High', MEDIUM: 'Medium', LOW: 'Low', WIN: 'Win', LOSS: 'Loss',
    GOOD: 'Good', BAD: 'To improve', NEUTRAL: 'Neutral' };
  return labels[String(value || '').toUpperCase()] || value || '';
}

function momentTime(seconds) {
  const time = Math.max(0, Math.floor(Number(seconds) || 0));
  return `${Math.floor(time / 60)}:${String(time % 60).padStart(2, '0')}`;
}

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchPatterns() {
  return readSnapshot('get_patterns', 'sample-patterns.json');
}

// ── render: header status line ──────────────────────────────────────────────
function renderHeader(d) {
  const all = Array.isArray(d?.patterns) ? d.patterns : [];
  const trends = all.filter(p => reviewMode(p) === 'trend');
  const saved = all.filter(p => reviewMode(p) === 'saved');
  const pending = d.pendingCount ?? trends.filter(p => !p.isReviewed).length;
  const parts = [pending === 0 ? 'No new mistake trends' : `${pending} mistake trend${pending === 1 ? '' : 's'} to review`];
  if (d.reviewedPatternCount) parts.push(`${d.reviewedPatternCount} reviewed`);
  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = parts.join(' · ');
  for (const mode of ['trend', 'saved']) {
    const button = $(`pat-view-${mode}`);
    if (button) button.setAttribute('aria-pressed', String(_view === mode));
  }
  $('pat-trend-count').textContent = String(trends.length);
  $('pat-saved-count').textContent = String(saved.length);
  $('pat-view-help').textContent = _view === 'trend'
    ? 'Compare clips and bookmarks marked To improve for your current learning objectives, repeated across at least two games. Reviewed trends stay here to revisit.'
    : 'Revisit the clips and bookmarks you saved, grouped by learning objective. Good examples and mistakes belong here.';
  $('pat-picker-title').textContent = _view === 'trend' ? 'Choose a mistake trend' : 'Choose saved moments';
  show($('pat-refresh-trends'), _trendRefreshNeeded);
}

// ── render: pattern selector cards ──────────────────────────────────────────
// The whole card selects the pattern (select_pattern). Reuses the .gamerow hover
// hype; the left edge bar carries the pattern's severity color. Completed
// reviews stay selectable so their saved moments can be revisited.
function buildPatCard(p, idx) {
  const el = tpl('tpl-patcard');
  const sev = el.querySelector('.pat-sev');
  const title = el.querySelector('.pat-card-title');
  const detail = el.querySelector('.pat-card-detail');
  const sub = el.querySelector('.pat-card-sub');
  const state = el.querySelector('.pat-card-state');
  const cue = el.querySelector('.gamerow-cue');

  const saved = reviewMode(p) === 'saved';
  sev.textContent = saved ? 'Saved moments' : displayLabel(p.severityLabel || p.severity);
  if (p.severityHex) {
    sev.style.color = p.severityHex;
    sev.style.borderColor = p.severityHex;
  }

  title.textContent = p.title || '';
  detail.textContent = p.detail || '';
  sub.textContent = p.subtitle || '';

  state.textContent = saved ? 'Revisit' : p.isReviewed ? 'Reviewed' : 'To review';
  state.classList.toggle('warn', !saved && !p.isReviewed);

  // Active card reads loud (accent rim) so the selection is obvious.
  if (idx === _patIdx) el.classList.add('pat-card-active');
  el.setAttribute('aria-pressed', String(idx === _patIdx));

  if (cue) cue.firstChild.textContent = (idx === _patIdx ? 'Selected' : saved || p.isReviewed ? 'Revisit moments' : 'Compare moments') + ' ';

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
    const pending = list.filter(p => !p.isReviewed).length;
    sub.textContent = _view === 'saved' ? `${list.length} collection${list.length === 1 ? '' : 's'}`
      : `${pending} to review · ${list.length - pending} reviewed`;
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
  rtag.textContent = displayLabel(m.resultLabel);
  rtag.classList.toggle('win', !!m.win);
  rtag.classList.toggle('loss', !m.win);
  if (m.resultHex) {
    rtag.style.color = m.resultHex;
    rtag.style.borderColor = m.resultHex;
  }
  $('m-title').textContent = m.title || '';
  $('m-glabel').textContent = [m.championLabel, m.timeLabel].filter(Boolean).join(' · ');
  $('m-source-label').textContent = m.sourceKind === 'bookmark' ? 'Bookmark preview · up to 15s each side'
    : 'Saved clip';

  // VOD surface — header text + scrub endpoints; degrade gracefully with nothing
  // to play. A moment plays from the game's recording when it is on disk, else
  // from the clip file it kept (the sidecar only admits moments with one of the
  // two, or start-less game-level anchors).
  const playable = playableSource(m) !== null;
  $('m-vhead').textContent = playable ? 'Play this moment' : '';
  show($('m-novod'), !playable);
  show($('m-play'), playable);
  const surface = $('m-surface');
  surface.classList.toggle('pat-surface-novod', !playable);
  surface.setAttribute('aria-label', playable ? 'Play this moment' : 'No recording available for this moment');
  surface.setAttribute('aria-disabled', String(!playable));
  surface.tabIndex = playable ? 0 : -1;
  if (m.gameId != null) surface.dataset.gameId = String(m.gameId);
  // Stamp the moment's start time so the VOD player can jump straight to it.
  if (m.startTimeSeconds != null) surface.dataset.startSeconds = String(m.startTimeSeconds);
  else delete surface.dataset.startSeconds;
  $('m-tstart').textContent = m.startTimeSeconds != null ? momentTime(m.startTimeSeconds) : m.timeLabel || '';
  // The scrub's right edge is the moment's END time (fall back to the start
  // label only when the moment has no timed end) — both ends showing the same
  // label made the strip meaningless.
  $('m-tend').textContent = m.endTimeSeconds != null
    ? momentTime(m.endTimeSeconds)
    : (m.timeLabel || '');

  // Note panel — editable, autosaves on pause/blur. Load WITHOUT triggering a
  // save (the programmatic value-set must not look like a user edit). Bind this
  // moment as the one the textarea is now editing.
  const note = $('m-note');
  _editingMoment = m;
  _suppressNoteSave = true;
  note.value = m.note || '';
  _suppressNoteSave = false;
  setMomentStatus('');
  show($('m-clipt'), !!m.hasClip || m.sourceKind === 'clip');
  renderQuality();

  // Prev / next bounds.
  const moms = activeMoments();
  $('m-position').textContent = `Moment ${_momIdx + 1} of ${moms.length}`;
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
  el.querySelector('.pat-moment-number').textContent = String(idx + 1);

  rg.textContent = [m.championLabel, m.timeLabel].filter(Boolean).join(' · ');

  show(clip, true);
  if (clip) clip.textContent = m.sourceKind === 'bookmark' ? 'Bookmark' : 'Clip';

  const polarity = m.polarity || 'neutral';
  pol.textContent = displayLabel(m.polarityLabel || polarity);
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
  el.setAttribute('aria-pressed', String(idx === _momIdx));
  el.setAttribute('aria-label', `Moment ${idx + 1}: ${m.title || m.championLabel || 'Review moment'}`);

  el.dataset.momIdx = String(idx);
  return el;
}

function renderRail() {
  const host = $('pat-rail');
  clear(host);
  const moms = activeMoments();

  // "24 of 500" when the playlist is capped or some moments have nothing left to
  // watch — the card still counts every instance the detector found.
  const sub = $('rail-sub');
  const p = activePattern();
  const total = Number(p && p.totalMomentCount) || moms.length;
  const batchStart = Math.floor(_momIdx / MOMENT_BATCH_SIZE) * MOMENT_BATCH_SIZE;
  const batchEnd = Math.min(batchStart + MOMENT_BATCH_SIZE, moms.length);
  if (sub) {
    sub.textContent = total > moms.length
      ? `${moms.length} of ${total} moments`
      : `${moms.length} moment${moms.length === 1 ? '' : 's'}`;
    const gone = Number(p && p.unwatchableMomentCount) || 0;
    sub.title = gone > 0
      ? `${gone} moment${gone === 1 ? '' : 's'} left out: recording gone and no clip kept.`
      : '';
  }

  moms.slice(batchStart, batchEnd).forEach((m, i) => host.appendChild(buildMomRow(m, batchStart + i)));
  show($('pat-batches'), moms.length > MOMENT_BATCH_SIZE);
  $('pat-batch-label').textContent = `${batchStart + 1}–${batchEnd} of ${moms.length}`;
  $('pat-batch-prev').disabled = batchStart === 0;
  $('pat-batch-next').disabled = batchEnd >= moms.length;
}

// ── render: finish-review controls ─────────────────────────────────────────
function renderClosure() {
  const p = activePattern();
  if (!p) return;
  const saved = reviewMode(p) === 'saved';
  show($('pat-finish'), !saved);
  show($('pat-pending'), !saved);
  $('pat-pending-text').textContent =
    p.isReviewed ? 'You have reviewed this trend. You can return to these moments and update your takeaways anytime.'
      : 'After comparing these saved mistakes, decide what to try next and finish this review.';
  const btn = $('pat-markrev');
  if (btn) btn.disabled = _reviewSaving || _qualitySaving || _trendRefreshNeeded;
  show(btn, !saved && !p.isReviewed);
}

// ── render: the active-pattern panel (player + rail + closure) ──────────────
function renderActive() {
  const p = activePattern();
  if (!p) {
    show($('pat-main'), false);
    return;
  }
  show($('pat-main'), true);
  $('pat-review-title').textContent = p.title || 'Selected pattern';
  $('pat-compare-title').textContent = reviewMode(p) === 'saved' ? 'Revisit your moments' : 'Compare the mistakes';
  $('pat-compare-help').textContent = reviewMode(p) === 'saved'
    ? 'Replay what you saved and revisit your takeaways. Bookmarks play a short window from the recording.'
    : 'Watch these saved moments across games. What decision keeps repeating, and what will you change next time?';
  const hasMoments = activeMoments().length > 0;
  show($('pat-no-moments'), !hasMoments);
  show(document.querySelector('.pat-playcol'), hasMoments);
  show(document.querySelector('.pat-railcol'), hasMoments);
  show($('pat-review-status'), false);
  if (!hasMoments) return;
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
  const selectedKey = activePattern()?.patternKey;
  _data = d;
  if (!_viewInitialized && Array.isArray(d?.patterns) && d.patterns.length) {
    _view = d.patterns.some(p => reviewMode(p) === 'trend') ? 'trend' : 'saved';
    _viewInitialized = true;
  }
  clearError();

  // A backend failure arrives as errorText on an otherwise-valid snapshot (the
  // sidecar still answers 200) — surface it instead of a clean empty state.
  if (d && d.errorText) {
    renderError(new Error(d.errorText));
  }

  const list = patterns();
  if (list.length === 0) {
    resetInlineVideo();
    _editingMoment = null;
    renderHeader(d);
    renderPicker();
    show($('pat-main'), false);
    // A load failure shows the error panel INSTEAD of the "no patterns yet"
    // copy — the two must never claim the empty state simultaneously.
    const empty = $('pat-empty');
    show(empty, !(d && d.errorText));
    $('pat-empty-h').textContent = _view === 'saved' ? 'No saved moments yet.' : 'No recurring mistakes yet.';
    $('pat-empty-p').textContent = _view === 'saved'
      ? 'Save a clip or bookmark while reviewing a game. Attach a learning objective to keep related moments together.'
      : 'A trend appears when clips or bookmarks marked To improve repeat across at least two games for a current learning objective. Your other saved moments are ready to revisit in Saved moments.';
    show($('pat-reload'), false);
    playEntrance();
    return;
  }
  show($('pat-empty'), false);

  // Retain the selection when refreshing, including a completed review.
  const selectedIdx = list.findIndex(p => p.patternKey === selectedKey);
  if (selectedIdx >= 0) _patIdx = selectedIdx;
  else { _patIdx = Math.min(_patIdx, list.length - 1); _momIdx = 0; }
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
    _trendRefreshNeeded = false;
    render(data);
  } catch (err) {
    renderError(err);
    console.error('[patterns] load failed:', err);
  } finally {
    _loading = false;
  }
}

// ── selection helpers (pure client-side over the loaded snapshot) ────────────
function selectView(mode) {
  if (_reviewSaving || _qualitySaving || !['trend', 'saved'].includes(mode) || mode === _view) return;
  flushOutgoingNote();
  resetInlineVideo();
  _editingMoment = null;
  _view = mode;
  _viewInitialized = true;
  _patIdx = 0;
  _momIdx = 0;
  render(_data);
}

function selectPattern(idx) {
  if (_reviewSaving || _qualitySaving) return;
  const list = patterns();
  if (idx < 0 || idx >= list.length) return;
  if (idx === _patIdx) { focusReview(); return; }
  // Flush the moment we're leaving before switching patterns.
  flushOutgoingNote();
  resetInlineVideo();   // stop any inline clip from the previous pattern
  _patIdx = idx;
  _momIdx = 0;
  renderPicker();   // refresh active-card highlight
  renderActive();
  focusReview();
}

function focusReview() {
  const heading = $('pat-review-title');
  heading?.focus({ preventScroll: true });
  heading?.closest('.pat-review-heading')?.scrollIntoView({ block: 'start',
    behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' });
}

function gotoMoment(idx) {
  if (_reviewSaving || _qualitySaving) return;
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

// ── note autosave (WRITE) ────────────────────────────────────────────────────
// Notes update the saved clip or bookmark. Viewing or editing a bookmark never
// extracts a clip; its preview remains a window into the original recording.

function setMomentStatus(msg) {
  const el = $('m-nstatus');
  if (!el) return;
  el.textContent = msg || '';
  show(el, !!msg);
}

// Flush a SPECIFIC moment's note (captured so navigation flushes the right one).
let _noteWrites = Promise.resolve();
function flushMomentNote(moment, text) {
  // Blur and Finish can flush the same note together. Serialize writes so Finish
  // waits for the accepted save and never removes a pattern with an unsaved note.
  const write = _noteWrites.then(() => saveMomentNote(moment, text));
  _noteWrites = write.catch(() => false);
  return write;
}

async function saveMomentNote(moment, text) {
  if (!moment) return true;
  const trimmed = (text || '').trim();
  const prev = (moment.note || '').trim();
  if (trimmed === prev) return true;

  const invoke = await getInvoke();
  try {
    const payload = {
      evidenceId: moment.evidenceId,
      bookmarkId: moment.bookmarkId ?? null,
      autoClip: false,
      text: trimmed,
      gameId: moment.gameId,
      championName: moment.championName || '',
      vodPath: moment.vodPath || '',
      title: moment.title || '',
      polarity: moment.polarity || 'neutral',
      // null (NOT 0) for a start-less game-level moment — the server's clip
      // branch requires a real start so it never extracts a garbage 0:00 clip.
      startTimeS: moment.startTimeSeconds != null ? moment.startTimeSeconds : null,
      endTimeS: moment.endTimeSeconds != null ? moment.endTimeSeconds : null,
      alreadyClipped: !!moment.hasClip || moment.sourceKind === 'clip',
    };
    const res = invoke ? await invoke('save_pattern_moment_note', { payload }) : null;
    if (res?.ok === false) throw new Error(res.error || 'Note save failed');
    // Reflect the saved state on the in-memory moment so re-renders are correct.
    moment.note = trimmed;
    moment.hasNote = trimmed.length > 0;
    // A moment can appear both in its objective collection and a mistake trend.
    for (const p of (_data?.patterns || [])) {
      for (const other of (p.moments || [])) {
        const same = moment.bookmarkId != null ? other.bookmarkId === moment.bookmarkId
          : moment.evidenceId != null && other.evidenceId === moment.evidenceId;
        if (same) { other.note = trimmed; other.hasNote = trimmed.length > 0; }
      }
    }

    if (ReferenceEquals(moment, activeMoment())) {
      setMomentStatus('Saved');
    }
    // Keep the rail note preview and finish controls in sync.
    renderRail();
    renderClosure();
    return true;
  } catch (err) {
    if (ReferenceEquals(moment, activeMoment())) setMomentStatus("Couldn't save");
    console.error('[patterns] save_pattern_moment_note failed:', err);
    return false;
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
  return flushMomentNote(moment, text);
}

function renderQuality() {
  const m = activeMoment();
  for (const quality of ['good', 'bad', 'neutral']) {
    const button = $(`m-quality-${quality}`);
    if (!button) continue;
    button.setAttribute('aria-pressed', String((m?.polarity || 'neutral') === quality));
    button.disabled = _qualitySaving || _reviewSaving;
  }
}

async function setMomentQuality(quality) {
  const moment = activeMoment();
  if (!moment || _qualitySaving || _reviewSaving || !['good', 'bad', 'neutral'].includes(quality)
    || quality === (moment.polarity || 'neutral')) return;
  _qualitySaving = true;
  renderQuality();
  const note = $('m-note');
  if (note) note.readOnly = true;
  try {
    if (!await commitPendingNote()) throw new Error('Save the note before changing the rating');
    const invoke = await getInvoke();
    const bookmark = Number(moment.bookmarkId) > 0;
    const result = invoke ? await invoke(bookmark ? 'set_bookmark_quality' : 'set_evidence_polarity', {
      payload: bookmark ? { bookmarkId: moment.bookmarkId, quality }
        : { evidenceId: moment.evidenceId, polarity: quality },
    }) : null;
    if (result?.ok === false) throw new Error(result.error || 'Rating save failed');
    for (const p of (_data?.patterns || [])) {
      for (const other of (p.moments || [])) {
        const same = moment.bookmarkId != null ? other.bookmarkId === moment.bookmarkId
          : moment.evidenceId != null && other.evidenceId === moment.evidenceId;
        if (same) {
          other.polarity = quality;
          other.polarityLabel = displayLabel(quality);
          other.accentHex = quality === 'good' ? '#8ee7ba' : quality === 'bad' ? '#f3a3a8' : '#aaa7b5';
        }
      }
    }
    _trendRefreshNeeded = true;
    renderHeader(_data);
    renderRail();
    setMomentStatus('Rating saved. Update trends to apply it.');
  } catch (err) {
    setMomentStatus("Couldn't save the rating. Please try again.");
    console.error('[patterns] rating save failed:', err);
  } finally {
    _qualitySaving = false;
    if (note) note.readOnly = false;
    renderQuality();
    renderClosure();
  }
}

// ── mark pattern reviewed (WRITE) ────────────────────────────────────────────
function keepReviewedPattern(p) {
  p.isReviewed = true;
  _data.pendingCount = Math.max(0, (_data.pendingCount ?? 1) - 1);
  _data.reviewedPatternCount = (_data.reviewedPatternCount ?? 0) + 1;
  _data.hasPending = _data.pendingCount > 0;
  resetInlineVideo();
  _editingMoment = null;
  _patIdx = 0;
  _momIdx = 0;
  render(_data);
}

async function markReviewed() {
  const p = activePattern();
  if (!p || _reviewSaving || _qualitySaving || _trendRefreshNeeded || p.isReviewed || reviewMode(p) === 'saved') return;
  _reviewSaving = true;
  const btn = $('pat-markrev');
  if (btn) btn.disabled = true;
  const note = $('m-note');
  if (note) note.readOnly = true;
  try {
    if (!await commitPendingNote()) throw new Error('Save the note before finishing');
    const invoke = await getInvoke();
    const result = invoke ? await invoke('mark_pattern_reviewed', {
      payload: {
        patternKey: p.patternKey,
        kind: p.kind || '',
        momentCount: Array.isArray(p.moments) ? p.moments.length : 0,
      },
    }) : null;
    if (result?.ok === false) throw new Error(result.error || 'Review save failed');
    keepReviewedPattern(p);
    // Refresh the authoritative counts while preserving access to review history.
    if (invoke) await loadPatterns();
    if (activePattern()) focusReview();
    else $('pat-empty-h')?.focus({ preventScroll: true });
  } catch (err) {
    $('pat-review-status').textContent = "Couldn't finish the review. Please try again.";
    show($('pat-review-status'), true);
    console.error('[patterns] mark_pattern_reviewed failed:', err);
  } finally {
    _reviewSaving = false;
    if (btn) btn.disabled = false;
    if (note) note.readOnly = false;
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
let _inlineWindow = null;

// Reset the inline player back to the poster. The media half (pause + clear src) is
// delegated to the transport core; this keeps the patterns-only chrome cleanup (hide
// video + transport bar, drop the playing state, restore the enlarge layout).
function resetInlineVideo() {
  if (_T) { _T.unload(); if (_T.isExpanded()) _T.toggleEnlarge(); }
  show($('m-video'), false);
  show($('m-transport'), false);
  _patVideoLoadedFor = null;
  _inlineActive = false;
  _inlineWindow = null;
  const surface = $('m-surface');
  if (surface) surface.classList.remove('pat-surface-playing');
}

// Load + play the active moment's clip inline via the shared transport. Resolves
// the asset URL from the moment's vodPath, reveals the <video> + transport bar, and
// lets the core jump to the moment's start + play. Clicking the poster the first
// time loads; once loaded, the transport bar (and clicking the video) controls it.
// What to play for a moment: the game's recording seeked to the moment's start,
// else the clip file the moment kept (a clip already starts at the moment, so it
// plays from 0). null when neither is on disk.
function playableSource(m) {
  if (!m) return null;
  if (m.hasVod && m.vodPath) return { path: m.vodPath, startSeconds: m.startTimeSeconds != null ? Number(m.startTimeSeconds) : 0,
    endSeconds: m.endTimeSeconds != null ? Number(m.endTimeSeconds) : null,
    gameTimeAtVideoStart: m.gameTimeAtVideoStart || 0 };
  if (m.hasClip && m.clipPath) return { path: m.clipPath, startSeconds: 0 };
  return null;
}

function playMoment() {
  const m = activeMoment();
  const src = playableSource(m);
  if (!m || !src) return;
  const vid = $('m-video');
  const surface = $('m-surface');
  if (!vid || !surface || !_T) return;

  // Already loaded for this moment → just toggle play/pause.
  if (_patVideoLoadedFor === m && vid.getAttribute('src')) {
    _T.toggle();
    return;
  }

  const url = resolveAssetUrl(_core, src.path);
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
  _inlineWindow = Number.isFinite(src.endSeconds) && src.endSeconds > src.startSeconds
    ? { start: src.startSeconds, end: src.endSeconds } : null;
  _T.load(url, {
    startSeconds: src.startSeconds,
    gameTimeAtVideoStart: src.gameTimeAtVideoStart || 0,
    autoplay: true,
    onError: () => { setMomentStatus('Could not load this clip.'); resetInlineVideo(); },
  });
}

// A bookmark preview is a short window of its VOD, not the rest of the match.
// Transport times already include the recording's game-time offset.
function constrainPlayback(replay = false) {
  if (!_T || !_inlineWindow) return;
  const { start, end } = _inlineWindow;
  if (_T.currentTime < start) _T.seekTo(start);
  else if (_T.currentTime >= end) {
    if (replay) _T.seekTo(start);
    else { _T.pause(); if (_T.currentTime > end) _T.seekTo(end); }
  }
}

// ── single delegated action handler ─────────────────────────────────────────
// select_pattern = clicking a pattern selector card (client-side, no backend).
// goto_moment    = clicking a moment in the rail (client-side).
// prev/next_moment = step the active playlist (client-side).
// play_moment    = the VOD surface → load + play the moment's clip INLINE.
// playpause/seek/mute/fullscreen = the shared transport bar, forwarded to _T.
const ACTIONS = new Set(['set_quality', 'select_view', 'select_pattern', 'goto_moment', 'prev_moment', 'next_moment', 'prev_batch', 'next_batch', 'play_moment', 'mark_reviewed', 'reload_patterns', 'playpause', 'seek', 'mute', 'fullscreen']);

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // Transport bar actions (play/pause, ◀▶ step seek, mute, enlarge) are owned by
  // the shared core — forward them and stop. Keeps one delegated handler.
  if (_T && _T.handleAction(action, target)) return;

  if (action === 'set_quality') {
    await setMomentQuality(target.dataset.quality);
    return;
  }
  if (action === 'select_view') {
    selectView(target.dataset.view);
    return;
  }
  if (action === 'prev_batch' || action === 'next_batch') {
    const batchStart = Math.floor(_momIdx / MOMENT_BATCH_SIZE) * MOMENT_BATCH_SIZE;
    gotoMoment(batchStart + (action === 'prev_batch' ? -MOMENT_BATCH_SIZE : MOMENT_BATCH_SIZE));
    return;
  }
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
  if (action === 'reload_patterns') {
    if (_reviewSaving || _qualitySaving) return;
    if (await commitPendingNote()) {
      resetInlineVideo();
      await loadPatterns();
    }
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

// v3.11: a timeline correction landed somewhere (VOD panel, review death links);
// pattern moments anchor on those events, so refetch. Never while the user is typing
// a note. loadPatterns() is a no-op when a load is already in flight.
window.addEventListener('revu:events-corrected', () => {
  const a = document.activeElement;
  if (a && (a.tagName === 'TEXTAREA' || a.tagName === 'INPUT')) return;
  loadPatterns();
});

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
    modalExpand: true,
    fullGlyphs: true,
  });
  _T.attachVideo({ clickToToggle: true, stopProp: true });
  $('m-video').addEventListener('timeupdate', () => constrainPlayback());
  $('m-video').addEventListener('seeking', () => constrainPlayback());
  $('m-video').addEventListener('play', () => constrainPlayback(true));
  // Transport keyboard (Space, ◀▶ seek, Up/Down step, F/Esc enlarge). #m-note typing
  // is exempt (INPUT/TEXTAREA guard). When a role=button row/surface is focused, let
  // patterns' own keydown (below) handle Enter/Space activation instead of toggling.
  _T.attachKeyboard({
    onJumpRow: (ev) => !!(ev.target.closest && ev.target.closest('[data-action][role="button"]')),
  });

  // Prime the asset/invoke paths under Electron (no-op in browser preview).
  getInvoke();
  _core = await getMedia();
  loadPatterns();
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
