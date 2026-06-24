// First Review Tutorial - shared guided layer for the real Revu workflow.
// It stores progress in app config and listens for existing page write events.

// Re-injection guard: shell-outer.js reloads framed iframes on LCU events, which can
// re-run this module in the same realm. Without this, the top-level listeners + the
// global would re-bind and a single click could fire double save_config / navigation.
if (window.__revuFirstReviewTutorialLoaded) {
  // Already initialized in this realm — do not re-bind listeners.
} else {
  window.__revuFirstReviewTutorialLoaded = true;

const STEPS = Object.freeze({
  OBJECTIVE: 'objective',
  START_BLOCK: 'start_block',
  QUEUE: 'queue',
  PREGAME: 'pregame',
  INGAME: 'ingame',
  WAIT_VOD: 'wait_vod',
  VOD: 'vod',
  VOD_MOMENT: 'vod_moment',
  REVIEW: 'review',
  DONE: 'done',
});

const STEP_LABELS = {
  objective: '1 of 7',
  start_block: '2 of 7',
  queue: '3 of 7',
  pregame: '4 of 7',
  ingame: '5 of 7',
  wait_vod: '6 of 7',
  vod: '6 of 7',
  vod_moment: '6 of 7',
  review: '7 of 7',
};

const PAGE_BY_STEP = {
  objective: 'objectives.html?tutorial=first-review',
  start_block: 'dashboard.html',
  queue: 'dashboard.html',
  pregame: 'pregame.html',
  ingame: 'ingame.html',
  wait_vod: 'dashboard.html',
  vod: 'dashboard.html',
  vod_moment: 'dashboard.html',
  review: 'review.html',
};

const OBJECTIVE_FIELD_GUIDE = [
  {
    title: 'Name the objective',
    body: 'Use "Review every death" so the goal is obvious when it appears in pre-game, in-game, and VOD review.',
    target: '#f-title',
  },
  {
    title: 'Set the skill area',
    body: 'Deaths / decision review makes this about understanding why deaths happened, not just lowering the death count.',
    target: '#f-skill',
  },
  {
    title: 'Keep it Primary',
    body: 'Primary objectives get the strongest placement in the workflow, which is what a first review habit needs.',
    target: '#f-type',
  },
  {
    title: 'Practice through the whole game',
    body: 'Pre-Game, In-Game, and Post-Game are all enabled so Revu can prompt before queue, remind during play, and review after.',
    target: '.obj-checks',
  },
  {
    title: 'Use any phase',
    body: 'Death review can happen in lane, mid game, late game, or teamfights, so the focus phase stays on Any phase.',
    target: '#f-focus',
  },
  {
    title: 'Use free text',
    body: 'For a new player, the measured criterion should be a written explanation, not a stat target.',
    target: '#f-crit-metric',
  },
  {
    title: 'Define completion',
    body: 'The completion criteria is one reviewed death clip with a written reason for why it happened.',
    target: '#f-criteria',
  },
  {
    title: 'Read the prompts',
    body: 'These prompts tell the user what to think about before queue, during the game, and after the VOD is ready.',
    target: '#f-prompts',
  },
  {
    title: 'Leave champions empty',
    body: 'An empty champion list means this objective works for every champion, which is right for a first-run tutorial.',
    target: '#f-champ-input',
  },
  {
    title: 'Track Death events',
    body: 'Death must be selected here. That is the link that lets the VOD viewer auto-clip death moments later.',
    target: '#f-events',
  },
  {
    title: 'Create the objective',
    body: 'The setup is ready. Creating it will save the objective id, make it priority, and move the tutorial to Start Block.',
    target: '#form-submit',
  },
];

let _invoke = null;
let _cfg = null;
let _panel = null;
let _target = null;
let _rendering = false;
let _objectivePrefilled = false;
let _objectiveGuideIndex = 0;
let _vodPoll = null;
let _vodPollAttempts = 0;

const $ = (id) => document.getElementById(id);

async function getInvoke() {
  if (_invoke) return _invoke;
  try {
    const mod = await import('@tauri-apps/api/core');
    if (mod && typeof mod.invoke === 'function') {
      _invoke = mod.invoke;
      return _invoke;
    }
  } catch (_) {
    // Tauri module is unavailable in browser preview.
  }
  if (window.__TAURI__ && window.__TAURI__.core && typeof window.__TAURI__.core.invoke === 'function') {
    _invoke = window.__TAURI__.core.invoke.bind(window.__TAURI__.core);
    return _invoke;
  }
  return null;
}

function pageFile() {
  return (window.location.pathname || '').split('/').pop().toLowerCase() || 'dashboard.html';
}

function gameIdFromUrl() {
  const n = Number(new URLSearchParams(window.location.search).get('gameId') || 0);
  return Number.isFinite(n) && n > 0 ? n : 0;
}

function active(cfg = _cfg) {
  return !!cfg
    && !cfg.firstReviewTutorialCompleted
    && !cfg.firstReviewTutorialDismissed
    && !!cfg.firstReviewTutorialStep;
}

function normalizeCfg(cfg) {
  const c = cfg || {};
  return {
    ...c,
    firstReviewTutorialStep: String(c.firstReviewTutorialStep || ''),
    firstReviewTutorialCompleted: !!c.firstReviewTutorialCompleted,
    firstReviewTutorialDismissed: !!c.firstReviewTutorialDismissed,
    firstReviewTutorialObjectiveId: Number(c.firstReviewTutorialObjectiveId || 0),
    firstReviewTutorialGameId: Number(c.firstReviewTutorialGameId || 0),
  };
}

async function readConfig() {
  const invoke = await getInvoke();
  if (!invoke) return normalizeCfg(_cfg);
  try {
    _cfg = normalizeCfg(await invoke('get_config'));
  } catch (err) {
    console.warn('[tutorial] get_config failed:', err);
    _cfg = normalizeCfg(_cfg);
  }
  return _cfg;
}

async function saveConfig(patch) {
  const invoke = await getInvoke();
  const payload = { ...patch };
  if (!invoke) {
    _cfg = normalizeCfg({ ...(_cfg || {}), ...payload });
    return _cfg;
  }
  await invoke('save_config', { payload });
  _cfg = normalizeCfg({ ...(_cfg || {}), ...payload });
  return _cfg;
}

function routeForStep(step) {
  if ((step === STEPS.VOD || step === STEPS.VOD_MOMENT || step === STEPS.WAIT_VOD) && _cfg?.firstReviewTutorialGameId) {
    return `vodplayer.html?gameId=${encodeURIComponent(_cfg.firstReviewTutorialGameId)}`;
  }
  if (step === STEPS.REVIEW && _cfg?.firstReviewTutorialGameId) {
    return `review.html?gameId=${encodeURIComponent(_cfg.firstReviewTutorialGameId)}`;
  }
  return PAGE_BY_STEP[step] || 'dashboard.html';
}

function go(target) {
  if (target) window.location.href = target;
}

async function startTutorial() {
  await saveConfig({
    firstReviewTutorialStep: STEPS.OBJECTIVE,
    firstReviewTutorialCompleted: false,
    firstReviewTutorialDismissed: false,
    firstReviewTutorialObjectiveId: 0,
    firstReviewTutorialGameId: 0,
  });
  go('objectives.html?tutorial=first-review');
}

async function dismissTutorial() {
  clearVodPoll();
  await saveConfig({
    firstReviewTutorialStep: '',
    firstReviewTutorialDismissed: true,
  });
  removePanel();
}

async function completeTutorial() {
  clearVodPoll();
  await saveConfig({
    firstReviewTutorialStep: '',
    firstReviewTutorialCompleted: true,
    firstReviewTutorialDismissed: false,
  });
  removePanel();
}

async function advance(step, patch = {}) {
  await saveConfig({ firstReviewTutorialStep: step, ...patch });
  renderTutorial();
}

function removePanel() {
  if (_panel) _panel.remove();
  _panel = null;
  setTarget(null);
}

function setTarget(selector) {
  if (_target) _target.classList.remove('frt-highlight');
  _target = null;
  if (!selector) return;
  const el = document.querySelector(selector);
  if (!el) return;
  _target = el;
  _target.classList.add('frt-highlight');
  try { _target.scrollIntoView({ behavior: 'smooth', block: 'center' }); } catch (_) { /* ignore */ }
}

function panel({ title, body, target, primary, secondary, tertiary, tone }) {
  removePanel();
  setTarget(target);

  const wrap = document.createElement('section');
  wrap.className = `frt-panel${tone ? ` ${tone}` : ''}`;
  wrap.setAttribute('aria-live', 'polite');

  const top = document.createElement('div');
  top.className = 'frt-top';
  const label = document.createElement('span');
  label.className = 'frt-step';
  label.textContent = STEP_LABELS[_cfg?.firstReviewTutorialStep] || 'Tutorial';
  const skip = document.createElement('button');
  skip.type = 'button';
  skip.className = 'frt-skip';
  skip.textContent = 'Skip';
  skip.addEventListener('click', dismissTutorial);
  top.append(label, skip);

  const h = document.createElement('h2');
  h.textContent = title;
  const p = document.createElement('p');
  p.textContent = body;

  const actions = document.createElement('div');
  actions.className = 'frt-actions';
  for (const def of [primary, secondary, tertiary].filter(Boolean)) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = def.kind === 'ghost' ? 'frt-btn ghost' : 'frt-btn';
    btn.textContent = def.label;
    btn.addEventListener('click', def.onClick);
    actions.appendChild(btn);
  }

  wrap.append(top, h, p, actions);
  document.body.appendChild(wrap);
  _panel = wrap;
}

