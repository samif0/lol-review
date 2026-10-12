// Display-only translations for machine-authored objective metadata. Never use
// this formatter for user titles, prompt answers, criteria, or champion names.
const LABELS = new Map([
  ['PRIMARY', 'Gameplay skill'], ['MENTAL', 'Mindset habit'], ['MINI', 'Short focus drill'],
  ['FOCUS', 'Short focus drill'], ['FOCUS DRILL', 'Short focus drill'],
  ['EXPLORING', 'Exploring'], ['DRILLING', 'Drilling'], ['INGRAINING', 'Ingraining'], ['READY', 'Ready'],
  ['PRE-GAME', 'Pre-game'], ['IN-GAME', 'In-game'], ['POST-GAME', 'Post-game'],
  ['PREGAME', 'Pre-game'], ['INGAME', 'In-game'], ['POSTGAME', 'Post-game'],
  ['PRE', 'Pre-game'], ['IN', 'In-game'], ['POST', 'Post-game'], ['ALL PHASES', 'All phases'],
  ['ACTIVE', 'Active'], ['COMPLETED', 'Completed'],
  ['MASTERED', 'Mastered'], ['MASTERY — NO DATA YET', 'Mastery — no data yet'],
  ['LOCKED', 'Locked'], ['NOT MEASURED YET', 'Not measured yet'],
]);

function displayPart(part) {
  const value = part.trim();
  const known = LABELS.get(value.toUpperCase());
  if (known) return known;
  let match;
  if ((match = value.match(/^(-?\d+)\s+(?:PTS|POINTS?)$/i))) {
    return `${match[1]} ${Number(match[1]) === 1 ? 'point' : 'points'}`;
  }
  if ((match = value.match(/^(\d+(?:\/\d+)?)\s+GAMES?$/i))) {
    return `${match[1]} ${match[1] === '1' ? 'game' : 'games'}`;
  }
  if ((match = value.match(/^(\d+)\s+OF\s+(\d+)\s+GAMES?$/i))) {
    return `${match[1]} of ${match[2]} ${match[2] === '1' ? 'game' : 'games'}`;
  }
  if ((match = value.match(/^HIT\s+(\d+\/\d+)\s+GAMES$/i))) return `Hit ${match[1]} games`;
  if ((match = value.match(/^MASTERY\s+(\d+)%$/i))) return `Mastery ${match[1]}%`;
  if ((match = value.match(/^(\d+)%\s+OVER\s+(\d+)\+\s+GAMES\s*&\s*(\d+)\+\s+DAYS$/i))) {
    return `${match[1]}% over ${match[2]}+ games & ${match[3]}+ days`;
  }
  // Unknown future metadata stays intact instead of guessing its capitalization.
  return value;
}

export function objectiveDisplayText(value) {
  return String(value ?? '').split(/\s*[·•]\s*/).map(displayPart).join(' · ');
}

export function objectiveTypeLabel(objective) {
  if (objective.isMini) return 'Short focus drill';
  if (objective.isMental) return 'Mindset habit';
  return objectiveDisplayText(objective.type);
}

export function objectivePhaseLabel(value) {
  return String(value ?? '').split(/\s*\+\s*/).map(displayPart).join(' + ');
}

// The tracked-events picker for a new objective. Mirrors
// Revu.Core.Models.GameEvent.TrackableTokens.Catalog (a desktop test keeps them equal):
// only events Revu detects on its own sit outside the "Mark these yourself" group.
export const MANUAL_EVENT_GROUP = 'Mark these yourself';
export const TRACKABLE_EVENT_TOKENS = [
  { token: 'KILL', group: 'Combat', label: 'Kill', color: '#28c76f' },
  { token: 'DEATH', group: 'Combat', label: 'Death', color: '#ea5455' },
  { token: 'ASSIST', group: 'Combat', label: 'Assist', color: '#0099ff' },
  { token: 'MULTI_KILL', group: 'Combat', label: 'Multikill', color: '#fbbf24' },
  { token: 'FIRST_BLOOD', group: 'Combat', label: 'First Blood', color: '#ef4444' },
  { token: 'DRAGON', group: 'Map objectives', label: 'Dragon', color: '#c89b3c' },
  { token: 'BARON', group: 'Map objectives', label: 'Baron', color: '#8b5cf6' },
  { token: 'HERALD', group: 'Map objectives', label: 'Herald', color: '#06b6d4' },
  { token: 'TURRET', group: 'Map objectives', label: 'Turret', color: '#f97316' },
  { token: 'INHIBITOR', group: 'Map objectives', label: 'Inhibitor', color: '#ec4899' },
  { token: 'JUNGLE_GANK', group: MANUAL_EVENT_GROUP, label: 'Died to Gank', color: '#d6455e' },
  { token: 'FOG_DEATH', group: MANUAL_EVENT_GROUP, label: 'Died in Fog', color: '#b23a72' },
  { token: 'RECALL', group: MANUAL_EVENT_GROUP, label: 'Recall', color: '#a9c8ff' },
  { token: 'ALL_IN', group: MANUAL_EVENT_GROUP, label: 'All-in', color: '#f87171' },
  { token: 'TRADE', group: MANUAL_EVENT_GROUP, label: 'Trade', color: '#ffb86b' },
  { token: 'SHORT_TRADE', group: MANUAL_EVENT_GROUP, label: 'Short Trade', color: '#ffd9a3' },
  { token: 'EXTENDED_TRADE', group: MANUAL_EVENT_GROUP, label: 'Extended Trade', color: '#ff9248' },
  { token: 'JUNGLE_PROXIMITY', group: MANUAL_EVENT_GROUP, label: 'Jungler Near', color: '#b07cd8' },
  { token: 'ENEMY_JUNGLE_PROXIMITY', group: MANUAL_EVENT_GROUP, label: 'Enemy Jungler Near', color: '#ff7a9e' },
  { token: 'ALLY_JUNGLE_PROXIMITY', group: MANUAL_EVENT_GROUP, label: 'Ally Jungler Near', color: '#6bd6c8' },
  { token: 'TEAMFIGHT', group: MANUAL_EVENT_GROUP, label: 'Teamfight', color: '#f3a3a8' },
  { token: 'OUTNUMBERED_TEAMFIGHT', group: MANUAL_EVENT_GROUP, label: 'Outnumbered Fight', color: '#f26d7d' },
  { token: 'EVEN_TEAMFIGHT', group: MANUAL_EVENT_GROUP, label: 'Even-Numbers Fight', color: '#f3a3a8' },
  { token: 'NUMBERS_UP_TEAMFIGHT', group: MANUAL_EVENT_GROUP, label: 'Numbers-Up Fight', color: '#8ee7ba' },
  { token: 'ABSENT_TEAMFIGHT', group: MANUAL_EVENT_GROUP, label: 'Fight Without You', color: '#9fb0c3' },
];

export function objectiveMetaText(objective) {
  const text = objective.metaText || [objective.levelName, objective.phaseLabel,
    objective.score != null ? `${objective.score} points` : null].filter(Boolean).join(' · ');
  return objectiveDisplayText(text);
}
