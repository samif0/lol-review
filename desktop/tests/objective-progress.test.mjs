import test from 'node:test';
import assert from 'node:assert/strict';
import { objectiveProgress, renderObjectiveProgress } from '../ui/objective-progress.mjs';

const details = overrides => ({ successThresholdPct: 80, minGames: 3, minSpanDays: 5,
  spanDays: 6, recentSuccessMet: true, successRateMet: true, ...overrides });
const objective = overrides => ({ score: 20, gameCount: 12, masteryQualifyingGames: 5,
  masteryPct: 80, masteryMet: false, masteryDetails: details(), ...overrides });

test('practice milestones advance at their point thresholds and show distance to the next stage', () => {
  const cases = [
    [0, 'Exploring', 'Drilling', 15], [14, 'Exploring', 'Drilling', 1],
    [15, 'Drilling', 'Ingraining', 15], [29, 'Drilling', 'Ingraining', 1],
    [30, 'Ingraining', 'Ready', 20], [49, 'Ingraining', 'Ready', 1],
    [50, 'Ready', null, 0], [65, 'Ready', null, 0],
  ];
  for (const [score, stage, nextStage, pointsToNext] of cases) {
    const p = objectiveProgress(objective({ score }));
    assert.equal(p.stage.name, stage, `Stage at ${score} points`);
    assert.equal(p.nextStage?.name ?? null, nextStage);
    assert.equal(p.pointsToNext, pointsToNext);
  }
});

test('completion eligibility can come from consistent success before the practice milestones finish', () => {
  const mastered = objectiveProgress(objective({ score: 7, masteryMet: true }));
  assert.equal(mastered.ready, true);
  assert.equal(mastered.stage.name, 'Exploring', 'Practice points remain separate from the success check');
  assert.match(mastered.title, /ready to complete/i);
  assert.match(mastered.hint, /success and consistency/i);

  const legacy = objectiveProgress(objective({ score: 50, masteryPct: 0, masteryQualifyingGames: 0,
    masteryDetails: details({ spanDays: 0, recentSuccessMet: false, successRateMet: false }) }));
  assert.equal(legacy.ready, true, 'Previously earned point-based readiness must not regress');
  assert.equal(legacy.successRate, null, 'No qualifying games cannot imply measured failure');
  assert.match(legacy.hint, /50 practice points/i);

  const pending = objectiveProgress(objective());
  assert.equal(pending.ready, false, 'The UI must not grant completion merely from rounded display values');
});

test('success uses qualifying evidence instead of all linked games and distinguishes no data from zero success', () => {
  const unmeasured = objectiveProgress(objective({ gameCount: 12, masteryQualifyingGames: 0, masteryPct: 0 }));
  assert.equal(unmeasured.games, 0);
  assert.equal(unmeasured.successRate, null);
  assert.match(unmeasured.title, /first game/i);

  const measured = objectiveProgress(objective({ masteryQualifyingGames: 5, masteryPct: 0,
    masteryDetails: details({ successRateMet: false }) }));
  assert.equal(measured.successRate, 0);
  assert.equal(measured.rateMet, false);
  assert.match(measured.title, /success rate/i);

  const oneGame = objectiveProgress(objective({ gameCount: 12, masteryQualifyingGames: 1, masteryPct: 100 }));
  assert.equal(oneGame.games, 1);
  assert.match(oneGame.hint, /2 more games/i);
  assert.equal(oneGame.ready, false);
});

test('rounded percentages never overrule an unmet server success target', () => {
  const p = objectiveProgress(objective({ masteryPct: 80, masteryQualifyingGames: 44,
    masteryDetails: details({ successRateMet: false }) }));
  assert.equal(p.successRate, 80);
  assert.equal(p.rateMet, false);
  assert.equal(p.ready, false);
  assert.match(p.hint, /below the 80% target/i);
});

test('elapsed time and recent consistency each remain visible reasons for an unfinished objective', () => {
  const shortSpan = objectiveProgress(objective({ masteryPct: 100,
    masteryDetails: details({ spanDays: 2 }) }));
  assert.equal(shortSpan.days, 2);
  assert.match(shortSpan.title, /over time/i);
  assert.match(shortSpan.hint, /at least 5 days apart/i);
  assert.equal(shortSpan.ready, false);

  const recentMisses = objectiveProgress(objective({
    masteryDetails: details({ spanDays: 12, recentSuccessMet: false }) }));
  assert.equal(recentMisses.rateMet, true);
  assert.equal(recentMisses.recentMet, false);
  assert.match(recentMisses.title, /recent/i);
  assert.equal(recentMisses.ready, false);
});

