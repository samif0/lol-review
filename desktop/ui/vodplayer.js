// Revu desktop — VOD player. Plays the local Ascent recording for a game via an
// HTML <video> fed by Tauri's asset: protocol (convertFileSrc). Custom transport
// (play/seek/rate), a seek bar with bookmark markers, and a moments list that
// jumps the video. Opened as vodplayer.html?gameId=N.
//
// The transport CORE (play/pause, ◀▶ step seek, the seek-step model, speed, mute,
// enlarge, the time readout, and the transport keyboard keys) lives in the shared
// ./vodtransport.js module — the Patterns inline player uses the same core so the
// two feel identical. This page keeps all its FULL-CHROME features here (event
// timeline + markers, moments list, clip/bookmark/evidence/share tools, the ?t=
// deep-link, OPEN REVIEW) and delegates only the transport to the core (_T).

import { createTransport, resolveAssetUrl, tauriCore } from './vodtransport.js';

const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }
function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }
function tpl(id) { return $(id).content.firstElementChild.cloneNode(true); }
function clock(s) { s = Math.max(0, Math.floor(s || 0)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; }

let _vod = null;       // the loaded VOD snapshot
let _core = null;      // cached Tauri core ({invoke, convertFileSrc}) or null
let _gameId = 0;       // the game id from ?gameId=N
let _objectives = [];  // active objectives for the Quick Bookmark objective picker
let _autoClipEnabled = false; // config.autoClipObjectivesEnabled — gates the auto-clip button
let _autoClipBusy = false;    // true while an auto-clip run is in flight
let _autoClipHintMsg = '';    // last auto-clip result message (survives objbar re-render)
let _autoClipHintErr = false; // whether _autoClipHintMsg is an error
let _T = null;         // shared transport core (play/seek/step/rate/mute/enlarge)
// ── Objective-framed viewer state ────────────────────────────────────────────
// The VOD page opens framed on ONE objective at a time (the default, not a mode).
// _focusedObjId is the currently-framed objective id; its events/moments are loud,
// everything else dims. null only when the user has zero active objectives (the
// viewer then falls back to the full-game timeline + a "set an objective" nudge).
let _focusedObjId = null;   // currently focused objective id (number) or null
let _framed = false;        // true when ≥1 objective and we're framing
// True once the user manually changes the corresponding objective picker. While false,
// that picker FOLLOWS the focused objective (so a clip/bookmark made under a frame ties
// to it — P-034); once the user overrides, we stop auto-syncing it. One flag per picker:
// the Quick Bookmark picker (vp-bm-obj) and the Clip picker (vp-clip-obj) move
// independently, so the user can aim a clip at a different objective than a bookmark.
let _bmObjUserSet = false;
let _clipObjUserSet = false;
const video = () => $('vp-video');

// This game's MARKERS for an objective: the AUTOMATED extractions only (game events —
// deaths, flashes, recalls, objectives, etc.), sorted by time. Manually-made clips and
// bookmarks are NOT markers — they're "moments" the user chose to save, counted in the
// Moments panel instead. Keeping the two separate means the per-objective marker count
// measures what the game produced for that objective, not how diligently the user clipped
// (a count that would otherwise climb every time you save a clip). Shared-token aware: a
// DEATH event counts for every objective tracking DEATH (objectiveIds list); falls back
// to the single objectiveId for older snapshots.
function markersForObjective(objId) {
  if (objId == null) return [];
  const id = Number(objId);
  const v = _vod || {};
  const out = [];
  for (const e of (v.gameEvents || [])) {
    const hit = Array.isArray(e.objectiveIds)
      ? e.objectiveIds.some((x) => Number(x) === id)
      : (e.objectiveId != null && Number(e.objectiveId) === id);
    if (hit) out.push({ seconds: e.gameTimeSeconds || 0, label: e.label || '' });
  }
  return out.sort((a, b) => a.seconds - b.seconds);
}

// Count of this game's automated markers for an objective. 0 = "empty this game" (the
// objective was tracked but no automated event fired for it — still a lesson).
function markerCountForObjective(objId) {
  return markersForObjective(objId).length;
}

function hasObjectiveTie(e) {
  return (Array.isArray(e.objectiveIds) && e.objectiveIds.length > 0) || e.objectiveId != null;
}

function objectiveTiedEventCount() {
  const v = _vod || {};
  return (v.gameEvents || []).filter(hasObjectiveTie).length;
}

function objectiveTitle(objId) {
  const id = Number(objId);
  const o = _objectives.find((x) => Number(x.objectiveId) === id);
  return (o && o.title) ? o.title : 'focused objective';
}

// Does the currently-focused objective track the synthetic TEAMFIGHT token? Gates
// whether teamfight zones stay loud under the frame. Reads tracksTeamfight (from the
// active-objectives endpoint); falls back to scanning trackedTokens. Defaults false
// when unknown (older snapshot) so TF zones dim rather than falsely prioritize.
function focusedTracksTeamfight() {
  const o = _objectives.find((x) => Number(x.objectiveId) === _focusedObjId);
  if (!o) return false;
  if (typeof o.tracksTeamfight === 'boolean') return o.tracksTeamfight;
  const toks = o.trackedTokens || o.tokens || [];
  return Array.isArray(toks) && toks.some((t) => String(t).toUpperCase() === 'TEAMFIGHT');
}

// Objective type → the timeline/chrome token-color family. primary=accent (default),
// mental=teal (win), mini=gold — derived from objective.type, no color column needed.
function objTypeClass(o) {
  if (!o) return 't-primary';
  if (o.isMini || o.type === 'mini') return 't-mini';
  if (o.isMental || o.type === 'mental') return 't-mental';
  return 't-primary';
}

// The tracked-event TOKENS for an objective, as short codes with a color family, for
// the active tab's chip row. Tokens are raw types (KILL/DEATH/DRAGON…), SPELL_* spells,
// or TEAMFIGHT — each mapped to its proper 3-letter code (reusing the timeline's
// deriveShortLabel) rather than blind-sliced. Degrades to nothing if absent (graceful).
function objTokenChips(o) {
  const toks = (o && (o.trackedTokens || o.tokens || o.eventTokens)) || [];
  const arr = Array.isArray(toks) ? toks : [];
  return arr.slice(0, 6).map((raw) => {
    const tok = String(raw && (raw.code || raw.label) || raw).toUpperCase();
    let code, fam = '';
    if (tok === 'TEAMFIGHT') { code = 'TF'; fam = 'tok-loss'; }
    else if (tok.startsWith('SPELL_')) { code = tok.slice(6, 9); fam = 'tok-summoner'; }
    else { code = deriveShortLabel(tok, tok); }
    // Color family by the canonical token (so DRG/HLD/BAR read gold, DTH red, etc.).
    if (!fam) {
      if (/DRAGON|HERALD|BARON|TOWER|TURRET|RIFT|ELDER|INHIB/.test(tok)) fam = 'tok-gold';
      else if (/RECALL|BACK/.test(tok)) fam = 'tok-recall';
      else if (/TRADE/.test(tok)) fam = 'tok-trade';
      else if (/FLASH|SUMMONER|IGNITE|TELEPORT|SMITE|EXHAUST|HEAL|BARRIER|CLEANSE|GHOST/.test(tok)) fam = 'tok-summoner';
      else if (/GANK|DEATH|FIRST/.test(tok)) fam = 'tok-loss';
      else if (/KILL|ASSIST|MULTI/.test(tok)) fam = 'tok-win';
    }
    return { code, fam };
  });
}

// ── Clip-tool state (in/out points, quality) ────────────────────────────────
// Mirrors the WinUI VodPlayerViewModel clip range: -1 = unset. HasClipRange is a
// range >= 1s. SelectedClipQuality is '' | good | neutral | bad.
let _clipIn = -1;
let _clipOut = -1;
let _clipQuality = '';
let _clipBusy = false;

// ── Seek step ────────────────────────────────────────────────────────────────
// The seek-step model (STEP_CHOICES, current step, the "step Ns" readout, Up/Down
// cycling) now lives entirely in the shared transport core (_T). vodplayer drives it
// via _T.nudgeStep (Up/Down) and _T.seekByStep (◀▶ / Left/Right); the readout is
// rendered by the core through the vp-time ref.

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchVod() {
  const params = new URLSearchParams(window.location.search);
  const gameId = Number(params.get('gameId') || 0);
  _gameId = gameId;
  const core = await tauriCore();
  _core = core;
  if (core && gameId > 0) {
    const data = await core.invoke('get_vod', { gameId });
    // Best-effort: active objectives feed the Quick Bookmark objective picker.
    try {
      const r = await core.invoke('get_active_objectives');
      _objectives = Array.isArray(r && r.objectives) ? r.objectives : [];
    } catch (_) { _objectives = []; }
    // Best-effort: the auto-clip toggle gates the Auto-clip objectives button.
    try {
      const cfg = await core.invoke('get_config');
      _autoClipEnabled = !!(cfg && cfg.autoClipObjectivesEnabled);
    } catch (_) { _autoClipEnabled = false; }
    return { data, core };
  }
  // Browser preview: no Tauri / no real file. Use the sample (no playable video).
  const res = await fetch('./sample-vod.json');
  if (!res.ok) throw new Error(`sample-vod.json ${res.status}`);
  return { data: await res.json(), core: null };
}

// Re-fetch the VOD snapshot after a write and re-render the bookmark/marker UI
// WITHOUT touching the <video> element (so playback position is preserved).
// Manual invalidation — there's no message bus.
async function reloadBookmarks() {
  if (!_core || _gameId <= 0) return;
  try {
    const data = await _core.invoke('get_vod', { gameId: _gameId });
    _vod = data;
    const v = video();
    placeMarkers(v.duration || _vod.gameDurationSeconds || 0);
    renderMoments();
    // Marker counts per objective may have changed (a write can add/retag a marker),
    // so refresh the tab-bar counts + the in-objective stepper too.
    renderObjBar();
    renderAutoClipPanel();
    renderMarkerStepper();
  } catch (err) {
    console.error('[vodplayer] reload after write failed:', err);
  }
}

// ── render ──────────────────────────────────────────────────────────────────
function render(data, core) {
  _vod = data;
  const champ = data.championName || 'Game';
  const matchup = data.enemyChampion ? `${champ} vs ${data.enemyChampion}` : champ;
  $('vp-title').textContent = matchup;
  const statusB = document.querySelector('#statusline b');
  statusB.textContent = [data.resultText, data.gameMode, data.datePlayed].filter(Boolean).join(' · ');

  if (!data.hasVod || !data.filePath) {
    show($('vp-novod'), true);
    show($('vp-wrap'), false);
    return;
  }
  show($('vp-novod'), false);
  show($('vp-wrap'), true);

  // Context line takes over from the loading statusline (the old eyebrow/hero are gone).
  show($('statusline'), false);
  show($('vp-context'), true);
  const ctx = $('vp-ctx-meta');
  if (ctx) {
    const bits = [matchup, data.resultText, data.gameMode].filter(Boolean).join(' · ');
    ctx.innerHTML = '<b>' + bits + '</b> &nbsp;·&nbsp; <span class="vp-ctx-frame">reviewing by objective</span>';
  }

  // OPEN REVIEW → the structured review for this game (this nav IS wired). Now a link
  // in the context line (was the big primary card). Keep both href + onclick robust.
  const openBtn = $('vp-open-review');
  if (openBtn) {
    const gid = data.gameId;
    const href = gid ? `review.html?gameId=${encodeURIComponent(gid)}` : 'review.html';
    openBtn.setAttribute('href', href);
    openBtn.onclick = (e) => { e.preventDefault(); window.location.href = href; };
  }

  // Establish the objective frame BEFORE first paint: pick the focused objective and
  // decide framed vs. the zero-objective full-game fallback.
  initObjectiveFrame();

  // Populate the timeline + moments + tools FIRST, off the snapshot, so the event
  // timeline (coded bars) and moments render even before (or without) a playable
  // video. onMeta re-places markers with the real duration once it loads.
  populateObjectivePicker();
  renderObjBar();
  renderAutoClipPanel();
  placeMarkers(_vod.gameDurationSeconds || 0);
  renderMoments();
  renderMarkerStepper();
  renderClipState();

  // The key step: convert the absolute file path → an asset URL the webview can
  // stream (with range requests, so seeking works).
  const v = video();
  const assetUrl = resolveAssetUrl(core, data.filePath);
  if (!assetUrl) {
    renderError(new Error(
      'No way to load the local video. convertFileSrc is unavailable; the asset ' +
      'protocol may not be enabled. Path: ' + data.filePath));
    return;
  }
  v.src = assetUrl;

  // The transport core (_T) owns the per-video listeners that drive the play/pause
  // + mute glyphs, click-to-toggle, and the time readout + seek-fill + playhead
  // (via the refs handed to createTransport in boot()). We keep ONLY the
  // page-specific listeners here: onMeta (timeline markers + the ?t= deep-link)
  // and the error surface.
  v.addEventListener('loadedmetadata', onMeta, { once: true });
  v.addEventListener('error', () => {
    const me = v.error;
    renderError(new Error(
      `video failed to load (code ${me ? me.code : '?'}). ` +
      `src=${v.src.slice(0, 80)}…; file moved, codec unsupported, or asset scope blocks it.`));
  });
  // Kick off loading explicitly.
  v.load();
}

// (resolveAssetUrl now lives in ./vodtransport.js — imported above — and is shared
// byte-for-byte with the Patterns inline player.)

function onMeta() {
  const v = video();
  const dur = v.duration || _vod.gameDurationSeconds || 0;
  // The mono time readout + seek-fill + playhead are owned by the transport core
  // (its timeupdate listener, wired in boot() via the seekFillEl/playheadEl/timeEl
  // refs); refresh the readout once here so the right duration shows immediately.
  if (_T) _T.refreshReadout();
  placeMarkers(dur);
  // Reposition any clip In/Out markers now the real duration is known (they were
  // % of a possibly-stale fallback duration before metadata loaded).
  renderClipOverlay();
  // A ?t=SECONDS param (from a pattern moment / bookmark) jumps straight there.
  const t = Number(new URLSearchParams(window.location.search).get('t') || 0);
  if (t > 0) { if (_T) _T.seekTo(t); v.play().catch(() => {}); }
}


// ── OBJECTIVE FRAMING ─────────────────────────────────────────────────────────
// Decide the initial focused objective + whether we frame at all. Framed when ≥1
// active objective. Pick: highest-priority objective WITH markers this game, else
// the first objective with markers, else the first active objective. When zero
// objectives, _framed=false → full-game fallback + nudge banner.
function initObjectiveFrame() {
  if (!_objectives.length) {
    _framed = false; _focusedObjId = null;
    const nudge = $('vp-nudge');
    if (nudge && !nudge.dataset.dismissed) show(nudge, true);
    show($('vp-objbar'), false);
    return;
  }
  _framed = true;
  show($('vp-nudge'), false);
  const withMarks = _objectives.filter((o) => markerCountForObjective(o.objectiveId) > 0);
  const pool = withMarks.length ? withMarks : _objectives;
  const priority = pool.find((o) => o.isPriority) || pool[0];
  _focusedObjId = priority != null ? Number(priority.objectiveId) : null;
}

// Build the objective tab bar (the permanent framed-viewer header). One tab per
// active objective, color-by-type, with this game's marker count; the focused one
// is expanded and carries type·phase + title + tokens + "markers this game".
function renderObjBar() {
  const bar = $('vp-objbar');
  if (!bar) return;
  if (!_framed) { show(bar, false); clear(bar); return; }
  show(bar, true);
  clear(bar);
  for (const o of _objectives) {
    const id = Number(o.objectiveId);
    const count = markerCountForObjective(id);
    const active = id === _focusedObjId;
    const tab = document.createElement('div');
    tab.className = `vp-objtab ${objTypeClass(o)}` + (active ? ' is-active' : '') + (count === 0 ? ' is-empty' : '');
    tab.dataset.objId = String(id);
    tab.setAttribute('role', 'button');
    tab.tabIndex = 0;

    const typeLabel = (o.type || (o.isMini ? 'mini' : o.isMental ? 'mental' : 'primary')).toUpperCase();
    const phase = (o.phaseLabel || '').toUpperCase();

    const top = document.createElement('div');
    top.className = 'vp-objtab-top';
    const typeEl = document.createElement('span');
    typeEl.className = 'vp-objtab-type';
    typeEl.textContent = active && phase ? `${typeLabel} · ${phase}` : typeLabel;
    const countEl = document.createElement('span');
    countEl.className = 'vp-objtab-count';
    countEl.textContent = String(count);
    top.appendChild(typeEl); top.appendChild(countEl);
    tab.appendChild(top);

    const nameEl = document.createElement('div');
    nameEl.className = 'vp-objtab-name';
    nameEl.textContent = o.title || '(untitled objective)';
    tab.appendChild(nameEl);

    if (active) {
      const detail = document.createElement('div');
      detail.className = 'vp-objtab-detail';
      const marks = document.createElement('span');
      marks.className = 'vp-objtab-marks';
      marks.innerHTML = `<b>${count}</b> markers this game`;
      detail.appendChild(marks);
      const chips = objTokenChips(o);
      if (chips.length) {
        const wrap = document.createElement('span');
        wrap.className = 'vp-objtab-tokens';
        for (const c of chips) {
          const t = document.createElement('span');
          t.className = `vp-objtok ${c.fam}`;
          t.textContent = c.code;
          wrap.appendChild(t);
        }
        detail.appendChild(wrap);
      }
      tab.appendChild(detail);

      // Auto-clip button for THIS objective. Shown only when the Settings toggle is on,
      // there's a recording on disk, and this objective has markers this game. One click
      // saves a ~45s clip (30s before to 15s after) around each of its events. Idempotent.
      if (_autoClipEnabled && _vod && _vod.hasVod && _vod.filePath && count > 0) {
        const acRow = document.createElement('div');
        acRow.className = 'vp-objtab-autoclip';
        const acBtn = document.createElement('button');
        acBtn.type = 'button';
        acBtn.className = 'vp-objclip-btn';
        acBtn.dataset.action = 'autoclip_objective';
        acBtn.dataset.objId = String(id);
        acBtn.disabled = _autoClipBusy;
        acBtn.textContent = _autoClipBusy ? 'Auto-clipping…' : `⬇ Auto-clip ${count} event${count === 1 ? '' : 's'}`;
        acRow.appendChild(acBtn);
        const acHint = document.createElement('div');
        acHint.className = 'vp-objclip-hint';
        acHint.id = 'vp-objclip-hint';
        if (_autoClipHintMsg) { acHint.textContent = _autoClipHintMsg; acHint.classList.toggle('err', _autoClipHintErr); }
        else { acHint.hidden = true; }
        acRow.appendChild(acHint);
        tab.appendChild(acRow);
      }
    }
    bar.appendChild(tab);
  }
}

function renderAutoClipPanel() {
  const host = $('vp-autoclip-tools');
  const btn = $('vp-autoclip-btn');
  const meta = $('vp-autoclip-meta');
  if (!host || !btn) return;

  const hasRecording = !!(_vod && _vod.hasVod && _vod.filePath);
  show(host, hasRecording);
  if (!hasRecording) return;

  const scoped = _framed && _focusedObjId != null;
  const objId = scoped ? Number(_focusedObjId) : null;
  const count = scoped ? markerCountForObjective(objId) : objectiveTiedEventCount();
  const eventWord = count === 1 ? 'event' : 'events';
  const title = scoped ? objectiveTitle(objId) : '';

  btn.dataset.objId = objId != null ? String(objId) : '';
  btn.disabled = true;

  let label = 'Auto-clip objective events';
  let detail = '';
  let isErr = false;

  if (!_autoClipEnabled) {
    label = 'Auto-clip off in Settings';
    detail = 'Turn on Auto-clip objective events in Settings to batch-save clips.';
  } else if (count <= 0) {
    label = 'No objective events to auto-clip';
    detail = scoped
      ? `The focused objective (${title}) has no tied timeline events in this game.`
      : 'No objective-tied timeline events were detected for this game.';
  } else if (_autoClipBusy) {
    label = 'Auto-clipping...';
    detail = _autoClipHintMsg || (scoped
      ? `Saving clips for ${title}.`
      : 'Saving clips for all objective-tied events.');
    isErr = _autoClipHintErr;
  } else {
    btn.disabled = false;
    label = scoped
      ? `Auto-clip ${count} focused ${eventWord}`
      : `Auto-clip ${count} objective ${eventWord}`;
    detail = scoped
      ? `Clips events tied to ${title}.`
      : 'Clips every objective-tied event in this game.';
  }

  if (_autoClipHintMsg && !_autoClipBusy) {
    detail = _autoClipHintMsg;
    isErr = _autoClipHintErr;
  }

  btn.textContent = label;
  if (meta) {
    meta.textContent = detail;
    meta.classList.toggle('err', isErr);
    show(meta, !!detail);
  }
}

// Switch the focused objective. Reframes the timeline + moments + marker stepper
// IN PLACE — no video reload, so playback position is preserved (mirrors the
// reloadBookmarks philosophy). No writes; pure client-side re-render.
function setFocusedObjective(objId) {
  if (!_framed || objId == null) return;
  const id = Number(objId);
  if (id === _focusedObjId) return;
  _focusedObjId = id;
  _autoClipHintMsg = ''; _autoClipHintErr = false; // clear stale auto-clip status from the prior objective
  const v = video();
  renderObjBar();
  renderAutoClipPanel();
  placeMarkers((v && v.duration) || _vod.gameDurationSeconds || 0);
  renderMoments();
  renderMarkerStepper();
  // Keep the Quick Bookmark + Clip pickers tracking the frame so the next bookmark/clip
  // ties to the objective now in focus. populateObjectivePicker() skips whichever picker
  // the user has overridden, so this never clobbers a deliberate pick.
  populateObjectivePicker();
}

// The 1-based ordinal of the marker nearest the current playhead (the one the user
// is "on" or just passed). Position-aware so the readout tracks where you ARE as the
// video scrubs, not a stale click index — that off-by-one was bug #2.
function currentMarkerOrdinal(marks) {
  if (!marks.length) return 0;
  const v = video();
  const t = (v && v.currentTime) || 0;
  // The last marker at/<= the playhead (with a small tolerance), else the first one.
  let idx = -1;
  for (let i = 0; i < marks.length; i++) { if (marks[i].seconds <= t + 0.25) idx = i; else break; }
  return (idx < 0 ? 0 : idx) + 1;
}

// In-objective marker stepper: ◀ ▶ jump between THIS objective's markers (distinct
// from the tab bar that switches objectives — two nav levels). Hidden when unframed
// or when the focused objective has no markers this game. The "i / N" readout is
// derived from the PLAYHEAD each render, so it stays honest as the video scrubs.
function renderMarkerStepper() {
  const wrap = $('vp-mkstep');
  if (!wrap) return;
  const marks = _framed ? markersForObjective(_focusedObjId) : [];
  if (!marks.length) { show(wrap, false); return; }
  show(wrap, true);
  const ord = currentMarkerOrdinal(marks);
  const cnt = $('vp-mk-count');
  if (cnt) cnt.innerHTML = `marker <b>${ord}</b> / ${marks.length}`;
  const v = video();
  const t = (v && v.currentTime) || 0;
  const prev = $('vp-mk-prev'); const next = $('vp-mk-next');
  // Disable based on whether a marker exists strictly before / after the playhead.
  if (prev) prev.disabled = !marks.some((m) => m.seconds < t - 0.25);
  if (next) next.disabled = !marks.some((m) => m.seconds > t + 0.25);
}

// Step to the next (delta +1) or previous (delta -1) marker RELATIVE TO THE PLAYHEAD,
// not a stored index. ▶ = first marker after the current time, ◀ = first before. This
// fixes the off-by-one where ▶ from the start jumped to the 2nd marker (bug #2).
function stepMarker(delta) {
  const marks = markersForObjective(_focusedObjId);
  if (!marks.length) return;
  const v = video();
  const t = (v && v.currentTime) || 0;
  let target = null;
  if (delta > 0) {
    target = marks.find((m) => m.seconds > t + 0.25);          // first strictly after
  } else {
    for (const m of marks) { if (m.seconds < t - 0.25) target = m; else break; } // last before
  }
  if (target && _T) {
    _T.seekTo(target.seconds);
    if (v && v.paused) v.play().catch(() => {});
  }
  renderMarkerStepper();
}

// Markers on the EVENT TIMELINE. Two layers:
//  1. Live game events (kills/deaths/objectives) rendered as the original Revu
//     "3-letter code + bar" markers: a vertical bar at the event time topped with
//     its shortLabel (KIL/DTH/DRG/…), colored win/loss/gold/neutral via the
//     server-supplied kind + colorHex. Click-to-seek.
//  2. Bookmark markers (clip = gold, plain = accent) below the track so saved
//     moments stay visible. Click-to-seek.
// When FRAMED, only events tied to the focused objective render loud (the objective
// lane); every other event dims to a faint clickable tick (evbar-dim).
function placeMarkers(dur) {
  const host = $('vp-markers');
  clear(host);
  if (!dur) return;

  const pctOf = (s) => Math.max(0, Math.min(100, (s / dur) * 100));

  // 0. Teamfight zones — clusters of combat events drawn as a soft band so the user
  //    can SEE where fights happened. These are WHOLE-GAME context, not tied to a
  //    specific objective — so when FRAMED they must respect the frame: stay loud
  //    ONLY if the focused objective actually tracks TEAMFIGHT, otherwise dim like
  //    every other non-focused element. (Bug: a 0-marker objective showed loud TF
  //    bands, making teamfighting look prioritized when it wasn't tracked at all.)
  const tfFocused = !_framed || focusedTracksTeamfight();
  for (const tf of teamfightZones(_vod.gameEvents || [])) {
    const band = document.createElement('span');
    band.className = 'evtf' + (tfFocused ? '' : ' evtf-dim');
    band.style.left = `${pctOf(tf.startS)}%`;
    band.style.width = `${Math.max(0.8, pctOf(tf.endS) - pctOf(tf.startS))}%`;
    band.title = `Teamfight ${clock(tf.startS)}–${clock(tf.endS)} · ${tf.count} events`;
    const lbl = document.createElement('span');
    lbl.className = 'evtf-lbl';
    lbl.textContent = 'TF';
    band.appendChild(lbl);
    host.appendChild(band);
  }

  // 1. Live events → coded bars with importance-tiered HEIGHTS so the timeline
  //    reads at a glance: major objectives (Baron/Dragon/Herald/Tower/Inhibitor)
  //    are tall, kills/deaths medium, everything else short. The tiering also
  //    spreads the code labels onto different rows so they collide less. A final
  //    anti-overlap pass hides a label when it would sit too close to the last
  //    shown label IN THE SAME TIER (kept reachable via the bar's hover/title).
  const events = (_vod.gameEvents || [])
    .slice()
    .sort((a, b) => (a.gameTimeSeconds || 0) - (b.gameTimeSeconds || 0));

  // Track the last label x-position per tier so near-duplicates drop their label.
  const lastLabelPctByTier = { objective: -99, major: -99, medium: -99, summoner: -99, recall: -99, minor: -99 };
  // Wider than a bare code's width so labels get real breathing room and the
  // timeline never turns into a wall of text (the dense-text complaint).
  const MIN_LABEL_GAP_PCT = 5.5;
  // Objective-tied labels reserve a wider exclusion so neighboring NON-objective
  // labels yield around them (objective takes priority + no overlap).
  const OBJ_RESERVE_PCT = 4.0;
  // x-positions where an objective marker reserved space; non-objective labels
  // within OBJ_RESERVE_PCT of any of these are suppressed so the objective wins.
  const objectiveLabelXs = [];

  // What counts as "focused" (loud) depends on framing:
  //  • FRAMED → an event is loud when the FOCUSED objective is among the objectives
  //    that track its token (objectiveIds — a list, because a shared token like DEATH
  //    or SPELL_FLASH can be tracked by several objectives at once). Falls back to the
  //    single objectiveId for older snapshots. Everything else dims to a tick.
  //  • UNFRAMED (zero objectives) → original behavior: any objective-tied event loud.
  const matchesFocus = (e) => Array.isArray(e.objectiveIds)
    ? e.objectiveIds.some((id) => Number(id) === _focusedObjId)
    : (e.objectiveId != null && Number(e.objectiveId) === _focusedObjId);
  const isFocused = (e) => _framed ? matchesFocus(e) : (e.objectiveId != null);

  // TWO-PASS placement so FOCUSED events take priority: focused first (they always
  // render their label + reserve their slot), the rest second (they yield + dim).
  const ordered = events.filter(isFocused).concat(events.filter((e) => !isFocused(e)));

  for (const e of ordered) {
    // Always a SHORT code on the track — never a long raw label. Even if the
    // server sends a verbose shortLabel for a new event type, clamp it so it
    // can't sprawl across the timeline (the "Lower Drag Objective Contest" leak).
    const code = shortCode(e.shortLabel || deriveShortLabel(e.eventType, e.label));
    const kind = e.kind || 'neutral';
    const focused = isFocused(e);
    // FRAMED + not focused → a faint dim tick (still clickable, code on hover).
    const dimmed = _framed && !focused;
    // Focused events ride a dedicated 'objective' lane (tall, own row, distinct ring).
    const tier = focused ? 'objective' : eventTier(e.eventType, e.label);
    const leftPct = pctOf(e.gameTimeSeconds);

    const bar = document.createElement('span');
    // Focused events ride the objective lane (its own color + height), so DON'T also
    // stamp the per-kind class. Dimmed events get the faint tick. Otherwise (unframed
    // untied) the per-kind color + tier height as before.
    bar.className = focused
      ? 'evbar evbar-objective'
      : dimmed
        ? 'evbar evbar-dim'
        : `evbar evbar-${kind} evbar-${tier}`;
    bar.style.left = `${leftPct}%`;
    // Focused bars take the objective's color; dim ticks stay neutral (class-driven);
    // unframed untied events keep the per-type color.
    if (!dimmed) {
      const ringColor = focused ? (e.objectiveColorHex || e.colorHex) : e.colorHex;
      if (ringColor) bar.style.setProperty('--evc', ringColor);
    }
    bar.style.pointerEvents = 'auto';
    bar.style.cursor = 'pointer';
    const objSuffix = e.objectiveId != null && e.objectiveTitle ? ` · ◎ ${e.objectiveTitle}` : '';
    bar.title = `${e.timeLabel} ${e.label}${e.summary ? ' · ' + e.summary : ''}${objSuffix}`.trim();
    bar.dataset.action = 'jump';
    bar.dataset.seconds = String(e.gameTimeSeconds);

    // Label policy. Focused events ALWAYS label (never suppressed) and reserve a
    // slot. Dimmed ticks NEVER show a static label (code on hover only — that's the
    // point of dimming). Unframed untied events label when clear of their tier
    // neighbor AND clear of every focused reservation.
    const labellessTier = tier === 'minor' || tier === 'recall';
    let showLabel;
    if (focused) {
      showLabel = true;
      objectiveLabelXs.push(leftPct);
    } else if (dimmed) {
      showLabel = false;
    } else {
      const clearOfTier = leftPct - lastLabelPctByTier[tier] >= MIN_LABEL_GAP_PCT;
      const clearOfObjective = objectiveLabelXs.every((x) => Math.abs(leftPct - x) >= OBJ_RESERVE_PCT);
      showLabel = !labellessTier && clearOfTier && clearOfObjective;
    }
    if (showLabel) {
      const lbl = document.createElement('span');
      lbl.className = 'evbar-code';
      lbl.textContent = code;
      bar.appendChild(lbl);
      lastLabelPctByTier[tier] = leftPct;
    } else {
      // Keep the code available on hover even when the static label is suppressed.
      bar.classList.add('evbar-nolabel');
      bar.dataset.code = code;
    }

    host.appendChild(bar);
  }

  // 2. Bookmarks (clip → gold, plain → accent) as small markers under the track.
  for (const b of (_vod.bookmarks || [])) {
    const m = document.createElement('span');
    m.className = 'ev ev-bm' + (b.hasClip ? ' ev-clip' : '');
    m.style.left = `${pctOf(b.gameTimeSeconds)}%`;
    m.style.pointerEvents = 'auto';
    m.style.cursor = 'pointer';
    m.title = `${b.timeLabel} ${b.note || ''}`.trim();
    m.dataset.action = 'jump';
    m.dataset.seconds = String(b.gameTimeSeconds);
    host.appendChild(m);
  }
}

// Importance tier for an event → drives the bar height + label policy. Major =
// map objectives (decisive), medium = kills/deaths (player-relevant), summoner =
// flash/summoner casts (always labeled so the user can spot spell usage), minor =
// everything else. Keeps the busiest timelines readable.
function eventTier(eventType, label) {
  const t = String(eventType || label || '').toUpperCase().replace(/[^A-Z]/g, '');
  if (/BARON|DRAGON|ELDER|HERALD|RIFT|TOWER|TURRET|INHIB|NEXUS|ACE|OBJECTIVE|CONTEST/.test(t)) return 'major';
  if (/KILL|DEATH|MULTIKILL|FIRSTBLOOD|PENTA|QUADRA|TRIPLE|DOUBLE|GANK|JUNGLEGANK|SKIRMISH/.test(t)) return 'medium';
  if (/FLASH|SUMMONER|SPELL|IGNITE|TELEPORT|SMITE|EXHAUST|HEAL|BARRIER|CLEANSE|GHOST/.test(t)) return 'summoner';
  if (/RECALL|BACK/.test(t)) return 'recall';
  if (/TRADE/.test(t)) return 'trade';
  return 'minor';
}

// Derive teamfight zones from the event stream. A teamfight = a cluster of combat
// events (kills/deaths/assists/multikills/first-blood) where consecutive events are
// within GAP seconds of each other and the cluster holds at least MIN_EVENTS. Each
// zone is { startS, endS, count } spanning the first→last event of the cluster.
function teamfightZones(events) {
  const GAP = 14;        // seconds between consecutive combat events to stay one fight
  const MIN_EVENTS = 3;  // a fight needs at least this many combat events
  const combat = events
    .filter((e) => /KILL|DEATH|ASSIST|MULTI|FIRST/.test(String(e.eventType || e.label || '').toUpperCase()))
    .map((e) => e.gameTimeSeconds || 0)
    .filter((s) => s > 0)
    .sort((a, b) => a - b);
  const zones = [];
  let cluster = [];
  const flush = () => {
    if (cluster.length >= MIN_EVENTS) {
      zones.push({ startS: cluster[0], endS: cluster[cluster.length - 1], count: cluster.length });
    }
    cluster = [];
  };
  for (const s of combat) {
    if (cluster.length === 0 || s - cluster[cluster.length - 1] <= GAP) {
      cluster.push(s);
    } else {
      flush();
      cluster.push(s);
    }
  }
  flush();
  return zones;
}

// Clamp any label to a compact track code: strip to letters/digits, upper-case,
// keep it short so the timeline stays legible no matter what the server sends.
// Multi-word labels collapse to an acronym of their initials (e.g.
// "Lower Drag Objective Contest" → "LDOC", then trimmed to 4).
function shortCode(raw) {
  const s = String(raw || '').trim();
  if (!s) return 'EVT';
  // Already a tidy code (<= 5 chars, no spaces): use as-is, upper-cased.
  if (!/\s/.test(s) && s.length <= 5) return s.toUpperCase();
  const words = s.split(/\s+/).filter(Boolean);
  if (words.length > 1) {
    return words.map((w) => w[0]).join('').toUpperCase().slice(0, 4);
  }
  return s.replace(/[^A-Za-z0-9]/g, '').slice(0, 4).toUpperCase() || 'EVT';
}

// Fallback 3-letter code when the server didn't supply shortLabel (older
// snapshots). Maps the common event types to the original Revu codes.
function deriveShortLabel(eventType, label) {
  const t = String(eventType || label || '').toUpperCase();
  const map = {
    KILL: 'KIL', DEATH: 'DTH', ASSIST: 'AST', MULTIKILL: 'MTK',
    DRAGON: 'DRG', BARON: 'BAR', HERALD: 'HLD', RIFTHERALD: 'HLD',
    TOWER: 'TWR', TURRET: 'TWR', INHIBITOR: 'INH', ELDER: 'ELD',
    FIRSTBLOOD: 'FB', ACE: 'ACE', GANK: 'GNK', WARD: 'WRD', RECALL: 'RCL',
    FLASH: 'FLS', SUMMONERSPELL: 'SUM', LEVELUP: 'LVL',
    TRADE: 'TRD', SHORT_TRADE: 'STR', EXTENDED_TRADE: 'XTR',
    JUNGLE_GANK: 'GNK',
  };
  if (map[t]) return map[t];
  // Generic: first three letters of the type, upper-cased.
  return (t.replace(/[^A-Z]/g, '').slice(0, 3) || 'EVT');
}

// Current right-panel filter: 'auto' | 'clips' | 'bm'.
let _bmFilter = 'auto';

// Build the three 'Moments to Review' lanes from the snapshot:
//   auto  → evidence inbox auto-detected moments (timeline_region)
//   clips → saved clips (evidence rows with a clip)
//   bm    → plain bookmarks (no clip) from the bookmarks list
// Falls back gracefully when a lane is empty (older snapshots without evidence
// still render bookmarks/clips from the bookmarks list).
function momentLanes() {
  const v = _vod || {};
  let auto = (v.autoMoments || []).map((m) => normMoment(m, 'Auto moment'));
  // Saved clips: the evidence-inbox clip rows PLUS any bookmark-backed clip that has
  // no evidence row of its own. A clip saved as a bookmark-with-clip (e.g. via Quick
  // Bookmark, or before an evidence row was minted) would otherwise be invisible the
  // moment ANY evidence clip exists — the old `if (clips.length === 0)` fallback only
  // showed bookmark clips when there were zero evidence clips, silently dropping the
  // odd one out. Merge instead, de-duped on the underlying bookmark id so a clip with
  // both an evidence row and a bookmark row isn't listed twice.
  let clips = (v.savedClips || []).map((m) => normMoment(m, 'Saved clip'));
  const evidenceBmIds = new Set(
    clips.map((c) => c.shareBookmarkId).filter((id) => id)
  );
  const bookmarkClips = (v.bookmarks || [])
    .filter((b) => b.hasClip && !evidenceBmIds.has(b.id))
    .map((b) => normBookmark(b, true));
  clips = clips.concat(bookmarkClips);
  // Bookmarks lane = plain (non-clip) bookmarks.
  let bm = (v.bookmarks || []).filter((b) => !b.hasClip).map((b) => normBookmark(b, false));
  // FRAMED → scope every lane to the focused objective, BUT keep UNTAGGED moments
  // (objectiveId == null) visible in every frame. A clip/bookmark the user saved is a
  // real moment regardless of whether it happens to be tagged to the objective now in
  // focus; hiding an untagged clip just because you're framed is the "my clips don't
  // show" bug (P-034). Only moments tagged to a DIFFERENT objective are scoped out.
  // Unframed (zero objectives) shows everything, as before.
  if (_framed && _focusedObjId != null) {
    const inFrame = (m) => m.objectiveId == null || Number(m.objectiveId) === _focusedObjId;
    auto = auto.filter(inFrame);
    clips = clips.filter(inFrame);
    bm = bm.filter(inFrame);
  }
  // Order every lane chronologically (by game time) so the moment list reads in the
  // same left-to-right order as the event timeline — a clip made out of sequence
  // (e.g. an early-game moment tagged last) sits where it belongs on the clock, not
  // at the bottom by insertion order. Stable numeric sort on the shared `seconds`.
  const byTime = (a, b) => (a.seconds || 0) - (b.seconds || 0);
  auto.sort(byTime);
  clips.sort(byTime);
  bm.sort(byTime);
  return { auto, clips, bm };
}

// Normalize an evidence-inbox row → a uniform moment shape the renderer consumes.
function normMoment(m, srcLabel) {
  return {
    id: null,                       // not a plain bookmark (no bookmark id)
    evidenceId: m.id != null ? m.id : null, // the evidence row id (for tag + note writes)
    seconds: m.startTimeSeconds != null ? m.startTimeSeconds : 0,
    startTimeSeconds: m.startTimeSeconds != null ? m.startTimeSeconds : null,
    endTimeSeconds: m.endTimeSeconds != null ? m.endTimeSeconds : null,
    timeLabel: m.timeLabel || clock(m.startTimeSeconds || 0),
    srcLabel: m.hasClip ? 'Saved clip' : srcLabel,
    isClip: !!m.hasClip,
    // Prefer the user's note, then the auto title; the editable buffer is the note
    // (title is the auto-detected name and shouldn't overwrite a real note).
    note: m.note || '',
    // Card text never shows a bare placeholder for a clip — fall back title → time.
    noteDisplay: m.note || m.title
      || (m.hasClip ? `Clip @ ${m.timeLabel || clock(m.startTimeSeconds || 0)}` : '(no note)'),
    objectiveId: m.objectiveId != null ? m.objectiveId : null,
    objectiveTitle: m.objectiveTitle || '',
    polarity: m.polarity || 'neutral',
    polarityColorHex: m.polarityColorHex || '',
    status: m.status || '',
    editable: false,
    // Evidence rows (auto + clip) get the objective tag picker + editable note.
    evidenceEditable: m.id != null,
    // Evidence-backed clips write via the evidence route, not the bookmark route.
    bookmarkObjEditable: false,
    bookmarkId: null,
    // Saved-clip rows carry the underlying bookmark id (= SourceId) + share state.
    shareBookmarkId: m.shareBookmarkId || 0,
    shareUrl: m.shareUrl || '',
  };
}

// Normalize a bookmark → the same uniform moment shape. Carries the bookmark id
// so plain (non-clip) bookmark rows can offer inline note-edit + delete.
function normBookmark(b, isClip) {
  return {
    id: b.id != null ? b.id : null,
    evidenceId: null,               // bookmark-derived rows have no evidence id
    seconds: b.gameTimeSeconds || 0,
    startTimeSeconds: b.gameTimeSeconds || 0,
    endTimeSeconds: null,
    timeLabel: b.timeLabel || clock(b.gameTimeSeconds || 0),
    srcLabel: isClip ? 'Saved clip' : 'Bookmark',
    isClip: !!isClip,
    note: b.note || '',
    // A clip-bookmark with no note still reads meaningfully (time-stamped) instead
    // of an empty "(no note)" — the "clips lost their text" complaint.
    noteDisplay: b.note
      || (isClip ? `Clip @ ${b.timeLabel || clock(b.gameTimeSeconds || 0)}` : '(no note)'),
    objectiveId: b.objectiveId != null ? b.objectiveId : null,
    objectiveTitle: b.objectiveTitle || '',
    polarity: '',
    polarityColorHex: '',
    status: '',
    editable: !isClip && b.id != null, // only plain bookmarks get edit/delete
    evidenceEditable: false,
    // Clip-bookmarks carry a bookmark id → they CAN be objective-tagged via the
    // bookmark route (set_bookmark_objective), so they get the objective picker too.
    bookmarkObjEditable: !!isClip && b.id != null,
    bookmarkId: b.id != null ? b.id : null,
    // Clip bookmarks can be shared; the bookmark id IS the share target.
    shareBookmarkId: isClip && b.id != null ? b.id : 0,
    shareUrl: b.shareUrl || '',
  };
}

function renderMoments() {
  const { auto, clips, bm } = momentLanes();
  const total = auto.length + clips.length + bm.length;

  // Tab counts + open badge.
  const setTxt = (id, txt) => { const e = $(id); if (e) e.textContent = txt; };
  setTxt('vp-c-all', `(${auto.length})`);
  setTxt('vp-c-clips', `(${clips.length})`);
  setTxt('vp-c-bm', `(${bm.length})`);
  setTxt('vp-open-count', `${total} open`);

  const shown = _bmFilter === 'clips' ? clips : _bmFilter === 'bm' ? bm : auto;

  const host = $('vp-bookmarks');
  clear(host);
  // Empty-state copy. When FRAMED, clarify that "moments" (tagged clips/bookmarks)
  // are distinct from the timeline "markers" the objective tab counts — otherwise
  // "36 markers this game" next to "no moments" reads as a contradiction.
  const emptyEl = $('vp-moments-empty');
  if (emptyEl) {
    if (_framed && _focusedObjId != null) {
      const marks = markerCountForObjective(_focusedObjId);
      emptyEl.textContent = marks > 0
        ? `No moments tagged for this objective yet. Its ${marks} automated marker${marks === 1 ? '' : 's'} (deaths, flashes, recalls…) are on the track — clip or bookmark one to add it here.`
        : 'No moments for this objective this game.';
    } else {
      emptyEl.textContent = 'No moments tagged for this game yet.';
    }
  }
  show($('vp-moments-empty'), shown.length === 0);
  for (const m of shown) {
    const el = tpl('tpl-bookmark');
    el.dataset.seconds = String(m.seconds);
    el.querySelector('.vp-bm-src').textContent = m.srcLabel;
    el.querySelector('.vp-bm-time').textContent = m.timeLabel;
    el.querySelector('.vp-bm-note').textContent = m.noteDisplay;
    show(el.querySelector('.vp-bm-clip'), m.isClip);

    // Plain bookmark rows (carry an id) get an inline edit/delete block: a note
    // field that autosaves on blur/Enter, and a delete button. Auto/clip rows
    // keep the block hidden (their triage lives in the shared evidence routes).
    const edit = el.querySelector('.vp-bm-edit');
    if (edit && m.editable) {
      show(edit, true);
      el.dataset.bmId = String(m.id);
      const noteInput = edit.querySelector('.vp-bm-editnote');
      if (noteInput) {
        noteInput.value = m.note;
        // Don't let typing/clicking in the field trigger the row's jump action.
        noteInput.addEventListener('click', (e) => e.stopPropagation());
        noteInput.addEventListener('keydown', (e) => e.stopPropagation());
      }
      const del = edit.querySelector('.vp-bm-del');
      if (del) del.dataset.bmId = String(m.id);
    } else if (edit) {
      edit.remove();
    }

    // Evidence edit block — auto-moment + saved-clip rows (carry an evidence id):
    // an objective tag picker + an editable note. The picker attaches/detaches the
    // objective (set_evidence_objective); the note autosaves on blur/Enter
    // (pattern/moment/note → UpdateNoteAsync).
    const evEdit = el.querySelector('.vp-ev-edit');
    if (evEdit && m.evidenceEditable && m.evidenceId != null) {
      show(evEdit, true);
      el.dataset.evId = String(m.evidenceId);
      const sel = evEdit.querySelector('.vp-ev-obj');
      if (sel) {
        fillObjectiveSelect(sel, m.objectiveId);
        sel.dataset.evId = String(m.evidenceId);
        sel.dataset.route = 'evidence';
        sel.addEventListener('click', (e) => e.stopPropagation());
      }
      const note = evEdit.querySelector('.vp-ev-note');
      if (note) {
        note.value = m.note;
        note.dataset.evId = String(m.evidenceId);
        note.dataset.startS = m.startTimeSeconds != null ? String(m.startTimeSeconds) : '';
        note.dataset.endS = m.endTimeSeconds != null ? String(m.endTimeSeconds) : '';
        note.dataset.hasClip = m.isClip ? '1' : '0';
        note.addEventListener('click', (e) => e.stopPropagation());
        note.addEventListener('keydown', (e) => e.stopPropagation());
      }
    } else if (evEdit && m.bookmarkObjEditable && m.bookmarkId != null) {
      // Clip-bookmark rows (no evidence id, but a bookmark id) still get an
      // objective picker — it writes via the bookmark route, not the evidence one.
      // No note field here (the bookmark note edits live in the plain-bookmark path).
      show(evEdit, true);
      const sel = evEdit.querySelector('.vp-ev-obj');
      if (sel) {
        fillObjectiveSelect(sel, m.objectiveId);
        sel.dataset.bmId = String(m.bookmarkId);
        sel.dataset.route = 'bookmark';
        sel.addEventListener('click', (e) => e.stopPropagation());
      }
      const note = evEdit.querySelector('.vp-ev-note');
      if (note) note.remove();
    } else if (evEdit) {
      evEdit.remove();
    }

    // Share block — only for saved-clip rows that resolve to a bookmark id. The
    // button uploads the clip to revu.lol (or, if already shared, copies the link).
    const shareWrap = el.querySelector('.vp-bm-share');
    if (shareWrap && m.isClip && m.shareBookmarkId) {
      show(shareWrap, true);
      el.dataset.shareBmId = String(m.shareBookmarkId);
      const btn = shareWrap.querySelector('.vp-share-btn');
      const copyBtn = shareWrap.querySelector('.vp-copy-btn');
      const lbl = shareWrap.querySelector('.vp-share-lbl');
      const urlEl = shareWrap.querySelector('.vp-share-url');
      const shared = !!m.shareUrl;
      if (btn) {
        btn.dataset.shareBmId = String(m.shareBookmarkId);
        btn.dataset.shareUrl = m.shareUrl || '';
        btn.classList.toggle('is-shared', shared);
        // Set only the label span so the icon survives. Shared rows show "Shared"
        // (the dedicated Copy button handles copying); unshared show "Share clip".
        if (lbl) lbl.textContent = shared ? 'Shared' : 'Share clip';
      }
      if (copyBtn) {
        copyBtn.dataset.shareBmId = String(m.shareBookmarkId);
        copyBtn.dataset.shareUrl = m.shareUrl || '';
        show(copyBtn, shared);
      }
      if (urlEl) {
        if (shared) { urlEl.textContent = m.shareUrl; show(urlEl, true); }
        else { urlEl.textContent = ''; show(urlEl, false); }
      }
      renderShareJobForRow(m.shareBookmarkId, shared);
      // No per-element click listeners here — rows re-render on every reload, which
      // would leak handlers. The document-level delegated handler owns share_clip +
      // copy_clip_link and stops propagation so the row's jump never fires.
    } else if (shareWrap) {
      shareWrap.remove();
    }

    // Optional triage badges: polarity dot tint + objective tag + status. These
    // reuse the existing .b badge slot via the clip badge's neighbors; we append
    // lightweight badges so older templates still render the core row.
    const badges = el.querySelector('.mbadges');
    if (badges) {
      if (m.objectiveTitle) {
        const tag = document.createElement('span');
        tag.className = 'b';
        tag.textContent = m.objectiveTitle;
        badges.appendChild(tag);
      }
      if (m.status && m.status !== 'needs_review') {
        const st = document.createElement('span');
        st.className = 'b';
        st.textContent = m.status === 'evidence' ? 'Evidence'
          : m.status === 'highlight' ? 'Highlight' : m.status;
        badges.appendChild(st);
      }
      // Tint the source badge by polarity (good/bad) when present.
      if (m.polarityColorHex && (m.polarity === 'good' || m.polarity === 'bad')) {
        const srcb = el.querySelector('.vp-bm-src');
        if (srcb) { srcb.style.color = m.polarityColorHex; srcb.style.borderColor = m.polarityColorHex; }
      }
    }
    host.appendChild(el);
  }
}

// 3-way tab filter (Auto / Clips / Bookmarks) — client-side over the lanes.
function wireTabs() {
  const tabs = $('vp-tabs');
  if (!tabs) return;
  tabs.addEventListener('click', (ev) => {
    const tab = ev.target.closest('.tab');
    if (!tab) return;
    _bmFilter = tab.dataset.filter || 'auto';
    tabs.querySelectorAll('.tab').forEach((t) => t.classList.toggle('on', t === tab));
    renderMoments();
  });
}

// Wire the framed-viewer controls: objective tab bar (switch objective), the
// in-objective marker stepper (◀ ▶), and the zero-objective nudge dismiss. All
// client-side; no writes. Delegated so it survives renderObjBar() rebuilds.
function wireFraming() {
  const bar = $('vp-objbar');
  if (bar) {
    const pick = (target) => {
      // The per-objective auto-clip button lives inside the active tab; its own action
      // handler owns the click, so don't also treat it as a frame switch.
      if (target.closest('[data-action="autoclip_objective"]')) return;
      const tab = target.closest('.vp-objtab');
      if (tab && tab.dataset.objId) setFocusedObjective(Number(tab.dataset.objId));
    };
    bar.addEventListener('click', (ev) => pick(ev.target));
    bar.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); pick(ev.target); }
    });
  }
  const prev = $('vp-mk-prev'); if (prev) prev.addEventListener('click', () => stepMarker(-1));
  const next = $('vp-mk-next'); if (next) next.addEventListener('click', () => stepMarker(1));
  // Keep the "marker i / N" readout + prev/next disabled-state honest as the video
  // scrubs (the ordinal is playhead-derived). Throttled to ~once/sec; only when framed.
  const v = video();
  if (v) {
    let last = 0;
    v.addEventListener('timeupdate', () => {
      if (!_framed) return;
      const now = Math.floor(v.currentTime);
      if (now === last) return;
      last = now;
      renderMarkerStepper();
    });
  }
  const nx = $('vp-nudge-x');
  if (nx) nx.addEventListener('click', () => {
    const n = $('vp-nudge'); if (n) { n.dataset.dismissed = '1'; show(n, false); }
  });
}

