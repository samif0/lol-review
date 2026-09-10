// Revu desktop — Matchups (matchup journal) page renderer for the glass-aurora
// layout. Renders the JSON returned by the Tauri command `get_matchups`
// (see Revu.Sidecar GET /api/matchups) AND drives the card CRUD via the
// create_matchup / create_matchup_from_last_game / update_matchup /
// save_matchup_notes / delete_matchup commands, plus the Markdown export
// (get_matchups_export_markdown → clipboard). Mirrors rules.js conventions:
//   • getInvoke() prefers @tauri-apps/api/core, falls back to window.__TAURI__.
//   • Outside Tauri it fetches ./sample-matchups.json so the page previews in a
//     plain browser (writes no-op with a console note; the export builds its
//     Markdown client-side so COPY still demonstrates).
//   • Every server string is written via textContent (never innerHTML); ids ride
//     dataset attributes only.
//   • ONE delegated [data-action] click handler.
//   • After every write we refetch get_matchups — EXCEPT the inline note saves,
//     which update the in-memory copy and flash "Saved" (no re-render, so the
//     textarea under the cursor is never rebuilt mid-edit).
//   • The page also refetches on its own when the sidecar SAVES a game (the LCU
//     'gameEnded' event over the same stream pregame.js uses) and when the window
//     comes back into view — deferred while you are mid-edit — so "New card from
//     last game" points at the game you just played without a manual reload.

// ── invoke resolver ────────────────────────────────────────────────────────
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

// Tauri event listener (the Rust host re-emits the sidecar's SSE stream as
// 'lcu-event'). Same resolver shape as pregame.js; null outside Tauri.
async function getListen() {
  try {
    const mod = await import('@tauri-apps/api/event');
    if (mod && typeof mod.listen === 'function') return mod.listen;
  } catch (_) { /* fall through */ }
  if (window.__TAURI__ && window.__TAURI__.event && typeof window.__TAURI__.event.listen === 'function') {
    return window.__TAURI__.event.listen.bind(window.__TAURI__.event);
  }
  return null;
}

// ── small DOM helpers ───────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function show(el, on) { if (el) el.hidden = !on; }
function clear(el) { while (el && el.firstChild) el.removeChild(el.firstChild); }
function tpl(id) {
  const t = $(id);
  return t.content.firstElementChild.cloneNode(true);
}
function setVal(id, v) { const el = $(id); if (el) el.value = v == null ? '' : String(v); }
function getVal(id) { const el = $(id); return el ? el.value.trim() : ''; }
function plural(n, one) { return `${n} ${one}${n === 1 ? '' : 's'}`; }
function errText(err) { return (err && err.message) ? err.message : String(err); }

// ── lane vocabulary (mirror the sidecar's fixed lane table) ──────────────────
// Display order everywhere: top, jungle, mid, bot, support. `slots` is how many
// champion inputs show per side; the slot labels are the form's field captions.
const LANES = [
  { value: 'top', label: 'Top', slots: 1, ally: ['Your champion'], enemy: ['Enemy champion'] },
  { value: 'jungle', label: 'Jungle', slots: 2, ally: ['Your jungler', 'Your mid'], enemy: ['Enemy jungler', 'Enemy mid'] },
  { value: 'mid', label: 'Mid', slots: 1, ally: ['Your champion'], enemy: ['Enemy champion'] },
  { value: 'bot', label: 'Bot', slots: 2, ally: ['Your ADC', 'Your support'], enemy: ['Enemy ADC', 'Enemy support'] },
  { value: 'support', label: 'Support', slots: 2, ally: ['Your ADC', 'Your support'], enemy: ['Enemy ADC', 'Enemy support'] },
];
const LANE_BY = Object.fromEntries(LANES.map((l) => [l.value, l]));
// Form input ids per side, in slot order (slot 2 hidden for one-champ lanes).
const ALLY_IDS = ['f-ally1', 'f-ally2'];
const ENEMY_IDS = ['f-enemy1', 'f-enemy2'];

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchMatchups() {
  // Prefer the REAL backend (Tauri invoke → sidecar → your DB); fall back to the
  // bundled sample only when invoke is genuinely unavailable (browser preview).
  const invoke = await getInvoke();
  if (invoke) {
    return invoke('get_matchups');
  }
  const res = await fetch('./sample-matchups.json');
  if (!res.ok) throw new Error(`sample-matchups.json ${res.status}`);
  return res.json();
}

// Flatten lanes → groups → cards from a snapshot (fixed server order kept).
function allCards(d) {
  const out = [];
  const lanes = d && Array.isArray(d.lanes) ? d.lanes : [];
  for (const l of lanes) {
    for (const g of (Array.isArray(l.groups) ? l.groups : [])) {
      for (const c of (Array.isArray(g.cards) ? g.cards : [])) out.push(c);
    }
  }
  return out;
}

