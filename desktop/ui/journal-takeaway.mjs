// Journal's Home pull-quote: the takeaway the player wrote in a recent review, and a
// private check-in under it ("Did you do it?"). The answers stay on this device in
// localStorage. They never write the review's focus adherence, which answers a
// different question ("Did you practice what you intended?") about one game.
// Every server string is written with textContent. Nothing here throws on storage.

export const FOLLOWTHROUGH_KEY = 'revu.journal.followthrough.v1';
export const FOLLOWTHROUGH_ANSWERS = Object.freeze(['yes', 'partly', 'notyet']);
export const FOLLOWTHROUGH_LIMIT = 50;
/** At most this many get_review calls when the dashboard carries no lastTakeaway. */
export const TAKEAWAY_REVIEW_LOOKUPS = 3;
export const TAKEAWAY_EMPTY = 'Write a takeaway in your next review.';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const attempt = (fn, fallback = null) => { try { return fn(); } catch (_) { return fallback; } };
const text = value => (typeof value === 'string' ? value.trim() : '');
const defaultStorage = () => attempt(() => globalThis.localStorage);

/** A game id as its storage key, or null for anything that is not a positive safe integer. */
function gameKey(gameId) {
  const value = Number(gameId);
  return Number.isSafeInteger(value) && value > 0 ? String(value) : null;
}

/** Every saved answer as {gameId: answer}. Missing, bad or unreadable storage means none. */
export function readFollowThrough(storage = defaultStorage()) {
  const raw = attempt(() => storage.getItem(FOLLOWTHROUGH_KEY));
  const parsed = typeof raw === 'string' ? attempt(() => JSON.parse(raw)) : null;
  const answers = {};
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return answers;
  for (const [id, answer] of Object.entries(parsed)) {
    const key = gameKey(id);
    if (key && FOLLOWTHROUGH_ANSWERS.includes(answer)) answers[key] = answer;
  }
  return answers;
}

/** The saved answer for one takeaway's game, or null. */
export function followThroughFor(gameId, storage = defaultStorage()) {
  const key = gameKey(gameId);
  return key ? readFollowThrough(storage)[key] ?? null : null;
}

/**
 * Save (or, with any other answer, clear) the check-in for one takeaway's game. Riot game
 * ids grow over time, so the cap drops the oldest games' answers first; the game just
 * answered always stays. Returns {ok, answers}; ok is false when storage refused the write.
 */
export function saveFollowThrough(gameId, answer, storage = defaultStorage()) {
  const key = gameKey(gameId);
  const answers = readFollowThrough(storage);
  if (!key) return { ok: false, answers };
  if (FOLLOWTHROUGH_ANSWERS.includes(answer)) answers[key] = answer;
  else delete answers[key];
  const keys = Object.keys(answers).sort((a, b) => Number(b) - Number(a)).slice(0, FOLLOWTHROUGH_LIMIT);
  if (answers[key] && !keys.includes(key)) keys[keys.length - 1] = key;
  const kept = Object.fromEntries(keys.map(id => [id, answers[id]]));
  let ok = true;
  try { storage.setItem(FOLLOWTHROUGH_KEY, JSON.stringify(kept)); } catch (_) { ok = false; }
  return { ok, answers: kept };
}

/** The takeaway in a review form: "One thing to take into your next game", else the older "focus next". */
export function takeawayText(form) {
  return text(form?.reviewNotes) || text(form?.focusNext);
}

/** A valid takeaway {gameId, championName, enemyChampion, win, datePlayed, timestamp, text}, or null. */
export function normalizeTakeaway(value) {
  if (!value || typeof value !== 'object') return null;
  const key = gameKey(value.gameId);
  const words = text(value.text);
  if (!key || !words) return null;
  const timestamp = Number(value.timestamp);
  return {
    gameId: Number(key),
    championName: text(value.championName),
    enemyChampion: text(value.enemyChampion),
    win: typeof value.win === 'boolean' ? value.win : null,
    datePlayed: text(value.datePlayed),
    timestamp: Number.isFinite(timestamp) && timestamp > 0 ? timestamp : null,
    text: words,
  };
}