// ── transport ───────────────────────────────────────────────────────────────
// seekTo delegates to the transport core (kept as a thin local alias because the
// timeline jump/seek-bar code below calls it). The core clamps to >= 0.
function seekTo(seconds) { if (_T) _T.seekTo(seconds); }

document.addEventListener('click', (ev) => {
  const t = ev.target.closest('[data-action]');
  if (!t) return;
  const action = t.dataset.action;
  const v = video();
  // playpause / seek (◀▶ by current step) / mute / fullscreen-enlarge are all owned
  // by the shared transport core; forward them through its one entry point.
  if (_T && _T.handleAction(action, t)) return;
  if (action === 'jump') {
    seekTo(Number(t.dataset.seconds || 0));
    if (v.paused) v.play();
  }
});

// The speed dropdown's change → playbackRate is wired by the transport core (it
// receives the rateSel ref in boot()), so no separate wireSpeedSelect is needed.

// ENLARGE (not OS fullscreen) is owned by the transport core's toggleEnlarge: it
// toggles .vp-expanded on #vp-wrap (CSS hides the OPEN REVIEW card, the clip/quick-
// bookmark "duo", and the Moments panel, and stretches the video) and flips the
// button glyph. The onExpandChange callback (wired in boot()) scrolls the stage
// into view on enlarge. A second press (button, F, or Esc) restores the layout.

