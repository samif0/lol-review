export const PRACTICE_STAGES = [
  { name: 'Exploring', at: 0 },
  { name: 'Drilling', at: 15 },
  { name: 'Ingraining', at: 30 },
  { name: 'Ready', at: 50 },
];

const count = value => typeof value === 'number' && Number.isFinite(value) ? Math.max(0, Math.floor(value)) : null;
const positive = (value, fallback) => count(value) > 0 ? count(value) : fallback;

// Points measure practice; the percentage measures success across qualifying
// games. Never turn absent evidence into a 0% success rate or invent elapsed days.
export function objectiveProgress(o) {
  const score = count(o.score) ?? 0;
  const details = o.masteryDetails || {};
  const threshold = Math.min(100, positive(details.successThresholdPct, 80));
  const minGames = positive(details.minGames, 3);
  const minDays = positive(details.minSpanDays, 5);
  const games = count(o.masteryQualifyingGames);
  const days = count(details.spanDays);
  const successRate = games > 0 && typeof o.masteryPct === 'number' && Number.isFinite(o.masteryPct)
    ? Math.max(0, Math.min(100, o.masteryPct)) : null;
  const rateMet = typeof details.successRateMet === 'boolean' ? details.successRateMet
    : successRate === null ? null : successRate > threshold ? true : successRate < threshold ? false : null;
  const recentMet = typeof details.recentSuccessMet === 'boolean' ? details.recentSuccessMet : null;
  const stageIndex = PRACTICE_STAGES.reduce((current, stage, i) => score >= stage.at ? i : current, 0);
  const stage = PRACTICE_STAGES[stageIndex];
  const nextStage = PRACTICE_STAGES[stageIndex + 1] || null;
  const pointsToNext = nextStage ? nextStage.at - score : 0;
  const ready = o.masteryMet === true || score >= 50;
  let title, hint;
  if (ready) {
    title = 'Ready to complete';
    hint = score >= 50 ? 'You reached 50 practice points. Mark this complete when you’re ready to move on.'
      : 'Your success and consistency checks are met. You can mark this objective complete.';
  } else if (games === 0) {
    title = 'Start with your first game';
    hint = o.hasStructuredCriteria ? 'Play with this objective so Revu can check your game-stat target.'
      : 'Practice this objective, then confirm how it went in your game review.';
  } else if (games !== null && games < minGames) {
    title = 'Keep gathering practice';
    const remaining = minGames - games;
    hint = `${remaining} more ${remaining === 1 ? 'game' : 'games'} needed for the consistency check. Keep practicing and reviewing this objective.`;
  } else if (rateMet === false) {
    title = 'Build a steadier success rate';
    hint = successRate >= threshold
      ? `You’re just below the ${threshold}% target. Keep meeting your success condition.`
      : `Aim for success in at least ${threshold}% of the games counted for this objective.`;
  } else if (days !== null && days < minDays) {
    title = 'Keep it consistent over time';
    hint = `Your games currently span ${days} ${days === 1 ? 'day' : 'days'}. Keep practicing until your first and latest counted games are at least ${minDays} days apart.`;
  } else if (recentMet === false) {
    title = 'Show a recent run of success';
    hint = 'Meet your success condition in the last 3 games in a row, or in 8 of your last 10.';
  } else {
    title = 'Building consistency';
    hint = 'Keep practicing and reviewing this objective. Revu checks success across games and over time.';
  }
  return { score, stageIndex, stage, nextStage, pointsToNext, ready, title, hint,
    successRate, games, days, threshold, minGames, minDays, rateMet, recentMet };
}

export function renderObjectiveProgress(card, o) {
  const p = objectiveProgress(o);
  const set = (selector, value) => { const el = card.querySelector(selector); if (el) el.textContent = value; };
  set('.obj-practice-next', p.nextStage ? `${p.pointsToNext} ${p.pointsToNext === 1 ? 'point' : 'points'} to ${p.nextStage.name}` : 'Practice milestones complete');
  const stages = card.querySelector('.obj-practice-stages');
  stages.replaceChildren();
  for (const [i, step] of PRACTICE_STAGES.entries()) {
    const item = document.createElement('li');
    const name = document.createElement('span'); name.className = 'obj-stage-name'; name.textContent = step.name;
    const target = document.createElement('span'); target.className = 'obj-stage-target'; target.textContent = `${step.at} points`;
    item.append(name, target);
    item.classList.toggle('is-current', i === p.stageIndex);
    item.classList.toggle('is-past', i < p.stageIndex);
    if (i === p.stageIndex) {
      item.setAttribute('aria-current', 'step');
      const current = document.createElement('span'); current.className = 'obj-stage-current'; current.textContent = 'You are here';
      item.appendChild(current);
    }
    stages.appendChild(item);
  }
  set('.obj-readiness-title', p.title);
  set('.obj-readiness-hint', p.hint);
  set('.obj-success-value', p.successRate === null ? '—' : p.rateMet === false && p.successRate >= p.threshold ? `<${p.threshold}%` : `${p.successRate}%`);
  set('.obj-success-target', `${p.threshold}% target`);
  set('.obj-games-value', String(p.games ?? '—'));
  set('.obj-games-target', `At least ${p.minGames} games`);
  set('.obj-days-value', p.days === null ? '—' : `${p.days} ${p.days === 1 ? 'day' : 'days'}`);
  set('.obj-days-target', `${p.minDays} days needed`);
  set('.obj-success-source', o.hasStructuredCriteria
    ? 'Success means meeting the game-stat target above. Only games where that target was evaluated count.'
    : 'Success means the game was recorded as practiced for this objective. You can update that check in your game review. Linked games count toward this rate.');
  set('.obj-readiness-rule', `Reach ${p.threshold}% success across at least ${p.minGames} counted games, with your first and latest games at least ${p.minDays} days apart.`);
  card.querySelector('.obj-readiness')?.classList.toggle('is-ready', p.ready);
  return p;
}
