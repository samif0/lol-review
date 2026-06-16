// Revu desktop dashboard — data-driven renderer for the glass-aurora layout.
// Renders the JSON returned by the Tauri command `get_dashboard`.
// All server-supplied strings are written via textContent (never innerHTML)
// to keep the surface XSS-free. Colors arrive as *Hex strings and are applied
// to style/stroke properties only.

// ── invoke resolver ────────────────────────────────────────────────────────
// Prefer the official @tauri-apps/api/core import; fall back to the global the
// Tauri webview injects. In a plain browser (no Tauri) both are absent and we
// fall through to a local sample JSON so the page previews standalone.
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

const RING_CIRCUMFERENCE = 150.8; // 2·π·r, r=24

// ── data fetch ──────────────────────────────────────────────────────────────
async function fetchDashboard() {
  // Prefer the REAL backend (Tauri invoke → sidecar → your DB). Only fall back
  // to the bundled sample when invoke is genuinely unavailable (plain browser
  // preview). Detecting via getInvoke() is more reliable than window.__TAURI__,
  // which may not be populated yet when this runs.
  const invoke = await getInvoke();
  if (invoke) {
    return invoke('get_dashboard');
  }
  const res = await fetch('./sample-dashboard.json');
  if (!res.ok) throw new Error(`sample-dashboard.json ${res.status}`);
  return res.json();
}

// ── render: header ──────────────────────────────────────────────────────────
function renderHeader(d) {
  const stats = d.stats || {};
  const intent = d.intent || {};
  // Greeting comes from the server, which ALREADY personalizes it with the signed-in
  // player's Riot IGN (e.g. "Good afternoon, bye.") — so we just render it as-is.
  // (An earlier client-side insert double-added the name → "…, bye, bye."; removed.)
  if (d.greeting) $('hero-title').textContent = d.greeting;

  const parts = [];
  const total = stats.totalGames || 0;
  parts.push(total === 0 ? 'No games yet today.' : `${total} game${total === 1 ? '' : 's'} today.`);
  if (stats.avgMental != null) parts.push(`30-day mental ${stats.avgMental}`);
  if (stats.adherenceStreak != null) parts.push(`${stats.adherenceStreak} days clean`);
  // First sentence is bolded via the <b> element; the rest is plain mono text.
  const statusB = document.querySelector('#statusline b');
  statusB.textContent = parts[0];
  // Rebuild the trailing " · a · b" run as plain text nodes after the <b>.
  const line = $('statusline');
  // Remove any previously appended trailing nodes (keep dot + <b>).
  while (line.lastChild && line.lastChild !== statusB) line.removeChild(line.lastChild);
  const tail = parts.slice(1);
  if (tail.length) {
    line.appendChild(document.createTextNode(' · ' + tail.join(' · ')));
  }
}

// ── render: next step ───────────────────────────────────────────────────────
// A session block is ACTIVE when Start Block set an intention (intent.sessionIntention)
// but End Block hasn't recorded a debrief yet (intent.debriefRating == null). In that
// state the card flips to "End Block" (the close-out ritual) over the active intention.
function renderNextStep(d) {
  const ns = d.nextStep || {};
  const intent = d.intent || {};
  const blockActive = !!intent.sessionIntention && (intent.debriefRating == null);
  const cta = $('nextstep-cta');

  if (blockActive) {
    $('nextstep-k').textContent = 'In session · End Block';
    $('nextstep-h').textContent = 'Wrap the block.';
    $('nextstep-p').textContent = `Focus: ${intent.sessionIntention}. Rate how the block went and lock it in.`;
    cta.textContent = 'END BLOCK →';
    cta.dataset.action = 'end_block';
    return;
  }

  // Server may override the card copy; defaults match the mockup.
  $('nextstep-k').textContent = ns.kicker || 'Next step · Start Block';
  $('nextstep-h').textContent = ns.title || 'Set your intent before you queue.';
  $('nextstep-p').textContent = ns.detail ||
    'A 30-second ritual: name one focus, check your priority objective, lock in.';
  cta.textContent = ns.ctaLabel || 'START BLOCK →';
  cta.dataset.action = ns.action || 'start_block';
}

