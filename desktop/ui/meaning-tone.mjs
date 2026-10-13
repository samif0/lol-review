// Meaning colors without hex. Pages write a tone name into data-tone (or a result into
// data-result) and appearance.css maps it to the active theme's token, so a win, a
// death or a pending item reads the same way in every theme and mode. The sidecar
// still sends legacy hex values; toneFromHex maps the known ones to a tone.
// Pure functions, no DOM. Unknown input always means 'neutral'.

/** Every tone appearance.css defines a --tone for. */
export const TONES = Object.freeze(['ok', 'win', 'loss', 'danger', 'pending', 'gold', 'calm', 'neutral', 'muted',
  'clip', 'note', 'kill', 'assist', 'death', 'dragon', 'void', 'structure', 'fight', 'recall', 'summoner', 'trade',
  'away', 'proximity']);

const HEX_TONES = Object.freeze({
  '#8ee7ba': 'ok', '#7ec9a0': 'ok', '#9cddba': 'ok',
  '#f3a3a8': 'loss', '#ff7a9e': 'loss',
  '#d6455e': 'death', '#b23a72': 'death',
  '#f26d7d': 'fight', '#f87171': 'fight',
  '#f3c794': 'pending', '#c9956a': 'pending',
  '#d4a44e': 'gold', '#e8c15e': 'gold',
  '#5ec4d4': 'calm', '#7cc7ff': 'calm',
  '#7fd4ff': 'summoner',
  '#a9c8ff': 'recall',
  '#ffb86b': 'trade', '#ffd9a3': 'trade', '#ff9248': 'trade',
  '#b07cd8': 'proximity',
  '#6bd6c8': 'win',
  '#d38c90': 'danger',
  '#9d8bff': 'neutral', '#a79ec2': 'neutral', '#8a80a8': 'neutral', '#aaa7b5': 'neutral', '#9fb0c3': 'neutral',
  '#9ca3af': 'neutral', '#7b8494': 'neutral', '#6b7280': 'neutral', '#7fe0d4': 'neutral',
});

// Event types and trackable tokens (src/Revu.Core/Models/GameEvent.cs, VodSnapshotBuilder.cs).
const TYPE_TONES = Object.freeze({
  KILL: 'kill', MULTI_KILL: 'kill',
  ASSIST: 'assist',
  DEATH: 'death', FIRST_BLOOD: 'death', JUNGLE_GANK: 'death', FOG_DEATH: 'death',
  DRAGON: 'dragon',
  BARON: 'void', HERALD: 'void',
  TURRET: 'structure', INHIBITOR: 'structure', LEVEL_UP: 'structure', UNCERTAIN_COMBAT: 'structure', UNCERTAIN: 'structure',
  FLASH: 'summoner', SUMMONER_SPELL: 'summoner', SUMMONER: 'summoner',
  RECALL: 'recall',
  TRADE: 'trade', SHORT_TRADE: 'trade', EXTENDED_TRADE: 'trade',
  ALL_IN: 'fight', TEAMFIGHT: 'fight', OUTNUMBERED_TEAMFIGHT: 'fight', EVEN_TEAMFIGHT: 'fight', NUMBERS_UP_TEAMFIGHT: 'fight',
  ABSENT_TEAMFIGHT: 'away',
  JUNGLE_PROXIMITY: 'proximity', ENEMY_JUNGLE_PROXIMITY: 'loss', ALLY_JUNGLE_PROXIMITY: 'win',
});

// The sidecar's marker buckets (VodSnapshotBuilder.BucketEvent, VodTeamfightMapper).
const KIND_TONES = Object.freeze({
  win: 'kill', loss: 'death', gold: 'gold', summoner: 'summoner', recall: 'recall', trade: 'trade',
  'all-in': 'fight', teamfight: 'fight', 'teamfight-away': 'away',
});

const POLARITY_TONES = Object.freeze({ good: 'ok', positive: 'ok', bad: 'loss', negative: 'loss' });

const own = (map, key) => (Object.prototype.hasOwnProperty.call(map, key) ? map[key] : null);
const upper = value => (typeof value === 'string' ? value.trim().toUpperCase() : '');
const lower = value => (typeof value === 'string' ? value.trim().toLowerCase() : '');

/** The tone for a legacy hex color (case-insensitive); unknown colors are 'neutral'. */
export function toneFromHex(hex) {
  return own(HEX_TONES, lower(hex)) || 'neutral';
}

/** The tone for an objective's trackable token (KILL, JUNGLE_GANK, SPELL_FLASH, ...). */
export function tokenTone(token) {
  const type = upper(token);
  if (type.startsWith('SPELL_')) return 'summoner';
  return own(TYPE_TONES, type) || 'neutral';
}

/**
 * The tone for one VOD timeline event. The event type decides first, then the
 * refinements the sidecar carries (jungle gank, whose jungler, a fight without you),
 * then the marker kind, then the legacy hex. An unknown type is a plain map marker.
 */
export function eventTone({ eventType, kind, shortLabel, teamfight, colorHex } = {}) {
  const type = upper(eventType), code = upper(shortLabel), bucket = lower(kind);
  if (type === 'JUNGLE_PROXIMITY') {
    if (code === 'EJX') return 'loss';
    if (code === 'AJX') return 'win';
    return 'proximity';
  }
  if (type === 'TEAMFIGHT' || bucket === 'teamfight' || bucket === 'teamfight-away') {
    const away = bucket === 'teamfight-away' || lower(teamfight?.self) === 'away';
    return away ? 'away' : 'fight';
  }
  if (code === 'GNK') return 'death';
  const fromType = type.startsWith('SPELL_') ? 'summoner' : own(TYPE_TONES, type);
  if (fromType) return fromType;
  const fromKind = own(KIND_TONES, bucket);
  if (fromKind) return fromKind;
  const fromHex = own(HEX_TONES, lower(colorHex));
  if (fromHex) return fromHex;
  return type ? 'structure' : 'neutral';
}

/** Evidence or moment polarity (good, bad, neutral) as 'ok', 'loss' or 'neutral'. */
export function polarityTone(polarity) {
  return own(POLARITY_TONES, lower(polarity)) || 'neutral';
}

/** A game's result for data-result: 'remake' wins over the score, else 'win' or 'loss'. */
export function resultOf({ win, isRemake } = {}) {
  if (isRemake === true || isRemake === 1 || isRemake === 'true') return 'remake';
  return win === true || win === 1 || win === 'true' ? 'win' : 'loss';
}