// ── render: header status line ──────────────────────────────────────────────
function renderHeader(d) {
  const lanes = Array.isArray(d.lanes) ? d.lanes : [];
  const groups = lanes.reduce((n, l) => n + (Array.isArray(l.groups) ? l.groups.length : 0), 0);
  const total = d.totalCount ?? lanes.reduce((n, l) => n + (l.cardCount ?? 0), 0);

  const parts = [];
  if (total === 0) {
    parts.push('No cards yet');
  } else {
    parts.push(plural(total, 'card'), plural(groups, 'matchup'), plural(lanes.length, 'lane'));
  }

  const statusB = document.querySelector('#statusline b');
  if (statusB) statusB.textContent = parts.join(' · ');
}

// ── render: the LAST GAME action + its mono line ────────────────────────────
// Disabled (title = the sidecar's reason) when the last game can't be resolved;
// reads "OPEN LAST GAME'S CARD" when a card is already linked to that game.
function renderLastGame(d) {
  const lg = (d && d.lastGame) || null;
  const btn = $('from-last');
  const available = !!(lg && lg.available);
  const existing = available && lg.existingCardId != null;
  // v3.9.2: the sidecar may know the lane + your side but not the opponents (a
  // game recovered from the client's match history), or only be guessing the
  // lane from your primary role; either way the click opens the form instead
  // of creating the card, and `hint` says which. Older snapshots have neither
  // field → treat as a plain create. An existing card is never "opens form".
  const enemyKnown = !available || existing || lg.enemyKnown !== false;
  const opensForm = available && !existing && (!enemyKnown || !!lg.hint);

  btn.textContent = existing ? "OPEN LAST GAME'S CARD" : 'NEW CARD FROM LAST GAME';
  btn.disabled = !available;
  if (!available) {
    btn.title = (lg && lg.unavailableReason) || 'No last game available.';
  } else if (existing) {
    btn.title = 'Jump to the card already linked to your last game.';
  } else if (opensForm) {
    btn.title = lg.hint || 'Start a card from your last game; some details still need filling in.';
  } else {
    btn.title = 'Start a card pre-filled with your last game’s lane and champions.';
  }

  // The mono line under the buttons — always in the page, never only in a
  // tooltip: the matchup + result when known; the opponents hint when only
  // your side was recorded; the sidecar's reason when the button is off.
  const line = $('mj-lastgame');
  const title = $('mj-lastgame-title');
  const detail = $('mj-lastgame-game');
  if (!lg) { show(line, false); return; }
  if (!available) {
    title.textContent = lg.gameLabel || '';
    detail.textContent = lg.unavailableReason || 'Not available yet.';
    line.classList.add('mj-lastgame-off');
    show(line, true);
  } else if (opensForm) {
    title.textContent = lg.matchupTitle || '';
    detail.textContent = [lg.gameLabel, lg.hint].filter(Boolean).join(' · ');
    line.classList.add('mj-lastgame-off');
    show(line, true);
  } else if (lg.matchupTitle || lg.gameLabel) {
    title.textContent = lg.matchupTitle || '';
    detail.textContent = lg.gameLabel || '';
    line.classList.remove('mj-lastgame-off');
    show(line, true);
  } else {
    show(line, false);
  }
}

// ── render: one card → one group → one lane ──────────────────────────────────
// Note boxes grow to fit their text — no scrollbar, no resize grip. The
// height is measured from scrollHeight, so it runs after the cards are in the
// document (render), on every keystroke, and when the window changes width.
// A box that isn't laid out yet (scrollHeight 0) keeps its `rows` height.
function autosizeNote(ta) {
  ta.style.height = 'auto';
  const h = ta.scrollHeight;
  if (h > 0) ta.style.height = `${h}px`;
  else ta.style.height = '';
}
function autosizeAllNotes() {
  document.querySelectorAll('.mj-note-in').forEach(autosizeNote);
}

function buildCard(c) {
  const el = tpl('tpl-card');
  if (c.id != null) el.dataset.cardId = String(c.id);

  el.querySelector('.mj-card-date').textContent = c.createdAtText || c.dateText || '';

  const game = el.querySelector('.mj-card-game');
  if (c.hasGame && c.gameLabel) {
    game.textContent = c.gameLabel;
    show(game, true);
  } else {
    show(game, false);
  }

  el.querySelector('.mj-note-in[data-field="prior"]').value = c.prior || '';
  el.querySelector('.mj-note-in[data-field="observed"]').value = c.observed || '';
  return el;
}

function buildGroup(g) {
  const el = tpl('tpl-group');
  if (g.key) el.dataset.groupKey = String(g.key);
  el.querySelector('.mj-group-title').textContent = g.title || '';
  const cards = Array.isArray(g.cards) ? g.cards : [];
  el.querySelector('.mj-group-n').textContent = plural(g.cardCount ?? cards.length, 'card');
  const host = el.querySelector('.mj-group-cards');
  for (const c of cards) host.appendChild(buildCard(c));
  return el;
}