function mismatchPanel(step) {
  const target = routeForStep(step);
  panel({
    title: 'Continue the first review tutorial',
    body: 'This guided flow is in progress. Jump back to the next step when you are ready.',
    primary: { label: 'Continue', onClick: () => go(target) },
    secondary: { label: 'Restart', kind: 'ghost', onClick: startTutorial },
  });
}

async function findDeathObjective() {
  const invoke = await getInvoke();
  if (!invoke) return null;
  try {
    const res = await invoke('get_active_objectives');
    const rows = Array.isArray(res?.objectives) ? res.objectives : [];
    return rows.find((o) => Array.isArray(o.trackedTokens)
      && o.trackedTokens.some((t) => String(t).toUpperCase() === 'DEATH')) || null;
  } catch (err) {
    console.warn('[tutorial] get_active_objectives failed:', err);
    return null;
  }
}

async function useObjective(id) {
  const objectiveId = Number(id) || 0;
  const invoke = await getInvoke();
  if (invoke && objectiveId > 0) {
    try { await invoke('set_objective_priority', { payload: { id: objectiveId } }); }
    catch (err) { console.warn('[tutorial] set objective priority failed:', err); }
  }
  await saveConfig({
    firstReviewTutorialObjectiveId: objectiveId,
    firstReviewTutorialStep: STEPS.START_BLOCK,
  });
  go('dashboard.html');
}

