import test from 'node:test';
import assert from 'node:assert/strict';
import { formatTranscriptTime, noteTranscriptProgress, renderClipTranscript, transcriptProgressText } from '../ui/clip-transcript.mjs';

// Minimal DOM: elements with children, dataset, textContent and listeners.
function fakeDocument() {
  const doc = {
    createElement(tag) {
      const listeners = new Map();
      const el = { tagName: tag.toUpperCase(), className: '', dataset: {}, children: [], hidden: false, open: false, type: '',
        _text: '', ownerDocument: doc,
        get textContent() { return this._text + this.children.map(child => child.textContent).join(''); },
        set textContent(value) { this._text = String(value); this.children = []; },
        get firstChild() { return this.children[0] || null; },
        appendChild(child) { this.children.push(child); return child; },
        removeChild(child) { this.children = this.children.filter(node => node !== child); return child; },
        addEventListener(type, fn) { listeners.set(type, [...(listeners.get(type) || []), fn]); },
        dispatch(type, event = {}) { for (const fn of listeners.get(type) || []) fn({ preventDefault() {}, stopPropagation() {}, ...event }); },
        set innerHTML(_) { throw new Error('transcripts render with textContent only'); },
      };
      return el;
    },
  };
  return doc;
}
const all = (el, predicate) => [el, ...el.children.flatMap(child => all(child, () => true))].filter(predicate);
const byClass = (el, name) => all(el, node => node.className.split(' ').includes(name));
const host = () => { const doc = fakeDocument(); const el = doc.createElement('div'); el.hidden = true; return el; };
const segments = [{ start: 1.234, end: 3.5, text: 'first gank comes at three minutes' },
  { start: 65.5, end: 70, text: '  ward the river  ' }];
const narration = (status, extra = {}) => ({ bookmarkId: 7, transcriptStatus: status, transcript: null, ...extra });

test('a ready transcript renders a collapsed disclosure with counted lines that carry their clip time', () => {
  const el = host();
  renderClipTranscript(el, narration('ready', { transcript: { version: 1, language: 'en', segments } }));
  assert.equal(el.hidden, false);
  const [details] = byClass(el, 'vp-clip-transcript');
  assert.equal(details.tagName, 'DETAILS');
  assert.equal(details.open, false);
  assert.equal(details.children[0].tagName, 'SUMMARY');
  assert.equal(details.children[0].textContent, 'Transcript (2 lines)');
  assert.equal(byClass(el, 'vp-clip-transcript-caption')[0].textContent, 'Auto-generated transcript');
  const lines = byClass(el, 'vp-clip-transcript-line');
  assert.deepEqual(lines.map(line => [line.tagName, line.type, line.dataset.action, line.dataset.t, line.dataset.bmId]),
    [['BUTTON', 'button', 'transcript_seek', '1.234', '7'], ['BUTTON', 'button', 'transcript_seek', '65.5', '7']]);
  assert.deepEqual(lines.map(line => line.children.map(child => child.textContent)),
    [['0:01', 'first gank comes at three minutes'], ['1:05', 'ward the river']]);
  // Clicks on the disclosure chrome never reach the row's jump action.
  assert.equal(el.dataset.action, 'transcript_panel');
  assert.equal(details.dataset.action, 'transcript_panel');
});

test('one line, empty segments and malformed segments use the documented copy', () => {
  const one = host();
  renderClipTranscript(one, narration('ready', { transcript: { version: 1, language: '', segments: [segments[0], { start: -1, text: 'bad' }, { start: 2, text: '   ' }, null] } }));
  assert.equal(byClass(one, 'vp-clip-transcript')[0].children[0].textContent, 'Transcript (1 line)');
  for (const transcript of [{ version: 1, language: 'en', segments: [] }, null, { segments: 'nope' }]) {
    const el = host();
    renderClipTranscript(el, narration('ready', { transcript }));
    assert.equal(el.textContent, 'No speech detected.');
    assert.equal(byClass(el, 'vp-clip-transcript').length, 0);
  }
});

test('an opened transcript stays open across row re-renders', () => {
  const ready = narration('ready', { bookmarkId: 99, transcript: { version: 1, language: 'en', segments } });
  const first = host();
  renderClipTranscript(first, ready);
  const details = byClass(first, 'vp-clip-transcript')[0];
  details.open = true; details.dispatch('toggle');
  const second = host();
  renderClipTranscript(second, ready);
  assert.equal(byClass(second, 'vp-clip-transcript')[0].open, true);
  byClass(second, 'vp-clip-transcript')[0].open = false; byClass(second, 'vp-clip-transcript')[0].dispatch('toggle');
  const third = host();
  renderClipTranscript(third, ready);
  assert.equal(byClass(third, 'vp-clip-transcript')[0].open, false);
});

