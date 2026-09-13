import test from 'node:test';
import assert from 'node:assert/strict';
import { guideStage, guideEventPatch } from '../ui/first-review-guide.mjs';

test('legacy seven-step progress resumes in the shorter guide', () => {
  assert.equal(guideStage('pregame'), 1);
  for (const step of ['wait_vod', 'vod', 'vod_moment', 'review']) assert.equal(guideStage(step), 2);
});
test('page events do not activate dismissed help or regress later progress', () => {
  assert.equal(guideEventPatch({firstReviewTutorialStep:'objective',firstReviewTutorialDismissed:true}, 'objective-created', {id:4}), null);
  assert.equal(guideEventPatch({firstReviewTutorialStep:'vod'}, 'block-started'), null);
  assert.equal(guideEventPatch({firstReviewTutorialStep:'review'}, 'objective-created', {id:4}), null);
});
test('only persisted objective ids advance goal creation; saving any review can complete help', () => {
  const config = {firstReviewTutorialStep:'objective'};
  assert.equal(guideEventPatch(config, 'objective-created', {id:0}), null);
  assert.deepEqual(guideEventPatch(config, 'objective-created', {id:3}), {firstReviewTutorialStep:'start_block',firstReviewTutorialObjectiveId:3});
  assert.deepEqual(guideEventPatch(config, 'review-saved'), {firstReviewTutorialStep:'',firstReviewTutorialCompleted:true,firstReviewTutorialDismissed:false});
});