function buildLane(l) {
  const el = tpl('tpl-lane');
  if (l.lane) el.dataset.lane = String(l.lane);
  el.querySelector('.mj-lane-name').textContent = l.laneLabel || (LANE_BY[l.lane] ? LANE_BY[l.lane].label : l.lane) || '';
  const groups = Array.isArray(l.groups) ? l.groups : [];
  const count = l.cardCount ?? groups.reduce((n, g) => n + (Array.isArray(g.cards) ? g.cards.length : 0), 0);
  el.querySelector('.mj-lane-n').textContent = String(count);
  const host = el.querySelector('.mj-lane-groups');
  for (const g of groups) host.appendChild(buildGroup(g));
  return el;
}

function renderLanes(lanes) {
  const host = $('mj-lanes');
  clear(host);
  for (const l of lanes) host.appendChild(buildLane(l));
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  $('err-detail').textContent = errText(err);
  show($('errpanel'), true);
}
function clearError() { show($('errpanel'), false); }

// ── entrance: stagger the main sections rising in on load ───────────────────
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  const order = [
    $('mj-lanes'),
    $('mj-empty'),
  ].filter((el) => el && !el.hidden);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── inline notes: autosave on blur / Ctrl+Enter ──────────────────────────────
// _notes holds the last-fetched (or last-saved) text per card id so a blur that
// changed nothing never writes. save_matchup_notes carries ONLY the changed
// field; on success we update the map + flash "Saved" — no refetch. A failed
// save shows the error inline on the card and leaves the text in place.
const _notes = new Map();
const _savedTimers = new WeakMap();
// save_matchup_notes calls still in flight — a refetch waits for them so the
// rebuilt textareas show what the user just typed, not the pre-edit copy.
const _pendingSaves = new Set();
const NOTE_FIELDS = new Set(['prior', 'observed']);
const SAVED_FLASH_MS = 1200;

function rememberNotes(d) {
  _notes.clear();
  for (const c of allCards(d)) {
    if (c.id == null) continue;
    _notes.set(Number(c.id), { prior: c.prior || '', observed: c.observed || '' });
  }
}

function flashSaved(ta) {
  const note = ta.closest('.mj-note');
  const mark = note ? note.querySelector('.mj-saved') : null;
  if (!mark) return;
  const prev = _savedTimers.get(mark);
  if (prev) clearTimeout(prev);
  show(mark, true);
  _savedTimers.set(mark, setTimeout(() => {
    show(mark, false);
    _savedTimers.delete(mark);
  }, SAVED_FLASH_MS));
}

function setCardError(card, msg) {
  const e = card ? card.querySelector('.mj-card-err') : null;
  if (!e) return;
  e.textContent = msg || '';
  show(e, !!msg);
}

async function saveNote(ta) {
  const card = ta.closest('[data-card-id]');
  const id = Number(card && card.dataset.cardId);
  const field = ta.dataset.field;
  if (!(id > 0) || !NOTE_FIELDS.has(field)) return;

  // A write for this box is already in flight — re-check once it lands.
  if (ta.dataset.busy === '1') { ta.dataset.dirty = '1'; return; }

  const known = _notes.get(id) || { prior: '', observed: '' };
  const text = ta.value;
  if (text === (known[field] ?? '')) return;   // unchanged — never write

  const invoke = await getInvoke();
  if (!invoke) {
    console.info(`[matchups] (preview) save ${field} — no Tauri backend.`);
    known[field] = text;
    _notes.set(id, known);
    flashSaved(ta);
    return;
  }

  ta.dataset.busy = '1';
  const write = invoke('save_matchup_notes', { payload: { id, [field]: text } });
  _pendingSaves.add(write);
  try {
    await write;
    known[field] = text;
    _notes.set(id, known);
    setCardError(card, '');
    flashSaved(ta);
  } catch (err) {
    setCardError(card, errText(err));
    console.error('[matchups] save_matchup_notes failed:', err);
  } finally {
    _pendingSaves.delete(write);
    delete ta.dataset.busy;
    if (ta.dataset.dirty === '1') {
      delete ta.dataset.dirty;
      saveNote(ta);
    }
  }
}

// ── scroll-to / highlight a card (after "from last game") ────────────────────
function cardElById(id) {
  const want = String(id);
  return Array.from(document.querySelectorAll('[data-card-id]')).find((el) => el.dataset.cardId === want) || null;
}
const HIGHLIGHT_MS = 1800;
function scrollToCard(id, opts) {
  const el = cardElById(id);
  if (!el) return false;
  el.scrollIntoView({ behavior: 'smooth', block: 'center' });
  el.classList.add('mj-card-hi');
  setTimeout(() => el.classList.remove('mj-card-hi'), HIGHLIGHT_MS);
  if (opts && opts.focusPrior) {
    const ta = el.querySelector('.mj-note-in[data-field="prior"]');
    if (ta) ta.focus({ preventScroll: true });
  }
  return true;
}

