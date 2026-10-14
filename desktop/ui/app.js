import { $, show, clear, tpl } from './dom.mjs';
import { readSnapshot } from './data.mjs';
import { getInvoke } from './platform/index.mjs';
import { objectiveMetaText } from './objective-labels.mjs';
import { mountMissedGames } from './missed-games.mjs';
import { registerLeaveGuard } from './leave-guard.mjs';
import { mountJournalTakeaway, relativeDay } from './journal-takeaway.mjs';
import { mountPatternPill } from './home-patterns.mjs';

// Revu Home: a greeting, one composer that starts or finishes a session, a few
// suggestions, the matches ready to review and the last 30 days. Renders the JSON
// returned by the desktop command `get_dashboard`. Every server string is written
// with textContent (never innerHTML). Meaning colors come from data-result and the
// theme tokens, never from the snapshot's hex fields.

const REVIEW_ROWS = 3;

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchDashboard() {
  return readSnapshot('get_dashboard', 'sample-dashboard.json');
}

// ── render: greeting ────────────────────────────────────────────────────────
// The server personalizes the greeting with the player's Riot name ("Good evening,
// bye."), so it is rendered as-is apart from its closing period: a salutation.
// The signed-in-but-unlinked variant moves its request into a link under it.
const ACCOUNT_NOTE = /\.?\s*Link your Riot account\.?$/i;
function renderHeader(d) {
  const stats = d.stats || {};
  if (d.greeting) {
    let greeting = String(d.greeting).replace(/\. Lock in\.?$/i, '.');
    const unlinked = ACCOUNT_NOTE.test(greeting);
    if (unlinked) greeting = greeting.replace(ACCOUNT_NOTE, '.');
    $('hero-title').textContent = greeting.replace(/\.$/, '');
    show($('hero-account'), unlinked);
  }

  const total = stats.totalGames || 0;
  const statusB = document.querySelector('#statusline b');
  statusB.textContent = total === 0 ? 'No games yet today.' : `${total} game${total === 1 ? '' : 's'} today.`;
  const line = $('statusline');
  // Clear any older trailing status text while retaining the daily game count.
  while (line.lastChild && line.lastChild !== statusB) line.removeChild(line.lastChild);
}

// ── render: the composer ────────────────────────────────────────────────────
// A session block is ACTIVE when Start session set an intention (intent.sessionIntention)
// but Finish session has not recorded a debrief yet (intent.debriefRating == null). Then
// the composer shows that focus and its one action is Finish session.
let _blockActive = false;
// Coach tag: seeded from the day's current tag so a same-day restart keeps it (a silent
// reset would retro-untag the day and flood coach games back into the review queue).
// The snapshot keeps intent.withCoach even after the session ends.
let _coachSeed = false;
let _withCoach = false;
let _coachTouched = false;

function renderNextStep(d) {
  const intent = d.intent || {};
  const blockActive = !!intent.sessionIntention && (intent.debriefRating == null);
  _blockActive = blockActive;
  const cta = $('nextstep-cta');
  $('nextstep').classList.toggle('is-active', blockActive);
  _coachSeed = !!intent.withCoach;
  if (!_coachTouched) setCoach(_coachSeed);
  renderStintLine(d);

  if (blockActive) {
    // A session carried over from a prior day (started, never finished) still gets
    // Finish session so it can be closed. Its own date rides on the button so
    // end_block targets that row, not just today.
    const carried = !!intent.carriedOver;
    const blockTag = [
      intent.stintBlockNumber != null ? `Block #${intent.stintBlockNumber}` : '',
      intent.withCoach ? 'With coach' : '',
    ].filter(Boolean).join(' · ');
    const baseKicker = carried ? 'Previous session' : 'Session in progress';
    $('nextstep-k').textContent = blockTag ? `${baseKicker} · ${blockTag}` : baseKicker;
    $('nextstep-h').textContent = String(intent.sessionIntention);
    $('nextstep-p').textContent = carried
      ? 'Finish your last session. Take a moment to reflect on how it went.'
      : "When you're done playing, close the session with a quick reflection.";
    cta.textContent = 'Finish session';
    cta.dataset.action = 'end_block';
    if (intent.blockDate) cta.dataset.blockDate = String(intent.blockDate);
    else delete cta.dataset.blockDate;
    return;
  }
  delete cta.dataset.blockDate;
  if (_finishOpen) closeFinishEditor({ restoreFocus: false });
  $('nextstep-k').textContent = '';
  $('nextstep-h').textContent = '';
  $('nextstep-p').textContent = '';
  cta.textContent = 'Start session';
  cta.dataset.action = 'start_block';
}