async function renderObjectiveStep() {
  if (!_objectivePrefilled) {
    const existing = await findDeathObjective();
    if (existing && existing.objectiveId) {
      panel({
        title: 'Use your death-review objective',
        body: 'You already have an active objective tied to deaths. The tutorial can use it instead of creating a duplicate.',
        target: '#active-list, #focus-list',
        primary: { label: 'Use existing objective', onClick: () => useObjective(existing.objectiveId) },
        secondary: { label: 'Create a fresh one', kind: 'ghost', onClick: prefillObjective },
      });
      return;
    }
    prefillObjective();
  }

  renderObjectiveGuidePanel();
}

function prefillObjective() {
  _objectivePrefilled = true;
  _objectiveGuideIndex = 0;
  window.dispatchEvent(new CustomEvent('revu:first-review-prefill-objective'));
}

function renderObjectiveGuidePanel() {
  const max = OBJECTIVE_FIELD_GUIDE.length - 1;
  const idx = Math.max(0, Math.min(_objectiveGuideIndex, max));
  _objectiveGuideIndex = idx;
  const guide = OBJECTIVE_FIELD_GUIDE[idx];
  const atEnd = idx >= max;
  const primary = atEnd
    ? { label: 'Create objective', onClick: () => $('form-submit')?.click() }
    : { label: 'Next option', onClick: () => { _objectiveGuideIndex += 1; renderObjectiveStep(); } };
  const secondary = idx > 0
    ? { label: 'Back', kind: 'ghost', onClick: () => { _objectiveGuideIndex -= 1; renderObjectiveStep(); } }
    : { label: 'Refill defaults', kind: 'ghost', onClick: prefillObjective };
  const tertiary = idx > 0
    ? { label: 'Refill defaults', kind: 'ghost', onClick: prefillObjective }
    : null;
  panel({
    title: guide.title,
    body: `Option ${idx + 1} of ${OBJECTIVE_FIELD_GUIDE.length}. ${guide.body}`,
    target: guide.target,
    primary,
    secondary,
    tertiary,
  });
}