// ── render: stat strip (fixed order) ────────────────────────────────────────
function renderStrip(d) {
  const s = d.stats || {};
  const strip = $('strip');
  clear(strip);

  const cells = [
    { k: 'Games',      v: String(s.totalGames ?? 0),        sub: 'TODAY',          flag: false },
    { k: 'Win Rate',   v: s.winratePercent ?? '—',          sub: s.winRateSub ?? '', flag: false },
    { k: 'Avg Mental', v: s.avgMental != null ? String(s.avgMental) : '—', sub: s.avgMentalSub ?? '', flag: false },
    { k: 'Adherence',  v: s.adherenceStreak != null ? String(s.adherenceStreak) : '—', sub: s.adherenceSub ?? 'DAYS CLEAN', flag: true },
    { k: 'Patterns',   v: s.reviewedPatternCount != null ? String(s.reviewedPatternCount) : '0', sub: s.patternsReviewedSub ?? 'CROSS-GAME', flag: false },
  ];

  for (const c of cells) {
    const el = tpl('tpl-stat');
    if (c.flag) el.classList.add('flag');
    el.querySelector('.k').textContent = c.k;
    el.querySelector('.v').textContent = c.v;
    el.querySelector('.s').textContent = c.sub;
    strip.appendChild(el);
  }
}

// ── render: VOD pending (small inline hint, not a section) ──────────────────
function renderVod(d) {
  const v = d.vodPending || {};
  const showIt = !!v.show;
  show($('vod-hint'), showIt);
  if (!showIt) return;
  $('vod-line').textContent = v.text || '';
  const btn = $('vod-btn');
  if (v.gameId != null) btn.dataset.gameId = String(v.gameId);
}

// ── render: unreviewed game rows ────────────────────────────────────────────
// The WHOLE ROW is the review action (no button) — data-action="open_review",
// role=button + tabindex for keyboard, with the hype hover animation (lift +
// glow + edge bar + sweep + "REVIEW →" cue) defined on .gamerow in styles.css.
// VOD evidence is the separate hint line (data-action="review_vod" → vod viewer).
// DEFERRED: both nav targets are stubs until the Review + VOD pages are ported
// (only the dashboard exists today). See memory project_tauri_dashboard_interactions.
function renderUnreviewed(d) {
  const u = d.unreviewed || {};
  const host = $('unreviewed');
  clear(host);
  const items = Array.isArray(u.items) ? u.items : [];

  if (u.allReviewed || items.length === 0) {
    show($('unreviewed-label'), false);
    return;
  }
  show($('unreviewed-label'), true);

  for (const g of items) {
    const el = tpl('tpl-gamerow');
    const vline = el.querySelector('.vline');
    const vsmall = el.querySelector('.vsmall');
    const cue = el.querySelector('.gamerow-cue');

    // Primary line: "Champ vs Enemy — W · K/D/A". Build it from fields (the
    // server's statsLine is the CS/dmg string, which belongs in the sub-line).
    const champ = g.championName || 'Game';
    const matchup = g.enemyChampion ? `${champ} vs ${g.enemyChampion}` : champ;
    const wl = g.winLossText ? `: ${g.winLossText}` : '';
    const kda = g.kdaText ? ` · ${g.kdaText}` : '';
    vline.textContent = `${matchup}${wl}${kda}`;
    if (g.winLossColorHex) vline.style.color = g.winLossColorHex;
    // Sub-line: mode/date/duration, then the CS/dmg stats.
    const subParts = [g.metaLine, g.statsLine].filter(Boolean);
    vsmall.textContent = subParts.join('  ·  ');

    // The whole ROW is the review action (see deferred-nav note). Carry the
    // gameId + a re-review label on the card itself, not a button.
    if (g.gameId != null) el.dataset.gameId = String(g.gameId);
    if (cue) cue.firstChild.textContent = (g.hasReview ? 'OPEN' : 'REVIEW') + ' ';

    // SKIP dismisses an UNREVIEWED game; it carries the same gameId. Hide it on
    // already-reviewed rows (skipping a review you've written makes no sense).
    const skip = el.querySelector('.gamerow-skip');
    if (skip) {
      if (g.gameId != null) skip.dataset.gameId = String(g.gameId);
      show(skip, !g.hasReview);
    }

    // The left edge bar rests in the game's win/loss color (green/red), then
    // energizes to the violet accent on hover (--wl drives the resting bar).
    if (g.winLossColorHex) el.style.setProperty('--wl', g.winLossColorHex);

    host.appendChild(el);
  }
}