// v3.3: an old coaching stint in the composer footer: name, blocks so far and the
// with-coach / solo split. Starting a stint left the app in v3.3.3, so End stint is
// the only way out of one (its blocks keep their stint history).
function renderStintLine(d) {
  const el = $('nextstep-stint');
  const text = $('nextstep-stint-text');
  if (!el || !text) return;
  const st = d.stint;
  if (!st || !st.name) {
    el.hidden = true;
    text.textContent = '';
    return;
  }
  const parts = [`Coaching stint · ${st.name}`, `${st.blocksTotal ?? 0} blocks`];
  if ((st.blocksTotal ?? 0) > 0) {
    parts.push(`${st.blocksWithCoach ?? 0} with coach · ${st.blocksSolo ?? 0} solo`);
  }
  text.textContent = parts.join(' · ');
  el.hidden = false;
}

function setCoach(on) {
  _withCoach = !!on;
  const pill = $('nextstep-coach');
  if (pill) {
    pill.setAttribute('aria-checked', String(_withCoach));
    pill.classList.toggle('is-on', _withCoach);
  }
  const label = $('nextstep-coach-text');
  if (label) label.textContent = _withCoach ? 'With coach' : 'Solo';
}
function toggleCoach() {
  _coachTouched = true;
  setCoach(!_withCoach);
  syncActiveWork();
}

// ── the objective pill (D5: choosing one only fills in the intention) ─────────
let _objectives = [];
let _chosenObjective = '';
function renderObjectivePill(d) {
  _objectives = (Array.isArray(d.activeObjectives) ? d.activeObjectives : []).filter(o => o && o.title);
  const label = $('nextstep-objective-label');
  if (!label) return;
  const priority = _objectives.find(o => o.isPriority) || _objectives[0];
  label.textContent = _chosenObjective || (priority ? priority.title : 'Learning objectives');
  if (_menuOpen) buildObjectiveMenu();
}

let _menuOpen = false;
function menuElement(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}
function buildObjectiveMenu() {
  const menu = $('nextstep-objective-menu');
  clear(menu);
  menu.appendChild(menuElement('p', 'home-menu-h', 'Practice a learning objective'));
  if (_objectives.length === 0) {
    menu.appendChild(menuElement('p', 'home-menu-empty', 'No learning objectives yet.'));
    const add = menuElement('a', 'ui-row home-menu-item home-menu-link', 'Add a learning objective');
    add.href = 'objectives.html';
    menu.appendChild(add);
    return;
  }
  for (const objective of _objectives) {
    const item = menuElement('button', 'ui-row home-menu-item');
    item.type = 'button';
    item.appendChild(menuElement('span', 'home-menu-t', objective.title));
    const meta = [objective.isPriority ? 'Priority' : '', objectiveMetaText(objective)].filter(Boolean).join(' · ');
    if (meta) item.appendChild(menuElement('span', 'home-menu-m', meta));
    item.addEventListener('click', () => chooseObjective(objective));
    menu.appendChild(item);
  }
  const manage = menuElement('a', 'ui-row home-menu-item home-menu-link', 'Manage learning objectives');
  manage.href = 'objectives.html';
  menu.appendChild(manage);
}
function menuItems() {
  return [...($('nextstep-objective-menu')?.querySelectorAll('.home-menu-item') || [])];
}
function openObjectiveMenu() {
  if (_menuOpen || _blockActive) return;
  _menuOpen = true;
  buildObjectiveMenu();
  show($('nextstep-objective-menu'), true);
  $('nextstep-objective').setAttribute('aria-expanded', 'true');
  menuItems()[0]?.focus();
}
function closeObjectiveMenu({ restoreFocus = false } = {}) {
  if (!_menuOpen) return;
  _menuOpen = false;
  show($('nextstep-objective-menu'), false);
  $('nextstep-objective').setAttribute('aria-expanded', 'false');
  if (restoreFocus) $('nextstep-objective').focus();
}
function chooseObjective(objective) {
  const input = $('nextstep-input');
  const title = String(objective?.title || '').slice(0, 120);
  if (!input || !title) return;
  input.value = title;
  _chosenObjective = title;
  $('nextstep-objective-label').textContent = title;
  closeObjectiveMenu();
  input.focus();
  syncActiveWork();
}

