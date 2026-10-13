// Appearance: the theme (Warm, Neutral, Journal, Aura) and the mode (follow Windows,
// dark or light) for every page. One renderer-local localStorage entry holds the
// choice, so nav-prepaint.js can apply it synchronously before the first paint of the
// shell and of every framed page; the sidecar config is untouched. nav-prepaint.js is
// a classic script and cannot import this module, so it repeats these rules, and
// tests/appearance.test.mjs keeps the two in agreement. Nothing here throws.
//
// <html> carries the result: data-theme, data-mode (always the resolved dark or light),
// data-mode-pref (the saved choice), data-page and data-daypart (Aura's time of day).

export const APPEARANCE_KEY = 'revu.appearance';
export const APPEARANCE_VERSION = 1;
export const APPEARANCE_EVENT = 'revu:appearance';
/**
 * On the shell's <html> while it shows a choice that storage refused. The native mirror
 * (appearance-native.mjs) then paints the window but keeps appearance.json, so the next
 * launch opens in the saved look, as the Settings copy promises.
 */
export const UNSAVED_ATTRIBUTE = 'data-appearance-unsaved';
export const THEMES = Object.freeze(['warm', 'neutral', 'journal', 'aura']);
export const MODES = Object.freeze(['system', 'dark', 'light']);
export const DAYPARTS = Object.freeze(['morning', 'day', 'evening', 'night']);
// Changing the default mode to 'system' is a one-line change here and in nav-prepaint.js.
export const DEFAULT_THEME = 'warm';
export const DEFAULT_MODE = 'dark';
/** Faces preloaded before first paint: the active theme's body and display faces. */
export const FONT_PRELOADS = Object.freeze({
  warm: Object.freeze(['instrument-sans', 'source-serif-4-roman']),
  neutral: Object.freeze(['geist']),
  journal: Object.freeze(['instrument-sans', 'source-serif-4-roman']),
  aura: Object.freeze(['instrument-sans', 'source-serif-4-roman']),
});

const attempt = (fn, fallback = null) => { try { return fn(); } catch (_) { return fallback; } };
const oneOf = (list, value) => typeof value === 'string' && list.includes(value);

/** A valid {theme, mode} from any value; each unknown field falls back to its default. */
export function normalizeAppearance(value) {
  const source = value && typeof value === 'object' ? value : {};
  return {
    theme: oneOf(THEMES, source.theme) ? source.theme : DEFAULT_THEME,
    mode: oneOf(MODES, source.mode) ? source.mode : DEFAULT_MODE,
  };
}

/** The stored JSON text (or a parsed object) as {theme, mode}. Bad JSON means the defaults. */
export function parseAppearance(raw) {
  const value = typeof raw === 'string' ? attempt(() => JSON.parse(raw)) : raw;
  return normalizeAppearance(value);
}

/** The saved choice. A missing key, bad JSON or a storage that throws means the defaults. */
export function readAppearance(storage = attempt(() => globalThis.localStorage)) {
  return parseAppearance(attempt(() => storage.getItem(APPEARANCE_KEY)));
}

/**
 * A preview override, `appearance=<theme>-<mode>` in a query string or a shell deep-link
 * hash (index.html#dashboard.html?appearance=aura-light). Never stored. Null if absent or invalid.
 */
export function appearanceOverride(query) {
  const match = /[?&]appearance=([a-z]+)-([a-z]+)(?=[&#]|$)/i.exec(typeof query === 'string' ? query : '');
  if (!match) return null;
  const theme = match[1].toLowerCase(), mode = match[2].toLowerCase();
  return oneOf(THEMES, theme) && oneOf(MODES, mode) ? { theme, mode } : null;
}

/** data-page: the file name without .html; '/' or '' (the shell under clean URLs) is 'index'. */
export function pageFromPath(pathname) {
  const file = String(pathname ?? '').split('/').pop().toLowerCase().replace(/\.html$/, '');
  return file || 'index';
}

/**
 * The resolved 'dark' or 'light' for a saved mode. System follows the shell's resolved
 * mode when a frame can read it (the shell is the one authority), else the OS setting.
 */
export function resolveMode(pref, { prefersLight = false, topMode = '' } = {}) {
  const mode = oneOf(MODES, pref) ? pref : DEFAULT_MODE;
  if (mode !== 'system') return mode;
  if (topMode === 'dark' || topMode === 'light') return topMode;
  return prefersLight ? 'light' : 'dark';
}

/** Aura's time of day: morning 05 to 11, day 11 to 17, evening 17 to 21, night 21 to 05. */
export function daypartFor(hour) {
  const value = Number(hour);
  if (!Number.isFinite(value)) return 'evening';
  const at = ((Math.floor(value) % 24) + 24) % 24;
  if (at >= 5 && at < 11) return 'morning';
  if (at >= 11 && at < 17) return 'day';
  if (at >= 17 && at < 21) return 'evening';
  return 'night';
}

/** What a document currently shows, read back from its <html> attributes. */
export function readAppliedAppearance(doc = attempt(() => globalThis.document)) {
  const root = attempt(() => doc.documentElement);
  const get = name => attempt(() => root.getAttribute(name));
  const theme = get('data-theme'), mode = get('data-mode'), pref = get('data-mode-pref'), daypart = get('data-daypart');
  return {
    theme: oneOf(THEMES, theme) ? theme : DEFAULT_THEME,
    mode: mode === 'light' ? 'light' : 'dark',
    pref: oneOf(MODES, pref) ? pref : DEFAULT_MODE,
    daypart: oneOf(DAYPARTS, daypart) ? daypart : daypartFor(new Date().getHours()),
    page: pageFromPath(attempt(() => doc.location.pathname, '')),
  };
}

function applyIn(win, value, saved) {
  try {
    if (typeof win?.__revuAppearance?.reapply !== 'function') return false;
    // Mark first, so the native mirror reads the marker together with the new colors.
    attempt(() => {
      const root = win.document.documentElement;
      if (saved) root.removeAttribute(UNSAVED_ATTRIBUTE);
      else root.setAttribute(UNSAVED_ATTRIBUTE, '');
    });
    win.__revuAppearance.reapply({ ...value });
    return true;
  } catch (_) { return false; }
}

/**
 * Save a choice and apply it at once. The shell applies it and forwards it to the
 * frame; the storage event never reaches the writing document, and the look still
 * changes for this session when storage refuses the write. The document that applies
 * it carries UNSAVED_ATTRIBUTE exactly while it shows a choice that was not saved.
 * Returns {ok, applied, theme, mode}; ok is false when the choice could not be saved.
 */
export function saveAppearance(next, options = {}) {
  const has = name => Object.prototype.hasOwnProperty.call(options, name);
  const scope = has('scope') ? options.scope : attempt(() => globalThis.window);
  const storage = has('storage') ? options.storage : attempt(() => scope?.localStorage ?? globalThis.localStorage);
  const value = normalizeAppearance(next);
  let ok = true;
  try { storage.setItem(APPEARANCE_KEY, JSON.stringify({ v: APPEARANCE_VERSION, ...value })); }
  catch (_) { ok = false; }
  const applied = applyIn(attempt(() => scope?.top), value, ok) || applyIn(scope, value, ok);
  return { ok, applied, ...value };
}
