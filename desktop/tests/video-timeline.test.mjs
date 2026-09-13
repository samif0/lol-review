import test from 'node:test';
import assert from 'node:assert/strict';
import { gameToMedia, mediaToGame } from '../ui/video-timeline.mjs';
import { createTransport } from '../ui/vodtransport.js';

test('loading-screen footage maps game events to media and back without changing game timestamps', () => {
  assert.equal(gameToMedia(120, -35), 155);
  assert.equal(mediaToGame(155, -35), 120);
  assert.equal(mediaToGame(10, -35), 0);
  assert.equal(gameToMedia(120, 0), 120);
  assert.equal(gameToMedia(120, Infinity), 120);
});
test('transport seeks/steps in game time and resets timing for an extracted clip', () => {
  const listeners = new Map();
  const video = { currentTime: 0, duration: 635, addEventListener: (key, fn) => listeners.set(key, fn),
    removeEventListener: key => listeners.delete(key), load() {}, play: async () => {}, pause() {} };
  const transport = createTransport({ video });
  transport.setTimeOrigin(-35);
  transport.seekTo(120); assert.equal(video.currentTime, 155);
  assert.equal(transport.currentTime, 120); assert.equal(transport.duration, 600);
  transport.seekByStep(1); assert.equal(video.currentTime, 160);
  transport.load('clip', { startSeconds: 0, autoplay: false });
  listeners.get('loadedmetadata')(); assert.equal(video.currentTime, 0);
  assert.equal(transport.currentTime, 0);
});
