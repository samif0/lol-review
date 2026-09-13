import { getInvoke } from './platform/index.mjs';
import { guideStage, guideIsActive, guideEventPatch } from './first-review-guide.mjs';

// Optional inline help. Existing config fields preserve older tutorial progress.
if (!window.__revuFirstReviewTutorialLoaded) {
  window.__revuFirstReviewTutorialLoaded = true;
  const stylesheet = document.createElement('link');
  stylesheet.rel = 'stylesheet';
  stylesheet.href = new URL('./tutorial.css', import.meta.url).href;
  document.head.append(stylesheet);
  let config = {};
  let pending = Promise.resolve();
  let expanded = false;
  const page = location.pathname.split('/').pop();

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text) node.textContent = text;
    return node;
  }
  async function readConfig() {
    const invoke = await getInvoke();
    if (invoke) config = await invoke('get_config');
    return config;
  }
  async function save(patch) {
    const invoke = await getInvoke();
    if (invoke) await invoke('save_config', { payload: patch });
    config = { ...config, ...patch };
  }
  function report(error) {
    console.warn('[quick-guide]', error);
    const preview = String(error?.message || error).includes('isolated host testing');
    if (preview && !document.querySelector('#review-guide')) return;
    let status = document.querySelector('#review-guide-error');
    if (!status) {
      status = element('p', 'review-guide-error');
      status.id = 'review-guide-error';
      status.setAttribute('role', preview ? 'status' : 'alert');
      (document.querySelector('#review-guide') || document.querySelector('.shell') || document.body).append(status);
    }
    status.classList.toggle('is-preview', preview);
    status.textContent = preview ? 'Preview mode: explore the guide here. Progress is not saved.' : 'The guide could not save your progress. Your other work is safe. Try opening the guide again.';
  }
  function schedule(action) {
    // Serial config writes prevent simultaneous page events from regressing help.
    pending = pending.then(action).catch(report);
    return pending;
  }
  function link(label, href) {
    const a = element('a', 'review-guide-link', label);
    a.href = href;
    return a;
  }
  function button(label, action) {
    const b = element('button', 'review-guide-link', label);
    b.type = 'button';
    b.addEventListener('click', () => schedule(action));
    return b;
  }
  function paint() {
    document.querySelector('#review-guide')?.remove();
    for (const entry of document.querySelectorAll('.frt-launch')) entry.hidden = false;
    for (const launch of document.querySelectorAll('[data-action="start_first_review_tutorial"]')) {
      launch.textContent = guideIsActive(config) ? 'Continue quick guide →' : 'Open quick guide →';
    }
    if (!guideIsActive(config) || ['ingame.html', 'pregame.html'].includes(page)) return;
    const shell = document.querySelector('.shell');
    if (!shell) return;
    const stage = guideStage(config.firstReviewTutorialStep);
    const guide = element('details', 'review-guide');
    guide.id = 'review-guide';
    guide.open = expanded;
    const summary = element('summary', 'review-guide-summary');
    summary.append(element('strong', '', 'Your first review'), element('span', '', `Step ${stage + 1} of 3 · ${['Choose a learning objective', 'Play with a focus', 'Review a moment'][stage]}`));
    guide.append(summary);
    guide.addEventListener('toggle', () => { expanded = guide.open; });
    const content = element('div', 'review-guide-content');
    content.append(element('p', 'review-guide-intro', 'Use these three steps at your own pace. You can keep exploring Revu with the guide open.'));
    const list = element('ol', 'review-guide-steps');
    const gameId = Number(config.firstReviewTutorialGameId);
    const validGame = Number.isSafeInteger(gameId) && gameId > 0;
    const steps = [
      ['Choose one learning objective', 'Pick a skill to practice. Give it a clear name; detailed tracking can wait.', 'Choose a learning objective', 'objectives.html'],
      ['Play with a focus', 'Start a session from Home, then play League. Check recording settings before you queue.', 'Go to Home', 'dashboard.html?intent=startblock'],
      ['Review one useful moment', 'Open a match, watch its recording when available, and write what you will try next.', 'Open a match', validGame ? `review.html?gameId=${gameId}` : 'games.html'],
    ];
    steps.forEach(([title, copy, action, href], index) => {
      const row = element('li', `review-guide-item${index === stage ? ' is-current' : ''}${index < stage ? ' is-complete' : ''}`);
      if (index === stage) row.setAttribute('aria-current', 'step');
      row.append(element('span', 'review-guide-number', index < stage ? '✓' : String(index + 1)));
      const body = element('div');
      body.append(element('h3', '', title), element('p', '', copy), link(action, href));
      if (index === 0 && stage === 0) {
        body.append(button('I already have a learning objective', async () => {
          const invoke = await getInvoke();
          const response = invoke ? await invoke('get_active_objectives') : {};
          const goals = Array.isArray(response?.objectives) ? response.objectives : [];
          const goalId = Number(goals[0]?.objectiveId);
          if (!Number.isSafeInteger(goalId) || goalId <= 0) {
            body.append(element('p', '', 'Create a learning objective from the Learning objectives page first. It can be as simple as “check the map before trading.”'));
            return;
          }
          await save({ firstReviewTutorialStep: 'start_block', firstReviewTutorialObjectiveId: goalId });
          paint();
        }));
      }
      if (index === 1) body.append(link('Recording settings', 'settings.html'));
      row.append(body);
      list.append(row);
    });
    const footer = element('div', 'review-guide-footer');
    footer.append(element('span', '', 'You can reopen this guide from Home or Settings.'), button('Dismiss guide', dismiss));
    content.append(list, footer);
    guide.append(content);
    const anchor = document.querySelector('.home-orientation') || shell.querySelector('.hero') || shell.firstElementChild;
    if (anchor) anchor.after(guide); else shell.prepend(guide);
  }
  async function start() {
    await readConfig();
    const patch = guideIsActive(config) ? null : { firstReviewTutorialStep: 'objective', firstReviewTutorialCompleted: false, firstReviewTutorialDismissed: false, firstReviewTutorialObjectiveId: 0, firstReviewTutorialGameId: 0 };
    if (patch) config = { ...config, ...patch };
    expanded = true;
    document.querySelector('#review-guide-error')?.remove();
    paint();
    document.querySelector('#review-guide summary')?.focus();
    // Help remains readable even when the host cannot save preferences.
    if (patch) await save(patch);
  }
  async function dismiss() {
    const patch = { firstReviewTutorialStep: '', firstReviewTutorialDismissed: true };
    config = { ...config, ...patch };
    paint();
    document.querySelector('[data-action="start_first_review_tutorial"]')?.focus();
    await save(patch);
  }
  async function complete() {
    await save({ firstReviewTutorialStep: '', firstReviewTutorialCompleted: true, firstReviewTutorialDismissed: false });
    paint();
  }
  window.RevuFirstReviewTutorial = {
    start: () => schedule(start), dismiss: () => schedule(dismiss), complete: () => schedule(complete),
    advance: (step, patch = {}) => schedule(async () => { await save({ ...patch, firstReviewTutorialStep: step }); paint(); }),
    render: () => schedule(async () => { await readConfig(); paint(); }),
  };
  document.addEventListener('click', event => {
    if (event.target.closest('[data-action="start_first_review_tutorial"]')) {
      event.preventDefault();
      schedule(start);
    }
  });
  for (const name of ['objective-created', 'block-started', 'autoclip-done', 'review-saved']) {
    window.addEventListener(`revu:first-review-${name}`, event => schedule(async () => {
      await readConfig();
      const patch = guideEventPatch(config, name, event.detail);
      if (patch) { await save(patch); paint(); }
    }));
  }
  const initialize = () => schedule(async () => {
    await readConfig();
    const gameId = Number(new URLSearchParams(location.search).get('gameId'));
    if (guideIsActive(config) && ['review.html', 'vodplayer.html'].includes(page) && Number.isSafeInteger(gameId) && gameId > 0) {
      await save({ firstReviewTutorialStep: 'review', firstReviewTutorialGameId: gameId });
    }
    paint();
  });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
}