// ── Quick Bookmark + bookmark CRUD (WRITE) ───────────────────────────────────
// Add a note-bookmark at the current video time, edit a bookmark's note, delete a
// bookmark. Each invokes the sidecar then reloadBookmarks() (re-fetches the VOD
// snapshot and re-renders markers/list without touching <video>). No-ops in
// preview (no Tauri backend).

// Fill the Quick Bookmark objective <select> from the active objectives loaded in
// fetchVod(). When the viewer is FRAMED on an objective, default the picker to THAT
// objective so a clip/bookmark made under the frame is tagged to what the user is
// reviewing (the moment then shows in the focused panel instead of silently vanishing
// — P-034). The user can still override to "No objective" or another objective. Safe
// to call repeatedly; re-synced on objective switch. Fills BOTH the Quick Bookmark
// (vp-bm-obj) and the Clip (vp-clip-obj) pickers, but each only while the user hasn't
// overridden it — so re-syncing on a frame switch never clobbers a deliberate pick.
function populateObjectivePicker() {
  const frameSel = _framed ? _focusedObjId : null;
  const bm = $('vp-bm-obj');
  if (bm && !_bmObjUserSet) fillObjectiveSelect(bm, frameSel);
  const clip = $('vp-clip-obj');
  if (clip && !_clipObjUserSet) fillObjectiveSelect(clip, frameSel);
}

