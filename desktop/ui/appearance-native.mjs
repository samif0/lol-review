// Shell only (index.html): mirrors the colors the shell shows into the native window.
// The host paints its window background with them, so a resize, maximize or renderer
// reload never exposes a stale color, and it keeps them for the next launch's splash
// (electron/appearance.mjs). Watching data-frame-page catches the pages whose own
// tokens restyle the shell (Aura's lights-off VOD page). A choice that storage refused
// (UNSAVED_ATTRIBUTE) is sent with persist:false: the window paints it, and the next
// launch still opens in the saved look. A browser preview has no host, so it does
// nothing there.
import { getInvoke } from './platform/index.mjs';
import { THEMES, UNSAVED_ATTRIBUTE } from './appearance.mjs';

// data-mode-pref too: a switch to or from System can keep the colors yet changes the next launch.
export const WATCHED_ATTRIBUTES = Object.freeze(['data-theme', 'data-mode', 'data-mode-pref', 'data-frame-page', UNSAVED_ATTRIBUTE]);
export const MIRROR_DELAY_MS = 150;
const PREFS = Object.freeze(['system', 'dark', 'light']);

const byte = value => Math.max(0, Math.min(255, Math.round(value)));
const hex = channels => '#' + channels.map(value => byte(value).toString(16).padStart(2, '0')).join('');

/**
 * A computed CSS color as #rrggbb: rgb()/rgba() and color(srgb ...) as Chromium serializes
 * them, or a hex value. Null for anything else, and for any color that is not opaque,
 * because the window can only paint a solid color.
 */
export function cssColorToHex(value) {
  const text = typeof value === 'string' ? value.trim().toLowerCase() : '';
  let match = /^#([0-9a-f]{6})$/.exec(text);
  if (match) return text;
  match = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:\s*[,/]\s*([\d.]+%?))?\s*\)$/.exec(text);
  if (match) {
    if (match[4] !== undefined && parseFloat(match[4]) < (match[4].endsWith('%') ? 100 : 1)) return null;
    return hex(match.slice(1, 4).map(Number));
  }
  match = /^color\(srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)(?:\s*\/\s*([\d.]+%?))?\s*\)$/.exec(text);
  if (match) {
    if (match[4] !== undefined && parseFloat(match[4]) < (match[4].endsWith('%') ? 100 : 1)) return null;
    return hex(match.slice(1, 4).map(value => Number(value) * 255));
  }
  return null;
}

/**
 * The theme's own --bg and --t1 in `mode`, read from a hidden probe that carries the theme
 * and mode (the token blocks are element-agnostic), or null when they are not solid colors.
 */
export function readThemeColors(doc, view, theme, mode) {
  if (!THEMES.includes(theme) || typeof doc?.createElement !== 'function' || !doc.body) return null;
  const probe = doc.createElement('i');
  probe.hidden = true;
  probe.setAttribute('aria-hidden', 'true');
  probe.setAttribute('data-theme', theme);
  probe.setAttribute('data-mode', mode);
  try {
    doc.body.appendChild(probe);
    const style = view.getComputedStyle(probe);
    const background = cssColorToHex(style.getPropertyValue('--bg'));
    const text = cssColorToHex(style.getPropertyValue('--t1'));
    return background && text ? { background, text } : null;
  } catch (_) { return null; } finally { try { probe.remove(); } catch (_) {} }
}

/**
 * What the shell shows now: {mode, background, text, pref}, or null if it has no solid colors.
 * A System choice also carries `alt`, the theme's colors in the other mode, so the next launch
 * opens in the right colors when Windows has changed mode since.
 */
export function readShownAppearance(doc, view) {
  try {
    const root = doc.documentElement;
    const body = view.getComputedStyle(doc.body || root);
    const background = cssColorToHex(body.backgroundColor) || cssColorToHex(view.getComputedStyle(root).backgroundColor);
    const text = cssColorToHex(body.color);
    if (!background || !text) return null;
    const pref = root.getAttribute('data-mode-pref');
    const mode = root.getAttribute('data-mode') === 'light' ? 'light' : 'dark';
    const alt = pref === 'system' ? readThemeColors(doc, view, root.getAttribute('data-theme'), mode === 'light' ? 'dark' : 'light') : null;
    return { mode, background, text, ...(PREFS.includes(pref) ? { pref } : {}), ...(alt ? { alt } : {}) };
  } catch (_) { return null; }
}

/** True while the shell shows a choice that storage refused (saveAppearance marks it). */
export function isUnsavedAppearance(doc) {
  try { return doc.documentElement.getAttribute(UNSAVED_ATTRIBUTE) !== null; } catch (_) { return false; }
}

/**
 * True while the framed page restyles the shell for itself (Aura dims the window around a
 * VOD, appearance.css). Those colors are painted but never saved, so the next launch opens
 * in the theme's own colors.
 */
export function isPageTint(doc) {
  try {
    const root = doc.documentElement;
    return root.getAttribute('data-theme') === 'aura' && root.getAttribute('data-frame-page') === 'vodplayer';
  } catch (_) { return false; }
}

/**
 * Sends the shell's colors now and again after each theme, mode or page change, at most
 * once per settled change (debounced) and only when they differ from the last one sent.
 * An unsaved choice or a page tint adds persist:false, so the host paints the window but keeps its file.
 */
export function startAppearanceMirror({ invoke, doc = globalThis.document, view = globalThis.window,
  delay = MIRROR_DELAY_MS, Observer = view?.MutationObserver } = {}) {
  let timer = null, sent = '', stopped = false;
  const send = async () => {
    timer = null;
    if (stopped) return;
    const shown = readShownAppearance(doc, view);
    if (!shown) return;
    const payload = isUnsavedAppearance(doc) || isPageTint(doc) ? { ...shown, persist: false } : shown;
    const key = JSON.stringify(payload);
    if (key === sent) return;
    sent = key;
    try { await invoke('set_window_appearance', { payload }); }
    catch (_) { if (sent === key) sent = ''; } // A closing or failed host gets the next change again.
  };
  const schedule = () => {
    if (stopped) return;
    if (timer !== null) view.clearTimeout(timer);
    timer = view.setTimeout(send, delay);
  };
  const observer = new Observer(schedule);
  observer.observe(doc.documentElement, { attributes: true, attributeFilter: [...WATCHED_ATTRIBUTES] });
  const ready = send();
  return {
    ready,
    stop() {
      stopped = true;
      observer.disconnect();
      if (timer !== null) view.clearTimeout(timer);
      timer = null;
    },
  };
}

async function boot() {
  try { if (window.top !== window) return; } catch (_) { return; }
  let invoke = null;
  try { invoke = await getInvoke(); } catch (_) { return; }
  if (typeof invoke === 'function') startAppearanceMirror({ invoke });
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') void boot();