function renderStartBlockStep() {
  panel({
    title: 'Start a review block',
    body: 'Write one focus before you queue. Use something simple like: Play normally, then review every death after the game.',
    target: '#nextstep-cta',
    primary: { label: 'Write focus', onClick: () => $('nextstep-cta')?.click() },
    secondary: { label: 'I already started one', kind: 'ghost', onClick: () => advance(STEPS.QUEUE) },
  });
}

function renderQueueStep() {
  panel({
    title: 'Queue up',
    body: 'Start a League game. When champ select opens, Revu will switch to the pre-game window and show your matchup, intent, active objective, and prompts.',
    target: '#statusline',
    primary: { label: 'Open objectives', kind: 'ghost', onClick: () => go('objectives.html') },
  });
}

function renderPregameStep() {
  panel({
    title: 'Use the pre-game window',
    body: 'This screen shows matchup context, your mood check, this game intent, the priority objective, practiced toggles, and any prompt answers. These save into the game when it ends.',
    target: '#intent-card, #obj-card, #prompts-card',
    primary: { label: 'Got it', onClick: () => advance(STEPS.INGAME) },
  });
}

function renderIngameStep() {
  panel({
    title: 'Use the in-game window lightly',
    body: 'During the game, treat Revu as a reference. Keep your focus simple. The important review work happens after the game when deaths and clips are available.',
    target: '#prompts-card, #intent-card',
    primary: { label: 'I will review after', onClick: () => advance(STEPS.WAIT_VOD) },
  });
}

function renderSettingsVodScanStep() {
  panel({
    title: 'Scan for the recording',
    body: 'If the match ended but no VOD is linked yet, scan your Ascent recordings folder. Then return to the VOD and Revu will check again.',
    target: '#scan-btn, #ascentFolder',
    primary: { label: 'Scan VODs', onClick: () => $('scan-btn')?.click() },
    secondary: { label: 'Return to VOD', kind: 'ghost', onClick: () => go(routeForStep(STEPS.WAIT_VOD)) },
  });
}

function clearVodPoll() {
  if (_vodPoll) clearTimeout(_vodPoll);
  _vodPoll = null;
  _vodPollAttempts = 0;
}

async function pollVodReady(gameId) {
  const invoke = await getInvoke();
  if (!invoke || !(gameId > 0)) return false;
  try {
    const vod = await invoke('get_vod', { gameId });
    return !!(vod && vod.hasVod && vod.filePath);
  } catch (_) {
    return false;
  }
}