// ── active work (P-035 guard for the composer) ──────────────────────────────
// The shell reads window.__revuActiveWork before an LCU auto-show or a recovered-games
// reload replaces this page, and Home reads it before its own refetches. The composer
// is always on screen, so the flag follows what the player has put into it.
function setActiveWork(on) {
  try { window.__revuActiveWork = !!on; } catch (_) { /* non-window host */ }
}
function composerText() {
  return String($('nextstep-input')?.value || '').trim();
}
function selectedRating() {
  const checked = document.querySelector('#nextstep-rate input:checked');
  const value = Number(checked?.value);
  return Number.isInteger(value) && value >= 1 && value <= 10 ? value : 0;
}
/** Words or a rating the player would lose by leaving. */
function composerDirty() {
  if (composerText()) return true;
  return _finishOpen && (selectedRating() > 0 || String($('nextstep-note')?.value || '').trim() !== '');
}
function hasActiveWork() {
  return _finishOpen || !!composerText() || (_coachTouched && _withCoach !== _coachSeed);
}
function syncActiveWork() { setActiveWork(hasActiveWork()); }

function resetComposer() {
  const input = $('nextstep-input');
  if (input) input.value = '';
  _chosenObjective = '';
  _coachTouched = false;
  setCoach(_coachSeed);
  renderObjectivePill(_snapshot || {});
  syncActiveWork();
}

function setComposerBusy(on) {
  for (const id of ['nextstep-input', 'nextstep-cta', 'nextstep-coach', 'nextstep-objective']) {
    const el = $(id);
    if (el) el.disabled = !!on;
  }
}

// ── Start session ───────────────────────────────────────────────────────────
let _starting = false;
async function submitIntent() {
  if (_blockActive || _starting) return;
  const input = $('nextstep-input');
  const intention = composerText();
  if (!intention) { input?.focus(); return; }
  const invoke = await getInvoke();
  if (!invoke) {
    console.info('[dashboard] (preview) start_block, no desktop host.');
    return;
  }
  _starting = true;
  setComposerBusy(true);
  let started = false;
  try {
    await invoke('start_block', { payload: { intention, withCoach: _withCoach } });
    started = true;
    window.dispatchEvent(new CustomEvent('revu:first-review-block-started', {
      detail: { intention },
    }));
    input.value = '';
    _chosenObjective = '';
    _coachTouched = false;
    syncActiveWork();
    await loadDashboard();
  } catch (err) {
    renderError(err);
    console.error('[dashboard] action "start_block" failed:', err);
  } finally {
    _starting = false;
    setComposerBusy(false);
  }
  // The field hides once the session runs; keep focus in the composer either way.
  if (started && _blockActive) $('nextstep-cta')?.focus();
  else input?.focus();
}

// ── Finish session (rating 1-10 and an optional note) ───────────────────────
let _finishOpen = false;
function openFinishEditor() {
  if (!_blockActive || _finishOpen) return;
  _finishOpen = true;
  for (const radio of document.querySelectorAll('#nextstep-rate input')) radio.checked = false;
  const note = $('nextstep-note');
  if (note) note.value = '';
  $('nextstep-rate-hint').textContent = '';
  $('nextstep').classList.add('is-finishing');
  show($('nextstep-finish'), true);
  syncActiveWork();
  document.querySelector('#nextstep-rate input')?.focus();
}
function closeFinishEditor({ restoreFocus = true } = {}) {
  _finishOpen = false;
  $('nextstep').classList.remove('is-finishing');
  show($('nextstep-finish'), false);
  syncActiveWork();
  if (restoreFocus) $('nextstep-cta')?.focus();
}
let _finishing = false;
async function submitFinish() {
  if (!_finishOpen || _finishing) return;
  const rating = selectedRating();
  if (!rating) {
    $('nextstep-rate-hint').textContent = 'Pick a rating from 1 to 10.';
    document.querySelector('#nextstep-rate input')?.focus();
    return;
  }
  const invoke = await getInvoke();
  if (!invoke) {
    console.info('[dashboard] (preview) end_block, no desktop host.');
    closeFinishEditor();
    return;
  }
  _finishing = true;
  const save = $('nextstep-save');
  if (save) save.disabled = true;
  try {
    // Carry the open block's own date so a carried-over (prior-day) session closes
    // the right row; omitted when it is today's session.
    const payload = { rating, note: String($('nextstep-note')?.value || '').trim() };
    const cta = $('nextstep-cta');
    if (cta.dataset.blockDate) payload.date = cta.dataset.blockDate;
    await invoke('end_block', { payload });
    closeFinishEditor({ restoreFocus: false });
    await loadDashboard();
    $('nextstep-input')?.focus();
  } catch (err) {
    renderError(err);
    console.error('[dashboard] action "end_block" failed:', err);
  } finally {
    _finishing = false;
    if (save) save.disabled = false;
  }
}

