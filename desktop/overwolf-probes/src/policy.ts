import path from 'node:path';
import { PRESETS, type RecordingPreset } from './recording-policy.js';

export const LEAGUE_ID = 5426;
export const LEAGUE_EXE = 'League of Legends.exe';
export const FEATURES = ['matchState', 'counters', 'damage', 'abilities', 'announcer'] as const;
// Deliberately narrow initial scope. All other/unknown queues remain unobserved.
export const QUEUES = new Set([0, 400, 420, 430, 440]);
export const MAX_ROWS = 20_000;
export const MAX_JOURNAL_BYTES = 4 * 1024 * 1024;

export function isLeague(game: { id: number; type: string; processInfo?: { pid?: number; fullPath: string } }): boolean {
  return game.id === LEAGUE_ID && game.type === 'Game' &&
    Number.isSafeInteger(game.processInfo?.pid) && game.processInfo!.pid! > 0 &&
    path.win32.basename(game.processInfo!.fullPath).toLowerCase() === LEAGUE_EXE.toLowerCase();
}

export function dimensions(width: number, height: number, preset: RecordingPreset) {
  if (![width, height].every(n => Number.isInteger(n) && n >= 64 && n <= 7680)) throw new Error('invalid-source-dimensions');
  if (!Object.hasOwn(PRESETS, preset)) throw new Error('invalid-capture-preset');
  const { width: maxWidth, height: maxHeight, fps } = PRESETS[preset];
  const scale = Math.min(1, maxWidth / width, maxHeight / height);
  return { baseWidth: width, baseHeight: height, outputWidth: Math.floor(width * scale / 2) * 2,
    outputHeight: Math.floor(height * scale / 2) * 2, fps };
}

type Payload = { gameId?: unknown; feature?: unknown; category?: unknown; key?: unknown; value?: unknown };
export type Observation = { feature: string; key: string; value: number | boolean | string; receiptMs: number; gameClock: number | null; clockAgeMs: number | null };
const numeric = (v: unknown): number | undefined => {
  if (typeof v !== 'number' && !(typeof v === 'string' && /^\d+(\.\d+)?$/.test(v) && v.length < 24)) return undefined;
  const n = Number(v);
  return Number.isFinite(n) && n >= 0 && n < 1e12 ? n : undefined;
};
const damage = new Set(['total_damage_dealt', 'total_damage_dealt_to_champions', 'total_damage_taken',
  'physical_damage_dealt_player', 'magic_damage_dealt_player', 'true_damage_dealt_player',
  'physical_damage_dealt_to_champions', 'magic_damage_dealt_to_champions', 'true_damage_dealt_to_champions',
  'physical_damage_taken', 'magic_damage_taken', 'true_damage_taken']);

/** Discards arbitrary fields before serialization. Match identity is used only in memory to detect a reset. */
export class ObservationFilter {
  queue: number | undefined;
  clock: number | null = null;
  dropped = 0;
  revision = 0;
  resetReason = 'initial';
  private clockReceipt: number | null = null;
  private lastReceipt = -1;
  private matchId: string | undefined;
  reset(reason = 'lifecycle') {
    this.queue = undefined; this.clock = null; this.clockReceipt = null;
    this.lastReceipt = -1; this.matchId = undefined; this.resetReason = reason; this.revision++;
  }
  accept(payload: unknown, receiptMs: number, source?: 'info' | 'event'): Observation | undefined {
    if (!Number.isFinite(receiptMs) || receiptMs < 0) return this.reject();
    if (receiptMs < this.lastReceipt) { this.reset('receipt-reordered'); return this.reject(); }
    this.lastReceipt = receiptMs;
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) return this.reject();
    const p = payload as Payload;
    if (p.gameId !== LEAGUE_ID || typeof p.feature !== 'string' || typeof p.key !== 'string' ||
        !FEATURES.includes(p.feature as typeof FEATURES[number])) return this.reject();
    // Electron's info/event channels have different contracts. In particular,
    // a launcher queue or an event masquerading as queueId must not unlock data.
    if (source === 'info' && !((p.feature === 'matchState' && p.category === 'game_info') ||
        (p.feature === 'damage' && p.category === 'damage' && p.key.startsWith('total_')))) return this.reject();
    if (source === 'event' && (p.category !== undefined || (p.feature === 'matchState' && p.key !== 'matchStart') ||
        (p.feature === 'damage' && p.key.startsWith('total_')))) return this.reject();
    if (p.feature === 'matchState' && p.key === 'matchId') {
      const id = typeof p.value === 'string' && /^\d{1,24}$/.test(p.value) ? p.value :
        typeof p.value === 'number' && Number.isSafeInteger(p.value) && p.value >= 0 ? String(p.value) : undefined;
      if (id === undefined || (this.matchId !== undefined && this.matchId !== id)) this.reset('match-changed');
      this.matchId = id;
      return this.reject();
    }
    if (p.feature === 'matchState' && p.key === 'matchStarted') {
      // Persistent info may reset to null/sentinel values between matches.
      if (p.value !== true && p.value !== 'true') this.reset('match-reset');
      return this.reject();
    }
    if (p.feature === 'matchState' && p.key === 'queueId') {
      const queue = numeric(p.value);
      if (queue === undefined || !QUEUES.has(queue)) { this.reset('queue-unavailable'); return this.reject(); }
      if (this.queue !== undefined && this.queue !== queue) this.reset('queue-changed');
      this.queue = queue;
      return this.observation(p.feature, p.key, queue, receiptMs);
    }
    if (this.queue === undefined) return this.reject();
    let value: Observation['value'] | undefined;
    if (p.feature === 'damage' && damage.has(p.key)) value = numeric(p.value);
    if (p.feature === 'counters' && p.key === 'match_clock') {
      value = numeric(p.value);
      if (value === undefined || (this.clock !== null && value < this.clock)) {
        this.reset('clock-reset'); return this.reject();
      }
      // Duplicate ticks do not make an old clock sample appear fresh.
      if (value !== this.clock) { this.clock = value; this.clockReceipt = receiptMs; }
    }
    if (p.feature === 'abilities' && p.key === 'usedAbility' && p.value && typeof p.value === 'object') {
      const slot = numeric((p.value as { type?: unknown }).type);
      if (slot !== undefined && Number.isInteger(slot) && slot >= 1 && slot <= 4) value = slot;
    }
    if (p.feature === 'announcer' && (p.key === 'victory' || p.key === 'defeat') && p.value === null) value = true;
    if (value === undefined) return this.reject();
    const observation = this.observation(p.feature, p.key, value, receiptMs);
    if (p.feature === 'announcer') this.reset('match-ended');
    return observation;
  }
  private observation(feature: string, key: string, value: Observation['value'], receiptMs: number): Observation {
    return { feature, key, value, receiptMs, gameClock: this.clock,
      clockAgeMs: this.clockReceipt === null ? null : receiptMs - this.clockReceipt };
  }
  private reject(): undefined { this.dropped++; return undefined; }
}

export function inside(root: string, candidate: string): boolean {
  const rel = path.relative(path.resolve(root), path.resolve(candidate));
  return rel !== '' && !path.isAbsolute(rel) && rel !== '..' && !rel.startsWith(`..${path.sep}`);
}
