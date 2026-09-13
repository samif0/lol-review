import { $, show, tpl } from './dom.mjs';
import { readSnapshot } from './data.mjs';
import { getInvoke, getListen } from './platform/index.mjs';

// Revu desktop — searchable Matchup notes with one open matchup at a time.
// Renders the JSON returned by the Electron command `get_matchups`
// (see Revu.Sidecar GET /api/matchups) AND drives the card CRUD via the
// create_matchup / create_matchup_from_last_game / update_matchup /
// save_matchup_notes / delete_matchup commands, plus the Markdown export
// (get_matchups_export_markdown → clipboard). Mirrors rules.js conventions:
//   • getInvoke() uses the shared platform boundary and detects browser previews.
//   • Outside Electron it fetches ./sample-matchups.json so the page previews in a
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

// Electron event listener (the Electron host re-emits the sidecar's SSE stream as
// 'lcu-event'). Same resolver shape as pregame.js; null outside Electron.


// ── small DOM helpers ───────────────────────────────────────────────────────
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
  return readSnapshot('get_matchups', 'sample-matchups.json');
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
    parts.push('No notes yet');
  } else {
    parts.push(plural(total, 'note'), plural(groups, 'matchup'), plural(lanes.length, 'lane'));
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

  btn.textContent = existing ? 'Open last match note' : 'From last match';
  btn.disabled = !available;
  if (!available) {
    btn.title = (lg && lg.unavailableReason) || 'No last game available.';
  } else if (existing) {
    btn.title = 'Open the note already linked to your last match.';
  } else if (opensForm) {
    btn.title = lg.hint || 'Start a note from your last match; some details still need filling in.';
  } else {
    btn.title = 'Start a note with your last match’s lane and champions filled in.';
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

function buildCard(c, existing) {
  const el = existing || tpl('tpl-card');
  if (c.id != null) el.dataset.cardId = String(c.id);

  const date = c.createdAtText || c.dateText || '';
  const dateEl = el.querySelector('.mj-card-date');
  dateEl.textContent = date;
  const gameLabel = c.hasGame ? c.gameLabel || '' : '';
  show(dateEl, !!date && !gameLabel.split(/\s*[·•]\s*/).includes(date));

  const game = el.querySelector('.mj-card-game');
  game.textContent = gameLabel;
  show(game, !!gameLabel);
  show(el.querySelector('.mj-card-context'), !!date || !!gameLabel);

  const known = _notes.get(Number(c.id)) || {};
  for (const field of NOTE_FIELDS) {
    const input = el.querySelector(`.mj-note-in[data-field="${field}"]`);
    // Refresh saved content, but retain any newer raw edit or failed save.
    if (!existing || (input.dataset.busy !== '1' && input.value === (known[field] ?? ''))) input.value = c[field] || '';
  }
  return el;
}

let _openGroupKey = null;
function openGroup(group) {
  _openGroupKey = group.dataset.groupKey;
  for (const other of document.querySelectorAll('.mj-group')) other.open = other === group;
  group.querySelectorAll('.mj-note-in').forEach(autosizeNote);
}

// Move only changed entries. Collapsing and filtering never detach an editor,
// and an ordinary refresh keeps stable cards (including pending saves) mounted.
function reconcileChildren(host, children) {
  let next = host.firstElementChild;
  for (const child of children) {
    if (child === next) next = next.nextElementSibling;
    else host.insertBefore(child, next);
  }
  const keep = new Set(children);
  for (const child of [...host.children]) if (!keep.has(child)) child.remove();
}

function normalizeSearch(value) {
  return String(value ?? '').normalize('NFKD').replace(/\p{M}/gu, '').toLowerCase().replace(/[’']/g, '');
}

function buildGroup(g, lane, existing, cardNodes) {
  const el = existing || tpl('tpl-group');
  el.dataset.groupKey = String(g.key || `${lane.lane}|${g.title || ''}`);
  el.setAttribute('name', 'matchup-notes');
  el.open = el.dataset.groupKey === _openGroupKey;
  el.querySelector('.mj-group-title').textContent = g.title || '';
  const cards = Array.isArray(g.cards) ? g.cards : [];
  el.querySelector('.mj-group-n').textContent = plural(g.cardCount ?? cards.length, 'note');
  const newest = [...cards].sort((a, b) => (Number(b.createdAt) || 0) - (Number(a.createdAt) || 0))
    .find(card => card.createdAtText || card.dateText);
  const date = newest?.createdAtText || newest?.dateText || '';
  const dateEl = el.querySelector('.mj-group-date');
  dateEl.textContent = date ? `Latest ${date}` : '';
  show(dateEl, !!date);
  el.dataset.search = normalizeSearch([g.title, lane.lane, lane.laneLabel,
    ...(g.allyChamps || []), ...(g.enemyChamps || []),
    ...cards.flatMap(card => [card.matchupTitle, ...(card.allyChamps || []), ...(card.enemyChamps || [])]),
  ].join(' '));
  const host = el.querySelector('.mj-group-cards');
  reconcileChildren(host, cards.map(card => buildCard(card, cardNodes.get(String(card.id)))));
  if (!existing) el.addEventListener('toggle', () => {
    if (el.open) openGroup(el);
    else if (_openGroupKey === el.dataset.groupKey) _openGroupKey = null;
  });
  return el;
}

function buildLane(l, existing, groupNodes, cardNodes) {
  const el = existing || tpl('tpl-lane');
  if (l.lane) el.dataset.lane = String(l.lane);
  el.querySelector('.mj-lane-name').textContent = l.laneLabel || (LANE_BY[l.lane] ? LANE_BY[l.lane].label : l.lane) || '';
  const groups = Array.isArray(l.groups) ? l.groups : [];
  const count = l.cardCount ?? groups.reduce((n, g) => n + (Array.isArray(g.cards) ? g.cards.length : 0), 0);
  el.querySelector('.mj-lane-n').textContent = String(count);
  const host = el.querySelector('.mj-lane-groups');
  reconcileChildren(host, groups.map(group => buildGroup(group, l,
    groupNodes.get(String(group.key || `${l.lane}|${group.title || ''}`)), cardNodes)));
  return el;
}

function renderLanes(lanes) {
  const host = $('mj-lanes');
  const oldLanes = new Map([...host.querySelectorAll('.mj-lane')].map(el => [el.dataset.lane, el]));
  const oldGroups = new Map([...host.querySelectorAll('.mj-group')].map(el => [el.dataset.groupKey, el]));
  const oldCards = new Map([...host.querySelectorAll('[data-card-id]')].map(el => [el.dataset.cardId, el]));
  _openGroupKey = [...oldGroups.values()].find(el => el.open)?.dataset.groupKey || null;
  reconcileChildren(host, lanes.map(lane => buildLane(lane, oldLanes.get(lane.lane), oldGroups, oldCards)));
  applyFilters();
}

function applyFilters() {
  const tokens = normalizeSearch($('mj-search')?.value).trim().split(/\s+/).filter(Boolean);
  const laneFilter = $('mj-lane-filter')?.value || 'all';
  let total = 0, matched = 0;
  for (const lane of document.querySelectorAll('.mj-lane')) {
    let visible = 0;
    for (const group of lane.querySelectorAll('.mj-group')) {
      total++;
      const content = `${group.dataset.search} ${normalizeSearch([...group.querySelectorAll('.mj-note-in')].map(input => input.value).join(' '))}`;
      const matches = (laneFilter === 'all' || laneFilter === lane.dataset.lane) && tokens.every(token => content.includes(token));
      show(group, matches);
      if (matches) { visible++; matched++; }
    }
    lane.querySelector('.mj-lane-n').textContent = String(visible);
    show(lane, visible > 0);
  }
  if ($('mj-results')) $('mj-results').textContent = plural(matched, 'matchup');
  show($('mj-no-results'), total > 0 && matched === 0);
  // Revealed textareas may have been measured while their details were closed.
  for (const group of document.querySelectorAll('.mj-group')) {
    if (group.open && !group.hidden && !group.closest('.mj-lane')?.hidden) group.querySelectorAll('.mj-note-in').forEach(autosizeNote);
  }
}

function clearFilters() {
  setVal('mj-search', '');
  setVal('mj-lane-filter', 'all');
  applyFilters();
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
const _noteWrites = new WeakMap();
let _notesRevision = 0;
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

function saveNote(ta) {
  if (_noteWrites.has(ta)) return _noteWrites.get(ta);
  const write = performNoteSave(ta);
  _noteWrites.set(ta, write);
  _pendingSaves.add(write);
  const done = () => { _noteWrites.delete(ta); _pendingSaves.delete(write); };
  write.then(done, done);
  return write;
}

async function flushPendingNotes() {
  while (_pendingSaves.size) await Promise.allSettled([..._pendingSaves]);
}

async function performNoteSave(ta) {
  const card = ta.closest('[data-card-id]');
  const id = Number(card && card.dataset.cardId);
  const field = ta.dataset.field;
  if (!(id > 0) || !NOTE_FIELDS.has(field)) return;

  ta.dataset.busy = '1';
  try {
    // Keep follow-up edits inside the same pending operation. Refresh must not
    // rebuild from the first response while a newer answer is still being saved.
    while (ta.value !== (_notes.get(id)?.[field] ?? '')) {
      const text = ta.value;
      const invoke = await getInvoke();
      if (invoke) await invoke('save_matchup_notes', { payload: { id, [field]: text } });
      else console.info(`[matchups] (preview) save ${field} — no Electron backend.`);
      _notes.set(id, { ...(_notes.get(id) || {}), [field]: text });
      _notesRevision++;
      setCardError(card, '');
      flashSaved(ta);
    }
  } catch (err) {
    setCardError(card, errText(err));
    console.error('[matchups] save_matchup_notes failed:', err);
  } finally {
    delete ta.dataset.busy;
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
  const group = el.closest('.mj-group');
  if (group) {
    if (group.hidden || group.closest('.mj-lane')?.hidden) clearFilters();
    openGroup(group);
    if (!opts?.focusPrior) group.querySelector('summary')?.focus({ preventScroll: true });
  }
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
  $('form-title').textContent = 'New note';
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
  $('form-title').textContent = 'Edit note';
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

  if (_editId != null) payload.id = _editId;
  const cmd = _editId != null ? 'update_matchup' : 'create_matchup';
  const oldCard = payload.id != null ? cardElById(payload.id) : null;
  const submittedLive = Object.fromEntries([...NOTE_FIELDS].map(field => [field,
    oldCard?.querySelector(`.mj-note-in[data-field="${field}"]`)?.value]));

  _submitting = true;
  if (submitBtn) submitBtn.disabled = true;
  try {
    const invoke = await getInvoke();
    if (!invoke) {
      console.info('[matchups] (preview) submit — no Electron backend.');
      closeForm();
      return;
    }
    await flushPendingNotes();
    const result = await invoke(cmd, { payload });
    if (payload.id != null) {
      // The full edit form is an explicit save. Replace its old inline values,
      // while preserving any additional inline edits made during the request.
      for (const field of NOTE_FIELDS) {
        const input = oldCard?.querySelector(`.mj-note-in[data-field="${field}"]`);
        if (input && input.value === submittedLive[field]) input.value = payload[field];
      }
      _notes.set(Number(payload.id), { prior: payload.prior, observed: payload.observed });
    }
    closeForm();
    await loadMatchups();
    const id = result?.id ?? payload.id;
    if (id != null) scrollToCard(id);
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
    console.info('[matchups] (preview) action "from_last_game" — no Electron backend.');
    return;
  }

  btn.disabled = true;
  try {
    const res = await invoke('create_matchup_from_last_game', { payload: {} });
    if (res && res.partial) {
      // The card can't be created outright: the opponents weren't recorded
      // (and couldn't be looked up), the lane is only a guess from your
      // primary role, or (v3.10.1) the matchup was estimated when the game
      // ended and Riot hasn't confirmed it yet. Open the form pre-filled and
      // linked to the game instead of creating a half-empty or mis-filed card.
      const lane = LANE_BY[res.lane] ? res.lane : 'top';
      const slots = LANE_BY[lane].slots;
      const enemy = Array.isArray(res.enemyChamps) ? res.enemyChamps : [];
      const known = enemy.slice(0, slots).filter(Boolean).length;
      const note = known === 0
        ? "the client didn't record the opponents; add them."
        : known < slots
          ? "one opponent wasn't recorded; add them."
          : res.laneIsGuess ? 'lane guessed from your primary role; check it.'
            : res.estimated ? 'estimated when the game ended; check it.' : '';
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
      flashButton(btn, 'Copy failed', 'err');
      return;
    }
    const count = Number(built.count) || 0;
    if (count === 0) {
      flashButton(btn, 'Nothing to copy', null);
      return;
    }
    await navigator.clipboard.writeText(built.markdown);
    flashButton(btn, `Copied ${count}`, 'ok');
  } catch (err) {
    flashButton(btn, 'Copy failed', 'err');
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

  const lanes = Array.isArray(d.lanes) ? d.lanes : [];
  const empty = d.isEmpty || lanes.length === 0;

  renderLanes(lanes);
  rememberNotes(d);
  autosizeAllNotes();
  show($('x-copy'), !empty);
  show($('mj-browser'), !empty);

  if (empty) {
    // Keep the heading separate from guidance, including for older snapshots
    // whose emptyMessage contains the entire onboarding paragraph.
    $('mj-empty-h').textContent = 'No matchup notes yet';
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
  try {
    let data;
    while (true) {
      await flushPendingNotes();
      const revision = _notesRevision;
      data = await fetchMatchups();
      await flushPendingNotes();
      // A completed save may have overtaken this response. Fetch again before
      // comparing live fields with their newly confirmed baseline.
      if (revision === _notesRevision) break;
    }
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
const LOCAL_ACTIONS = new Set(['new_card', 'edit_card', 'cancel_form', 'copy_card', 'clear_filters']);
const ACTIONS = new Set([
  'new_card', 'edit_card', 'cancel_form', 'submit_form',
  'from_last_game', 'copy_markdown', 'copy_card', 'delete_card', 'clear_filters',
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
    else if (action === 'clear_filters') { clearFilters(); $('mj-search')?.focus(); }
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
    console.info(`[matchups] (preview) action "${action}" — no Electron backend.`);
    return;
  }

  target.disabled = true;
  try {
    await flushPendingNotes();
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
  if (ev.target && ev.target.id === 'mj-lane-filter') applyFilters();
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
  if (ta?.id === 'mj-search') applyFilters();
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

  // Ask the Electron host to open (or join) the SSE stream — idempotent; the shell
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