// ── the sidebar's Start a session (window.__revuStartSession) ───────────────
// Focuses the composer: the intention field, or Finish session while a session runs.
// With the Finish editor open, Finish session is hidden, so focus goes to the rating
// (the chosen one, else the first) or Save reflection instead.
// It never reloads the page, so nothing typed is lost.
function startSession() {
  if (_finishOpen) {
    (document.querySelector('#nextstep-rate input:checked')
      || document.querySelector('#nextstep-rate input')
      || $('nextstep-save'))?.focus();
  } else if (_blockActive) $('nextstep-cta')?.focus();
  else $('nextstep-input')?.focus();
  return true;
}
try { window.__revuStartSession = startSession; } catch (_) { /* non-window host */ }

// ── render: suggestions and the matches ready to review ─────────────────────
const matchupOf = g => {
  const champ = g.championName || 'Game';
  return g.enemyChampion ? `${champ} vs ${g.enemyChampion}` : champ;
};
const initialsOf = name => String(name || '').replace(/[^\p{L}]/gu, '').slice(0, 2);
function kdaOf(g) {
  if ([g.kills, g.deaths, g.assists].every(Number.isFinite)) return `${g.kills} / ${g.deaths} / ${g.assists}`;
  return g.kdaText ? String(g.kdaText).split('/').map(part => part.trim()).join(' / ') : '';
}
function setText(root, selector, value) {
  const el = root.querySelector(selector);
  if (el) el.textContent = value;
  return el;
}

function renderSuggestion(g) {
  const pill = $('home-suggest-review');
  if (!pill) return;
  show(pill, !!g);
  if (!g) return;
  pill.href = `review.html?gameId=${encodeURIComponent(g.gameId)}`;
  $('home-suggest-review-text').textContent = `Review ${matchupOf(g)}`;
}

function reviewRow(g, today) {
  const el = tpl('tpl-gamerow');
  const matchup = matchupOf(g);
  setText(el, '.vline', matchup);
  setText(el, '.home-champ', initialsOf(g.championName || 'Game'));
  const outcome = g.isRemake ? 'remake' : typeof g.win === 'boolean' ? (g.win ? 'win' : 'loss') : '';
  const result = el.querySelector('.home-review-result');
  result.textContent = outcome === 'remake' ? 'Remake' : outcome ? (g.win ? 'Victory' : 'Defeat') : (g.winLossText || '');
  show(result, !!result.textContent);
  if (outcome) result.dataset.result = outcome;
  const day = relativeDay(g.datePlayed, today);
  const when = day ? day[0].toUpperCase() + day.slice(1) : g.datePlayed;
  const meta = [g.gameMode, g.duration, when].filter(Boolean);
  setText(el, '.home-review-meta', meta.length ? meta.join(' · ') : (g.metaLine || ''));
  setText(el, '.home-kda', kdaOf(g));
  const open = el.querySelector('.home-review-open');
  open.href = `review.html?gameId=${encodeURIComponent(g.gameId)}`;
  open.setAttribute('aria-label', `Review ${matchup}`);
  const skip = el.querySelector('.home-review-skip');
  skip.dataset.gameId = String(g.gameId);
  skip.setAttribute('aria-label', `Skip review for ${matchup}`);
  return el;
}