// ── render: active objectives (SVG rings) ───────────────────────────────────
function renderObjectives(d) {
  const objs = Array.isArray(d.activeObjectives) ? d.activeObjectives : [];
  const host = $('objectives');
  clear(host);

  if (objs.length === 0) {
    show($('objectives-label'), false);
    return;
  }
  show($('objectives-label'), true);

  for (const o of objs) {
    const el = tpl('tpl-objective');
    const progress = Math.max(0, Math.min(1, Number(o.progress) || 0));
    const offset = RING_CIRCUMFERENCE * (1 - progress);

    const track = el.querySelector('.ring-track');
    const prog = el.querySelector('.ring-prog');
    track.setAttribute('stroke', o.levelDimColorHex || 'rgba(255,255,255,0.13)');
    prog.setAttribute('stroke', o.levelColorHex || '#9d8bff');
    // Start empty (full offset = 0% drawn), then transition to the target on the
    // next frame so the CSS transition on .ring-prog animates the arc filling.
    prog.setAttribute('stroke-dashoffset', String(RING_CIRCUMFERENCE));
    requestAnimationFrame(() => requestAnimationFrame(() => {
      prog.setAttribute('stroke-dashoffset', String(offset));
    }));

    el.querySelector('.pc').textContent =
      o.progressLabel || `${Math.round(progress * 100)}%`;

    const pill = el.querySelector('.pill');
    show(pill, !!o.isPriority);

    el.querySelector('.oname').textContent = o.title || '';
    el.querySelector('.ometa').textContent =
      o.metaText || [o.levelName, o.phaseLabel, o.score != null ? `${o.score} PTS` : null]
        .filter(Boolean).join(' · ').toUpperCase();

    // Make the whole objective card a button into its detail page (games + stats
    // + the per-objective notes/VOD flow). Keyboard-accessible; the shell's
    // frame.load re-syncs the Objectives nav item once we land there. NOTE: tpl()
    // returns the template's root, which IS the .card — so wire `el` itself, not a
    // descendant query (the card is not a child of the clone).
    if (o.id != null) {
      el.classList.add('obj-clickable');
      el.setAttribute('role', 'button');
      el.tabIndex = 0;
      const go = () => { window.location.href = `objectivegames.html?id=${encodeURIComponent(o.id)}`; };
      el.addEventListener('click', go);
      el.addEventListener('keydown', (ev) => {
        if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); go(); }
      });
    }

    host.appendChild(el);
  }
}

// ── error panel ─────────────────────────────────────────────────────────────
function renderError(err) {
  const panel = $('errpanel');
  $('err-detail').textContent = (err && err.message) ? err.message : String(err);
  show(panel, true);
}
function clearError() { show($('errpanel'), false); }

// ── entrance: stagger the main sections rising in on load ───────────────────
// Only on the FIRST render of a page load (not on every refresh) so re-polling
// the dashboard doesn't re-animate everything.
let _entranceDone = false;
function playEntrance() {
  if (_entranceDone) return;
  _entranceDone = true;
  const order = [
    $('nextstep')?.closest('.toprow'),
    $('strip'),
    $('unreviewed'),
    $('vod-hint'),
  ].filter(Boolean);
  order.forEach((el, i) => {
    el.classList.add('anim-rise', `anim-d${Math.min(i + 1, 5)}`);
  });
}

// ── top-level render ────────────────────────────────────────────────────────
function render(d) {
  clearError();
  renderHeader(d);
  renderNextStep(d);
  renderObjectives(d);
  renderStrip(d);
  renderUnreviewed(d);
  renderVod(d);
  playEntrance();
}

// ── load orchestration ──────────────────────────────────────────────────────
// The sidecar (C# backend) is spawned at app start and takes a moment to bind.
// If the first load races ahead of it, fetch throws "sidecar not ready" — so we
// retry a few times with backoff before surfacing an error. This makes a cold
// start self-heal instead of showing "Couldn't load the dashboard."
let _loading = false;
async function loadDashboard() {
  if (_loading) return;
  _loading = true;
  const maxAttempts = 25;       // ~25 × 400ms ≈ 10s of startup grace
  try {
    for (let attempt = 1; ; attempt++) {
      try {
        const data = await fetchDashboard();
        render(data);
        return;
      } catch (err) {
        const transient = /sidecar not ready|not ready|connection refused|failed to fetch/i.test(String(err));
        if (transient && attempt < maxAttempts) {
          await new Promise((r) => setTimeout(r, 400));
          continue;
        }
        renderError(err);
        console.error('[dashboard] load failed:', err);
        return;
      }
    }
  } finally {
    _loading = false;
  }
}

