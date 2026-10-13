import { APPEARANCE_EVENT, MODES, THEMES, readAppliedAppearance, resolveMode, saveAppearance } from './appearance.mjs';

// Settings card "Look and feel": the theme and the mode. Like the Audio devices card,
// it saves on change to a renderer-local entry (appearance.mjs), so it never joins the
// sidecar-backed dirty state or the leave guard. saveAppearance hands the choice to the
// shell, which applies it and forwards it to this frame at once; the radios and the
// previews then read back what the document shows. Each preview carries its own
// data-theme and the resolved data-mode, so it draws with that theme's tokens.

export const THEME_CHOICES = Object.freeze([
  Object.freeze({ id: 'warm', name: 'Warm', description: 'Warm neutrals with a serif for titles.' }),
  Object.freeze({ id: 'neutral', name: 'Neutral', description: 'Cool greys and one clean sans.' }),
  Object.freeze({ id: 'journal', name: 'Journal', description: 'Warm, with your own notes set in a serif.' }),
  Object.freeze({ id: 'aura', name: 'Aura', description: 'Warm, with a soft light that follows the time of day.' }),
]);
export const MODE_CHOICES = Object.freeze([
  Object.freeze({ id: 'system', name: 'Match Windows' }),
  Object.freeze({ id: 'dark', name: 'Dark' }),
  Object.freeze({ id: 'light', name: 'Light' }),
]);
export const APPEARANCE_SETTINGS_COPY = Object.freeze({
  themeSaved: 'Theme saved.',
  modeSaved: 'Mode saved.',
  notSaved: 'Could not save. Revu uses this look until you close it.',
});
export const STATUS_CLEAR_MS = 2400;

const attempt = (fn, fallback = null) => { try { return fn(); } catch (_) { return fallback; } };
const oneOf = (list, value) => typeof value === 'string' && list.includes(value);

/**
 * Wire the card. Returns null when the page has no #theme-settings card.
 * Options are injectable for tests: document, scope (the window), storage (omit it to
 * use scope.localStorage), save (appearance.mjs saveAppearance), prefersLight,
 * setTimer and clearTimer.
 */
export function initializeAppearanceSettings(options = {}) {
  const {
    document = globalThis.document,
    scope = attempt(() => globalThis.window),
    save = saveAppearance,
    prefersLight = () => attempt(() => !!scope.matchMedia('(prefers-color-scheme: light)').matches, false),
    setTimer = (callback, ms) => scope.setTimeout(callback, ms),
    clearTimer = id => scope.clearTimeout(id),
  } = options;
  const card = attempt(() => document.getElementById('theme-settings'));
  if (!card) return null;
  const status = attempt(() => document.getElementById('appearance-status'));
  const all = selector => attempt(() => [...card.querySelectorAll(selector)], []) || [];
  const themeInputs = all('input[name="appearanceTheme"]');
  const modeInputs = all('input[name="appearanceMode"]');
  const previews = all('.settings-theme-preview');
  // Only an explicit storage overrides saveAppearance's own choice (scope.localStorage).
  const saveOptions = Object.prototype.hasOwnProperty.call(options, 'storage')
    ? { scope, storage: options.storage } : { scope };
  let statusTimer = null, disposed = false;

  function setStatus(text, tone = '', autoClear = false) {
    if (!status) return;
    if (statusTimer !== null) { attempt(() => clearTimer(statusTimer)); statusTimer = null; }
    status.textContent = text || '';
    attempt(() => status.classList.remove('good', 'bad'));
    if (tone) attempt(() => status.classList.add(tone));
    if (text && autoClear) statusTimer = attempt(() => setTimer(() => { statusTimer = null; setStatus(''); }, STATUS_CLEAR_MS));
  }

  /** Check the radios that match what this document shows and give the previews its mode. */
  function sync() {
    if (disposed) return null;
    const shown = readAppliedAppearance(document);
    for (const input of themeInputs) input.checked = input.value === shown.theme;
    for (const input of modeInputs) input.checked = input.value === shown.pref;
    for (const preview of previews) {
      attempt(() => preview.setAttribute('data-mode', shown.mode));
      attempt(() => preview.setAttribute('data-daypart', shown.daypart));
    }
    return shown;
  }

  const checked = (inputs, list) => inputs.find(input => input.checked && oneOf(list, input.value))?.value ?? null;

  // Only when no document took the choice (no prepaint API at all): show it here anyway.
  function applyHere({ theme, mode }) {
    const root = attempt(() => document.documentElement);
    if (!root) return;
    const resolved = resolveMode(mode, { prefersLight: !!prefersLight() });
    attempt(() => {
      root.setAttribute('data-theme', theme);
      root.setAttribute('data-mode', resolved);
      root.setAttribute('data-mode-pref', mode);
      root.style.colorScheme = resolved;
    });
  }

  /** Save the checked theme and mode; kind ('theme' or 'mode') picks the status line. */
  function choose(kind) {
    const shown = readAppliedAppearance(document);
    const next = { theme: checked(themeInputs, THEMES) ?? shown.theme, mode: checked(modeInputs, MODES) ?? shown.pref };
    const result = attempt(() => save(next, saveOptions), { ok: false, applied: false, ...next }) || { ok: false, applied: false };
    if (!result.applied) applyHere(next);
    sync();
    if (result.ok === false) setStatus(APPEARANCE_SETTINGS_COPY.notSaved, 'bad');
    else setStatus(kind === 'mode' ? APPEARANCE_SETTINGS_COPY.modeSaved : APPEARANCE_SETTINGS_COPY.themeSaved, 'good', true);
    return result;
  }

  const onChange = event => {
    const name = event?.target?.name;
    if (name === 'appearanceTheme') choose('theme');
    else if (name === 'appearanceMode') choose('mode');
  };
  // The shell forwards every change (Windows mode, the time of day, another window's
  // save) to this frame, which announces it with revu:appearance.
  const onAppearance = () => { sync(); };
  card.addEventListener('change', onChange);
  attempt(() => scope.addEventListener(APPEARANCE_EVENT, onAppearance));
  sync();

  return {
    sync,
    choose,
    dispose() {
      disposed = true;
      if (statusTimer !== null) { attempt(() => clearTimer(statusTimer)); statusTimer = null; }
      attempt(() => card.removeEventListener('change', onChange));
      attempt(() => scope.removeEventListener(APPEARANCE_EVENT, onAppearance));
    },
  };
}