// ── create / edit form ────────────────────────────────────────────────────────
// One <form> serves both create_matchup (no id) and update_matchup (id set).
// _editId holds the id under edit, or null when creating.
let _editId = null;

function clearFormError() { show($('form-err'), false); $('form-err').textContent = ''; }
function setFormError(msg) { const e = $('form-err'); e.textContent = msg; show(e, true); }

// The Lane select drives how many champion inputs show per side + their captions.
function syncLaneFields() {
  const meta = LANE_BY[$('f-lane').value] || LANES[0];
  const two = meta.slots === 2;
  $('f-ally1-k').textContent = meta.ally[0];
  $('f-enemy1-k').textContent = meta.enemy[0];
  show($('f-ally2-wrap'), two);
  show($('f-enemy2-wrap'), two);
  if (two) {
    $('f-ally2-k').textContent = meta.ally[1];
    $('f-enemy2-k').textContent = meta.enemy[1];
  }
}

function revealForm() {
  clearFormError();
  syncLaneFields();
  show($('mj-form'), true);
  $('mj-form').scrollIntoView({ behavior: 'smooth', block: 'center' });
  $('f-ally1').focus();
}

// Open the form in create mode: blank fields, "Create" label. With a prefill
// (the "from last game" path when the card can't be created outright) the
// lane, the champions BY SLOT (allyChamps[i] → ALLY_IDS[i]; '' = unknown, so a
// lone support lands in the support field) and the game link are filled in;
// `note` explains what's left to do and the cursor lands on it — the first
// empty enemy slot, or the lane select when the lane was only a guess.
let _formGameId = null; // game a NEW card will link to (prefilled path only)
function openCreateForm(prefill) {
  _editId = null;
  _formGameId = null;
  $('form-title').textContent = 'New Card';
  $('form-submit').textContent = 'Create';
  const lane = prefill && LANE_BY[prefill.lane] ? prefill.lane : 'top';
  $('f-lane').value = lane;
  const ally = prefill && Array.isArray(prefill.allyChamps) ? prefill.allyChamps : [];
  const enemy = prefill && Array.isArray(prefill.enemyChamps) ? prefill.enemyChamps : [];
  ALLY_IDS.forEach((fid, i) => setVal(fid, ally[i] || ''));
  ENEMY_IDS.forEach((fid, i) => setVal(fid, enemy[i] || ''));
  setVal('f-prior', '');
  setVal('f-observed', '');
  if (prefill && Number(prefill.gameId) > 0) {
    _formGameId = Number(prefill.gameId);
    const when = prefill.gameLabel ? `${prefill.gameLabel} · ` : '';
    const note = prefill.note ? ` — ${prefill.note}` : '';
    $('f-game').textContent = `${when}game ${prefill.gameId}${note}`;
    show($('f-game-wrap'), true);
  } else {
    show($('f-game-wrap'), false);
  }
  revealForm();
  if (prefill) {
    const slots = (LANE_BY[lane] || LANES[0]).slots;
    const firstEmptyEnemy = ENEMY_IDS.slice(0, slots).find((id) => !getVal(id));
    if (firstEmptyEnemy) $(firstEmptyEnemy).focus();
    else if (prefill.laneIsGuess) $('f-lane').focus();
  }
}

// Open the form in edit mode for a card pulled from the last fetch.
function openEditForm(id) {
  const c = cardById(id);
  if (!c) { openCreateForm(); return; }
  _editId = c.id;
  _formGameId = null;
  $('form-title').textContent = 'Edit Card';
  $('form-submit').textContent = 'Save';
  $('f-lane').value = LANE_BY[c.lane] ? c.lane : 'top';

  const ally = Array.isArray(c.allyChamps) ? c.allyChamps : [];
  const enemy = Array.isArray(c.enemyChamps) ? c.enemyChamps : [];
  ALLY_IDS.forEach((fid, i) => setVal(fid, ally[i] || ''));
  ENEMY_IDS.forEach((fid, i) => setVal(fid, enemy[i] || ''));

  // Prefer the text sitting in the card's own textareas — an inline edit may be
  // newer than the last fetch (or still in flight).
  const el = cardElById(c.id);
  const notes = _notes.get(Number(c.id)) || {};
  const live = (field) => {
    const ta = el ? el.querySelector(`.mj-note-in[data-field="${field}"]`) : null;
    return ta ? ta.value : (notes[field] ?? c[field] ?? '');
  };
  setVal('f-prior', live('prior'));
  setVal('f-observed', live('observed'));

  if (c.hasGame) {
    $('f-game').textContent = c.gameLabel ? `${c.gameLabel} · game ${c.gameId}` : `Game ${c.gameId}`;
    show($('f-game-wrap'), true);
  } else {
    show($('f-game-wrap'), false);
  }
  revealForm();
}

function closeForm() {
  _editId = null;
  _formGameId = null;
  clearFormError();
  show($('mj-form'), false);
}