// ── inline intention editor (START BLOCK) ───────────────────────────────────
// Clicking START BLOCK swaps the button for a glass text field in place. The
// user names one focus, then we invoke('start_block',{intention}) and reload —
// the reload re-renders the next-step card, which restores the button. Escape
// or Cancel aborts back to the button without touching the backend.
let _intentOpen = false;
function openIntentEditor(cta) {
  if (_intentOpen) return;
  _intentOpen = true;
  const body = cta.closest('.body') || cta.parentElement;

  const wrap = document.createElement('div');
  wrap.className = 'intent-edit';

  const input = document.createElement('input');
  input.type = 'text';
  input.className = 'intent-input';
  input.maxLength = 120;
  input.placeholder = 'Name one focus for this block…';
  input.setAttribute('aria-label', 'Session intention');

  const actions = document.createElement('div');
  actions.className = 'intent-actions';

  const confirm = document.createElement('button');
  confirm.type = 'button';
  confirm.className = 'cta cta-sm';
  confirm.textContent = 'LOCK IN →';

  const cancel = document.createElement('button');
  cancel.type = 'button';
  cancel.className = 'intent-cancel';
  cancel.textContent = 'Cancel';

  actions.append(confirm, cancel);
  wrap.append(input, actions);

  // Swap the button out for the editor, then focus the field.
  cta.hidden = true;
  body.appendChild(wrap);
  input.focus();

  function close(restoreButton) {
    _intentOpen = false;
    wrap.remove();
    // If the backend write succeeded, loadDashboard() re-renders the card and a
    // fresh button arrives; only restore the original when we abort locally.
    if (restoreButton) cta.hidden = false;
  }

  async function submit() {
    const intention = input.value.trim();
    if (!intention) { input.focus(); return; }
    const invoke = await getInvoke();
    if (!invoke) {
      console.info('[dashboard] (preview) start_block — no Tauri backend.');
      close(true);
      return;
    }
    input.disabled = true;
    confirm.disabled = true;
    try {
      await invoke('start_block', { payload: { intention } });
      close(false);
      await loadDashboard();
    } catch (err) {
      renderError(err);
      console.error('[dashboard] action "start_block" failed:', err);
      input.disabled = false;
      confirm.disabled = false;
      input.focus();
    }
  }

  confirm.addEventListener('click', submit);
  cancel.addEventListener('click', () => close(true));
  input.addEventListener('keydown', (ev) => {
    if (ev.key === 'Enter') { ev.preventDefault(); submit(); }
    else if (ev.key === 'Escape') { ev.preventDefault(); close(true); }
  });
}