// The objective id a NEW clip / quick bookmark should be tagged with. The picker's own
// dropdown wins when it holds an explicit value; otherwise, when framed, fall back to
// the focused objective so framed work is never saved untagged (and then hidden by the
// focused-panel filter — P-034). `pickerId` selects which dropdown to read (the clip
// card and the quick-bookmark card each have their own). Returns null when nothing
// applies (unframed + no explicit pick).
function resolveSaveObjectiveId(pickerId) {
  const objEl = $(pickerId);
  if (objEl && objEl.value) return Number(objEl.value);
  if (_framed && _focusedObjId != null) return Number(_focusedObjId);
  return null;
}

// Fill an objective <select> with "No objective" + every active objective, marking
// the currently-attached one selected. Shared by the Quick Bookmark picker and the
// per-moment evidence / bookmark pickers so they stay consistent.
function fillObjectiveSelect(sel, selectedObjectiveId) {
  if (!sel) return;
  clear(sel);
  const cur = selectedObjectiveId != null ? Number(selectedObjectiveId) : null;
  const none = document.createElement('option');
  none.value = '';
  // When nothing is pre-selected, "No objective" IS the default and reads plainly.
  // When an objective is pre-selected (framed), this option is the explicit opt-out
  // so it's clear the clip auto-tags unless you pick this — you never have to hunt
  // for the right objective in the list; it's already chosen.
  none.textContent = cur != null ? 'No objective (untag)' : 'No objective';
  sel.appendChild(none);
  for (const o of _objectives) {
    const opt = document.createElement('option');
    opt.value = String(o.objectiveId);
    opt.textContent = o.title;
    if (cur != null && Number(o.objectiveId) === cur) opt.selected = true;
    sel.appendChild(opt);
  }
}