// Assemble the create/update payload from the form. Only the VISIBLE champion
// inputs for the selected lane count (slot 2 on a 1v1 lane is hidden and never
// read, so a stale value can't leak through). Slot 1 on each side is required;
// slot 2 (2v2 lanes) is optional — a card pre-filled from a thin participant
// map legitimately holds one champion per side, and the sidecar accepts 1..2.
// Returns null (+ inline error) when a required slot is blank.
function readFormPayload() {
  const meta = LANE_BY[$('f-lane').value] || LANES[0];
  const allyIds = ALLY_IDS.slice(0, meta.slots);
  const enemyIds = ENEMY_IDS.slice(0, meta.slots);
  const firstEmpty = [allyIds[0], enemyIds[0]].find((id) => !getVal(id));
  if (firstEmpty) {
    setFormError('Fill in your champion and the enemy champion.');
    $(firstEmpty).focus();
    return null;
  }
  const payload = {
    lane: meta.value,
    allyChamps: allyIds.map(getVal).filter(Boolean),
    enemyChamps: enemyIds.map(getVal).filter(Boolean),
    prior: getVal('f-prior'),
    observed: getVal('f-observed'),
  };
  // A NEW card opened from the "last game" partial path links to that game;
  // an edit never changes the link.
  if (_editId == null && _formGameId > 0) payload.gameId = _formGameId;
  return payload;
}

// Submit the form → create_matchup or update_matchup, then reload. One write at
// a time: disabling the button stops clicks, but the Ctrl/Cmd+Enter chord (and
// key repeat) reaches submitForm directly and would land a duplicate card while
// the first create_matchup is still in flight — hence the flag.
let _submitting = false;
async function submitForm(submitBtn) {
  if (_submitting) return;
  const payload = readFormPayload();
  if (!payload) return;

  const invoke = await getInvoke();
  if (!invoke) {
    console.info('[matchups] (preview) submit — no Tauri backend.');
    closeForm();
    return;
  }

  if (_editId != null) payload.id = _editId;
  const cmd = _editId != null ? 'update_matchup' : 'create_matchup';

  _submitting = true;
  if (submitBtn) submitBtn.disabled = true;
  try {
    await invoke(cmd, { payload });
    closeForm();
    await loadMatchups();
  } catch (err) {
    setFormError(errText(err));
    console.error(`[matchups] ${cmd} failed:`, err);
  } finally {
    _submitting = false;
    if (submitBtn) submitBtn.disabled = false;
  }
}

// ── "from last game" ─────────────────────────────────────────────────────────
// existingCardId set → just scroll to / highlight that card. Otherwise ask the
// sidecar to build the card, refetch, then scroll to the new card and drop the
// cursor in its Prior box (created:false = it already existed; jump only).
async function fromLastGame(btn) {
  const lg = _lastData && _lastData.lastGame;
  if (!lg || !lg.available) return;
  if (lg.existingCardId != null) {
    scrollToCard(lg.existingCardId, { focusPrior: false });
    return;
  }

  const invoke = await getInvoke();
  if (!invoke) {
    console.info('[matchups] (preview) action "from_last_game" — no Tauri backend.');
    return;
  }

  btn.disabled = true;
  try {
    const res = await invoke('create_matchup_from_last_game', { payload: {} });
    if (res && res.partial) {
      // The card can't be created outright: the opponents weren't recorded
      // (and couldn't be looked up), or the lane is only a guess from your
      // primary role. Open the form pre-filled and linked to the game instead
      // of creating a half-empty or mis-filed card.
      const lane = LANE_BY[res.lane] ? res.lane : 'top';
      const slots = LANE_BY[lane].slots;
      const enemy = Array.isArray(res.enemyChamps) ? res.enemyChamps : [];
      const known = enemy.slice(0, slots).filter(Boolean).length;
      const note = known === 0
        ? "the client didn't record the opponents; add them."
        : known < slots
          ? "one opponent wasn't recorded; add them."
          : res.laneIsGuess ? 'lane guessed from your primary role; check it.' : '';
      openCreateForm({
        lane,
        allyChamps: res.allyChamps || [],
        enemyChamps: enemy,
        gameId: res.gameId,
        gameLabel: res.gameLabel || '',
        laneIsGuess: !!res.laneIsGuess,
        note,
      });
      return;
    }
    await loadMatchups();
    if (res && res.id != null) scrollToCard(res.id, { focusPrior: res.created !== false });
  } catch (err) {
    renderError(err);
    console.error('[matchups] create_matchup_from_last_game failed:', err);
  } finally {
    // Re-derive the button state from the latest snapshot rather than blindly
    // re-enabling (the refetch may have flipped it to "open last game's card").
    renderLastGame(_lastData);
  }
}