async function renderWaitVodStep() {
  const gameId = gameIdFromUrl() || Number(_cfg?.firstReviewTutorialGameId || 0);
  if (gameId > 0 && gameId !== _cfg.firstReviewTutorialGameId) {
    await saveConfig({ firstReviewTutorialGameId: gameId });
  }
  if (gameId > 0 && await pollVodReady(gameId)) {
    await saveConfig({ firstReviewTutorialStep: STEPS.VOD, firstReviewTutorialGameId: gameId });
    renderVodStep();
    return;
  }

  const exhausted = _vodPollAttempts >= 15;
  panel({
    title: exhausted ? 'Recording is not linked yet' : 'Waiting for the recording',
    body: exhausted
      ? 'The game is saved, but Revu does not see a linked VOD yet. Open Settings and scan VODs, then return to this game.'
      : 'Revu is waiting for the Ascent recording to finish and link. This can take a short moment after the match ends.',
    target: '#vp-vod, #statusline',
    primary: exhausted
      ? { label: 'Open Settings', onClick: () => go('settings.html') }
      : { label: 'Check again', onClick: () => renderWaitVodStep() },
    secondary: gameId > 0
      ? { label: 'Open review', kind: 'ghost', onClick: () => openReview(gameId) }
      : null,
  });

  if (!exhausted && !_vodPoll) {
    _vodPollAttempts += 1;
    _vodPoll = setTimeout(() => {
      _vodPoll = null;
      renderWaitVodStep();
    }, 5000);
  }
}

function openReview(gameId) {
  const gid = Number(gameId || _cfg?.firstReviewTutorialGameId || 0);
  advance(STEPS.REVIEW, gid > 0 ? { firstReviewTutorialGameId: gid } : {}).then(() => {
    go(gid > 0 ? `review.html?gameId=${encodeURIComponent(gid)}` : 'review.html');
  });
}

function renderVodStep() {
  const btn = $('vp-autoclip-btn');
  const disabledNoEvents = btn && btn.disabled && /No objective events|No tied events/i.test(btn.textContent || '');
  panel({
    title: disabledNoEvents ? 'No deaths to auto-clip' : 'Auto-clip deaths',
    body: disabledNoEvents
      ? 'This game has no objective-tied death events to clip. That can happen in a clean game. Continue to the review and save what you learned.'
      : 'Use the Auto-clip objective events control. It calls the existing objective auto-clip path and saves clips around the death events.',
    target: '#vp-autoclip-tools',
    primary: disabledNoEvents
      ? { label: 'Open review', onClick: () => openReview(gameIdFromUrl()) }
      : { label: 'Auto-clip deaths', onClick: () => btn?.click() },
    secondary: { label: 'Skip to review', kind: 'ghost', onClick: () => openReview(gameIdFromUrl()) },
  });
}

function renderVodMomentStep() {
  panel({
    title: 'Review one death moment',
    body: 'Open a saved death clip, write a short note for why you died, and optionally share the clip. Sharing is useful but not required to finish the tutorial.',
    target: '#vp-moments, #vp-tabs',
    primary: { label: 'Open review', onClick: () => openReview(gameIdFromUrl()) },
    secondary: { label: 'Share first clip', kind: 'ghost', onClick: () => document.querySelector('.vp-share-btn:not(:disabled)')?.click() },
  });
}

function renderReviewStep() {
  panel({
    title: 'Save the review',
    body: 'Write why you died in the review notes or mistakes field, then save the review. This closes the loop from objective to game to clip to review.',
    target: '#rv-fields, #rv-savebtn',
    primary: { label: 'Save review', onClick: () => $('rv-savebtn')?.click() },
  });
}

// Show the dashboard launcher only while the tutorial is still available to run
// (not completed, not dismissed). Hidden as soon as the user finishes or skips it,
// so the gold "Start first review tutorial" link can't re-trigger the tour forever.
function syncLaunchButton(cfg) {
  const launch = document.querySelector('.frt-launch');
  if (!launch) return;
  const available = !!cfg && !cfg.firstReviewTutorialCompleted && !cfg.firstReviewTutorialDismissed;
  launch.hidden = !available;
}