/**
 * The latest takeaway. A dashboard that carries lastTakeaway (even null) is the answer.
 * Otherwise read the first history page and open at most `lookups` reviewed, non-remake
 * games, newest first, until one has a takeaway. A review that fails to load is skipped.
 * A history page that reports an error rejects, so the caller can retry later.
 */
export async function findLastTakeaway({ dashboard = null, readSnapshot, lookups = TAKEAWAY_REVIEW_LOOKUPS } = {}) {
  if (dashboard && typeof dashboard === 'object' && Object.prototype.hasOwnProperty.call(dashboard, 'lastTakeaway')) {
    return normalizeTakeaway(dashboard.lastTakeaway);
  }
  const page = await readSnapshot('get_games', 'sample-games.json', { view: 'history', page: 0 });
  // A library the sidecar could not read is not "no takeaway": throw, so the card hides
  // and the next Home render tries again.
  if (text(page?.errorText)) throw new Error(text(page.errorText));
  const rows = (Array.isArray(page?.items) ? page.items : [])
    .filter(row => row && row.hasReview === true && !row.isRemake && gameKey(row.gameId));
  for (const row of rows.slice(0, Math.max(0, lookups))) {
    let review = null;
    try {
      review = await readSnapshot('get_review', 'sample-review.json', { gameId: Number(row.gameId) });
    } catch (err) {
      console.warn('[journal] could not open a review for its takeaway:', err);
      continue;
    }
    const words = takeawayText(review?.subject?.form);
    if (words) return normalizeTakeaway({ ...row, text: words });
  }
  return null;
}

function asDate(today) {
  if (today instanceof Date && !Number.isNaN(today.getTime())) return today;
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(typeof today === 'string' ? today : '');
  return match ? new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3])) : new Date();
}
const dayLabel = date => `${MONTHS[date.getMonth()]} ${String(date.getDate()).padStart(2, '0')}`;
const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

/**
 * 'today' or 'yesterday' for a row's datePlayed ("Jun 14, 12:32", the sidecar's
 * MMM dd format) against the dashboard's today ("2026-06-14"), else ''. A date in
 * another language never matches, so it reads as itself.
 */
export function relativeDay(datePlayed, today = new Date()) {
  const day = text(datePlayed).split(',')[0].trim();
  if (!day) return '';
  const base = asDate(today);
  if (day === dayLabel(base)) return 'today';
  const before = new Date(base.getFullYear(), base.getMonth(), base.getDate() - 1);
  return day === dayLabel(before) ? 'yesterday' : '';
}

/** How the caption reads: {matchup, when, href}. when is ' today', ' yesterday', ' on Jun 14' or ''. */
export function describeTakeaway(takeaway, { today = new Date() } = {}) {
  const t = normalizeTakeaway(takeaway);
  if (!t) return null;
  const matchup = t.championName
    ? (t.enemyChampion ? `${t.championName} vs ${t.enemyChampion}` : t.championName)
    : 'your last game';
  let when = '';
  if (t.timestamp) {
    const played = new Date(t.timestamp < 1e12 ? t.timestamp * 1000 : t.timestamp);
    const base = asDate(today);
    const before = new Date(base.getFullYear(), base.getMonth(), base.getDate() - 1);
    when = sameDay(played, base) ? ' today' : sameDay(played, before) ? ' yesterday' : ` on ${MONTHS[played.getMonth()]} ${played.getDate()}`;
  } else {
    const relative = relativeDay(t.datePlayed, today);
    const day = t.datePlayed.split(',')[0].trim();
    when = relative ? ` ${relative}` : day ? ` on ${day}` : '';
  }
  return { matchup, when, href: `review.html?gameId=${encodeURIComponent(t.gameId)}`, text: t.text };
}