// ── copy as Markdown ─────────────────────────────────────────────────────────
// The result flashes on the button that was pressed ("COPIED 12" / "COPY FAILED")
// and reverts after a beat — no separate status line.
const COPY_FLASH_MS = 1400;
const _copyTimers = new WeakMap();
function flashButton(btn, text, kind) {
  if (!btn) return;
  if (!btn.dataset.label) btn.dataset.label = btn.textContent;
  const prev = _copyTimers.get(btn);
  if (prev) clearTimeout(prev);
  btn.textContent = text;
  btn.classList.remove('ok', 'err');
  if (kind) btn.classList.add(kind);
  _copyTimers.set(btn, setTimeout(() => {
    btn.textContent = btn.dataset.label;
    btn.classList.remove('ok', 'err');
    _copyTimers.delete(btn);
  }, COPY_FLASH_MS));
}

// One card as Markdown — the same shape MatchupJournalExporter.Build gives a
// card inside the full export (### title, **date**, #### Prior / #### Observed),
// built from what is on screen right now so an inline edit in progress is what
// gets copied.
function buildCardMarkdown(cardEl, card) {
  const group = cardEl.closest('.mj-group');
  const titleEl = group ? group.querySelector('.mj-group-title') : null;
  const title = (card && card.matchupTitle) || (titleEl && titleEl.textContent.trim()) || 'Matchup';
  // The export's date line is the ISO day (MatchupCardDto.DateText); the row
  // shows the friendlier "Sep 6, 2026", so prefer the DTO and only fall back to
  // what is rendered.
  const dateEl = cardEl.querySelector('.mj-card-date');
  const date = (card && card.dateText) || (dateEl && dateEl.textContent.trim()) || '';
  const read = (field) => {
    const ta = cardEl.querySelector(`.mj-note-in[data-field="${field}"]`);
    const v = ta ? ta.value : (card ? card[field] : '');
    return (v || '').trim() || '_(not written yet)_';
  };
  const lines = [`### ${title}`, ''];
  if (date) lines.push(`**${date}**`, '');
  lines.push('#### Prior', '', read('prior'), '', '#### Observed', '', read('observed'));
  return lines.join('\n') + '\n';
}

async function copyCard(btn) {
  const cardEl = btn.closest('[data-card-id]');
  if (!cardEl) return;
  const card = cardById(cardEl.dataset.cardId);
  try {
    await navigator.clipboard.writeText(buildCardMarkdown(cardEl, card));
    flashButton(btn, 'Copied', 'ok');
  } catch (err) {
    flashButton(btn, 'Copy failed', 'err');
    console.error('[matchups] copy_card failed:', err);
  }
}

// Browser-preview stand-in for GET /api/matchups/export: same lane / last-N
// filters and the same Markdown shape (## lane, ### title, **date**, #### Prior,
// #### Observed) built from the loaded snapshot.
function buildPreviewMarkdown(d, lane, last) {
  const lanes = (d && Array.isArray(d.lanes) ? d.lanes : []).filter((l) => !lane || l.lane === lane);

  // "Last N" keeps the N newest cards across the (lane-filtered) journal.
  let keep = null;
  if (last != null) {
    const newest = [];
    for (const l of lanes) for (const g of (l.groups || [])) for (const c of (g.cards || [])) newest.push(c);
    newest.sort((a, b) => (b.createdAt || 0) - (a.createdAt || 0));
    keep = new Set(newest.slice(0, last).map((c) => c.id));
  }

  // Mirrors MatchupJournalExporter.Build: heading, count line, ## lane, ### title,
  // **yyyy-mm-dd**, #### Prior / #### Observed with the same empty-note marker.
  const body = [];
  let count = 0;
  for (const l of lanes) {
    const groups = [];
    for (const g of (l.groups || [])) {
      const cards = (g.cards || []).filter((c) => !keep || keep.has(c.id));
      if (cards.length) groups.push({ g, cards });
    }
    if (!groups.length) continue;
    body.push('', `## ${l.laneLabel || l.lane}`);
    for (const { g, cards } of groups) {
      body.push('', `### ${g.title || ''}`);
      for (const c of cards) {
        body.push('', `**${c.dateText || c.createdAtText || ''}**`, '');
        body.push('#### Prior', '', c.prior || '_(not written yet)_', '');
        body.push('#### Observed', '', c.observed || '_(not written yet)_');
        count++;
      }
    }
  }
  const head = ['# Matchup Journal', '', count ? `${plural(count, 'card')} · exported (preview)` : '_No cards._'];
  return { ok: true, markdown: head.concat(body).join('\n') + '\n', count, fileName: 'revu-matchups-preview.md' };
}

