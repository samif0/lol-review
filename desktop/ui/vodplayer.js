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
let _T = null;         // shared transport core (play/seek/step/rate/mute/enlarge)
const video = () => $('vp-video');

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

  // OPEN REVIEW → the structured review for this game (this nav IS wired).
  const openBtn = $('vp-open-review');
  if (openBtn) {
    openBtn.onclick = () => {
      const gid = data.gameId;
      window.location.href = gid
        ? `review.html?gameId=${encodeURIComponent(gid)}`
        : 'review.html';
    };
  }

  // Populate the timeline + moments + tools FIRST, off the snapshot, so the event
  // timeline (coded bars) and moments render even before (or without) a playable
  // video. onMeta re-places markers with the real duration once it loads.
  populateObjectivePicker();
  placeMarkers(_vod.gameDurationSeconds || 0);
  renderMoments();
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


// Markers on the EVENT TIMELINE. Two layers:
//  1. Live game events (kills/deaths/objectives) rendered as the original Revu
//     "3-letter code + bar" markers: a vertical bar at the event time topped with
//     its shortLabel (KIL/DTH/DRG/…), colored win/loss/gold/neutral via the
//     server-supplied kind + colorHex. Click-to-seek.
//  2. Bookmark markers (clip = gold, plain = accent) below the track so saved
//     moments stay visible. Click-to-seek.
function placeMarkers(dur) {
  const host = $('vp-markers');
  clear(host);
  if (!dur) return;

  const pctOf = (s) => Math.max(0, Math.min(100, (s / dur) * 100));

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
  const lastLabelPctByTier = { major: -99, medium: -99, minor: -99 };
  const MIN_LABEL_GAP_PCT = 3.2; // ~ one 3-letter code's width on the timeline

  for (const e of events) {
    const code = e.shortLabel || deriveShortLabel(e.eventType, e.label);
    const kind = e.kind || 'neutral';
    const tier = eventTier(e.eventType, e.label);
    const leftPct = pctOf(e.gameTimeSeconds);

    const bar = document.createElement('span');
    bar.className = `evbar evbar-${kind} evbar-${tier}`;
    bar.style.left = `${leftPct}%`;
    if (e.colorHex) bar.style.setProperty('--evc', e.colorHex);
    bar.style.pointerEvents = 'auto';
    bar.style.cursor = 'pointer';
    bar.title = `${e.timeLabel} ${e.label}${e.summary ? ' · ' + e.summary : ''}`.trim();
    bar.dataset.action = 'jump';
    bar.dataset.seconds = String(e.gameTimeSeconds);

    // Show the code label only when it won't crowd the previous one in its tier.
    // Minor events stay label-less by default (their code shows on hover) to keep
    // the timeline clean; major/medium always try to label.
    const wantsLabel = tier !== 'minor';
    const clearOfNeighbor = leftPct - lastLabelPctByTier[tier] >= MIN_LABEL_GAP_PCT;
    if (wantsLabel && clearOfNeighbor) {
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
// map objectives (decisive), medium = kills/deaths (player-relevant), minor =
// everything else. Keeps the busiest timelines readable.
function eventTier(eventType, label) {
  const t = String(eventType || label || '').toUpperCase().replace(/[^A-Z]/g, '');
  if (/BARON|DRAGON|ELDER|HERALD|RIFT|TOWER|TURRET|INHIB|NEXUS|ACE/.test(t)) return 'major';
  if (/KILL|DEATH|MULTIKILL|FIRSTBLOOD|PENTA|QUADRA|TRIPLE|DOUBLE/.test(t)) return 'medium';
  return 'minor';
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
  const auto = (v.autoMoments || []).map((m) => normMoment(m, 'Auto moment'));
  // Saved clips: prefer the evidence-inbox clip rows; fall back to bookmark clips.
  let clips = (v.savedClips || []).map((m) => normMoment(m, 'Saved clip'));
  if (clips.length === 0) {
    clips = (v.bookmarks || []).filter((b) => b.hasClip).map((b) => normBookmark(b, true));
  }
  // Bookmarks lane = plain (non-clip) bookmarks.
  const bm = (v.bookmarks || []).filter((b) => !b.hasClip).map((b) => normBookmark(b, false));
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
    note: m.note || m.title || '',
    noteDisplay: m.note || m.title || '(no note)',
    objectiveId: m.objectiveId != null ? m.objectiveId : null,
    objectiveTitle: m.objectiveTitle || '',
    polarity: m.polarity || 'neutral',
    polarityColorHex: m.polarityColorHex || '',
    status: m.status || '',
    editable: false,
    // Evidence rows (auto + clip) get the objective tag picker + editable note.
    evidenceEditable: m.id != null,
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
    noteDisplay: b.note || '(no note)',
    objectiveId: null,
    objectiveTitle: '',
    polarity: '',
    polarityColorHex: '',
    status: '',
    editable: !isClip && b.id != null, // only plain bookmarks get edit/delete
    evidenceEditable: false,
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
        clear(sel);
        const none = document.createElement('option');
        none.value = '';
        none.textContent = 'No objective';
        sel.appendChild(none);
        for (const o of _objectives) {
          const opt = document.createElement('option');
          opt.value = String(o.objectiveId);
          opt.textContent = o.title;
          if (m.objectiveId != null && Number(o.objectiveId) === Number(m.objectiveId)) opt.selected = true;
          sel.appendChild(opt);
        }
        sel.dataset.evId = String(m.evidenceId);
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
// fetchVod(). Safe to call repeatedly.
function populateObjectivePicker() {
  const sel = $('vp-bm-obj');
  if (!sel) return;
  clear(sel);
  const none = document.createElement('option');
  none.value = '';
  none.textContent = 'No objective';
  sel.appendChild(none);
  for (const o of _objectives) {
    const opt = document.createElement('option');
    opt.value = String(o.objectiveId);
    opt.textContent = o.title;
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
  const objEl = $('vp-bm-obj');
  const note = noteEl ? noteEl.value.trim() : '';
  const objectiveId = objEl && objEl.value ? Number(objEl.value) : null;

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

function cancelShareLogin() {
  _pendingShareBmId = 0;
  show($('vp-sharelogin'), false);
  shareLoginErr('');
  const code = $('vp-sl-codebox');
  if (code) code.value = '';
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
    cancelShareLogin();
    if (bmId) await uploadShare(bmId);
  } catch (err) {
    shareLoginErr(errText(err));
  }
}

// The actual upload. Resolves clip path + champion server-side from the bookmark.
// On 401/needsLogin (expired session) reopens the login panel; other failures
// (e.g. the 90s cap) surface the server's message on the button.
async function uploadShare(bmId) {
  if (!_core || !bmId) return;
  const champ = (_vod && _vod.championName) ? _vod.championName : '';
  setShareBtnBusy(bmId, true, 'Sharing…');
  try {
    const res = await _core.invoke('share_clip', {
      payload: { gameId: _gameId, bookmarkId: Number(bmId), championName: champ },
    });
    // A non-throwing failure (e.g. server returned ok:false with a reason).
    if (res && res.ok === false) {
      setShareBtnBusy(bmId, false, res.error ? String(res.error) : 'Share failed');
      return;
    }
    const url = res && res.shareUrl ? String(res.shareUrl) : '';
    if (url) {
      const copied = copyToClipboard(url);
      setShareBtnDone(bmId, url, copied);
    }
    await reloadBookmarks();
  } catch (err) {
    // The Tauri layer surfaces "sidecar HTTP 401: …" for an expired session.
    if (/401|needsLogin|logged in|log in/i.test(String(err))) {
      openShareLogin(bmId, _shareEmail);
    } else {
      // Surface the real reason (e.g. the 90s-cap "trim and re-clip" message) on the
      // button instead of a generic failure, then restore the label shortly after.
      setShareBtnBusy(bmId, false, errText(err) || 'Share failed');
      console.error('[vodplayer] share_clip failed:', err);
    }
  }
}

// Entry from a row's Share button (unshared rows only — once shared the button is a
// "Shared" indicator and the dedicated Copy button handles copying).
async function shareClip(btn) {
  const bmId = Number(btn && btn.dataset && btn.dataset.shareBmId) || 0;
  if (!bmId) return;
  if (btn.dataset.shareUrl) return; // already shared — Copy button owns copying
  if (!_core) return;
  await uploadShare(bmId);
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
function setShareLabel(btn, text) {
  const span = btn && btn.querySelector('.vp-share-lbl');
  if (span) span.textContent = text; else if (btn) btn.textContent = text;
}
function setShareBtnBusy(bmId, busy, label) {
  const btn = shareBtnFor(bmId);
  if (!btn) return;
  btn.disabled = !!busy;
  if (label) setShareLabel(btn, label);
}
function setShareBtnDone(bmId, url, copied) {
  const btn = shareBtnFor(bmId);
  if (btn) {
    btn.disabled = false;
    btn.dataset.shareUrl = url;
    btn.classList.add('is-shared');
    setShareLabel(btn, 'Shared');
  }
  // Reveal + arm the dedicated Copy-link button now that a URL exists.
  const copyBtn = document.querySelector(`.vp-copy-btn[data-share-bm-id="${bmId}"]`);
  if (copyBtn) { copyBtn.dataset.shareUrl = url; show(copyBtn, true); }
}

// Normalize a Tauri/sidecar error to a displayable string (strips the HTTP prefix).
function errText(err) {
  const s = (err && err.message) ? err.message : String(err);
  const m = s.match(/sidecar HTTP \d+:\s*(.*)$/i);
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

// Evidence objective tag picker: attach/detach on change.
document.addEventListener('change', (ev) => {
  const sel = ev.target.closest && ev.target.closest('.vp-ev-obj');
  if (!sel) return;
  ev.stopPropagation();
  setEvidenceObjective(sel.dataset.evId, sel.value || '');
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
  const objEl = $('vp-bm-obj'); // reuse the Quick Bookmark objective picker selection
  const note = noteEl ? noteEl.value.trim() : '';
  const objectiveId = objEl && objEl.value ? Number(objEl.value) : null;

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

// Click the seek bar to scrub.
function wireSeekBar() {
  const seek = $('vp-seek');
  seek.addEventListener('click', (ev) => {
    const v = video();
    const dur = v.duration || _vod?.gameDurationSeconds || 0;
    if (!dur) return;
    const rect = seek.getBoundingClientRect();
    const pct = Math.max(0, Math.min(1, (ev.clientX - rect.left) / rect.width));
    seekTo(pct * dur);
  });
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
  try {
    const { data, core } = await fetchVod();
    render(data, core);
  } catch (err) {
    renderError(err);
    console.error('[vodplayer] load failed:', err);
  }
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
