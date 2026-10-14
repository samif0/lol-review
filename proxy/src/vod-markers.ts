/**
 * Markers document v1 for a full-game (VOD) share: validation and
 * normalization. Pure: no I/O, so the sidecar contract can be tested directly.
 *
 * The sidecar builds the document, this validates it, the site renders it.
 * Times (`t`, `end`) are GAME seconds; the watch page maps them to video time
 * as max(0, t - gameTimeAtVideoStart).
 *
 * Rules: a bad `version`, `gameTimeAtVideoStart`, marker `t`/`kind`, note
 * `t`/`kind`, or a list that is not an array (or too long) is a 400. Invalid
 * optional fields (game fields, tone, end, label) are dropped instead. Unknown
 * keys are dropped everywhere. Free text has control characters stripped, is
 * trimmed and cut to its limit.
 */

import {
  VOD_LABEL_MAX_CHARS,
  VOD_MARKERS_MAX_BYTES,
  VOD_MAX_MARKERS,
  VOD_MAX_NOTES,
  VOD_NOTE_MAX_CHARS,
  VOD_NOTES_MAX_BYTES,
} from "./share-limits";

export interface VodGameMeta {
  champion?: string;
  opponentChampion?: string;
  role?: string;
  result?: "win" | "loss" | "remake";
  durationS?: number;
  playedAt?: string;
}

export interface VodMarker {
  t: number;
  kind: string;
  label: string;
  tone?: "win" | "loss" | "gold" | "neutral";
}

export interface VodNote {
  t: number;
  end?: number;
  kind: "clip" | "bookmark";
  text: string;
}

export interface VodMetaDoc {
  version: 1;
  gameTimeAtVideoStart: number;
  game: VodGameMeta;
  markers: VodMarker[];
  notes: VodNote[] | null;
}

export type VodMetaResult =
  | { ok: true; doc: VodMetaDoc; hasNotes: boolean }
  | { ok: false; status: 400 | 413; error: "bad_markers" | "markers_too_large" | "notes_too_large"; message: string };

const MAX_GAME_SECONDS = 10800;
const MIN_VIDEO_START = -600;
const MAX_VIDEO_START = 7200;
const NAME_MAX_CHARS = 40;
const KIND_PATTERN = /^[A-Z0-9_]{1,32}$/;
const ROLE_PATTERN = /^[A-Za-z]{1,16}$/;
const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;
const RESULTS = new Set(["win", "loss", "remake"]);
const TONES = new Set(["win", "loss", "gold", "neutral"]);
const NOTE_KINDS = new Set(["clip", "bookmark"]);
// C0, DEL and C1 control characters.
// eslint-disable-next-line no-control-regex
const CONTROL_CHARS = /[\u0000-\u001f\u007f-\u009f]/g;

function isObject(v: unknown): v is Record<string, unknown> {
  return !!v && typeof v === "object" && !Array.isArray(v);
}

function isFiniteNumber(v: unknown): v is number {
  return typeof v === "number" && Number.isFinite(v);
}

/** Strip control characters, trim, cut to `max` code points (never splitting a pair), trim again. */
export function cleanText(raw: string, max: number): string {
  const stripped = raw.replace(CONTROL_CHARS, "").trim();
  const chars = Array.from(stripped);
  return chars.length > max ? chars.slice(0, max).join("").trim() : stripped;
}

function utf8Bytes(s: string): number {
  return new TextEncoder().encode(s).byteLength;
}

const roundTenth = (n: number) => Math.round(n * 10) / 10;
const roundHundredth = (n: number) => Math.round(n * 100) / 100;

function bad(message: string): VodMetaResult {
  return { ok: false, status: 400, error: "bad_markers", message };
}

function normalizeGame(raw: unknown): VodGameMeta | null {
  if (raw === undefined || raw === null) return {};
  if (!isObject(raw)) return null;
  const game: VodGameMeta = {};
  for (const key of ["champion", "opponentChampion"] as const) {
    const v = raw[key];
    if (typeof v !== "string") continue;
    const name = cleanText(v, NAME_MAX_CHARS);
    if (name) game[key] = name;
  }
  if (typeof raw.role === "string" && ROLE_PATTERN.test(raw.role)) game.role = raw.role.toUpperCase();
  if (typeof raw.result === "string" && RESULTS.has(raw.result)) game.result = raw.result as VodGameMeta["result"];
  if (typeof raw.durationS === "number" && Number.isInteger(raw.durationS) && raw.durationS >= 0 && raw.durationS <= MAX_GAME_SECONDS) {
    game.durationS = raw.durationS;
  }
  if (typeof raw.playedAt === "string" && DATE_PATTERN.test(raw.playedAt)) game.playedAt = raw.playedAt;
  return game;
}

function gameTime(v: unknown): number | null {
  if (!isFiniteNumber(v) || v < 0 || v > MAX_GAME_SECONDS) return null;
  return roundTenth(v);
}