// ── inline End Block editor ──────────────────────────────────────────────────
// Clicking END BLOCK swaps the button for a small close-out ritual in place: rate
// the block 1-10 (required) + an optional note, then invoke('end_block') and reload.
// Mirrors the Start Block editor. Escape / Cancel aborts without touching the backend.
let _endBlockOpen = false;
function openEndBlockEditor(cta) {
  if (_endBlockOpen) return;
  _endBlockOpen = true;
  const body = cta.closest('.body') || cta.parentElement;

  const wrap = document.createElement('div');
  wrap.className = 'intent-edit';

  const ratingRow = document.createElement('div');
  ratingRow.className = 'endblock-rating';
  const ratingLabel = document.createElement('span');
  ratingLabel.className = 'endblock-rating-k';
  ratingLabel.textContent = 'How did the block go?';
  ratingRow.appendChild(ratingLabel);
  const chips = document.createElement('div');
  chips.className = 'endblock-chips';
  let _rating = 0;
  for (let n = 1; n <= 10; n++) {
    const chip = document.createElement('button');
    chip.type = 'button';
    chip.className = 'endblock-chip';
    chip.textContent = String(n);
    chip.addEventListener('click', () => {
      _rating = n;
      chips.querySelectorAll('.endblock-chip').forEach((c, i) => c.classList.toggle('on', i + 1 === n));
    });
    chips.appendChild(chip);
  }
  ratingRow.appendChild(chips);

  const note = document.createElement('input');
  note.type = 'text';
  note.className = 'intent-input';
  note.maxLength = 240;
  note.placeholder = 'Optional: a note on how it went…';
  note.setAttribute('aria-label', 'Block note');

  const actions = document.createElement('div');
  actions.className = 'intent-actions';
  const confirm = document.createElement('button');
  confirm.type = 'button';
  confirm.className = 'cta cta-sm';
  confirm.textContent = 'LOCK IN →';
  const cancel = document.createElement('button');
  cancel.type = 'button';
  cancel.className = 'intent-cancel';
  cancel.textContent = 'Cancel';
  actions.append(confirm, cancel);

  wrap.append(ratingRow, note, actions);
  cta.hidden = true;
  body.appendChild(wrap);

  function close(restoreButton) {
    _endBlockOpen = false;
    wrap.remove();
    if (restoreButton) cta.hidden = false;
  }

  async function submit() {
    if (_rating < 1) { chips.classList.add('endblock-chips-need'); return; }
    const invoke = await getInvoke();
    if (!invoke) {
      console.info('[dashboard] (preview) end_block — no Tauri backend.');
      close(true);
      return;
    }
    confirm.disabled = true;
    note.disabled = true;
    try {
      await invoke('end_block', { payload: { rating: _rating, note: note.value.trim() } });
      close(false);
      await loadDashboard();
    } catch (err) {
      renderError(err);
      console.error('[dashboard] action "end_block" failed:', err);
      confirm.disabled = false;
      note.disabled = false;
    }
  }

  confirm.addEventListener('click', submit);
  cancel.addEventListener('click', () => close(true));
  note.addEventListener('keydown', (ev) => {
    if (ev.key === 'Enter') { ev.preventDefault(); submit(); }
    else if (ev.key === 'Escape') { ev.preventDefault(); close(true); }
  });
}

// ── single delegated action handler ─────────────────────────────────────────
// start_block  = opens the inline intention editor → invoke('start_block').
// skip_review  = the per-row SKIP chip → invoke('skip_review',{gameId}), reload.
// run_reset    = minimal mid-session reset → invoke('run_reset',{…}); the full
//                ritual lives on the Tilt page. Only fires if such a control is
//                rendered on the dashboard.
// open_review  = clicking a whole unreviewed row (→ review page, deferred).
// review_vod   = the separate VOD-evidence hint (→ vod viewer, deferred).
const ACTIONS = new Set(['start_block', 'end_block', 'run_reset', 'skip_review', 'review_vod', 'take_next_step', 'open_review']);

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

  // START BLOCK is interactive: collect the intention inline before writing.
  if (action === 'start_block') {
    openIntentEditor(target);
    return;
  }

  // END BLOCK is interactive too: the close-out ritual (rating + note) inline.
  if (action === 'end_block') {
    openEndBlockEditor(target);
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

  // Deferred nav targets have no backend command yet — don't invoke (would throw).
  if (DEFERRED.has(action)) {
    console.info(`[dashboard] action "${action}" — page not ported yet (deferred).`);
    return;
  }

  const invoke = await getInvoke();
  if (!invoke) {
    // Browser preview: no backend to talk to. Acknowledge the click in console.
    console.info(`[dashboard] (preview) action "${action}" — no Tauri backend.`);
    return;
  }

  const args = payloadFor(action, target);

  // `disabled` only exists on form controls; the game row is a <div>, so guard.
  const canDisable = 'disabled' in target;
  if (canDisable) target.disabled = true;
  try {
    // Only GROUP A write commands (run_reset, skip_review) reach this dynamic
    // site — start_block / open_review / review_vod / deferred return earlier —
    // so every command here takes a single `payload` arg. Wrap accordingly.
    await invoke(action, { payload: args });
    await loadDashboard();
  } catch (err) {
    renderError(err);
    console.error(`[dashboard] action "${action}" failed:`, err);
  } finally {
    if (canDisable) target.disabled = false;
  }
});

// Keyboard activation for the role="button" game rows (Enter / Space).
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' && ev.key !== ' ') return;
  // A real <button> (the per-row SKIP chip) handles its own Enter/Space natively;
  // don't also bubble up to the row's open_review, or a SKIP keypress fires both.
  if (ev.target.closest('button')) return;
  const target = ev.target.closest('[data-action][role="button"]');
  if (!target) return;
  ev.preventDefault();
  target.click();
});

// ── boot ────────────────────────────────────────────────────────────────────
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', loadDashboard);
} else {
  loadDashboard();
}
