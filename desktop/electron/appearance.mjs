// Native mirror of the theme the shell shows (ui/appearance-native.mjs sends it).
// set_window_appearance paints the window background, so a resize, maximize or
// renderer reload never exposes a stale color, and <profile>/appearance.json lets
// the next launch open the splash and the window in it before any page has loaded.
// Revu never sets nativeTheme.themeSource: Electron's default 'system' keeps the
// pages' prefers-color-scheme equal to the Windows app mode, which System mode follows.
import { readFile, rename, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { DEFAULT_MODE } from '../ui/appearance.mjs';

export const APPEARANCE_FILE = 'appearance.json';
const HEX = /^#[0-9a-f]{6}$/i;
const MODES = Object.freeze(['dark', 'light']);
const PREFS = Object.freeze(['system', 'dark', 'light']);
const KEYS = Object.freeze(['mode', 'background', 'text', 'pref', 'alt']);
// The default Warm theme (ui/appearance.css --bg, --t1, --t3), used before Revu has
// seen a page: the first launch, or an appearance.json that cannot be read.
export const DEFAULT_PALETTES = Object.freeze({
  dark: Object.freeze({ mode: 'dark', background: '#21201e', text: '#f3f0ea', muted: '#a29d93' }),
  light: Object.freeze({ mode: 'light', background: '#f8f7f3', text: '#1c1b19', muted: '#69645a' }),
});

/**
 * {mode, background, text, pref?, alt?} with lower-case #rrggbb colors, or a TypeError.
 * `alt` is {background, text}: the same theme in the other mode, so a System choice opens
 * in the right colors after Windows changes mode.
 */
export function validateWindowAppearance(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).some(key => !KEYS.includes(key)))
    throw new TypeError('Window appearance must contain only mode, background, text, pref and alt');
  if (!MODES.includes(value.mode)) throw new TypeError('Window appearance mode must be dark or light');
  for (const key of ['background', 'text']) {
    if (typeof value[key] !== 'string' || !HEX.test(value[key])) throw new TypeError(`Window appearance ${key} must be a #rrggbb color`);
  }
  if (value.pref !== undefined && !PREFS.includes(value.pref)) throw new TypeError('Window appearance pref must be system, dark or light');
  let alt = null;
  if (value.alt !== undefined) {
    const other = value.alt;
    if (!other || typeof other !== 'object' || Array.isArray(other) || Object.keys(other).some(key => key !== 'background' && key !== 'text')
      || ![other.background, other.text].every(color => typeof color === 'string' && HEX.test(color)))
      throw new TypeError('Window appearance alt must be {background, text} in #rrggbb colors');
    alt = { background: other.background.toLowerCase(), text: other.text.toLowerCase() };
  }
  return { mode: value.mode, background: value.background.toLowerCase(), text: value.text.toLowerCase(),
    ...(value.pref ? { pref: value.pref } : {}), ...(alt ? { alt } : {}) };
}

/**
 * A set_window_appearance payload as {appearance, persist}. persist is an optional boolean
 * beside the appearance keys; false (a choice the shell could not save) paints the window
 * but keeps appearance.json, so the next launch opens in the saved look.
 */
export function validateAppearanceReport(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new TypeError('Window appearance must contain only mode, background, text, pref and alt');
  const { persist = true, ...shown } = value;
  if (typeof persist !== 'boolean') throw new TypeError('Window appearance persist must be true or false');
  return { appearance: validateWindowAppearance(shown), persist };
}

/** The last appearance the shell reported, or null when there is none or it is unreadable. */
export async function readWindowAppearance(profile) {
  try {
    const { v, ...saved } = JSON.parse(await readFile(path.join(profile, APPEARANCE_FILE), 'utf8')) || {};
    return v === 1 ? validateWindowAppearance(saved) : null;
  } catch { return null; }
}

const channel = (hex, at) => parseInt(hex.slice(at, at + 2), 16);
/** `weight` of color a over color b, both #rrggbb. */
export function mixHex(a, b, weight) {
  return '#' + [1, 3, 5].map(at => Math.round(channel(a, at) * weight + channel(b, at) * (1 - weight))
    .toString(16).padStart(2, '0')).join('');
}

/** The saved appearance as a palette, with a secondary text color between its two colors. */
export function savedPalette(saved) {
  return { mode: saved.mode, background: saved.background, text: saved.text, muted: mixHex(saved.text, saved.background, 0.62) };
}

/** The saved theme in its other mode (a System choice's `alt`), or that mode's default palette. */
function otherPalette(saved, mode) {
  return saved?.alt ? savedPalette({ mode, ...saved.alt }) : DEFAULT_PALETTES[mode];
}

/**
 * The palette to open in when Windows is in `dark` (or light) app mode: the saved one,
 * unless it follows Windows and Windows has changed mode since, which gives the saved
 * theme in the current Windows mode (its `alt` colors, or the default palette when an
 * older file has none). With nothing saved, `fallback` decides: 'system' follows
 * Windows, otherwise that mode (the app's own default).
 */
export function windowPalette(saved, { dark = true, fallback = DEFAULT_MODE } = {}) {
  const windows = dark ? 'dark' : 'light';
  if (saved && (saved.pref !== 'system' || saved.mode === windows)) return savedPalette(saved);
  if (saved) return otherPalette(saved, windows);
  if (fallback === 'system') return DEFAULT_PALETTES[windows];
  return DEFAULT_PALETTES[fallback] || DEFAULT_PALETTES.dark;
}

/**
 * The splash cannot run script, so it carries both answers and lets its own
 * prefers-color-scheme choose: `base`, plus `light` when the page should follow Windows
 * (a saved System choice, or nothing saved while the app default is System). `light` is
 * null for a fixed mode. With nothing saved it opens in the app default, as the pages do.
 */
export function splashPalettes(saved) {
  if (!saved) return DEFAULT_MODE === 'system' ? { base: DEFAULT_PALETTES.dark, light: DEFAULT_PALETTES.light }
    : { base: DEFAULT_PALETTES[DEFAULT_MODE] || DEFAULT_PALETTES.dark, light: null };
  if (saved.pref !== 'system') return { base: savedPalette(saved), light: null };
  if (saved.mode === 'light') return { base: otherPalette(saved, 'dark'), light: savedPalette(saved) };
  return { base: savedPalette(saved), light: otherPalette(saved, 'light') };
}

/** Applies each reported appearance to the window and keeps the newest saved one on disk. */
export function createAppearanceController({ window, profile, saved = null }) {
  const file = path.join(profile, APPEARANCE_FILE);
  let written = saved ? JSON.stringify(saved) : '', saving = Promise.resolve();
  const persist = async (next, key) => {
    if (key === written) return true;
    const temp = file + '.tmp';
    try {
      await writeFile(temp, JSON.stringify({ v: 1, ...next }, null, 2) + '\n', { encoding: 'utf8', flush: true });
      await rename(temp, file);
      written = key;
      return true;
    } catch {
      await unlink(temp).catch(() => {});
      return false;
    }
  };
  return {
    apply(payload) {
      const { appearance: next, persist: keep } = validateAppearanceReport(payload);
      if (window && !window.isDestroyed()) window.setBackgroundColor(next.background);
      // An unsaved choice lasts for this session only; the file keeps the saved look.
      if (!keep) return Promise.resolve({ ok: true, saved: false });
      const key = JSON.stringify(next);
      const operation = saving.then(() => persist(next, key));
      saving = operation.catch(() => {});
      // A failed save only costs the next launch's first color; the window is already right.
      return operation.then(stored => ({ ok: true, saved: stored }));
    },
  };
}