test('pending, processing, sign-in, failure and quota states render exact copy and actions', () => {
  const cases = [
    ['pending', 'Writing transcript...', []],
    ['processing', 'Writing transcript...', []],
    ['needs_login', 'Sign in to get a transcript of your narration.Sign in', [['transcript_signin', 'Sign in']]],
    ['failed', 'Transcript failed. Try again.Retry', [['transcript_retry', 'Retry']]],
    ['quota', 'Daily transcript limit reached. Try again tomorrow.Retry', [['transcript_retry', 'Retry']]],
  ];
  for (const [status, text, actions] of cases) {
    const el = host();
    renderClipTranscript(el, narration(status));
    assert.equal(el.hidden, false, status);
    assert.equal(el.textContent, text, status);
    const buttons = all(el, node => node.tagName === 'BUTTON');
    assert.deepEqual(buttons.map(button => [button.dataset.action, button.textContent]), actions, status);
    for (const button of buttons) { assert.equal(button.dataset.bmId, '7'); assert.equal(button.type, 'button'); }
  }
});

test('no narration or an unknown status hides and clears the host', () => {
  const el = host();
  renderClipTranscript(el, narration('failed'));
  renderClipTranscript(el, null);
  assert.equal(el.hidden, true); assert.equal(el.children.length, 0);
  renderClipTranscript(el, narration('later'));
  assert.equal(el.hidden, true);
  renderClipTranscript(null, narration('failed')); // no host is a no-op
});

test('processing shows chunk counts from the last progress event for that clip only', () => {
  noteTranscriptProgress({ gameId: 1, bookmarkId: 7, transcriptStatus: 'processing', chunksDone: 3, chunksTotal: 10 });
  const el = host();
  renderClipTranscript(el, narration('processing'));
  assert.equal(el.textContent, 'Writing transcript (3 of 10)...');
  assert.equal(transcriptProgressText(8), 'Writing transcript...');
  noteTranscriptProgress({ gameId: 1, bookmarkId: 7, transcriptStatus: 'processing', chunksDone: 12, chunksTotal: 10 });
  assert.equal(transcriptProgressText(7), 'Writing transcript (10 of 10)...');
  noteTranscriptProgress({ gameId: 1, bookmarkId: 7, transcriptStatus: 'ready', chunksDone: 10, chunksTotal: 10 });
  assert.equal(transcriptProgressText(7), 'Writing transcript...');
  noteTranscriptProgress({ bookmarkId: 7, transcriptStatus: 'processing', chunksDone: 1, chunksTotal: 0 });
  assert.equal(transcriptProgressText(7), 'Writing transcript...');
  noteTranscriptProgress(null); noteTranscriptProgress({ bookmarkId: 'x' });
});

test('callbacks are optional and receive the line time or bookmark id', () => {
  const seen = [];
  const el = host();
  renderClipTranscript(el, narration('ready', { transcript: { version: 1, language: 'en', segments } }),
    { onSeek: (t, id) => seen.push(['seek', t, id]) });
  byClass(el, 'vp-clip-transcript-line')[1].dispatch('click');
  const failed = host();
  renderClipTranscript(failed, narration('failed'), { onRetry: id => seen.push(['retry', id]) });
  all(failed, node => node.tagName === 'BUTTON')[0].dispatch('click');
  const login = host();
  renderClipTranscript(login, narration('needs_login'), { onSignIn: id => seen.push(['signin', id]) });
  all(login, node => node.tagName === 'BUTTON')[0].dispatch('click');
  assert.deepEqual(seen, [['seek', 65.5, 7], ['retry', 7], ['signin', 7]]);
});

test('transcript times read as m:ss, or h:mm:ss past an hour', () => {
  assert.equal(formatTranscriptTime(0), '0:00');
  assert.equal(formatTranscriptTime(1.999), '0:01');
  assert.equal(formatTranscriptTime(599.5), '9:59');
  assert.equal(formatTranscriptTime(3725), '1:02:05');
  assert.equal(formatTranscriptTime(-4), '0:00');
  assert.equal(formatTranscriptTime('x'), '0:00');
});
