import test from 'node:test';
import assert from 'node:assert/strict';
import { clipboardTimers, deferredClipboard, fixture, plain, sharedClipControls } from './vod-player-fixture.mjs';

test('objective review events are chronological, shared-token aware, and exclude removed or invalid times while retaining zero', () => {
  const f = fixture({ events: [
    { id: 5, gameTimeSeconds: 45, objectiveIds: [7, 11], eventType: 'TEAMFIGHT', kind: 'teamfight-away' },
    { id: 1, gameTimeSeconds: 0, objectiveId: 7, eventType: 'DEATH' },
    ...[null, undefined, -1, NaN, Infinity, '12'].map((time, i) => ({ id: 100 + i, gameTimeSeconds: time, objectiveId: 7 })),
    { id: 9, gameTimeSeconds: 8, objectiveId: 7, removed: true },
    { id: 6, gameTimeSeconds: 20, objectiveIds: [11], objectiveId: 7 },
  ] });
  assert.deepEqual(plain(f.hooks.markersForObjective(7)).map(e => e.id), [1, 5]);
  assert.deepEqual(plain(f.hooks.markersForObjective(11)).map(e => e.id), [6, 5]);
  assert.equal(f.hooks.momentLanes().auto.length, 0, 'raw events never become writable evidence');
  f.hooks.renderMoments();
  assert.equal(f.$('vp-event-list').children.length, 2);
  assert.equal(f.$('vp-event-count').textContent, '2 events');
  assert.equal(f.$('vp-saved-moments').open, false);
});

test('unframed event rows use readable names and watching opens game-time lead-up without changing draft fields or saved editors', async () => {
  const f = fixture({ objectives: [], events: [
    { id: 1, gameTimeSeconds: 0, label: 'Spawn', eventType: 'START' },
    { id: 2, eventKey: 'death:35', gameTimeSeconds: 35.25, eventType: 'FOG_DEATH' },
    { id: 3, gameTimeSeconds: 60, eventType: 'SPELL_FLASH' },
  ] });
  f.$('vp-bm-note').value = '  Keep this\nnext time  ';
  f.$('vp-clip-note').value = 'Unfinished clip';
  f.hooks.renderMoments();
  const rows = f.$('vp-event-list').children;
  const editor = f.$('vp-bookmarks').appendChild(f.document.createElement('input')); editor.value = 'Existing inline draft';
  assert.deepEqual(rows.map(row => row.querySelector('.vp-event-title').textContent), ['Spawn', 'Death without vision', 'Spell flash']);
  assert.ok(rows.every(row => row.tagName === 'BUTTON' && row.type === 'button'));
  await f.emit('click', rows[1]);
  assert.deepEqual(f.seeks, [25.25]);
  assert.equal(f.$('vp-video').currentTime, 28.25, 'page delegates game/media offset conversion to transport');
  assert.deepEqual(plain(f.selections), [{ key: 'death:35', id: 2 }]);
  assert.equal(rows[1].getAttribute('aria-current'), 'true');
  assert.equal(f.$('vp-bookmarks').children[0], editor);
  assert.equal(editor.value, 'Existing inline draft');
  assert.equal(f.$('vp-bm-note').value, '  Keep this\nnext time  ');
  assert.equal(f.$('vp-clip-note').value, 'Unfinished clip');
  await f.emit('click', rows[0]);
  assert.equal(f.seeks.at(-1), 0);
  assert.equal(rows[1].getAttribute('aria-current'), null);
  assert.equal(f.writes.length, 0);
});

test('native objective selection preserves focus, playback and explicitly chosen draft pickers', () => {
  const f = fixture({ events: [{ id: 1, gameTimeSeconds: 10, objectiveId: 7 }, { id: 2, gameTimeSeconds: 30, objectiveId: 11 }] });
  f.hooks.renderObjBar(); f.hooks.wireFraming();
  const second = f.$('vp-objbar').children[1]; second.focus();
  f.$('vp-bm-note').value = 'Raw review draft'; f.$('vp-clip-note').value = 'Raw clip draft';
  f.$('vp-bm-obj').value = 'obj:7'; f.$('vp-clip-obj').value = 'obj:11'; f.hooks.setOverrides();
  f.$('vp-objbar').emit('keydown', { target: second, key: 'Enter', preventDefault() { throw new Error('Native button key must not be hijacked'); } });
  assert.equal(f.hooks.focused, 7, 'browser supplies the single native click');
  f.$('vp-objbar').emit('click', { target: second });
  assert.equal(f.hooks.focused, 11);
  assert.equal(f.document.activeElement.dataset.objId, '11');
  assert.equal(f.document.activeElement.getAttribute('aria-pressed'), 'true');
  assert.equal(f.document.activeElement.querySelector('.vp-objtab-count').textContent, '1 event');
  assert.equal(f.$('vp-event-list').children[0].dataset.reviewKey, 'id:2');
  assert.equal(f.$('vp-bm-obj').value, 'obj:7'); assert.equal(f.$('vp-clip-obj').value, 'obj:11');
  assert.equal(f.$('vp-bm-note').value, 'Raw review draft'); assert.equal(f.$('vp-clip-note').value, 'Raw clip draft');
  assert.equal(f.seeks.length, 0); assert.equal(f.writes.length, 0);
});

