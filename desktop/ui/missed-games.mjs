import { getInvoke, getListen } from './platform/index.mjs';

// Home card for finished games Revu did not save: League's match history has them,
// but the app was closed or missed the end of the game. The game Revu was tracking
// saves itself; these older ones wait for the player to save or dismiss them.
// Every server string is written with textContent.

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

// "Jun 14" from Unix seconds (local time).
export function shortDate(seconds) {
  const value = Number(seconds);
  if (!Number.isFinite(value) || value <= 0) return '';
  const date = new Date(value * 1000);
  return `${MONTHS[date.getMonth()]} ${date.getDate()}`;
}

function duration(seconds) {
  const value = Math.trunc(Number(seconds));
  if (!Number.isFinite(value) || value <= 0) return '';
  return `${Math.floor(value / 60)}:${String(value % 60).padStart(2, '0')}`;
}

export function describeMissedGame(game) {
  const champion = game?.championName || 'Game';
  const title = game?.enemyLaner ? `${champion} vs ${game.enemyLaner}` : champion;
  const kda = [game?.kills, game?.deaths, game?.assists].every(Number.isFinite)
    ? `${game.kills}/${game.deaths}/${game.assists} KDA` : '';
  const detail = [shortDate(game?.timestamp), duration(game?.gameDuration), kda].filter(Boolean).join(' · ');
  return { title, detail, result: game?.win ? 'Victory' : 'Defeat' };
}

const validGames = (games) => (Array.isArray(games) ? games : [])
  .filter((game) => Number.isSafeInteger(game?.gameId) && game.gameId > 0);

