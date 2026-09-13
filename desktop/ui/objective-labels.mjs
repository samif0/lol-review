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

export function objectiveMetaText(objective) {
  const text = objective.metaText || [objective.levelName, objective.phaseLabel,
    objective.score != null ? `${objective.score} points` : null].filter(Boolean).join(' · ');
  return objectiveDisplayText(text);
}