test('saved moments retain untagged rows and user disclosure choice; an explicit clip link opens its objective and lane', async () => {
  const f = fixture({ search: '?gameId=42&clip=41&t=20' });
  f.snapshot.bookmarks = [{ id: 1, gameTimeSeconds: 4, note: 'Untagged' }, { id: 2, gameTimeSeconds: 8, objectiveId: 11, note: 'Other objective' }];
  f.snapshot.savedClips = [{ id: 41, hasClip: true, startTimeSeconds: 20, objectiveId: 11 }];
  f.hooks.renderMoments();
  assert.equal(f.$('vp-saved-moments').open, true);
  assert.deepEqual(plain(f.hooks.momentLanes().bm).map(item => item.id), [1]);
  f.$('vp-saved-moments').open = false; f.hooks.renderMoments();
  assert.equal(f.$('vp-saved-moments').open, false, 'background refresh preserves a manual collapse');
  f.hooks.applyClipDeepLink();
  assert.equal(f.hooks.focused, 11); assert.equal(f.hooks.filter, 'clips');
  assert.equal(f.$('vp-saved-moments').open, true);
  assert.equal(f.$('vp-bookmarks').querySelector('[data-ev-id="41"]').scrolled, true);
  const clipTab = f.$('vp-tabs').children.find(tab => tab.dataset.filter === 'clips');
  assert.equal(clipTab.getAttribute('aria-pressed'), 'true');
  f.setRestore({ ...plain(f.hooks.captureVodViewState()), focusedObjectiveId: 7, filter: 'bm' });
  await f.hooks.restoreMatchView();
  assert.equal(f.$('vp-saved-moments').open, true, 'explicit clip remains visible after restoring old collapsed disclosures');
  assert.equal(f.hooks.focused, 11); assert.equal(f.hooks.filter, 'clips');
});

test('clip shortcuts reveal tools and restored raw clip drafts reopen once without forcing later disclosure choices', async () => {
  const f = fixture();
  f.transport.currentTime = 12; await f.emit('keydown', f.$('vp-video'), { key: 'i' });
  assert.equal(f.$('vp-clip-tools').open, true);
  f.$('vp-clip-tools').open = false; f.transport.currentTime = 28;
  await f.emit('keydown', f.$('vp-video'), { key: 'o' });
  assert.equal(f.$('vp-clip-tools').open, true);
  f.$('vp-clip-note').value = '  Finish this clip\n';
  const state = plain(f.hooks.captureVodViewState());
  f.setRestore(state); f.$('vp-clip-note').value = ''; f.$('vp-clip-tools').open = false;
  await f.hooks.restoreMatchView();
  assert.equal(f.$('vp-clip-tools').open, true);
  assert.equal(f.$('vp-clip-note').value, '  Finish this clip\n');
  assert.deepEqual(plain(f.hooks.captureVodViewState()).clip, state.clip);
  f.$('vp-clip-tools').open = false; await f.hooks.restoreMatchView();
  assert.equal(f.$('vp-clip-tools').open, false, 'metadata refresh must not reopen a user-collapsed tool');
  f.hooks.openShareLogin(5, '');
  assert.equal(f.$('vp-clip-tools').open, true); assert.equal(f.$('vp-sharelogin').hidden, false);
  assert.equal(f.writes.length, 0);
});

test('clip shortcuts keep cinema open, reveal the real editor, and focus the note only after the out point', async () => {
  const f = fixture();
  const tools = f.$('vp-clip-tools'), note = f.$('vp-clip-note'), video = f.$('vp-video');
  f.transport.toggleEnlarge(); video.focus(); video.paused = false;
  for (const [key, seconds] of [['i', 12], ['o', 28]]) {
    tools.open = false;
    f.transport.currentTime = seconds;
    const event = await f.emit('keydown', video, { key });
    assert.equal(event.defaultPrevented, true);
    assert.equal(f.transport.isExpanded(), true);
    assert.equal(f.$('vp-composer').hidden, false);
    assert.equal(f.$('vp-composer').contains(tools), true, 'uses the existing draft fields inside the visible composer');
    assert.equal(tools.open, true);
    if (key === 'i') {
      assert.notEqual(f.document.activeElement, note, 'starting the range leaves playback shortcuts available');
      assert.equal(video.paused, false);
    }
  }
  assert.equal(f.document.activeElement, note);
  assert.equal(video.paused, true);
  const state = plain(f.hooks.captureVodViewState());
  assert.equal(state.clip.start, 12);
  assert.equal(state.clip.end, 28);
  assert.equal(f.writes.length, 0);
});

