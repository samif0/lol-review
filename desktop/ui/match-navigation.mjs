const STORE_KEY = '__revuMatchViewsV1';
const VIEWS = new Set(['review', 'vod']);
const FILES = { review: 'review.html', vod: 'vodplayer.html' };
const clone = value => JSON.parse(JSON.stringify(value));
const validGame = value => Number.isSafeInteger(Number(value)) && Number(value) > 0;

// Plain data only: retaining a document or callback here would retain its player.
export function createMatchViewStore({ maxGames = 8 } = {}) {
  const games = new Map();
  return {
    save(gameId, view, state) {
      if (!validGame(gameId) || !VIEWS.has(view)) return false;
      const id = Number(gameId), snapshot = clone(state);
      const entry = games.get(id) || {};
      entry[view] = snapshot;
      games.delete(id); games.set(id, entry);
      while (games.size > Math.max(1, maxGames)) games.delete(games.keys().next().value);
      return true;
    },
    peek(gameId, view) {
      const state = games.get(Number(gameId))?.[view];
      return state ? clone(state) : null;
    },
    clear(gameId, view) {
      const entry = games.get(Number(gameId));
      if (!entry) return;
      delete entry[view];
      if (!Object.keys(entry).length) games.delete(Number(gameId));
    },
  };
}

function viewStore(scope) {
  // The existing shell survives iframe navigation. Keep just small snapshots,
  // with no second iframe or hidden video decoder running in the background.
  try {
    const owner = scope.top || scope;
    if (owner !== scope && owner.document) {
      if (!owner[STORE_KEY]) owner[STORE_KEY] = createMatchViewStore();
      return owner[STORE_KEY];
    }
  } catch (_) { /* Standalone pages use their tab's session storage below. */ }
  const memory = createMatchViewStore();
  let saved = [];
  try {
    const parsed = JSON.parse(scope.sessionStorage?.getItem(STORE_KEY) || '[]');
    if (Array.isArray(parsed)) saved = parsed.slice(-16);
    for (const entry of saved) memory.save(entry.gameId, entry.view, entry.state);
  } catch (_) { saved = []; }
  const persist = () => { try { scope.sessionStorage?.setItem(STORE_KEY, JSON.stringify(saved)); } catch (_) { /* In-memory fallback. */ } };
  return {
    peek: memory.peek,
    save(gameId, view, state) {
      if (!memory.save(gameId, view, state)) return false;
      saved = saved.filter(entry => entry.gameId !== gameId || entry.view !== view);
      saved.push({ gameId, view, state: clone(state) }); saved = saved.slice(-16); persist();
      return true;
    },
    clear(gameId, view) {
      memory.clear(gameId, view);
      saved = saved.filter(entry => entry.gameId !== gameId || entry.view !== view); persist();
    },
  };
}

export function matchViewTarget(target, base, currentGameId) {
  let origin, url;
  try { origin = new URL(base); url = new URL(target, origin); } catch (_) { return null; }
  if (url.protocol !== origin.protocol || url.host !== origin.host) return null;
  const view = Object.keys(FILES).find(key => url.pathname === new URL(FILES[key], origin).pathname);
  const gameId = Number(url.searchParams.get('gameId'));
  if (!view || !validGame(gameId)) return null;
  if (gameId === Number(currentGameId)) url.searchParams.set('resume', '1');
  return url;
}

export function hasExplicitVodTarget(search) {
  const params = new URLSearchParams(search);
  const time = params.get('t'), clip = Number(params.get('clip'));
  return (time !== null && time.trim() !== '' && Number.isFinite(Number(time)) && Number(time) >= 0)
    || (Number.isSafeInteger(clip) && clip > 0);
}

export function createMatchNavigation({ view, capture = () => null, restore = () => {}, beforeLeave, canCapture = () => true,
  document: doc = globalThis.document, scope = globalThis.window, store = viewStore(scope) } = {}) {
  if (!VIEWS.has(view)) throw new Error('Unknown match view');
  let gameId = 0, suspended = false, leaving = false, restored = false;
  const root = doc.getElementById('match-navigation');
  const status = root?.querySelector('[data-match-status]');
  const message = text => { if (status) { status.textContent = text; status.hidden = !text; } };
  const api = {
    setGame(value) {
      if (!validGame(value)) {
        gameId = 0; suspended = true; restored = false;
        if (root) root.hidden = true;
        return;
      }
      if (gameId !== Number(value)) { suspended = false; restored = false; }
      gameId = Number(value);
      if (root) root.hidden = false;
      for (const link of root?.querySelectorAll('[data-match-view]') || []) {
        const destination = link.dataset.matchView;
        if (!VIEWS.has(destination)) continue;
        link.href = `${FILES[destination]}?gameId=${gameId}&resume=1`;
        if (destination === view) link.setAttribute('aria-current', 'page');
        else link.removeAttribute('aria-current');
      }
    },
    captureState() {
      if (!gameId || suspended || !canCapture()) return true;
      try {
        const active = doc.activeElement;
        store.save(gameId, view, {
          version: 1, gameId, state: capture() ?? null,
          scroll: { left: scope.scrollX || 0, top: scope.scrollY || 0 },
          details: [...doc.querySelectorAll('details[id]')].map(element => ({ id: element.id, open: element.open })),
          focus: active?.id || '',
        });
        return true;
      } catch (_) {
        message('Could not keep this view. Please try switching again.');
        return false;
      }
    },
    async navigate(target) {
      const url = matchViewTarget(target, scope.location.href, gameId);
      if (!url || leaving) return false;
      if (!api.captureState()) return false;
      leaving = true; message('');
      try {
        if (await beforeLeave?.(message) === false) { leaving = false; return false; }
        // Keep edits made while an existing draft write was finishing, too.
        if (!api.captureState()) { leaving = false; return false; }
        scope.location.href = url.href;
        return true;
      } catch (_) {
        leaving = false; message('Could not switch views. Your current view is still open.');
        return false;
      }
    },
    async restoreState() {
      if (!gameId || restored) return false;
      restored = true;
      if (new URLSearchParams(scope.location.search).get('resume') !== '1') return false;
      const snapshot = store.peek(gameId, view);
      if (!snapshot || snapshot.version !== 1 || snapshot.gameId !== gameId) return false;
      try {
        await restore(snapshot.state);
        for (const detail of snapshot.details || []) {
          const element = doc.getElementById(detail.id);
          if (element?.tagName === 'DETAILS') element.open = !!detail.open;
        }
        // A deliberate moment jump takes priority over the previous scroll position.
        if (view !== 'vod' || !hasExplicitVodTarget(scope.location.search)) {
          const focus = snapshot.focus ? doc.getElementById(snapshot.focus) : null;
          if (focus?.getClientRects().length && !focus.closest('[hidden]')) focus.focus({ preventScroll: true });
          scope.scrollTo({ left: Math.max(0, Number(snapshot.scroll?.left) || 0),
            top: Math.max(0, Number(snapshot.scroll?.top) || 0), behavior: 'instant' });
        }
        store.clear(gameId, view);
        return true;
      } catch (_) {
        message('Your previous view could not be restored.');
        return false;
      }
    },
    invalidate() { store.clear(gameId, view); suspended = true; },
    enableCapture() { suspended = false; },
  };
  root?.addEventListener('click', event => {
    const link = event.target.closest?.('[data-match-view]');
    if (!link || event.button > 0 || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
    event.preventDefault();
    if (link.dataset.matchView !== view && gameId) void api.navigate(link.href);
  });
  scope.addEventListener('pagehide', api.captureState);
  return api;
}