// The count covers the whole recent queue; the snapshot's item list is capped, so its
// length is not the pending count. Home shows the newest few and links to the rest.
function renderUnreviewed(d) {
  const u = d.unreviewed || {};
  const host = $('unreviewed');
  clear(host);
  const items = Array.isArray(u.items) ? u.items : [];
  const total = Number(u.count);
  const count = u.allReviewed ? 0 : u.count != null && Number.isFinite(total)
    ? Math.max(0, Math.trunc(total)) : items.length;
  const pending = count > 0;
  show($('review-inbox'), pending);
  show($('unreviewed-empty'), !pending);
  document.querySelector('.home-page')?.classList.toggle('has-pending-review', pending);

  const rows = pending ? items.filter(item => item && item.gameId != null && !item.hasReview).slice(0, REVIEW_ROWS) : [];
  const remaining = Math.max(0, count - rows.length);
  show($('review-queue-footer'), pending && remaining > 0);
  $('review-queue-more').textContent = `${remaining} ${rows.length ? 'more ' : ''}waiting`;
  renderSuggestion(rows[0] || null);
  for (const g of rows) host.appendChild(reviewRow(g, d.today));
}

// ── render: the last 30 days ────────────────────────────────────────────────
// The snapshot carries 30-day baselines only inside its subtitle strings
// ("2W // 1L  ·  30-day 56%", "30-day avg 7.3"); both are empty under five games.
// The adherence streak stays in gold: it is a retention mechanic, never removed.
function renderStats(d) {
  const s = d.stats || {};
  const strip = $('strip');
  clear(strip);
  const winRate = /30-day (\d+)%/.exec(String(s.winRateSub || ''));
  const mental = /30-day avg ([\d.,]+)/.exec(String(s.avgMentalSub || ''));
  const streak = Number(s.adherenceStreak);
  const days = Number.isFinite(streak) ? Math.max(0, Math.trunc(streak)) : null;
  const cells = [
    { k: 'Win rate', v: winRate ? winRate[1] : '', unit: '%' },
    { k: 'Average mental', v: mental ? mental[1] : '' },
    // Not a 30-day figure: the sidecar counts every cross-game pattern ever marked reviewed.
    { k: 'Patterns reviewed, all time', v: String(Math.max(0, Math.trunc(Number(s.reviewedPatternCount) || 0))) },
    { k: 'Rules kept', v: days != null ? String(days) : '', unit: days === 1 ? 'day' : 'days', streak: true },
  ];
  for (const c of cells) {
    const el = tpl('tpl-stat');
    if (c.streak) el.classList.add('is-streak');
    if (!c.v) el.classList.add('is-empty');
    el.querySelector('.v').textContent = c.v || 'Not yet';
    el.querySelector('.s').textContent = c.v && c.unit ? c.unit : '';
    el.querySelector('.k').textContent = c.k;
    strip.appendChild(el);
  }
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  const panel = $('errpanel');
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show(panel, true);
}
function clearError() { show($('errpanel'), false); }

// ── top-level render ────────────────────────────────────────────────────────
let _snapshot = null;
let _takeaway = null;
function render(d) {
  _snapshot = d;
  clearError();
  renderHeader(d);
  renderNextStep(d);
  renderObjectivePill(d);
  renderStats(d);
  renderUnreviewed(d);
  _takeaway?.update(d);
  honorIntentDeepLink(d);
}

// ?intent=startblock: the Tilt Check ritual's closing step, the quick guide and the
// sidebar's Start a session land here promising the composer. Honor it once per page
// load, and never while a session is running (its action is Finish session).
let _intentDeepLinkDone = false;
function honorIntentDeepLink(d) {
  if (_intentDeepLinkDone) return;
  _intentDeepLinkDone = true;
  const wanted = new URLSearchParams(window.location.search).get('intent');
  if (wanted !== 'startblock') return;
  const intent = (d && d.intent) || {};
  const blockActive = !!intent.sessionIntention && (intent.debriefRating == null);
  if (blockActive) return;
  $('nextstep-input')?.focus();
}

// ── load orchestration ──────────────────────────────────────────────────────
// Electron waits for the sidecar to answer before it loads the shell, so a
// failed load is a real error: show it rather than retrying.
let _loading = false;
async function loadDashboard() {
  if (_loading) return;
  _loading = true;
  try {
    render(await fetchDashboard());
  } catch (err) {
    renderError(err);
    console.error('[dashboard] load failed:', err);
  } finally {
    _loading = false;
  }
}

// A remake verdict changed (3.14.1): today's games, the review inbox and the
// streak re-base. Refetch, but never while the player is mid-edit.
window.addEventListener('revu:remake-updated', () => {
  let editing = false;
  try { editing = !!window.__revuActiveWork; } catch (_) { editing = false; }
  if (!editing) void loadDashboard();
});