function bmHint(msg, isErr) {
  const h = $('vp-bm-hint');
  if (!h) return;
  h.textContent = msg || '';
  h.classList.toggle('err', !!isErr);
  show(h, !!msg);
}

async function addBookmark() {
  if (!_core || _gameId <= 0) { bmHint('Preview only; no backend to save to.', false); return; }
  const v = video();
  const timeS = Math.max(0, Math.floor(v.currentTime || 0));
  const noteEl = $('vp-bm-note');
  const note = noteEl ? noteEl.value.trim() : '';
  // Tag the bookmark to the Quick Bookmark picker's pick, or (when framed, no explicit
  // pick) the focused objective — so it shows in the focused panel, not vanishing (P-034).
  const objectiveId = resolveSaveObjectiveId('vp-bm-obj');

  const addBtn = $('vp-bm-add');
  if (addBtn) addBtn.disabled = true;
  try {
    const payload = { gameId: _gameId, timeS, note };
    if (objectiveId) payload.objectiveId = objectiveId;
    await _core.invoke('add_bookmark', { payload });
    if (noteEl) noteEl.value = '';
    bmHint(`Bookmark added at ${clock(timeS)}.`, false);
    await reloadBookmarks();
  } catch (err) {
    bmHint('Couldn’t save the bookmark.', true);
    console.error('[vodplayer] add_bookmark failed:', err);
  } finally {
    if (addBtn) addBtn.disabled = false;
  }
}

async function deleteBookmark(bookmarkId) {
  if (!_core || !bookmarkId) return;
  try {
    await _core.invoke('delete_bookmark', { payload: { bookmarkId: Number(bookmarkId) } });
    await reloadBookmarks();
  } catch (err) {
    console.error('[vodplayer] delete_bookmark failed:', err);
  }
}

async function saveBookmarkNote(bookmarkId, note) {
  if (!_core || !bookmarkId) return;
  try {
    await _core.invoke('update_bookmark_note', { payload: { bookmarkId: Number(bookmarkId), note: note || '' } });
    await reloadBookmarks();
  } catch (err) {
    console.error('[vodplayer] update_bookmark_note failed:', err);
  }
}

// ── Evidence (auto-moment / saved-clip) tag + note writes ─────────────────────
// The objective picker attaches/detaches an objective on the evidence row; when
// attaching with a gameId it also marks that objective practiced this game
// (set_evidence_objective). The note autosaves via the pattern/moment/note route
// (UpdateNoteAsync) which, for an auto-moment with a VOD + first note, also clips
// the window; alreadyClipped suppresses re-extraction once it's a clip.

async function setEvidenceObjective(evidenceId, objectiveId) {
  if (!_core || !evidenceId) return;
  try {
    const payload = { evidenceId: Number(evidenceId), gameId: _gameId };
    payload.objectiveId = objectiveId ? Number(objectiveId) : null; // null detaches
    await _core.invoke('set_evidence_objective', { payload });
    await reloadBookmarks();
  } catch (err) {
    console.error('[vodplayer] set_evidence_objective failed:', err);
  }
}

// Clip-bookmark objective tag: attach/detach on the bookmark row itself (no
// evidence row exists for these). Mirrors POST /api/bookmark/objective.
async function setBookmarkObjective(bookmarkId, objectiveId) {
  if (!_core || !bookmarkId) return;
  try {
    const payload = { bookmarkId: Number(bookmarkId) };
    payload.objectiveId = objectiveId ? Number(objectiveId) : null; // null detaches
    await _core.invoke('set_bookmark_objective', { payload });
    await reloadBookmarks();
  } catch (err) {
    console.error('[vodplayer] set_bookmark_objective failed:', err);
  }
}

async function saveEvidenceNote(noteEl) {
  if (!_core || !noteEl) return;
  const evidenceId = Number(noteEl.dataset.evId || 0);
  if (!evidenceId) return;
  const text = noteEl.value.trim();
  const startS = noteEl.dataset.startS !== '' ? Number(noteEl.dataset.startS) : null;
  const endS = noteEl.dataset.endS !== '' ? Number(noteEl.dataset.endS) : null;
  const alreadyClipped = noteEl.dataset.hasClip === '1';
  try {
    const payload = {
      evidenceId,
      text,
      gameId: _gameId,
      championName: (_vod && _vod.championName) ? _vod.championName : '',
      vodPath: (_vod && _vod.filePath) ? _vod.filePath : '',
      alreadyClipped,
    };
    if (startS != null) payload.startTimeS = startS;
    if (endS != null) payload.endTimeS = endS;
    await _core.invoke('save_pattern_moment_note', { payload });
    await reloadBookmarks();
  } catch (err) {
    console.error('[vodplayer] saveEvidenceNote failed:', err);
  }
}

// ── Clip share (revu.lol) + inline login-to-share ────────────────────────────
// Share uploads the clip via the sidecar (which needs the logged-in session token)
// and copies the public revu.lol link. If logged out / expired the sidecar returns
// 401+needsLogin; we reveal the inline email→OTP panel and stash the pending clip,
// then upload it after Verify. Mirrors VodPlayerViewModel.ShareClipAsync + the
// ShareLoginPanel email/OTP steps. textContent only; no innerHTML.

let _pendingShareBmId = 0;   // clip awaiting upload after a login completes
let _shareEmail = '';        // email entered in the login panel (carried to verify)
const SHARE_RETRY_DELAYS_MS = [1200, 2600, 5200];
const _shareJobs = new Map(); // bookmark id -> queued/uploading/error state
let _shareQueue = [];
let _shareQueueActive = false;
let _shareQueuePaused = false;
let _shareReloadNeeded = false;

function copyToClipboard(text) {
  try {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text);
      return true;
    }
  } catch (_) { /* clipboard blocked — fall through */ }
  return false;
}

function shareLoginErr(msg) {
  const e = $('vp-sl-err');
  if (!e) return;
  e.textContent = msg || '';
  show(e, !!msg);
}

function shareLoginMsg(msg) {
  const m = $('vp-sl-msg');
  if (!m) return;
  m.textContent = msg || '';
  show(m, !!msg);
}

// Reveal the inline login panel at the email step. Prefills the last-known email.
function openShareLogin(bmId, prefillEmail) {
  _pendingShareBmId = Number(bmId) || 0;
  const panel = $('vp-sharelogin');
  if (!panel) return;
  show(panel, true);
  show($('vp-sl-email'), true);
  show($('vp-sl-otp'), false);
  shareLoginErr('');
  shareLoginMsg('Log in to publish this clip to revu.lol.');
  const box = $('vp-sl-emailbox');
  if (box) { box.value = prefillEmail || _shareEmail || ''; box.focus(); }
  // The panel renders as a centered popover (CSS .vp-sharelogin[ ... ]:not([hidden]))
  // so it's column-agnostic — no scrollIntoView needed (it would chase the left card).
}