async function copyMarkdown(btn) {
  // v3.10: COPY ALL copies the whole journal; the per-card Copy button covers
  // "just this one". (The lane / last-N pickers of the old export card are gone;
  // the sidecar endpoint still accepts them.)
  const lane = '';
  const last = null;

  if (btn) btn.disabled = true;
  try {
    const invoke = await getInvoke();
    const built = invoke
      ? await invoke('get_matchups_export_markdown', { lane, last })
      : buildPreviewMarkdown(_lastData, lane, last);
    if (!built || typeof built.markdown !== 'string') {
      flashButton(btn, 'COPY FAILED', 'err');
      return;
    }
    const count = Number(built.count) || 0;
    if (count === 0) {
      flashButton(btn, 'NOTHING TO COPY', null);
      return;
    }
    await navigator.clipboard.writeText(built.markdown);
    flashButton(btn, `COPIED ${count}`, 'ok');
  } catch (err) {
    flashButton(btn, 'COPY FAILED', 'err');
    console.error('[matchups] copy_markdown failed:', err);
  } finally {
    if (btn) btn.disabled = false;
  }
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  clearError();
  renderHeader(d);
  renderLastGame(d);
  rememberNotes(d);

  const lanes = Array.isArray(d.lanes) ? d.lanes : [];
  const empty = d.isEmpty || lanes.length === 0;

  renderLanes(lanes);
  autosizeAllNotes();
  show($('x-copy'), !empty);

  if (empty) {
    if (d.emptyMessage) $('mj-empty-h').textContent = d.emptyMessage;
    show($('mj-empty'), true);
  } else {
    show($('mj-empty'), false);
  }

  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
// Concurrent callers are COALESCED, never dropped: a write that lands while a
// fetch is already in flight (delete → refetch, then "from last game" →
// refetch) is promised one follow-up fetch that starts after its own write, so
// scrollToCard(res.id) always finds the new card in the rendered snapshot.
let _lastData = null;
let _loadPromise = null;   // the fetch in flight
let _loadFollowUp = null;  // the ONE queued re-fetch shared by everyone who arrives mid-flight
function loadMatchups() {
  if (_loadPromise) {
    if (!_loadFollowUp) {
      _loadFollowUp = _loadPromise.then(() => { _loadFollowUp = null; return loadMatchups(); });
    }
    return _loadFollowUp;
  }
  _loadPromise = runLoad().finally(() => { _loadPromise = null; });
  return _loadPromise;
}
async function runLoad() {
  // Let inline note saves still in flight land first — the re-render rebuilds
  // every textarea from the snapshot, which must already carry the new text.
  if (_pendingSaves.size) await Promise.allSettled(Array.from(_pendingSaves));
  try {
    const data = await fetchMatchups();
    _lastData = data;
    render(data);
  } catch (err) {
    renderError(err);
    console.error('[matchups] load failed:', err);
  }
}

// Resolve a card's id back to the full card from the last fetch, so Edit can
// prefill from the in-memory row (no extra read).
function cardById(id) {
  if (_lastData == null || id == null) return null;
  return allCards(_lastData).find((c) => String(c.id) === String(id)) || null;
}

// Walk up to the card to find its id.
function cardIdForTarget(target) {
  const card = target.closest('[data-card-id]');
  return card && card.dataset.cardId ? card.dataset.cardId : null;
}

// ── single delegated action handler ─────────────────────────────────────────
// Local (no backend):
//   new_card    = open the create form.
//   edit_card   = open the edit form, prefilled.
//   cancel_form = close the form.
// Form submit:
//   submit_form = create_matchup / update_matchup.
// Hero / copy:
//   from_last_game = create_matchup_from_last_game (or jump to the linked card).
//   copy_markdown  = get_matchups_export_markdown → clipboard (whole journal).
//   copy_card      = this one card as Markdown → clipboard (local, no backend).
// Per-card mutation (carries {id}):
//   delete_card (confirms first) → delete_matchup.
const LOCAL_ACTIONS = new Set(['new_card', 'edit_card', 'cancel_form', 'copy_card']);
const ACTIONS = new Set([
  'new_card', 'edit_card', 'cancel_form', 'submit_form',
  'from_last_game', 'copy_markdown', 'copy_card', 'delete_card',
]);

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  // Local-only actions never touch the backend.
  if (LOCAL_ACTIONS.has(action)) {
    if (action === 'new_card') openCreateForm();
    else if (action === 'edit_card') openEditForm(cardIdForTarget(target));
    else if (action === 'cancel_form') closeForm();
    else if (action === 'copy_card') await copyCard(target);
    return;
  }

  if (action === 'submit_form') { await submitForm(target); return; }
  if (action === 'copy_markdown') { await copyMarkdown(target); return; }
  if (action === 'from_last_game') { await fromLastGame(target); return; }

  // delete_card — confirms before firing the hard delete.
  const id = cardIdForTarget(target);
  if (id == null) return;
  if (!window.confirm('Delete this matchup card? This cannot be undone.')) return;

  const invoke = await getInvoke();
  if (!invoke) {
    console.info(`[matchups] (preview) action "${action}" — no Tauri backend.`);
    return;
  }

  target.disabled = true;
  try {
    await invoke('delete_matchup', { payload: { id: Number(id) } });
    if (_editId != null && String(_editId) === String(id)) closeForm();
    await loadMatchups();
  } catch (err) {
    renderError(err);
    console.error(`[matchups] action "${action}" failed:`, err);
  } finally {
    target.disabled = false;
  }
});