// ── single delegated action handler ─────────────────────────────────────────
// start_block  = Start session: invoke('start_block') with the composer's intention.
// end_block    = Finish session: opens the rating and note inside the composer.
// skip_review  = the per-row Skip → invoke('skip_review',{gameId}), reload.
// run_reset    = minimal mid-session reset → invoke('run_reset',{…}); the full
//                ritual lives on the Tilt page. Only fires if such a control is rendered.
// open_review  = a whole game row → that game's review page.
// review_vod   = a VOD hint → the VOD viewer.
// end_stint    = End stint beside the coaching stint line → invoke('end_stint'), reload.
const ACTIONS = new Set(['start_block', 'end_block', 'run_reset', 'skip_review', 'review_vod', 'take_next_step', 'open_review', 'end_stint']);

// Nav-only stubs: no backend command yet.
const DEFERRED = new Set(['take_next_step']);

// Per-action payload builders. Anything not listed POSTs whatever gameId the
// element carries (skip_review). run_reset uses a fixed minimal payload.
function payloadFor(action, target) {
  if (action === 'run_reset') return { emotion: 'tilted', intensityBefore: 5 };
  const args = {};
  if (target.dataset.gameId != null) args.gameId = Number(target.dataset.gameId);
  return args;
}

document.addEventListener('click', async (ev) => {
  const target = ev.target.closest('[data-action]');
  if (!target) return;
  const action = target.dataset.action;
  if (!ACTIONS.has(action)) return;
  ev.preventDefault();

  if (action === 'start_block') {
    await submitIntent();
    return;
  }
  if (action === 'end_block') {
    openFinishEditor();
    return;
  }

  // Clicking a game row opens THAT game's review page.
  if (action === 'open_review') {
    const gid = target.dataset.gameId;
    window.location.href = gid ? `review.html?gameId=${encodeURIComponent(gid)}` : 'review.html';
    return;
  }

  // "Review VOD" opens the VOD player for that game.
  if (action === 'review_vod') {
    const gid = target.dataset.gameId;
    if (gid) window.location.href = `vodplayer.html?gameId=${encodeURIComponent(gid)}`;
    return;
  }

  // Deferred nav targets have no backend command yet; don't invoke (would throw).
  if (DEFERRED.has(action)) {
    console.info(`[dashboard] action "${action}": page not ported yet (deferred).`);
    return;
  }

  if (action === 'end_stint' && !window.confirm('End this coaching stint? Its sessions keep their history.')) return;

  const invoke = await getInvoke();
  if (!invoke) {
    // Browser preview: no backend to talk to. Acknowledge the click in console.
    console.info(`[dashboard] (preview) action "${action}", no desktop host.`);
    return;
  }

  const args = payloadFor(action, target);

  // `disabled` only exists on form controls; a game row is a <div>, so guard.
  const canDisable = 'disabled' in target;
  if (canDisable) target.disabled = true;
  try {
    // Only write commands (run_reset, skip_review, end_stint) reach this dynamic
    // site; every command here takes a single `payload` arg. Wrap accordingly.
    await invoke(action, { payload: args });
    if (action === 'skip_review') announceGamesChanged();
    await loadDashboard();
  } catch (err) {
    renderError(err);
    console.error(`[dashboard] action "${action}" failed:`, err);
  } finally {
    if (canDisable) target.disabled = false;
  }
});

// The shell's sidebar (recent matches, review count) refreshes when a page changes the
// match list without loading a new page (shell-recent.mjs).
function announceGamesChanged() {
  try { window.top?.dispatchEvent(new CustomEvent('revu:games-changed')); } catch (_) { /* no shell */ }
}

// Keyboard activation for role="button" game rows (Enter / Space).
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  // A real <button> (the per-row Skip) handles its own Enter/Space natively;
  // don't also bubble up to the row's open_review, or a Skip keypress fires both.
  if (ev.target.closest('button')) return;
  const target = ev.target.closest('[data-action][role="button"]');
  if (!target) return;
  ev.preventDefault();
  target.click();
});