function normalizeMarkers(raw: unknown): VodMarker[] | string {
  if (raw === undefined || raw === null) return [];
  if (!Array.isArray(raw)) return "markers must be an array";
  if (raw.length > VOD_MAX_MARKERS) return `markers may hold at most ${VOD_MAX_MARKERS} entries`;
  const out: VodMarker[] = [];
  for (let i = 0; i < raw.length; i++) {
    const m = raw[i];
    if (!isObject(m)) return `markers[${i}] must be an object`;
    const t = gameTime(m.t);
    if (t === null) return `markers[${i}].t must be a number from 0 to ${MAX_GAME_SECONDS}`;
    if (typeof m.kind !== "string" || !KIND_PATTERN.test(m.kind)) return `markers[${i}].kind must match [A-Z0-9_]{1,32}`;
    const label = typeof m.label === "string" ? cleanText(m.label, VOD_LABEL_MAX_CHARS) : "";
    const marker: VodMarker = { t, kind: m.kind, label: label || m.kind };
    if (typeof m.tone === "string" && TONES.has(m.tone)) marker.tone = m.tone as VodMarker["tone"];
    out.push(marker);
  }
  // Array.prototype.sort is stable, so equal times keep the sender's order.
  return out.sort((a, b) => a.t - b.t);
}

function normalizeNotes(raw: unknown): VodNote[] | null | string {
  if (raw === undefined || raw === null) return null;
  if (!Array.isArray(raw)) return "notes must be null or an array";
  if (raw.length > VOD_MAX_NOTES) return `notes may hold at most ${VOD_MAX_NOTES} entries`;
  const out: VodNote[] = [];
  for (let i = 0; i < raw.length; i++) {
    const n = raw[i];
    if (!isObject(n)) return `notes[${i}] must be an object`;
    const t = gameTime(n.t);
    if (t === null) return `notes[${i}].t must be a number from 0 to ${MAX_GAME_SECONDS}`;
    if (typeof n.kind !== "string" || !NOTE_KINDS.has(n.kind)) return `notes[${i}].kind must be clip or bookmark`;
    const note: VodNote = { t, kind: n.kind as VodNote["kind"], text: "" };
    if (isFiniteNumber(n.end)) {
      const end = roundTenth(n.end);
      if (end >= t && end <= MAX_GAME_SECONDS) note.end = end;
    }
    note.text = typeof n.text === "string" ? cleanText(n.text, VOD_NOTE_MAX_CHARS) : "";
    // Fixed key order in the stored JSON: t, end, kind, text.
    out.push(note.end === undefined ? { t, kind: note.kind, text: note.text } : { t, end: note.end, kind: note.kind, text: note.text });
  }
  return out.sort((a, b) => a.t - b.t);
}

/** Validate and normalize a markers document body (already read as text). */
export function normalizeVodMeta(text: string): VodMetaResult {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return bad("body must be JSON");
  }
  if (!isObject(raw)) return bad("body must be a JSON object");
  if (raw.version !== 1) return bad("version must be 1");

  const start = raw.gameTimeAtVideoStart;
  if (!isFiniteNumber(start) || start < MIN_VIDEO_START || start > MAX_VIDEO_START) {
    return bad(`gameTimeAtVideoStart must be a number from ${MIN_VIDEO_START} to ${MAX_VIDEO_START}`);
  }

  const game = normalizeGame(raw.game);
  if (game === null) return bad("game must be an object");

  const markers = normalizeMarkers(raw.markers);
  if (typeof markers === "string") return bad(markers);
  if (utf8Bytes(JSON.stringify(markers)) > VOD_MARKERS_MAX_BYTES) {
    return { ok: false, status: 413, error: "markers_too_large", message: `markers exceed ${VOD_MARKERS_MAX_BYTES} bytes` };
  }

  const notes = normalizeNotes(raw.notes);
  if (typeof notes === "string") return bad(notes);
  if (notes !== null && utf8Bytes(JSON.stringify(notes)) > VOD_NOTES_MAX_BYTES) {
    return { ok: false, status: 413, error: "notes_too_large", message: `notes exceed ${VOD_NOTES_MAX_BYTES} bytes` };
  }

  const doc: VodMetaDoc = {
    version: 1,
    gameTimeAtVideoStart: roundHundredth(start) + 0, // + 0 turns -0 into 0
    game,
    markers,
    notes,
  };
  return { ok: true, doc, hasNotes: Array.isArray(notes) && notes.length > 0 };
}

/** Public name for a VOD share from its game meta (at most 120 chars), or null. */
export function vodTitle(game: VodGameMeta): string | null {
  let title: string | null = null;
  if (game.champion && game.opponentChampion) title = `${game.champion} vs ${game.opponentChampion}`;
  else if (game.champion) title = `${game.champion} game`;
  return title && title.length > 120 ? title.slice(0, 120) : title;
}
