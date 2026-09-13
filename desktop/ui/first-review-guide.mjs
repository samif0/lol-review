const stages = { objective: 0, start_block: 1, queue: 1, pregame: 1, ingame: 1, wait_vod: 2, vod: 2, vod_moment: 2, review: 2, done: 2 };
export function guideStage(step) { return stages[step] ?? 0; }
export function guideIsActive(config) {
  return !!config?.firstReviewTutorialStep && !config.firstReviewTutorialCompleted && !config.firstReviewTutorialDismissed;
}
export function guideEventPatch(config, event, detail = {}) {
  if (!guideIsActive(config)) return null;
  const step = config.firstReviewTutorialStep;
  if (event === 'objective-created' && step === 'objective') {
    const id = Number(detail.id ?? detail.objectiveId);
    return Number.isSafeInteger(id) && id > 0 ? { firstReviewTutorialStep: 'start_block', firstReviewTutorialObjectiveId: id } : null;
  }
  if (event === 'block-started' && ['objective', 'start_block'].includes(step)) return { firstReviewTutorialStep: 'queue' };
  if (event === 'autoclip-done' && guideStage(step) === 2) return { firstReviewTutorialStep: 'review' };
  if (event === 'review-saved') return { firstReviewTutorialStep: '', firstReviewTutorialCompleted: true, firstReviewTutorialDismissed: false };
  return null;
}