// ── composer wiring (real DOM only) ─────────────────────────────────────────
function wireComposer() {
  const input = $('nextstep-input');
  input?.addEventListener('input', () => {
    // The pill names the objective while its words are still in the field.
    if (_chosenObjective && !input.value.includes(_chosenObjective)) {
      _chosenObjective = '';
      renderObjectivePill(_snapshot || {});
    }
    syncActiveWork();
  });
  input?.addEventListener('keydown', (ev) => {
    if (ev.key === 'Enter' && !ev.isComposing) { ev.preventDefault(); void submitIntent(); }
    else if (ev.key === 'Escape' && hasActiveWork()) { ev.preventDefault(); resetComposer(); }
  });
  $('nextstep-coach')?.addEventListener('click', toggleCoach);

  const pill = $('nextstep-objective');
  const menu = $('nextstep-objective-menu');
  pill?.addEventListener('click', () => (_menuOpen ? closeObjectiveMenu() : openObjectiveMenu()));
  pill?.addEventListener('keydown', (ev) => {
    if (ev.key === 'ArrowDown' && !_menuOpen) { ev.preventDefault(); openObjectiveMenu(); }
  });
  menu?.addEventListener('keydown', (ev) => {
    const items = menuItems();
    const at = items.indexOf(document.activeElement);
    if (ev.key === 'Escape') { ev.preventDefault(); ev.stopPropagation(); closeObjectiveMenu({ restoreFocus: true }); }
    else if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
      ev.preventDefault();
      const step = ev.key === 'ArrowDown' ? 1 : -1;
      items[(at + step + items.length) % items.length]?.focus();
    } else if (ev.key === 'Home' || ev.key === 'End') {
      ev.preventDefault();
      (ev.key === 'Home' ? items[0] : items.at(-1))?.focus();
    }
  });
  menu?.addEventListener('focusout', (ev) => {
    const next = ev.relatedTarget;
    if (next && !menu.contains(next) && next !== pill) closeObjectiveMenu();
  });
  document.addEventListener('click', (ev) => {
    if (_menuOpen && !ev.target.closest('.home-obj')) closeObjectiveMenu();
  });

  $('nextstep-save')?.addEventListener('click', () => { void submitFinish(); });
  $('nextstep-cancel')?.addEventListener('click', () => closeFinishEditor());
  $('nextstep-rate')?.addEventListener('change', () => {
    $('nextstep-rate-hint').textContent = '';
    syncActiveWork();
  });
  $('nextstep-finish')?.addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape') { ev.preventDefault(); closeFinishEditor(); }
    else if (ev.key === 'Enter' && ev.target.id === 'nextstep-note' && !ev.isComposing) { ev.preventDefault(); void submitFinish(); }
  });
}

// Words in the composer are unsaved work. The shell asks before its sidebar replaces
// Home (leave-guard.mjs); a link inside Home asks the same question. The quick guide's
// own "Go to Home" link focuses the composer instead of reloading the page.
let _leaveGuard = null;
function wireLeaving() {
  _leaveGuard = registerLeaveGuard({ isDirty: composerDirty });
  document.addEventListener('click', async (ev) => {
    const link = ev.target.closest('a[href]');
    if (!link || ev.defaultPrevented || ev.button !== 0 || ev.metaKey || ev.ctrlKey || ev.shiftKey || ev.altKey) return;
    if (link.target && link.target !== '_self') return;
    const url = new URL(link.href, window.location.href);
    if (/(^|\/)dashboard(\.html)?$/.test(url.pathname) && url.searchParams.get('intent') === 'startblock') {
      ev.preventDefault();
      startSession();
      return;
    }
    if (!composerDirty()) return;
    ev.preventDefault();
    if (await _leaveGuard.beforeNavigate()) window.location.href = url.href;
  });
}

// ── boot ────────────────────────────────────────────────────────────────────
// Finished games Revu did not save: Home lists them with Save and Dismiss. A saved game
// joins the review inbox and today's games, so Home refetches them, but never while
// the player is mid-edit.
const missedGamesOptions = {
  onSaved: () => {
    let editing = false;
    try { editing = !!window.__revuActiveWork; } catch (_) { editing = false; }
    if (!editing) void loadDashboard();
  },
};

function boot() {
  wireComposer();
  wireLeaving();
  _takeaway = mountJournalTakeaway(document, { readSnapshot });
  void loadDashboard();
  mountPatternPill(document);   // a mistake trend ready to review (home-patterns.mjs)
  void mountMissedGames(document, missedGamesOptions);
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