function cancelShareLogin(preserveQueue = false) {
  const bmId = _pendingShareBmId;
  _pendingShareBmId = 0;
  show($('vp-sharelogin'), false);
  shareLoginErr('');
  const code = $('vp-sl-codebox');
  if (code) code.value = '';
  if (!preserveQueue && _shareQueuePaused) {
    _shareQueuePaused = false;
    const current = _shareJobs.get(Number(bmId));
    if (current && current.status === 'needs_login') failShareJob(current, 'Log in to share clips.');
    const queued = _shareQueue.splice(0);
    for (const id of queued) {
      const job = _shareJobs.get(Number(id));
      if (job && job.status !== 'done') failShareJob(job, 'Log in to share queued clips.');
    }
  }
}

async function shareSendCode() {
  if (!_core) return;
  const box = $('vp-sl-emailbox');
  const email = box ? box.value.trim() : '';
  if (!email) { shareLoginErr('Enter an email to continue.'); return; }
  shareLoginErr('');
  shareLoginMsg('Sending a code…');
  try {
    const res = await _core.invoke('auth_login', { payload: { email } });
    _shareEmail = email;
    show($('vp-sl-email'), false);
    show($('vp-sl-otp'), true);
    shareLoginMsg((res && res.info) ? String(res.info) : `Check ${email} for a code.`);
    const code = $('vp-sl-codebox');
    if (code) code.focus();
  } catch (err) {
    shareLoginErr(errText(err));
  }
}