test('older or partial snapshots preserve unknown evidence without inventing zero days or passed checks', () => {
  const old = objectiveProgress({ score: 20, gameCount: 50, masteryPct: 0 });
  assert.equal(old.games, null);
  assert.equal(old.successRate, null);
  assert.equal(old.days, null);
  assert.equal(old.rateMet, null);
  assert.equal(old.recentMet, null);
  assert.equal(old.ready, false);

  const partial = objectiveProgress(objective({ masteryDetails: { spanDays: null, recentSuccessMet: null } }));
  assert.equal(partial.successRate, 80);
  assert.equal(partial.rateMet, null, 'A rounded percentage exactly at the target cannot prove the raw threshold passed');
  assert.equal(partial.days, null);
  assert.equal(partial.recentMet, null);
  assert.equal(partial.ready, false);
  assert.doesNotMatch(partial.hint, /0 days|checks are met/i);
});

test('target values come from mastery metadata and invalid numeric evidence stays unknown', () => {
  const p = objectiveProgress(objective({ masteryPct: 90, masteryQualifyingGames: 4,
    masteryDetails: details({ successThresholdPct: 90, minGames: 6, minSpanDays: 8 }) }));
  assert.equal(p.threshold, 90);
  assert.equal(p.minGames, 6);
  assert.equal(p.minDays, 8);
  assert.match(p.hint, /2 more games/i);

  for (const value of [null, undefined, NaN, Infinity, 'unknown']) {
    const unknown = objectiveProgress(objective({ masteryQualifyingGames: value, masteryPct: value,
      masteryDetails: { spanDays: value } }));
    assert.equal(unknown.games, null);
    assert.equal(unknown.days, null);
    assert.equal(unknown.successRate, null);
  }
});

function fixture() {
  function element(tag = 'div') {
    const classes = new Set(), attributes = new Map();
    return {
      tagName: tag.toUpperCase(), children: [], textContent: '',
      set innerHTML(_) { throw new Error('Progress labels must be rendered as text'); },
      classList: {
        contains: name => classes.has(name),
        toggle(name, on) { if (on) classes.add(name); else classes.delete(name); },
      },
      append(...children) { this.children.push(...children); },
      appendChild(child) { this.children.push(child); },
      replaceChildren(...children) { this.children = children; },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute: name => attributes.get(name) ?? null,
    };
  }
  const selectors = new Map();
  const node = selector => { if (!selectors.has(selector)) selectors.set(selector, element()); return selectors.get(selector); };
  return { node, card: { querySelector: node }, document: { createElement: element } };
}

test('rendering labels the current practice stage and keeps unknown success distinct through refreshes', t => {
  const f = fixture(), previousDocument = globalThis.document;
  globalThis.document = f.document;
  t.after(() => { if (previousDocument === undefined) delete globalThis.document; else globalThis.document = previousDocument; });

  renderObjectiveProgress(f.card, { score: 0, masteryQualifyingGames: 0, masteryPct: 0 });
  assert.equal(f.node('.obj-success-value').textContent, '—');
  assert.equal(f.node('.obj-days-value').textContent, '—');
  assert.equal(f.node('.obj-games-value').textContent, '0');
  assert.equal(f.node('.obj-games-target').textContent, 'At least 3 games');
  assert.equal(f.node('.obj-days-target').textContent, '5 days needed');
  let stages = f.node('.obj-practice-stages').children;
  assert.equal(stages.length, 4);
  assert.deepEqual(stages.map(node => node.getAttribute('aria-current')), ['step', null, null, null]);
  assert.match(f.node('.obj-success-source').textContent, /game review/i);

  renderObjectiveProgress(f.card, objective({ score: 30, masteryPct: 0, hasStructuredCriteria: true,
    masteryDetails: details({ successRateMet: false }) }));
  assert.equal(f.node('.obj-success-value').textContent, '0%');
  assert.equal(f.node('.obj-days-value').textContent, '6 days');
  assert.equal(f.node('.obj-games-value').textContent, '5', 'Counted games remain readable above the minimum');
  stages = f.node('.obj-practice-stages').children;
  assert.equal(stages.length, 4, 'Refreshing must replace milestone nodes rather than duplicate them');
  assert.deepEqual(stages.map(node => node.getAttribute('aria-current')), [null, null, 'step', null]);
  assert.match(f.node('.obj-success-source').textContent, /target.*evaluated/i);
  assert.equal(f.node('.obj-readiness').classList.contains('is-ready'), false);

  renderObjectiveProgress(f.card, objective({ masteryPct: 80,
    masteryDetails: details({ successRateMet: false }) }));
  assert.equal(f.node('.obj-success-value').textContent, '<80%', 'The rounded display must not imply the target has been met');
  assert.equal(f.node('.obj-success-target').textContent, '80% target');

  renderObjectiveProgress(f.card, objective({ masteryMet: true }));
  assert.equal(f.node('.obj-readiness').classList.contains('is-ready'), true);
  renderObjectiveProgress(f.card, objective({ masteryMet: false }));
  assert.equal(f.node('.obj-readiness').classList.contains('is-ready'), false);
});