// onSaved(gameId) runs after a game is saved, so the page can refresh what that game joins.
export function createMissedGamesCard({ section, list, heading, status, invoke, doc = globalThis.document, onSaved }) {
  let games = [];
  const pending = new Set();
  // Focus lands here when the last row the player acted on goes away.
  heading?.setAttribute?.('tabindex', '-1');

  function setStatus(text) {
    if (!status) return;
    status.textContent = text;
    status.hidden = !text;
  }

  function button(className, label, ariaLabel, onClick) {
    const el = doc.createElement('button');
    el.type = 'button';
    el.className = className;
    el.textContent = label;
    el.setAttribute('aria-label', ariaLabel);
    el.addEventListener('click', onClick);
    return el;
  }

  function row(game) {
    const { title, detail, result } = describeMissedGame(game);
    const el = doc.createElement('div');
    el.className = 'home-review-match';
    el.dataset.gameId = String(game.gameId);

    const info = doc.createElement('div');
    info.className = 'home-review-match-info';
    const head = doc.createElement('div');
    head.className = 'home-review-match-title';
    const name = doc.createElement('h3');
    name.className = 'vline';
    name.textContent = title;
    const outcome = doc.createElement('span');
    outcome.className = 'home-review-result';
    outcome.textContent = result;
    outcome.dataset.result = game.win ? 'win' : 'loss';
    head.appendChild(name);
    head.appendChild(outcome);
    const meta = doc.createElement('p');
    meta.className = 'vsmall';
    meta.textContent = detail;
    info.appendChild(head);
    info.appendChild(meta);

    const actions = doc.createElement('div');
    actions.className = 'home-review-actions';
    const busy = pending.has(game.gameId);
    const save = button('cta', 'Save game', `Save ${title} to your Match library`, () => resolve(game.gameId, 'save'));
    const dismiss = button('home-review-skip', 'Dismiss', `Dismiss ${title}`, () => resolve(game.gameId, 'dismiss'));
    save.disabled = busy;
    dismiss.disabled = busy;
    actions.appendChild(save);
    actions.appendChild(dismiss);

    el.appendChild(info);
    el.appendChild(actions);
    return el;
  }

  function render() {
    while (list.firstChild) list.removeChild(list.firstChild);
    section.hidden = games.length === 0;
    if (heading) {
      heading.textContent = games.length === 1
        ? '1 finished game wasn’t saved'
        : `${games.length} finished games weren’t saved`;
    }
    for (const game of games) list.appendChild(row(game));
  }

  function setGames(next) {
    games = validGames(next);
    render();
  }

  async function refresh() {
    try {
      const value = await invoke('get_missed_games');
      setGames(value?.games);
    } catch (err) {
      // Keep what is shown; the next missedReviews event or Home visit tries again.
      console.warn('[missed games] could not load the list:', err);
    }
  }

  function firstButton(node) {
    if (node?.tagName === 'BUTTON') return node;
    for (const child of node?.children || []) {
      const hit = firstButton(child);
      if (hit) return hit;
    }
    return null;
  }

  // The row the player acted on is rebuilt or gone: keep keyboard focus in the card, on the
  // row now in its place (or the last one), else on the heading.
  function restoreFocus(index) {
    if (section.hidden) return;
    const rows = list.children;
    const target = rows.length ? firstButton(rows[Math.min(index, rows.length - 1)]) : null;
    if (target && !target.disabled) target.focus?.();
    else heading?.focus?.();
  }

  async function resolve(gameId, action) {
    if (pending.has(gameId)) return;
    const active = doc?.activeElement;
    const hadFocus = !!active && typeof list.contains === 'function' && list.contains(active);
    const index = Math.max(0, games.findIndex((game) => game.gameId === gameId));
    pending.add(gameId);
    setStatus('');
    render();
    let failed = false;
    let result = null;
    try {
      result = await invoke('resolve_missed_game', { payload: { gameId, action } });
    } catch (err) {
      failed = true;
      console.error(`[missed games] ${action} failed:`, err);
    } finally {
      pending.delete(gameId);
    }
    await refresh();
    // A game that no longer waits was already saved or dismissed: nothing to try again.
    if (failed && games.some((game) => game.gameId === gameId)) {
      setStatus(action === 'save' ? 'Couldn’t save this game. Try again.' : 'Couldn’t dismiss this game. Try again.');
    }
    if (hadFocus) restoreFocus(index);
    if (!failed && result?.saved === true) notifySaved(gameId);
  }

  function notifySaved(gameId) {
    if (typeof onSaved !== 'function') return;
    try { onSaved(gameId); } catch (err) { console.warn('[missed games] refresh after save failed:', err); }
  }

  // The sidecar announces every change to the list as 'missedReviews' with the whole list.
  // A game Revu saves itself (the game just played, or one a Save here left to that save
  // while it was still landing) is announced as a saved 'gameEnded' or 'gameRecovered'.
  function handleEvent(message) {
    if ((message?.type === 'gameEnded' || message?.type === 'gameRecovered') && message.payload?.saved === true) {
      notifySaved(message.payload.gameId);
      return;
    }
    if (message?.type !== 'missedReviews') return;
    const payload = message.payload || {};
    if (Array.isArray(payload.games)) setGames(payload.games);
    else void refresh();
  }

  return { refresh, resolve, handleEvent, get games() { return games.slice(); } };
}

export async function mountMissedGames(doc = globalThis.document, { onSaved } = {}) {
  const section = doc?.getElementById?.('missed-games');
  if (!section) return null;
  let invoke = null;
  try { invoke = await getInvoke(); } catch (_) { invoke = null; }
  if (!invoke) return null; // browser preview: there is no match history to recover from

  const card = createMissedGamesCard({
    section,
    list: doc.getElementById('missed-games-list'),
    heading: doc.getElementById('missed-games-label'),
    status: doc.getElementById('missed-games-status'),
    invoke,
    doc,
    onSaved,
  });
  // Subscribe before the first fetch so no change slips between the two.
  try {
    const listen = await getListen();
    if (listen) {
      await listen('lcu-event', (event) => card.handleEvent(event?.payload));
      try { await invoke('start_lcu_events'); } catch (_) { /* the shell normally runs the stream already */ }
    }
  } catch (err) {
    console.warn('[missed games] live updates unavailable:', err);
  }
  await card.refresh();
  return card;
}