async function shareVerify() {
  if (!_core) return;
  const codeBox = $('vp-sl-codebox');
  const code = codeBox ? codeBox.value.trim() : '';
  if (!code) { shareLoginErr('Paste the code from your email.'); return; }
  shareLoginErr('');
  shareLoginMsg('Verifying…');
  try {
    await _core.invoke('auth_verify', { payload: { code, email: _shareEmail } });
    // Session persisted server-side — now upload the clip we stashed at Share time.
    const bmId = _pendingShareBmId;
    cancelShareLogin(true);
    refreshSignInHint(); // now signed in — drop the banner
    if (bmId) {
      _shareQueuePaused = false;
      enqueueShare(bmId, { front: true, resetAttempts: true });
    }
  } catch (err) {
    shareLoginErr(errText(err));
  }
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function shareJob(bmId) {
  const id = Number(bmId) || 0;
  if (!id) return null;
  let job = _shareJobs.get(id);
  if (!job) {
    job = { bmId: id, status: 'idle', attempts: 0, url: '', message: '', copied: false };
    _shareJobs.set(id, job);
  }
  return job;
}

function queueIndex(bmId) {
  return _shareQueue.indexOf(Number(bmId));
}

function queueContains(bmId) {
  return queueIndex(bmId) >= 0;
}

function enqueueShare(bmId, opts = {}) {
  const job = shareJob(bmId);
  if (!job || !_core) return;
  if (job.status === 'done' && job.url) return;
  if (opts.resetAttempts) job.attempts = 0;
  job.status = 'queued';
  job.message = '';
  if (!queueContains(job.bmId)) {
    if (opts.front) _shareQueue.unshift(job.bmId);
    else _shareQueue.push(job.bmId);
  }
  renderShareJob(job);
  processShareQueue();
}

async function processShareQueue() {
  if (_shareQueueActive || _shareQueuePaused) return;
  _shareQueueActive = true;
  try {
    while (_shareQueue.length > 0 && !_shareQueuePaused) {
      const bmId = _shareQueue.shift();
      const job = _shareJobs.get(Number(bmId));
      if (!job || job.status === 'done') continue;
      const result = await uploadShareJob(job);
      if (result === 'needs_login') break;
    }
  } finally {
    _shareQueueActive = false;
  }

  if (!_shareQueuePaused && _shareReloadNeeded) {
    _shareReloadNeeded = false;
    await reloadBookmarks();
  }

  if (!_shareQueuePaused && _shareQueue.length > 0) {
    processShareQueue();
  }
}

// The actual upload. Resolves clip path + champion server-side from the bookmark.
// Called only by the queue runner, so rapid clicks never overlap uploads.
async function uploadShareJob(job) {
  if (!_core || !job || !job.bmId) return 'failed';
  const champ = (_vod && _vod.championName) ? _vod.championName : '';
  for (;;) {
    job.status = 'uploading';
    job.message = '';
    renderShareJob(job);
    try {
      const res = await _core.invoke('share_clip', {
        payload: { gameId: _gameId, bookmarkId: Number(job.bmId), championName: champ },
      });
      if (res && res.ok === false) {
        const message = res.error ? String(res.error) : 'Share failed.';
        if (res.needsLogin) return pauseShareQueueForLogin(job, message);
        if (await retryShareJob(job, message)) continue;
        failShareJob(job, message);
        return 'failed';
      }
      const url = res && res.shareUrl ? String(res.shareUrl) : '';
      if (!url) {
        failShareJob(job, 'Share finished, but no link came back.');
        return 'failed';
      }
      const copied = copyToClipboard(url);
      job.status = 'done';
      job.url = url;
      job.copied = copied;
      job.message = copied ? 'Link copied.' : 'Shared. Use Copy to copy the link.';
      renderShareJob(job);
      window.dispatchEvent(new CustomEvent('revu:first-review-share-done', {
        detail: { gameId: _gameId, bookmarkId: job.bmId, shareUrl: url },
      }));
      _shareReloadNeeded = true;
      return 'done';
    } catch (err) {
      const raw = (err && err.message) ? err.message : String(err);
      const message = errText(err) || 'Share failed.';
      if (/401|needsLogin|logged in|log in/i.test(raw)) {
        return pauseShareQueueForLogin(job, message);
      }
      if (await retryShareJob(job, message, raw)) continue;
      failShareJob(job, message);
      console.error('[vodplayer] share_clip failed:', err);
      return 'failed';
    }
  }
}

function pauseShareQueueForLogin(job, message) {
  _shareQueuePaused = true;
  _pendingShareBmId = Number(job.bmId) || 0;
  job.status = 'needs_login';
  job.message = message || 'Log in to share clips.';
  renderShareJob(job);
  openShareLogin(job.bmId, _shareEmail);
  return 'needs_login';
}

function isRetryableShareError(message, raw) {
  const s = `${message || ''} ${raw || ''}`;
  if (/clip file not found|too large|90s|90 seconds|only mp4|only webm|not found|log in|logged in|unauthorized/i.test(s)) {
    return false;
  }
  return /temporarily unavailable|try again|too many uploads|wait a moment|timeout|timed out|request failed|couldn'?t reach|502|503|504|429/i.test(s);
}

async function retryShareJob(job, message, raw) {
  if (!isRetryableShareError(message, raw) || job.attempts >= SHARE_RETRY_DELAYS_MS.length) {
    return false;
  }
  const delay = SHARE_RETRY_DELAYS_MS[job.attempts];
  job.attempts += 1;
  job.status = 'retry_wait';
  job.message = `Temporary share issue. Retrying ${job.attempts}/${SHARE_RETRY_DELAYS_MS.length} in ${Math.ceil(delay / 1000)}s.`;
  renderShareJob(job);
  await sleep(delay);
  return true;
}

function failShareJob(job, message) {
  job.status = 'error';
  job.message = message || 'Share failed.';
  renderShareJob(job);
}

// Entry from a row's Share button (unshared rows only — once shared the button is a
// "Shared" indicator and the dedicated Copy button handles copying).
function shareClip(btn) {
  const bmId = Number(btn && btn.dataset && btn.dataset.shareBmId) || 0;
  if (!bmId) return;
  if (btn.dataset.shareUrl) return; // already shared — Copy button owns copying
  if (!_core) return;
  const existing = _shareJobs.get(bmId);
  if (existing && existing.status === 'needs_login') {
    openShareLogin(bmId, _shareEmail);
    return;
  }
  enqueueShare(bmId, { resetAttempts: existing && existing.status === 'error' });
}

// Copy an already-shared clip's link from the dedicated Copy button.
function copyClipLink(btn) {
  const url = (btn && btn.dataset && btn.dataset.shareUrl) || '';
  if (!url) return;
  const copied = copyToClipboard(url);
  const span = btn.querySelector('span');
  if (span) {
    const prev = span.textContent;
    span.textContent = copied ? 'Copied' : 'Copy failed';
    setTimeout(() => { if (span) span.textContent = prev || 'Copy'; }, 1600);
  }
}

// Set the share button's LABEL span (so the icon survives). Rows re-render, so query.
function shareBtnFor(bmId) {
  return document.querySelector(`.vp-share-btn[data-share-bm-id="${bmId}"]`);
}
function shareWrapFor(bmId) {
  const btn = shareBtnFor(bmId);
  return btn ? btn.closest('.vp-bm-share') : null;
}
function setShareLabel(btn, text) {
  const span = btn && btn.querySelector('.vp-share-lbl');
  if (span) span.textContent = text; else if (btn) btn.textContent = text;
}
function setShareBtnState(bmId, label, opts = {}) {
  const btn = shareBtnFor(bmId);
  if (!btn) return;
  btn.disabled = !!opts.disabled;
  btn.title = opts.title || '';
  btn.classList.toggle('is-queued', opts.state === 'queued');
  btn.classList.toggle('is-uploading', opts.state === 'uploading');
  btn.classList.toggle('is-error', opts.state === 'error');
  btn.classList.toggle('is-shared', opts.state === 'shared');
  if (label) setShareLabel(btn, label);
}
function setShareStatusChip(bmId, text, isErr) {
  const wrap = shareWrapFor(bmId);
  const urlEl = wrap ? wrap.querySelector('.vp-share-url') : null;
  if (!urlEl) return;
  urlEl.textContent = text || '';
  urlEl.classList.toggle('is-status', !!text && !/^https?:\/\//i.test(text));
  urlEl.classList.toggle('err', !!isErr);
  show(urlEl, !!text);
}
function setShareCopyVisible(bmId, url, visible) {
  const wrap = shareWrapFor(bmId);
  const copyBtn = wrap ? wrap.querySelector('.vp-copy-btn') : null;
  if (!copyBtn) return;
  copyBtn.dataset.shareUrl = url || '';
  show(copyBtn, !!visible);
}
function renderShareJobForRow(bmId, alreadyShared) {
  const job = _shareJobs.get(Number(bmId));
  if (!job) return;
  if (alreadyShared && job.status !== 'queued' && job.status !== 'uploading' && job.status !== 'retry_wait') {
    _shareJobs.delete(Number(bmId));
    return;
  }
  renderShareJob(job);
}
function renderShareJob(job) {
  if (!job) return;
  const bmId = Number(job.bmId);
  const pos = queueIndex(bmId) + 1;
  if (job.status === 'queued') {
    setShareBtnState(bmId, pos > 0 ? `Queued #${pos}` : 'Queued', { disabled: true, state: 'queued' });
    setShareStatusChip(bmId, 'Uploads run one at a time.', false);
    setShareCopyVisible(bmId, '', false);
  } else if (job.status === 'uploading') {
    setShareBtnState(bmId, 'Uploading…', { disabled: true, state: 'uploading' });
    setShareStatusChip(bmId, 'Uploading now. Other shares will wait.', false);
    setShareCopyVisible(bmId, '', false);
  } else if (job.status === 'retry_wait') {
    setShareBtnState(bmId, 'Retrying…', { disabled: true, state: 'uploading', title: job.message });
    setShareStatusChip(bmId, job.message, false);
    setShareCopyVisible(bmId, '', false);
  } else if (job.status === 'needs_login') {
    setShareBtnState(bmId, 'Log in to share', { disabled: false, state: 'error', title: job.message });
    setShareStatusChip(bmId, job.message || 'Log in to share clips.', true);
    setShareCopyVisible(bmId, '', false);
  } else if (job.status === 'error') {
    setShareBtnState(bmId, 'Retry share', { disabled: false, state: 'error', title: job.message });
    setShareStatusChip(bmId, job.message || 'Share failed.', true);
    setShareCopyVisible(bmId, '', false);
  } else if (job.status === 'done' && job.url) {
    setShareBtnDone(bmId, job.url, job.copied);
    setShareStatusChip(bmId, job.url, false);
  }
}
function setShareBtnDone(bmId, url, copied) {
  const btn = shareBtnFor(bmId);
  if (btn) {
    btn.disabled = false;
    btn.dataset.shareUrl = url;
    btn.classList.add('is-shared');
    btn.classList.remove('is-queued', 'is-uploading', 'is-error');
    setShareLabel(btn, 'Shared');
  }
  // Reveal + arm the dedicated Copy-link button now that a URL exists.
  setShareCopyVisible(bmId, url, true);
}

// Normalize a Tauri/sidecar error to a displayable string (strips the HTTP prefix).
function errText(err) {
  const s = (err && err.message) ? err.message : String(err);
  const m = s.match(/sidecar HTTP \d+(?:\s+[^:]+)?:\s*(.*)$/i);
  return m ? m[1] : s;
}

// Delegated WRITE handler — kept separate from the transport handler so it can be
// async. Add button + per-row delete.
document.addEventListener('click', async (ev) => {
  const t = ev.target.closest('[data-action]');
  if (!t) return;
  const action = t.dataset.action;
  if (action === 'add_bookmark') {
    ev.preventDefault();
    await addBookmark();
  } else if (action === 'delete_bookmark') {
    ev.preventDefault();
    ev.stopPropagation(); // don't let the row's jump fire
    await deleteBookmark(t.dataset.bmId);
  } else if (action === 'clip_in') {
    ev.preventDefault();
    setClipIn();
  } else if (action === 'clip_out') {
    ev.preventDefault();
    setClipOut();
  } else if (action === 'clip_clear') {
    ev.preventDefault();
    clearClip();
  } else if (action === 'clip_qual') {
    ev.preventDefault();
    setClipQuality(t.dataset.q || '');
  } else if (action === 'clip_save') {
    ev.preventDefault();
    await saveClip();
  } else if (action === 'autoclip_current') {
    ev.preventDefault();
    await runAutoClipForObjective(t.dataset.objId || null);
  } else if (action === 'autoclip_objective') {
    ev.preventDefault();
    ev.stopPropagation(); // don't let the objective tab's switch-frame click fire
    await runAutoClipForObjective(t.dataset.objId);
  } else if (action === 'share_clip') {
    ev.preventDefault();
    ev.stopPropagation(); // don't let the row's jump fire
    await shareClip(t);
  } else if (action === 'copy_clip_link') {
    ev.preventDefault();
    ev.stopPropagation(); // don't let the row's jump fire
    copyClipLink(t);
  } else if (action === 'share_send_code') {
    ev.preventDefault();
    await shareSendCode();
  } else if (action === 'share_verify') {
    ev.preventDefault();
    await shareVerify();
  } else if (action === 'share_login_cancel') {
    ev.preventDefault();
    cancelShareLogin();
  }
});

// Per-row note edit: save on blur or Enter. Listens at document level (the rows
// are re-rendered on every reload, so per-element listeners would leak).
document.addEventListener('blur', (ev) => {
  const t = ev.target;
  if (!t || !t.closest) return;
  const bmNote = t.closest('.vp-bm-editnote');
  if (bmNote) {
    const row = bmNote.closest('[data-bm-id]');
    if (row) saveBookmarkNote(row.dataset.bmId, bmNote.value.trim());
    return;
  }
  const evNote = t.closest('.vp-ev-note');
  if (evNote) saveEvidenceNote(evNote);
}, true);

// Objective tag picker: attach/detach on change. Two write routes — evidence rows
// (auto-moments + evidence-backed clips) go through set_evidence_objective; plain
// clip-bookmarks (no evidence id) go through set_bookmark_objective.
document.addEventListener('change', (ev) => {
  // Quick Bookmark / Clip pickers: no write here — they just set the objective the NEXT
  // bookmark/clip inherits. Mark the picker user-controlled so frame switches stop
  // auto-syncing it (the user has made an explicit choice — P-034). The two pickers track
  // their override state independently.
  const bmSel = ev.target.closest && ev.target.closest('#vp-bm-obj');
  if (bmSel) { _bmObjUserSet = true; return; }
  const clipSel = ev.target.closest && ev.target.closest('#vp-clip-obj');
  if (clipSel) { _clipObjUserSet = true; return; }
  const sel = ev.target.closest && ev.target.closest('.vp-ev-obj');
  if (!sel) return;
  ev.stopPropagation();
  if (sel.dataset.route === 'bookmark') {
    setBookmarkObjective(sel.dataset.bmId, sel.value || '');
  } else {
    setEvidenceObjective(sel.dataset.evId, sel.value || '');
  }
});

// ── Clip tool (I/O in-out points, quality, ffmpeg extract via Save Clip) ──────
// Mirrors the WinUI VodPlayerViewModel clip flow: I/O set the in/out points to the
// current video time, quality chips pick good/neutral/bad, Save Clip POSTs to the
// sidecar (ffmpeg → clip-backed bookmark + evidence). No-ops in preview.

function clipHint(msg, isErr) {
  const h = $('vp-clip-hint');
  if (!h) return;
  h.textContent = msg || '';
  h.classList.toggle('err', !!isErr);
  show(h, !!msg);
}

// Reflect the in/out range + Save enablement into the Clip card.
function renderClipState() {
  const inBtn = $('vp-clip-in');
  const outBtn = $('vp-clip-out');
  const clearBtn = $('vp-clip-clear');
  const saveBtn = $('vp-clip-save');
  const rangeEl = $('vp-clip-range');

  if (inBtn) inBtn.classList.toggle('is-set', _clipIn >= 0);
  if (outBtn) outBtn.classList.toggle('is-set', _clipOut >= 0);

  const hasRange = _clipIn >= 0 && _clipOut >= 0 && Math.abs(_clipOut - _clipIn) >= 1;
  if (rangeEl) {
    if (_clipIn >= 0 || _clipOut >= 0) {
      const a = _clipIn >= 0 ? clock(Math.min(_clipIn, _clipOut < 0 ? _clipIn : _clipOut)) : '—';
      const b = _clipOut >= 0 ? clock(Math.max(_clipOut, _clipIn < 0 ? _clipOut : _clipIn)) : '—';
      rangeEl.textContent = `${a} → ${b}`;
    } else {
      rangeEl.textContent = '';
    }
  }
  if (clearBtn) show(clearBtn, _clipIn >= 0 || _clipOut >= 0);
  if (saveBtn) saveBtn.disabled = !hasRange || _clipBusy;

  // Quality chip selection visuals.
  document.querySelectorAll('#vp-clip-qual .q').forEach((q) => {
    q.classList.toggle('on', q.dataset.q === _clipQuality);
  });

  renderClipOverlay();
}

// Draw the In/Out clip markers (and the band between them) onto the event
// timeline so setting Start/End is visible there, not just in the Clip card.
// Positions are % of duration; flags clamp to [0,100] so they never overflow.
function renderClipOverlay() {
  const band = $('vp-clip-band');
  const flagIn = $('vp-clip-flag-in');
  const flagOut = $('vp-clip-flag-out');
  if (!band && !flagIn && !flagOut) return;

  const v = video();
  const dur = (v && v.duration) || (_vod && _vod.gameDurationSeconds) || 0;
  const pct = (s) => (dur > 0 ? Math.max(0, Math.min(100, (s / dur) * 100)) : 0);

  if (flagIn) {
    const on = _clipIn >= 0;
    show(flagIn, on);
    if (on) flagIn.style.left = `${pct(_clipIn)}%`;
  }
  if (flagOut) {
    const on = _clipOut >= 0;
    show(flagOut, on);
    if (on) flagOut.style.left = `${pct(_clipOut)}%`;
  }
  if (band) {
    const both = _clipIn >= 0 && _clipOut >= 0;
    show(band, both);
    if (both) {
      const a = pct(Math.min(_clipIn, _clipOut));
      const b = pct(Math.max(_clipIn, _clipOut));
      band.style.left = `${a}%`;
      band.style.width = `${Math.max(0, b - a)}%`;
    }
  }
}

function setClipIn() {
  const v = video();
  _clipIn = Math.max(0, Math.floor(v.currentTime || 0));
  clipHint('');
  renderClipState();
}
function setClipOut() {
  const v = video();
  _clipOut = Math.max(0, Math.floor(v.currentTime || 0));
  clipHint('');
  renderClipState();
}
function clearClip() {
  _clipIn = -1; _clipOut = -1; _clipQuality = '';
  const note = $('vp-clip-note');
  if (note) note.value = '';
  clipHint('');
  // A fresh clip starts over: drop any per-clip objective override so the picker
  // re-follows the focused objective (the next clip auto-ties to what's in focus).
  _clipObjUserSet = false;
  populateObjectivePicker();
  renderClipState();
}
function setClipQuality(q) {
  _clipQuality = _clipQuality === q ? '' : q; // re-tap clears
  renderClipState();
}

async function saveClip() {
  if (_clipBusy) return;
  const hasRange = _clipIn >= 0 && _clipOut >= 0 && Math.abs(_clipOut - _clipIn) >= 1;
  if (!hasRange) { clipHint('Set an In and Out point first (I / O).', true); return; }
  if (!_core || _gameId <= 0) { clipHint('Preview only; no backend to export to.', false); return; }
  if (!_vod || !_vod.filePath) { clipHint('No recording on disk to clip from.', true); return; }

  const startTimeS = Math.min(_clipIn, _clipOut);
  const endTimeS = Math.max(_clipIn, _clipOut);
  const noteEl = $('vp-clip-note');
  const note = noteEl ? noteEl.value.trim() : '';
  // Tag the clip to the Clip card's own picker, or (when framed, no explicit pick) the
  // focused objective — otherwise a framed clip saves untagged and the focused-panel
  // filter hides it, so it "doesn't show" (P-034).
  const objectiveId = resolveSaveObjectiveId('vp-clip-obj');

  _clipBusy = true;
  renderClipState();
  clipHint('Saving clip…', false);
  try {
    const payload = {
      gameId: _gameId,
      vodPath: _vod.filePath,
      championName: _vod.championName || '',
      startTimeS,
      endTimeS,
      note,
      quality: _clipQuality,
    };
    if (objectiveId) payload.objectiveId = objectiveId;
    const res = await _core.invoke('extract_clip', { payload });
    if (res && res.ok) {
      const qualMsg = _clipQuality ? ` as ${_clipQuality}` : '';
      clipHint(`Clip saved${qualMsg} (${clock(startTimeS)}–${clock(endTimeS)}).`, false);
      clearClip();
      await reloadBookmarks();
      _bmFilter = 'clips';            // surface the new clip in the Clips lane
      document.querySelectorAll('#vp-tabs .tab').forEach((t) => t.classList.toggle('on', t.dataset.filter === 'clips'));
      renderMoments();
    } else {
      clipHint((res && res.error) || 'Clip save failed.', true);
    }
  } catch (err) {
    // The sidecar returns "...ffmpeg..." in the message when it isn't installed.
    const msg = String(err && err.message ? err.message : err);
    clipHint(/ffmpeg/i.test(msg) ? 'Clip save failed; is ffmpeg installed?' : 'Clip save failed.', true);
    console.error('[vodplayer] extract_clip failed:', err);
  } finally {
    _clipBusy = false;
    renderClipState();
  }
}

// ── Auto-clip objectives (focused/all objective events, on-demand) ───────────
// The Clip panel always carries the discoverable control when a recording is loaded.
// Framed clicks target the focused objective; unframed clicks omit objectiveId so the
// sidecar clips all objective-tied events. The active objective tab keeps its compact
// shortcut for the same per-objective path. Idempotent server-side.

// Set the auto-clip status line (persisted in state so it survives objbar re-renders).
function setAutoClipHint(msg, isErr) {
  _autoClipHintMsg = msg || '';
  _autoClipHintErr = !!isErr;
  const h = $('vp-objclip-hint');
  if (h) {
    h.textContent = _autoClipHintMsg;
    h.classList.toggle('err', _autoClipHintErr);
    h.hidden = !_autoClipHintMsg;
  }
  renderAutoClipPanel();
}

async function runAutoClipForObjective(objId) {
  if (_autoClipBusy) return;
  const hasObjId = objId != null && objId !== '';
  const oid = hasObjId ? Number(objId) : null;
  if (hasObjId && !oid) return;
  if (!_core || _gameId <= 0) { setAutoClipHint('Preview only; no backend to clip to.', false); return; }
  if (!_vod || !_vod.filePath) { setAutoClipHint('No recording on disk to clip from.', true); return; }
  if (!_autoClipEnabled) { setAutoClipHint('Turn on "Auto-clip objective events" in Settings first.', true); return; }

  const count = oid ? markerCountForObjective(oid) : objectiveTiedEventCount();
  if (count <= 0) {
    setAutoClipHint(oid ? 'No tied events for the focused objective in this game.' : 'No objective-tied events in this game.', false);
    return;
  }

  _autoClipBusy = true;
  setAutoClipHint('Auto-clipping… this can take a moment.', false);
  renderObjBar();   // reflect the disabled/busy button label
  renderAutoClipPanel();
  try {
    const payload = { gameId: _gameId };
    if (oid) payload.objectiveId = oid;
    const res = await _core.invoke('auto_clip_objectives', { payload });
    if (res && res.ok) {
      const created = Number(res.created || 0);
      const skipped = Number(res.skipped || 0);
      if (created > 0) {
        const extra = skipped > 0 ? ` (${skipped} skipped)` : '';
        setAutoClipHint(`Saved ${created} clip${created === 1 ? '' : 's'}${extra}.`, false);
        await reloadBookmarks();
        _bmFilter = 'clips';          // surface the new clips in the Clips lane
        document.querySelectorAll('#vp-tabs .tab').forEach((tb) => tb.classList.toggle('on', tb.dataset.filter === 'clips'));
        renderMoments();
        window.dispatchEvent(new CustomEvent('revu:first-review-autoclip-done', {
          detail: { gameId: _gameId, objectiveId: oid || null, created, skipped },
        }));
      } else if (res.reason === 'disabled') {
        setAutoClipHint('Turn on "Auto-clip objective events" in Settings first.', true);
      } else if (res.reason === 'no_vod') {
        setAutoClipHint('No recording on disk to clip from.', true);
      } else {
        setAutoClipHint('Nothing new to clip — these events are already clipped.', false);
      }
    } else {
      setAutoClipHint((res && res.error) || 'Auto-clip failed.', true);
    }
  } catch (err) {
    const msg = String(err && err.message ? err.message : err);
    setAutoClipHint(/ffmpeg/i.test(msg) ? 'Auto-clip failed; is ffmpeg installed?' : 'Auto-clip failed.', true);
    console.error('[vodplayer] auto_clip_objectives failed:', err);
  } finally {
    _autoClipBusy = false;
    renderObjBar();   // restore the button + show the persisted hint
    renderAutoClipPanel();
  }
}

// Click the seek bar to scrub.
// ── Timeline zoom + pan ───────────────────────────────────────────────────────
// Ctrl+wheel zooms the event timeline centered on the cursor; the content track grows
// wider than its viewport (--tl-zoom) and is panned with a pixel offset (--tl-pan).
// Drag-to-pan moves it (with a threshold so a plain click still seeks). Zoom + pan
// PERSIST across objective switches; reset only on reload. All event/playhead/clip
// positions are left:% inside .vp-tl-content, so they scale automatically.
const TL_ZOOM_MIN = 1;
const TL_ZOOM_MAX = 12;
let _tlZoom = 1;     // 1 = whole game fills the viewport
let _tlPan = 0;      // px the content is shifted left (<= 0). 0 = start at left edge.

// Clamp pan so the content can't be dragged past either edge of the viewport.
function clampPan() {
  const seek = $('vp-seek');
  if (!seek) return;
  const vw = seek.clientWidth;
  const contentW = vw * _tlZoom;
  const minPan = Math.min(0, vw - contentW); // most-negative (right edge flush)
  if (_tlPan > 0) _tlPan = 0;
  if (_tlPan < minPan) _tlPan = minPan;
}

// Write the zoom/pan to the DOM + badge. Called after any zoom/pan change.
function applyTimelineZoom() {
  const seek = $('vp-seek');
  const content = $('vp-tl-content');
  if (!seek || !content) return;
  if (_tlZoom < TL_ZOOM_MIN) _tlZoom = TL_ZOOM_MIN;
  if (_tlZoom > TL_ZOOM_MAX) _tlZoom = TL_ZOOM_MAX;
  clampPan();
  content.style.setProperty('--tl-zoom', String(_tlZoom));
  content.style.setProperty('--tl-pan', `${_tlPan}px`);
  const zoomed = _tlZoom > 1.001;
  seek.classList.toggle('is-zoomed', zoomed);
  // EARLY/MID/LATE are pinned to the viewport, so they'd lie once the content scrolls
  // under them — hide them while zoomed (they're coarse orientation, not exact marks).
  seek.querySelectorAll('.ph').forEach((el) => { el.style.opacity = zoomed ? '0' : ''; });
  const badge = $('vp-tl-zoom');
  if (badge) { badge.textContent = `${_tlZoom.toFixed(1)}×`; show(badge, zoomed); }
}

// Zoom by a factor, keeping the timeline-content point under `anchorClientX` fixed.
function zoomTimelineAt(anchorClientX, factor) {
  const seek = $('vp-seek');
  if (!seek) return;
  const rect = seek.getBoundingClientRect();
  const x = anchorClientX - rect.left;                 // cursor x within the viewport
  const contentXBefore = (x - _tlPan) / _tlZoom;       // game-fraction px under cursor
  const prev = _tlZoom;
  _tlZoom = Math.max(TL_ZOOM_MIN, Math.min(TL_ZOOM_MAX, _tlZoom * factor));
  if (_tlZoom === prev) return;
  // Solve new pan so the same content point stays under the cursor.
  _tlPan = x - contentXBefore * _tlZoom;
  if (_tlZoom <= 1.001) { _tlZoom = 1; _tlPan = 0; } // snapped back to whole game
  applyTimelineZoom();
}

function resetTimelineZoom() { _tlZoom = 1; _tlPan = 0; applyTimelineZoom(); }

// Map a click's clientX to a game fraction [0,1], accounting for zoom + pan.
function timelineFractionFromClientX(clientX) {
  const seek = $('vp-seek');
  const rect = seek.getBoundingClientRect();
  const x = clientX - rect.left;
  const contentX = x - _tlPan;                 // x within the (wider) content
  return Math.max(0, Math.min(1, contentX / (rect.width * _tlZoom)));
}

function wireSeekBar() {
  const seek = $('vp-seek');

  // Ctrl+wheel → zoom at cursor. Plain wheel is left alone (page scroll).
  seek.addEventListener('wheel', (ev) => {
    if (!ev.ctrlKey) return;
    ev.preventDefault();
    const factor = ev.deltaY < 0 ? 1.18 : 1 / 1.18;   // up = zoom in
    zoomTimelineAt(ev.clientX, factor);
  }, { passive: false });

  // Drag-to-pan (only meaningful when zoomed). A movement threshold distinguishes a
  // pan-drag from a click — under the threshold it falls through to click→seek.
  const DRAG_PX = 4;
  let down = null; // {x, pan, dragging}
  seek.addEventListener('pointerdown', (ev) => {
    if (ev.button !== 0) return;
    down = { x: ev.clientX, pan: _tlPan, dragging: false };
  });
  seek.addEventListener('pointermove', (ev) => {
    if (!down) return;
    const dx = ev.clientX - down.x;
    if (!down.dragging && Math.abs(dx) < DRAG_PX) return;
    if (_tlZoom <= 1.001) return;                 // nothing to pan at 1×
    down.dragging = true;
    seek.classList.add('is-panning');
    try { seek.setPointerCapture(ev.pointerId); } catch (_) {}
    _tlPan = down.pan + dx;
    applyTimelineZoom();
  });
  const endDrag = (ev) => {
    if (!down) return;
    const wasDragging = down.dragging;
    seek.classList.remove('is-panning');
    try { seek.releasePointerCapture(ev.pointerId); } catch (_) {}
    down = null;
    // If we actually panned, swallow the trailing click so it doesn't seek.
    if (wasDragging) { seek._suppressClick = true; setTimeout(() => { seek._suppressClick = false; }, 0); }
  };
  seek.addEventListener('pointerup', endDrag);
  seek.addEventListener('pointercancel', endDrag);

  // Click → seek (zoom/pan-aware). Suppressed right after a pan-drag.
  seek.addEventListener('click', (ev) => {
    if (seek._suppressClick) return;
    if (ev.target && ev.target.closest('#vp-tl-zoom')) return; // badge handles its own
    const v = video();
    const dur = v.duration || _vod?.gameDurationSeconds || 0;
    if (!dur) return;
    seekTo(timelineFractionFromClientX(ev.clientX) * dur);
  });

  // Zoom badge → reset to whole game.
  const badge = $('vp-tl-zoom');
  if (badge) badge.addEventListener('click', (ev) => { ev.stopPropagation(); resetTimelineZoom(); });

  // Re-clamp pan if the window resizes (viewport width changed).
  window.addEventListener('resize', () => { if (_tlZoom > 1.001) applyTimelineZoom(); });
}

// Keyboard: space = play/pause, arrows = seek, B = quick bookmark. Enter/Space on
// a focused moment row jumps to its time (the rows are role=button divs).
document.addEventListener('keydown', (ev) => {
  // Field-local Enter shortcuts FIRST (these fields are exempt from the global
  // typing-guard below): Enter in the Quick Bookmark note adds a bookmark; Enter
  // in a per-row edit-note field saves + blurs.
  if (ev.key === 'Enter') {
    if (ev.target && ev.target.id === 'vp-bm-note') {
      ev.preventDefault();
      addBookmark();
      return;
    }
    if (ev.target && ev.target.id === 'vp-clip-note') {
      ev.preventDefault();
      saveClip(); // Enter in the clip note saves the clip (mirrors WinUI ClipNoteBox)
      return;
    }
    const editNote = ev.target.closest && ev.target.closest('.vp-bm-editnote');
    if (editNote) {
      ev.preventDefault();
      editNote.blur(); // blur handler persists the note
      return;
    }
    const evNote = ev.target.closest && ev.target.closest('.vp-ev-note');
    if (evNote) {
      ev.preventDefault();
      evNote.blur(); // blur handler persists the evidence note
      return;
    }
  }
  // Never hijack typing in a text field, or the arrow/Space keys of a focused
  // <select> (the speed dropdown), for the global shortcuts.
  if (ev.target.tagName === 'INPUT' || ev.target.tagName === 'TEXTAREA' || ev.target.tagName === 'SELECT') return;

  const moment = ev.target.closest && ev.target.closest('.moment[data-action="jump"]');
  if (moment && (ev.key === 'Enter' || ev.key === ' ')) {
    ev.preventDefault();
    const v0 = video();
    seekTo(Number(moment.dataset.seconds || 0));
    if (v0 && v0.paused) v0.play();
    return;
  }
  // B — quick bookmark at the current time (mirrors the WinUI B-key shortcut).
  if (ev.key === 'b' || ev.key === 'B') {
    ev.preventDefault();
    addBookmark();
    return;
  }
  // I / O — set clip in / out points; S — save clip (WinUI clip-tool shortcuts).
  if (ev.key === 'i' || ev.key === 'I') { ev.preventDefault(); setClipIn(); return; }
  if (ev.key === 'o' || ev.key === 'O') { ev.preventDefault(); setClipOut(); return; }
  if (ev.key === 's' || ev.key === 'S') { ev.preventDefault(); saveClip(); return; }
  // F — enlarge / restore the video (mirrors the transport button).
  if (ev.key === 'f' || ev.key === 'F') { ev.preventDefault(); if (_T) _T.toggleEnlarge(); return; }
  // Esc — restore the layout if currently enlarged.
  if (ev.key === 'Escape') {
    if (_T && _T.isExpanded()) { ev.preventDefault(); _T.toggleEnlarge(); }
    return;
  }
  // Up / Down change the seek STEP (work even before the video has a src).
  if (ev.key === 'ArrowUp') { ev.preventDefault(); if (_T) _T.nudgeStep(1); return; }
  if (ev.key === 'ArrowDown') { ev.preventDefault(); if (_T) _T.nudgeStep(-1); return; }
  const v = video();
  if (!v || !v.src) return;
  if (ev.key === ' ') { ev.preventDefault(); if (_T) _T.toggle(); }
  // Left / Right seek backward / forward by the current step. preventDefault so
  // the arrows never scroll the page (the old behavior the user hit).
  else if (ev.key === 'ArrowLeft') { ev.preventDefault(); if (_T) _T.seekByStep(-1); }
  else if (ev.key === 'ArrowRight') { ev.preventDefault(); if (_T) _T.seekByStep(1); }
});

// ── error ─────────────────────────────────────────────────────────────────
function renderError(err) {
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show($('errpanel'), true);
}

// ── boot ──────────────────────────────────────────────────────────────────
async function boot() {
  // Build the shared transport core over the (full-chrome) player. It owns the
  // play/pause + mute glyphs, the time readout, the seek-fill + playhead, the speed
  // select, mute, and the in-app enlarge (toggling .vp-expanded on #vp-wrap, with a
  // scrollIntoView on enlarge). The page keeps its own click + keydown handlers and
  // forwards transport actions to _T.
  _T = createTransport({
    video: video(),
    timeEl: $('vp-time'),
    seekFillEl: $('vp-seek-fill'),
    playheadEl: $('vp-playhead'),
    playBtn: $('vp-play'),
    muteBtn: $('vp-mute'),
    fsBtn: $('vp-fs'),
    rateSel: $('vp-rate-sel'),
    expandTarget: $('vp-wrap'),
    expandClass: 'vp-expanded',
    fullGlyphs: true,
    durationFallback: () => (_vod && _vod.gameDurationSeconds) || 0,
    onExpandChange: (expanded) => {
      if (expanded) { const stage = document.querySelector('.vp-stage'); if (stage) stage.scrollIntoView({ behavior: 'smooth', block: 'start' }); }
    },
  });
  _T.attachVideo({ clickToToggle: true, stopProp: false });

  wireSeekBar();
  wireTabs();
  wireFraming();
  try {
    const { data, core } = await fetchVod();
    render(data, core);
  } catch (err) {
    renderError(err);
    console.error('[vodplayer] load failed:', err);
  }
  // Best-effort: surface the "sign in to share" hint when signed out. Never
  // blocks the player — a failed/absent auth check just leaves the banner hidden.
  refreshSignInHint();
}

// Show the sign-in hint banner only when signed out. Sharing a clip to revu.lol
// needs an account; everything else on this page works offline.
async function refreshSignInHint() {
  const hint = $('vp-signin-hint');
  if (!hint || !_core) return;
  try {
    const s = await _core.invoke('get_auth_status');
    show(hint, !(s && s.signedIn));
  } catch (_) {
    // Auth status unavailable — don't nag; leave the hint hidden.
    show(hint, false);
  }
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