async function renderTutorial() {
  if (_rendering) return;
  _rendering = true;
  try {
    const cfg = await readConfig();
    // Keep the dashboard "Start first review tutorial" launcher in sync with state:
    // hide it once the tutorial is completed or dismissed so a finished user can't
    // accidentally re-run the whole 7-step tour.
    syncLaunchButton(cfg);
    if (!active(cfg)) { removePanel(); return; }

    const step = cfg.firstReviewTutorialStep;
    const page = pageFile();

    if (page === 'objectives.html' && step === STEPS.OBJECTIVE) return await renderObjectiveStep();
    if (page === 'dashboard.html' && step === STEPS.START_BLOCK) return renderStartBlockStep();
    if (page === 'dashboard.html' && step === STEPS.QUEUE) return renderQueueStep();
    if (page === 'pregame.html') {
      if ([STEPS.QUEUE, STEPS.PREGAME].includes(step)) await saveConfig({ firstReviewTutorialStep: STEPS.PREGAME });
      return renderPregameStep();
    }
    if (page === 'ingame.html') {
      if ([STEPS.QUEUE, STEPS.PREGAME, STEPS.INGAME].includes(step)) await saveConfig({ firstReviewTutorialStep: STEPS.INGAME });
      return renderIngameStep();
    }
    if (page === 'vodplayer.html' && step === STEPS.WAIT_VOD) return await renderWaitVodStep();
    if (page === 'vodplayer.html' && step === STEPS.VOD) return renderVodStep();
    if (page === 'vodplayer.html' && step === STEPS.VOD_MOMENT) return renderVodMomentStep();
    if (page === 'settings.html' && step === STEPS.WAIT_VOD) return renderSettingsVodScanStep();
    if (page === 'review.html' && step === STEPS.REVIEW) return renderReviewStep();

    mismatchPanel(step);
  } finally {
    _rendering = false;
  }
}

window.RevuFirstReviewTutorial = {
  start: startTutorial,
  dismiss: dismissTutorial,
  complete: completeTutorial,
  advance,
  render: renderTutorial,
};

document.addEventListener('click', (ev) => {
  const trigger = ev.target.closest && ev.target.closest('[data-action="start_first_review_tutorial"]');
  if (!trigger) return;
  ev.preventDefault();
  startTutorial();
});

window.addEventListener('revu:first-review-objective-created', async (ev) => {
  const id = Number(ev.detail?.id || 0);
  const cfg = await readConfig();
  if (!active(cfg) || cfg.firstReviewTutorialStep !== STEPS.OBJECTIVE || !(id > 0)) return;
  await useObjective(id);
});

window.addEventListener('revu:first-review-block-started', async () => {
  const cfg = await readConfig();
  if (!active(cfg) || cfg.firstReviewTutorialStep !== STEPS.START_BLOCK) return;
  await advance(STEPS.QUEUE);
});

window.addEventListener('revu:first-review-autoclip-done', async () => {
  const cfg = await readConfig();
  if (!active(cfg) || cfg.firstReviewTutorialStep !== STEPS.VOD) return;
  await advance(STEPS.VOD_MOMENT);
});

window.addEventListener('revu:first-review-share-done', async () => {
  const cfg = await readConfig();
  if (!active(cfg) || cfg.firstReviewTutorialStep !== STEPS.VOD_MOMENT) return;
  renderVodMomentStep();
});

window.addEventListener('revu:first-review-vod-scan-done', async () => {
  const cfg = await readConfig();
  if (!active(cfg) || cfg.firstReviewTutorialStep !== STEPS.WAIT_VOD) return;
  go(routeForStep(STEPS.WAIT_VOD));
});

window.addEventListener('revu:first-review-review-saved', async () => {
  const cfg = await readConfig();
  if (!active(cfg) || cfg.firstReviewTutorialStep !== STEPS.REVIEW) return;
  await completeTutorial();
});

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', renderTutorial);
} else {
  renderTutorial();
}

} // end re-injection guard (window.__revuFirstReviewTutorialLoaded)