/**
 * The Home figure. `elements` holds figure, caption, link, when, quote, check, empty and
 * answers (the Yes, Partly and Not yet buttons, each with data-answer). `load()` resolves
 * the takeaway (or null); it runs once, until refresh(). Answers live in `storage`.
 */
export function createTakeawayCard({ elements, load, storage = defaultStorage(), today = () => new Date() }) {
  const { figure, caption, link, when, quote, check, empty, answers = [] } = elements;
  let current = null;
  let pending = null;
  let loaded = false;
  const local = {};

  const answerOf = gameId => local[gameId] ?? followThroughFor(gameId, storage);
  function paintAnswers() {
    const chosen = current ? answerOf(current.gameId) : null;
    for (const button of answers) {
      const on = !!chosen && button.dataset.answer === chosen;
      button.setAttribute('aria-pressed', String(on));
      button.classList?.toggle?.('is-on', on);
    }
  }
  function paint(takeaway) {
    const described = describeTakeaway(takeaway, { today: today() });
    current = described ? normalizeTakeaway(takeaway) : null;
    if (figure) figure.hidden = false;
    if (caption) caption.hidden = !described;
    if (quote) quote.hidden = !described;
    if (check) check.hidden = !described;
    if (empty) { empty.hidden = !!described; empty.textContent = TAKEAWAY_EMPTY; }
    if (described) {
      if (link) { link.textContent = described.matchup; link.setAttribute('href', described.href); }
      if (when) when.textContent = described.when;
      if (quote) quote.textContent = described.text;
    }
    paintAnswers();
  }

  function answer(value) {
    if (!current) return null;
    const next = answerOf(current.gameId) === value ? null : value;
    local[current.gameId] = next;
    const result = saveFollowThrough(current.gameId, next, storage);
    if (!result.ok) console.warn('[journal] this answer is kept until Revu closes; storage refused it.');
    paintAnswers();
    return next;
  }
  for (const button of answers) button.addEventListener?.('click', () => answer(button.dataset.answer));

  async function ensure() {
    if (loaded) return current;
    pending ??= (async () => {
      try {
        paint(await load());
        loaded = true;
      } catch (err) {
        if (figure) figure.hidden = true;
        console.warn('[journal] could not load the last takeaway:', err);
      } finally {
        pending = null;
      }
      return current;
    })();
    return pending;
  }

  return {
    ensure,
    refresh() { loaded = false; return ensure(); },
    answer,
    get takeaway() { return current; },
  };
}

/**
 * Wire the figure in `doc` and load it only while Journal is the theme: at once, or when
 * the player switches to Journal (the revu:appearance event). update(dashboard) hands
 * in each new Home snapshot; a snapshot with lastTakeaway repaints from it.
 */
export function mountJournalTakeaway(doc = globalThis.document, { readSnapshot, storage = defaultStorage(), win = globalThis.window } = {}) {
  const byId = id => doc?.getElementById?.(id) || null;
  const figure = byId('home-last');
  if (!figure) return null;
  let dashboard = null;
  const card = createTakeawayCard({
    elements: {
      figure, caption: byId('home-last-cap'), link: byId('home-last-link'), when: byId('home-last-when'),
      quote: byId('home-last-quote'), check: byId('home-check'), empty: byId('home-last-empty'),
      answers: [...(figure.querySelectorAll?.('[data-answer]') || [])],
    },
    storage,
    today: () => dashboard?.today || new Date(),
    load: () => findLastTakeaway({ dashboard, readSnapshot }),
  });
  const isJournal = () => attempt(() => doc.documentElement.getAttribute('data-theme')) === 'journal';
  attempt(() => win.addEventListener('revu:appearance', event => {
    if (event?.detail?.theme === 'journal') void card.ensure();
  }));
  return {
    card,
    update(next) {
      const carries = !!next && Object.prototype.hasOwnProperty.call(next, 'lastTakeaway');
      dashboard = next || null;
      if (!isJournal()) return;
      if (carries) void card.refresh();
      else void card.ensure();
    },
  };
}