test('B opens a bookmark draft in cinema without saving and preserves its captured time while typing', async () => {
  const f = fixture(); const video = f.$('vp-video'), note = f.$('vp-bm-note');
  f.transport.toggleEnlarge(); f.transport.currentTime = 42; video.paused = false;
  const opened = await f.emit('keydown', video, { key: 'b' });
  assert.equal(opened.defaultPrevented, true);
  assert.equal(f.transport.isExpanded(), true); assert.equal(video.paused, true);
  assert.equal(f.$('vp-composer').hidden, false);
  assert.equal(f.$('vp-composer').contains(note), true);
  assert.equal(f.document.activeElement, note);
  assert.equal(f.writes.length, 0, 'opening a bookmark is not a backend write');
  f.transport.currentTime = 75;
  note.value = '  Space before the fight\nKeep an exit  ';
  const letter = await f.emit('keydown', note, { key: 's' });
  const newline = await f.emit('keydown', note, { key: 'Enter' });
  assert.equal(letter.defaultPrevented, undefined); assert.equal(newline.defaultPrevented, undefined);
  await f.emit('keydown', note, { key: 's', ctrlKey: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 1);
  assert.deepEqual(f.writes[0], { command: 'add_bookmark', args: { payload: {
    gameId: 42, timeS: 42, note: 'Space before the fight\nKeep an exit', objectiveId: 7,
  } } });
  assert.equal(note.value, '');
  assert.equal(f.transport.isExpanded(), true);
  assert.match(f.$('vp-bm-hint').textContent, /saved/i);
  assert.equal(f.$('vp-bm-hint').hidden, false);
});

test('a fresh cinema bookmark at 19 minutes does not reuse the time of an empty composer opened at the start', async () => {
  for (const revisit of ['same page', 'restored page', 'switched to clip']) {
    let f = fixture();
    f.snapshot.gameDurationSeconds = 2400; f.transport.duration = 2400; f.$('vp-video').duration = 2400;
    f.transport.toggleEnlarge();
    await f.emit('keydown', f.$('vp-video'), { key: 'b' });
    f.$('vp-bm-note').value = '  \n  ';
    if (revisit === 'switched to clip') {
      f.$('vp-compose-clip').dataset.action = 'open_clip';
      await f.emit('click', f.$('vp-compose-clip'));
      assert.equal(plain(f.hooks.captureVodViewState()).bookmark.time, null, 'switching modes abandons an empty timestamp');
    } else {
      await f.emit('keydown', f.$('vp-bm-note'), { key: 'Escape' });
    }
    assert.equal(f.writes.length, 0);
    if (revisit === 'restored page') {
      const state = plain(f.hooks.captureVodViewState());
      state.bookmark.time = 0; // older saved views retain the empty draft's stale timestamp
      f = fixture();
      f.snapshot.gameDurationSeconds = 2400; f.transport.duration = 2400; f.$('vp-video').duration = 2400;
      f.setRestore(state); await f.hooks.restoreMatchView(); f.transport.toggleEnlarge();
    }
    f.transport.currentTime = 1140; f.$('vp-video').currentTime = 1140; f.$('vp-video').paused = false;
    await f.emit('keydown', f.$('vp-video'), { key: 'b' });
    f.$('vp-bm-note').value = 'Review the decision at nineteen minutes';
    f.transport.currentTime = 1200; f.$('vp-video').currentTime = 1200;
    await f.emit('keydown', f.$('vp-bm-note'), { key: 's', ctrlKey: true }); await f.hooks.flush();
    assert.equal(f.writes.length, 1);
    assert.equal(f.writes[0].args.payload.timeS, 1140, `${revisit}: capture the newly marked moment`);
    assert.equal(f.snapshot.bookmarks[0].gameTimeSeconds, 1140);
    assert.equal(f.$('vp-bookmarks').children[0].querySelector('.vp-bm-time').textContent, '19:00');
    assert.match(f.$('vp-bm-hint').textContent, /19:00/);
    assert.equal(f.transport.isExpanded(), true);
  }
});

test('an authored bookmark draft retains its marked time across closing and navigation even when playback later advances', async () => {
  for (const markedTime of [0, 1140]) {
    for (const revisit of ['same page', 'restored page']) {
      let f = fixture();
      f.snapshot.gameDurationSeconds = 2400; f.transport.duration = 2400; f.$('vp-video').duration = 2400;
      f.transport.currentTime = markedTime; f.$('vp-video').currentTime = markedTime; f.transport.toggleEnlarge();
      await f.emit('keydown', f.$('vp-video'), { key: 'b' });
      f.$('vp-bm-note').value = '  Keep this marked moment after returning from review  ';
      await f.emit('keydown', f.$('vp-bm-note'), { key: 'Escape' });
      if (revisit === 'restored page') {
        const state = plain(f.hooks.captureVodViewState());
        f = fixture();
        f.snapshot.gameDurationSeconds = 2400; f.transport.duration = 2400; f.$('vp-video').duration = 2400;
        f.setRestore(state); await f.hooks.restoreMatchView(); f.transport.toggleEnlarge();
      }
      f.transport.currentTime = 1200; f.$('vp-video').currentTime = 1200;
      await f.emit('keydown', f.$('vp-video'), { key: 'b' });
      await f.emit('keydown', f.$('vp-bm-note'), { key: 's', ctrlKey: true }); await f.hooks.flush();
      assert.equal(f.writes.length, 1);
      assert.equal(f.writes[0].args.payload.timeS, markedTime, `${revisit}: keep the authored draft's marked moment`);
      assert.equal(f.writes[0].args.payload.note, 'Keep this marked moment after returning from review');
      assert.equal(f.transport.isExpanded(), true);
    }
  }
});

test('Escape closes the composer before cinema, keeps drafts, and returns the real fields to their page', async () => {
  const f = fixture(); const video = f.$('vp-video'), note = f.$('vp-clip-note');
  const originalParent = f.$('vp-clip-tools').parentNode;
  f.transport.toggleEnlarge(); f.transport.currentTime = 12;
  await f.emit('keydown', video, { key: 'i' });
  f.transport.currentTime = 28; await f.emit('keydown', video, { key: 'o' });
  note.value = 'Keep this unsaved clip';
  const before = plain(f.hooks.captureVodViewState()).clip;
  const escape = await f.emit('keydown', note, { key: 'Escape' });
  assert.equal(escape.defaultPrevented, true);
  assert.equal(f.$('vp-composer').hidden, true);
  assert.equal(f.transport.isExpanded(), true);
  assert.equal(f.$('vp-clip-tools').parentNode, originalParent);
  assert.deepEqual(plain(f.hooks.captureVodViewState()).clip, before);
  await f.emit('keydown', video, { key: 'Escape' });
  assert.equal(f.transport.isExpanded(), false);
  assert.equal(f.writes.length, 0);
});

test('clip field shortcuts save in cinema and ordinary S remains available for note text', async () => {
  for (const shortcut of [{ key: 'Enter' }, { key: 's', ctrlKey: true }, { key: 'Enter', ctrlKey: true }]) {
    const f = fixture(); const video = f.$('vp-video'), note = f.$('vp-clip-note');
    f.transport.toggleEnlarge(); f.transport.currentTime = 12;
    await f.emit('keydown', video, { key: 'i' });
    f.transport.currentTime = 28; await f.emit('keydown', video, { key: 'o' });
    note.value = '  Safer spacing  ';
    const letter = await f.emit('keydown', note, { key: 's' });
    assert.equal(letter.defaultPrevented, undefined); assert.equal(f.writes.length, 0);
    await f.emit('keydown', note, { ...shortcut, repeat: true });
    await f.emit('keydown', note, { ...shortcut, isComposing: true });
    await f.hooks.flush(); assert.equal(f.writes.length, 0);
    const save = await f.emit('keydown', note, shortcut); await f.hooks.flush();
    assert.equal(save.defaultPrevented, true); assert.equal(f.writes.length, 1);
    assert.equal(f.writes[0].command, 'extract_clip');
    assert.equal(f.writes[0].args.payload.startTimeS, 12); assert.equal(f.writes[0].args.payload.endTimeS, 28);
    assert.equal(f.writes[0].args.payload.note, 'Safer spacing');
    assert.equal(note.value, ''); assert.equal(f.transport.isExpanded(), true);
    assert.match(f.$('vp-clip-hint').textContent, /saved/i, 'clearing the saved range must not clear its confirmation');
    assert.equal(f.$('vp-clip-hint').hidden, false);
  }
});

test('S outside text fields saves the active bookmark composer and held shortcut keys do not recapture drafts', async () => {
  const f = fixture(); const video = f.$('vp-video');
  f.transport.toggleEnlarge(); f.transport.currentTime = 35;
  await f.emit('keydown', video, { key: 'b' });
  f.$('vp-bm-note').value = 'Captured once';
  f.transport.currentTime = 80;
  await f.emit('keydown', video, { key: 'b', repeat: true });
  await f.emit('keydown', video, { key: 'b', isComposing: true });
  await f.emit('keydown', video, { key: 's', repeat: true });
  assert.equal(f.writes.length, 0);
  await f.emit('keydown', video, { key: 's' }); await f.hooks.flush();
  assert.equal(f.writes.length, 1); assert.equal(f.writes[0].command, 'add_bookmark');
  assert.equal(f.writes[0].args.payload.timeS, 35);
  assert.equal(f.writes[0].args.payload.note, 'Captured once');
});

test('failed composer saves preserve notes and ranges for retry without leaving cinema', async () => {
  for (const mode of ['clip', 'bookmark']) {
    let fail = true;
    const f = fixture({ invokeWrite: () => {
      if (fail) throw new Error('Temporary write failure');
      return { ok: true };
    } });
    const video = f.$('vp-video'); f.transport.toggleEnlarge(); f.transport.currentTime = 20;
    await f.emit('keydown', video, { key: mode === 'clip' ? 'i' : 'b' });
    if (mode === 'clip') { f.transport.currentTime = 40; await f.emit('keydown', video, { key: 'o' }); }
    const note = f.$(mode === 'clip' ? 'vp-clip-note' : 'vp-bm-note');
    const hint = f.$(mode === 'clip' ? 'vp-clip-hint' : 'vp-bm-hint');
    note.value = '  Keep the failed draft  ';
    const before = plain(f.hooks.captureVodViewState())[mode];
    await f.emit('keydown', note, { key: 's', ctrlKey: true }); await f.hooks.flush();
    assert.equal(f.writes.length, 1);
    assert.equal(f.transport.isExpanded(), true); assert.equal(f.$('vp-composer').hidden, false);
    assert.deepEqual(plain(f.hooks.captureVodViewState())[mode], before);
    assert.equal(hint.hidden, false); assert.equal(hint.classList.contains('err'), true);
    fail = false;
    await f.emit('keydown', note, { key: 's', ctrlKey: true }); await f.hooks.flush();
    assert.equal(f.writes.length, 2); assert.equal(note.value, ''); assert.match(hint.textContent, /saved/i);
  }
});

test('pending composer saves deduplicate submissions and preserve edits made while the backend writes', async () => {
  for (const mode of ['clip', 'bookmark']) {
    let resolveWrite;
    const f = fixture({ invokeWrite: () => new Promise(resolve => { resolveWrite = resolve; }) });
    const video = f.$('vp-video'); f.transport.toggleEnlarge(); f.transport.currentTime = 20;
    await f.emit('keydown', video, { key: mode === 'clip' ? 'i' : 'b' });
    if (mode === 'clip') { f.transport.currentTime = 40; await f.emit('keydown', video, { key: 'o' }); }
    const note = f.$(mode === 'clip' ? 'vp-clip-note' : 'vp-bm-note');
    note.value = 'Submitted note';
    await f.emit('keydown', note, { key: 's', ctrlKey: true });
    await f.emit('keydown', note, { key: 's', ctrlKey: true });
    assert.equal(f.writes.length, 1, 'two submissions share the active write');
    note.value = '  New note typed while saving  ';
    f.$(mode === 'clip' ? 'vp-clip-obj' : 'vp-bm-obj').value = 'obj:11';
    f.hooks.setOverrides();
    if (mode === 'clip') { f.transport.currentTime = 55; await f.emit('keydown', video, { key: 'o' }); }
    const newerDraft = plain(f.hooks.captureVodViewState())[mode];
    resolveWrite({ ok: true }); await f.hooks.flush();
    assert.deepEqual(plain(f.hooks.captureVodViewState())[mode], newerDraft);
    assert.equal(f.writes[0].args.payload.note, 'Submitted note');
    assert.equal(f.transport.isExpanded(), true);
  }
});

test('Make clip uses its bookmark card note and 15-second context, preserves an explicit no-objective tag, and saves in cinema', async () => {
  const f = fixture();
  f.snapshot.bookmarks = [{ id: 9, gameTimeSeconds: 40, note: 'Original bookmark' }];
  f.hooks.setFilter('bm'); f.hooks.renderMoments(); f.transport.toggleEnlarge();
  const row = f.$('vp-bookmarks').children[0], make = row.querySelector('.vp-bm-makeclip');
  row.querySelector('.vp-bm-editnote').value = '  Updated before blur  ';
  assert.equal(make.textContent, 'Make clip'); assert.equal(make.disabled, false);
  await f.emit('click', make);
  assert.deepEqual(f.seeks, [25], 'preparing the clip seeks to its lead-up rather than the bookmark itself');
  const draft = plain(f.hooks.captureVodViewState()).clip;
  assert.equal(draft.start, 25); assert.equal(draft.end, 55);
  assert.equal(draft.note, '  Updated before blur  '); assert.equal(draft.picker, ''); assert.equal(draft.userSet, true);
  assert.equal(f.document.activeElement, f.$('vp-clip-note'));
  assert.equal(f.transport.isExpanded(), true); assert.equal(f.$('vp-video').paused, true);
  assert.equal(f.writes.length, 0); assert.equal(f.snapshot.bookmarks.length, 1);
  await f.emit('keydown', f.$('vp-clip-note'), { key: 's', ctrlKey: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 1); assert.equal(f.writes[0].command, 'extract_clip');
  const payload = f.writes[0].args.payload;
  assert.equal(payload.startTimeS, 25); assert.equal(payload.endTimeS, 55); assert.equal(payload.note, 'Updated before blur');
  assert.equal('objectiveId' in payload, false, 'an untagged source must not inherit the currently focused objective');
  assert.equal('promptId' in payload, false);
  assert.deepEqual(f.snapshot.bookmarks, [{ id: 9, gameTimeSeconds: 40, note: 'Original bookmark' }]);
});

test('Make clip preserves an unfinished clip and reveals it for saving instead of replacing its draft', async () => {
  const f = fixture();
  f.snapshot.bookmarks = [{ id: 9, gameTimeSeconds: 40, note: 'Bookmark to convert' }];
  f.hooks.setFilter('bm'); f.hooks.renderMoments();
  f.transport.toggleEnlarge(); f.transport.currentTime = 8;
  await f.emit('keydown', f.$('vp-video'), { key: 'i' });
  f.transport.currentTime = 18; await f.emit('keydown', f.$('vp-video'), { key: 'o' });
  f.$('vp-clip-note').value = '  My unfinished clip  ';
  const before = plain(f.hooks.captureVodViewState()).clip;
  await f.emit('keydown', f.$('vp-clip-note'), { key: 'Escape' });
  await f.emit('click', f.$('vp-bookmarks').querySelector('.vp-bm-makeclip'));
  assert.deepEqual(plain(f.hooks.captureVodViewState()).clip, before);
  assert.equal(f.$('vp-composer').hidden, false); assert.equal(f.transport.isExpanded(), true);
  assert.equal(f.document.activeElement, f.$('vp-clip-note'));
  assert.match(f.$('vp-clip-hint').textContent, /unfinished clip/i);
  assert.equal(f.$('vp-clip-hint').classList.contains('err'), true);
  assert.equal(f.seeks.length, 0); assert.equal(f.writes.length, 0);
});

test('native card buttons keep Enter and Space activation instead of having the enclosing moment seek', async () => {
  for (const key of ['Enter', ' ']) {
    for (const action of ['make_clip_from_bookmark', 'delete_bookmark', 'delete_clip']) {
      const f = fixture();
      f.snapshot.bookmarks = [{ id: 9, gameTimeSeconds: 40, note: 'Native action', hasClip: action === 'delete_clip' }];
      f.hooks.setFilter(action === 'delete_clip' ? 'clips' : 'bm'); f.hooks.renderMoments();
      const button = f.$('vp-bookmarks').querySelector(`[data-action="${action}"]`);
      const icon = button.appendChild(f.document.createElement('span'));
      const activation = await f.emit('keydown', icon, { key });
      assert.equal(activation.defaultPrevented, undefined, 'browser retains native button activation');
      assert.equal(f.seeks.length, 0); assert.equal(f.writes.length, 0);
      await f.emit('click', icon); // the single click supplied by native activation
      if (action === 'make_clip_from_bookmark') {
        assert.deepEqual(f.seeks, [25]); assert.equal(f.writes.length, 0);
      } else {
        assert.equal(f.seeks.length, 0); assert.equal(f.confirmations.length, 1);
        assert.equal(f.writes.length, 1); assert.equal(f.writes[0].command, action);
        assert.equal(f.writes[0].args.payload.bookmarkId, 9);
      }
    }
  }
});

test('explicit No objective choices on clip and bookmark fields remain untagged when another objective is focused', async () => {
  for (const mode of ['clip', 'bookmark']) {
    const f = fixture(); const video = f.$('vp-video');
    f.transport.currentTime = 20;
    await f.emit('keydown', video, { key: mode === 'clip' ? 'i' : 'b' });
    if (mode === 'clip') { f.transport.currentTime = 40; await f.emit('keydown', video, { key: 'o' }); }
    const picker = f.$(mode === 'clip' ? 'vp-clip-obj' : 'vp-bm-obj');
    picker.value = ''; await f.emit('change', picker);
    const note = f.$(mode === 'clip' ? 'vp-clip-note' : 'vp-bm-note'); note.value = 'General lesson';
    await f.emit('keydown', note, { key: 's', ctrlKey: true }); await f.hooks.flush();
    assert.equal(f.writes.length, 1);
    assert.equal('objectiveId' in f.writes[0].args.payload, false);
    assert.equal('promptId' in f.writes[0].args.payload, false);
  }
});

test('review note Enter inserts a newline; Ctrl/Command+Enter saves once and reveals Notes without changing picker semantics', async () => {
  const f = fixture(); const note = f.$('vp-bm-note');
  note.value = '  First line\nSecond line  '; f.$('vp-bm-obj').value = 'obj:11'; f.hooks.setOverrides();
  const enter = await f.emit('keydown', note, { key: 'Enter' });
  assert.equal(enter.defaultPrevented, undefined); assert.equal(f.writes.length, 0);
  await f.emit('keydown', note, { key: 'Enter', ctrlKey: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 1);
  assert.deepEqual(f.writes[0], { command: 'add_bookmark', args: { payload: { gameId: 42, timeS: 0, note: 'First line\nSecond line', objectiveId: 11 } } });
  assert.equal(note.value, ''); assert.equal(f.$('vp-saved-moments').open, true); assert.equal(f.hooks.filter, 'bm');
  note.value = 'Command shortcut';
  await f.emit('keydown', note, { key: 'Enter', metaKey: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 2);
  await f.emit('keydown', note, { key: 'Enter', ctrlKey: true, repeat: true }); await f.hooks.flush();
  assert.equal(f.writes.length, 2);
});

test('a same-game link opens the no-recording player once and preserves raw note/clip drafts and controls', async () => {
  let resolveRead;
  const f = fixture({ noVod: true, invokeReply: () => new Promise(resolve => { resolveRead = resolve; }) });
  f.$('vp-bm-note').value = '  Review note\nnot saved  ';
  f.$('vp-clip-note').value = '  Clip draft  ';
  f.$('vp-video').muted = true; f.$('vp-video').volume = 0.3; f.$('vp-video').playbackRate = 1.5;
  f.transport.setStep(10);
  await f.linked(9); await f.linked(0); await f.linked('invalid');
  assert.equal(f.reads.length, 0);
  const pending = f.linked(42);
  await new Promise(resolve => setImmediate(resolve));
  await f.linked(42);
  assert.equal(f.reads.length, 1, 'duplicate links share the pending read');
  f.$('vp-bm-note').value += ' edited during fetch';
  resolveRead({ ...f.snapshot, hasVod: true, filePath: 'new-match.mp4' });
  await pending;
  assert.equal(f.$('vp-video').loads, 1);
  assert.equal(f.$('vp-novod').hidden, true); assert.equal(f.$('vp-wrap').hidden, false);
  assert.equal(f.$('vp-bm-note').value, '  Review note\nnot saved   edited during fetch');
  assert.equal(f.$('vp-clip-note').value, '  Clip draft  ');
  assert.equal(f.$('vp-video').playbackRate, 1.5); assert.equal(f.$('vp-video').muted, true);
  assert.equal(f.$('vp-video').volume, 0.3); assert.equal(f.transport.stepSeconds, 10);
  assert.equal(f.writes.length, 0);
  await f.linked(42);
  assert.equal(f.reads.length, 1); assert.equal(f.$('vp-video').loads, 1);
});

test('recording links wait for initial hydration and an older metadata response cannot undo the link', async () => {
  const pendingReads = [];
  const f = fixture({ noVod: true, initialLoading: true,
    invokeReply: () => new Promise(resolve => pendingReads.push(resolve)) });
  await f.linked(42);
  assert.equal(f.reads.length, 0);
  f.hooks.render(f.snapshot, f.core);
  f.hooks.setInitialLoading(false);
  const oldMetadata = f.hooks.reloadBookmarks();
  const linked = f.hooks.refreshLinkedRecording();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(pendingReads.length, 2);
  pendingReads[1]({ ...f.snapshot, hasVod: true, filePath: 'hydrated.mp4' });
  await linked;
  pendingReads[0]({ ...f.snapshot, hasVod: false, filePath: '' });
  await oldMetadata;
  assert.equal(f.hooks.captureVodViewState().filePath, 'hydrated.mp4');
  assert.equal(f.$('vp-video').loads, 1); assert.equal(f.$('vp-novod').hidden, true);
});

test('a link never resets active playback and a mismatched response cannot open another game', async () => {
  const active = fixture();
  const v = active.$('vp-video'); Object.assign(v, { currentSrc: 'revu-media://playing', paused: false, currentTime: 57.25, playbackRate: 2, muted: true });
  await active.linked(42);
  assert.equal(active.reads.length, 0); assert.equal(v.loads, undefined);
  assert.equal(v.currentTime, 57.25); assert.equal(v.playbackRate, 2); assert.equal(v.paused, false);
  const missing = fixture({ noVod: true, invokeReply: async () => ({ gameId: 99, hasVod: true, filePath: 'other.mp4' }) });
  await missing.linked(42);
  assert.equal(missing.$('vp-video').loads, undefined);
  assert.equal(missing.$('vp-novod').hidden, false);
  assert.equal(missing.hooks.captureVodViewState().gameId, 42);
});

test('Copy clip link waits for native acknowledgment and suppresses duplicate clicks without seeking', async () => {
  const started = deferredClipboard(), copied = deferredClipboard(), timers = clipboardTimers();
  const f = fixture({ timers, invokeWrite: (command) => {
    assert.equal(command, 'copy_text_to_clipboard'); started.resolve(); return copied.promise;
  } });
  const url = 'https://revu.lol/clip-42?view=full&name=Tempo%20reset#moment';
  const { copy, label } = sharedClipControls(f, url);
  let finished = false;
  const copying = f.emit('click', copy).then(() => { finished = true; });
  await started.promise;
  assert.equal(copy.disabled, true); assert.equal(label.textContent, 'Copying…');
  assert.equal(finished, false, 'the delegated click handler must await the native acknowledgment');
  await f.emit('click', copy);
  assert.deepEqual(f.writes, [{ command: 'copy_text_to_clipboard', args: { text: url } }]);
  assert.deepEqual(f.seeks, []);
  copied.resolve({ ok: true }); await copying;
  assert.equal(copy.disabled, false); assert.equal(label.textContent, 'Copied');
  assert.equal(copy.title, 'Copy link'); assert.equal(copy.dataset.shareUrl, url);
  timers.advance(1599); assert.equal(label.textContent, 'Copied');
  timers.advance(1); assert.equal(label.textContent, 'Copy');
});

test('failed clip-link copies stay visible and retryable until native copying succeeds', async () => {
  for (const failure of ['reject', 'not-ok', 'missing-acknowledgment']) {
    let attempt = 0;
    const timers = clipboardTimers();
    const f = fixture({ timers, invokeWrite: () => {
      if (++attempt > 1) return { ok: true };
      if (failure === 'reject') throw new Error('Clipboard is unavailable');
      return failure === 'not-ok' ? { ok: false } : undefined;
    } });
    const { copy, label } = sharedClipControls(f);
    await f.emit('click', copy);
    assert.equal(label.textContent, 'Copy failed', failure);
    assert.equal(copy.disabled, false);
    assert.match(copy.title, /copy failed|try again/i);
    timers.advance(5000); assert.equal(label.textContent, 'Copy failed', 'failure must not disappear before the user retries');
    await f.emit('click', copy);
    assert.equal(attempt, 2); assert.equal(label.textContent, 'Copied'); assert.equal(copy.disabled, false);
  }
});

test('blank URLs and preview mode never claim that a clip link was copied', async () => {
  const f = fixture();
  for (const text of ['', ' \n\t ']) {
    assert.equal(await f.hooks.copyToClipboard(text), false);
  }
  const empty = sharedClipControls(f, '');
  assert.equal(await f.hooks.copyClipLink(empty.copy), false);
  assert.equal(empty.label.textContent, 'Copy');
  assert.equal(empty.copy.disabled, false);
  assert.deepEqual(f.writes, []);
  f.hooks.setCore(null);
  const preview = sharedClipControls(f);
  await f.emit('click', preview.copy);
  assert.equal(preview.label.textContent, 'Copy failed');
  assert.equal(preview.copy.disabled, false);
  assert.deepEqual(f.writes, []);
});

test('a previous Copied timer cannot overwrite a new pending copy or its failure', async () => {
  let attempts = 0;
  const timers = clipboardTimers(), started = deferredClipboard(), second = deferredClipboard();
  const f = fixture({ timers, invokeWrite: () => {
    if (++attempts === 1) return { ok: true };
    started.resolve(); return second.promise;
  } });
  const { copy, label } = sharedClipControls(f);
  await f.emit('click', copy);
  assert.equal(label.textContent, 'Copied');
  timers.advance(800);
  const retry = f.emit('click', copy);
  await started.promise;
  timers.advance(800);
  assert.equal(label.textContent, 'Copying…');
  assert.equal(copy.disabled, true);
  second.resolve({ ok: false }); await retry;
  timers.advance(5000);
  assert.equal(label.textContent, 'Copy failed'); assert.equal(copy.disabled, false);
});

test('a published clip keeps its URL and completed upload when copying fails, without uploading again', async () => {
  for (const failure of ['reject', 'not-ok']) {
    const timers = clipboardTimers(), started = deferredClipboard(), copied = deferredClipboard();
    let copyAttempts = 0;
    const url = 'https://revu.lol/shared-tempo-301';
    const f = fixture({ timers, invokeWrite: (command) => {
      if (command === 'share_clip') return { ok: true, shareUrl: url };
      assert.equal(command, 'copy_text_to_clipboard');
      if (++copyAttempts > 1) return { ok: true };
      started.resolve(); return copied.promise;
    } });
    const { copy, share, link, label } = sharedClipControls(f, '');
    const job = f.hooks.shareJob(301);
    let finished = false;
    const uploading = f.hooks.uploadShareJob(job).then(result => { finished = true; return result; });
    await started.promise;
    assert.equal(finished, false, 'sharing waits for copying before choosing the accurate completion message');
    if (failure === 'reject') copied.reject(new Error('503 temporarily unavailable'));
    else copied.resolve({ ok: false });
    assert.equal(await uploading, 'done');
    assert.equal(job.status, 'done'); assert.equal(job.url, url); assert.equal(job.copied, false);
    assert.equal(job.attempts, 0); assert.match(job.message, /shared.*copy/i);
    assert.equal(copy.dataset.shareUrl, url); assert.equal(copy.hidden, false); assert.equal(link.textContent, url);
    assert.equal(share.dataset.shareUrl, url);
    assert.equal(timers.size, 0, 'clipboard failure must not schedule an upload retry');
    await f.emit('click', share);
    await f.emit('click', copy);
    assert.equal(label.textContent, 'Copied');
    assert.deepEqual(f.writes.map(write => write.command), ['share_clip', 'copy_text_to_clipboard', 'copy_text_to_clipboard']);
    assert.equal(job.url, url);
  }
});