// Lane select drives the form's champion inputs.
document.addEventListener('change', (ev) => {
  if (ev.target && ev.target.id === 'f-lane') syncLaneFields();
});

// Enter inside a form input must never navigate — route it to the same submit.
document.addEventListener('submit', (ev) => {
  if (ev.target && ev.target.id === 'mj-form') {
    ev.preventDefault();
    submitForm($('form-submit'));
  }
});

// Inline note boxes grow as you type …
document.addEventListener('input', (ev) => {
  const ta = ev.target;
  if (ta && ta.classList && ta.classList.contains('mj-note-in')) autosizeNote(ta);
});
let _resizeTimer = null;
window.addEventListener('resize', () => {
  clearTimeout(_resizeTimer);
  _resizeTimer = setTimeout(autosizeAllNotes, 80);
});

// … save on blur (capture: blur doesn't bubble) …
document.addEventListener('blur', (ev) => {
  const ta = ev.target;
  if (ta && ta.classList && ta.classList.contains('mj-note-in')) saveNote(ta);
}, true);

// … and on Ctrl/Cmd+Enter while still focused. The same chord inside the
// create/edit form submits it.
document.addEventListener('keydown', (ev) => {
  if (!(ev.ctrlKey || ev.metaKey) || ev.key !== 'Enter') return;
  const el = ev.target;
  if (!el || !el.classList) return;
  if (el.classList.contains('mj-note-in')) {
    ev.preventDefault();
    saveNote(el);
  } else if (el.closest && el.closest('#mj-form')) {
    ev.preventDefault();
    submitForm($('form-submit'));
  }
});

// Best-effort flush when the page is torn down (navigation, app close): a note
// box that still has focus never blurred, so push it now (mirrors review.js's
// pagehide draft flush). The shell also lists this page as active work, so the
// LCU auto-show never reloads it mid-edit — this covers the user's own exits.
window.addEventListener('pagehide', () => {
  const el = document.activeElement;
  if (el && el.classList && el.classList.contains('mj-note-in')) saveNote(el);
});

// ── live refresh: pick up the game you just played ──────────────────────────
// Because the shell treats this page as active work, champ select never reloads
// it — and so nothing reloads it when the game ENDS either: the page kept showing
// its pre-game state (v3.9.0 bug — "New card from last game" stayed off after a
// game). Listen to the LCU stream and refetch once the sidecar has SAVED the
// game. A refetch rebuilds every card, so while you are mid-edit (form open, a
// note focused, a save or submit in flight) it waits and re-checks each second
// until you are idle. Returning to the window (the app minimizes during a game)
// triggers the same idle-aware refresh.
let _refreshWhenIdle = false;
let _idleRefreshTimer = null;
const IDLE_RECHECK_MS = 1000;

function isEditing() {
  const form = $('mj-form');
  if (form && !form.hidden) return true;
  const el = document.activeElement;
  if (el && el.classList && el.classList.contains('mj-note-in')) return true;
  return _pendingSaves.size > 0 || _submitting;
}

function requestRefresh() {
  _refreshWhenIdle = true;
  tryIdleRefresh();
}

function tryIdleRefresh() {
  clearTimeout(_idleRefreshTimer);
  _idleRefreshTimer = null;
  if (!_refreshWhenIdle) return;
  if (isEditing()) {
    _idleRefreshTimer = setTimeout(tryIdleRefresh, IDLE_RECHECK_MS);
    return;
  }
  _refreshWhenIdle = false;
  loadMatchups();
}

async function wireLiveChannel() {
  const listen = await getListen();
  const invoke = await getInvoke();
  if (!listen || !invoke) return; // plain-browser preview: no live channel

  await listen('lcu-event', (event) => {
    const msg = event.payload || {};
    // Only a SAVED game changes what this page shows (a skipped casual game or
    // a remake leaves the journal's "last game" exactly where it was).
    if (msg.type === 'gameEnded' && msg.payload && msg.payload.saved === true) requestRefresh();
  });

  // Ask the Rust host to open (or join) the SSE stream — idempotent; the shell
  // normally already has it running.
  try { await invoke('start_lcu_events'); }
  catch (err) { console.error('[matchups] start_lcu_events failed:', err); }
}

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') requestRefresh();
});

// v3.10: the post-game matchup pass (Match-V5, ~90s after EOG) just filled the
// lane / champions the 'gameEnded' refetch was too early to see — refetch so
// "NEW CARD FROM LAST GAME" arms without a reload. shell-outer forwards the
// sidecar's matchupUpdated SSE into this frame.
window.addEventListener('revu:matchup-updated', () => requestRefresh());

// ── boot ────────────────────────────────────────────────────────────────────
function boot() {
  loadMatchups();
  wireLiveChannel();
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
