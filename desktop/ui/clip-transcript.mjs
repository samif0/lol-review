// Transcript under a narrated clip row (C2 document, C10 copy). Rendering uses
// textContent only. Buttons carry data-action so the VOD page's delegated click
// handler owns seek / retry / sign-in; optional callbacks serve other hosts.

const progressByBookmark = new Map(); // bookmarkId -> { done, total } from the last SSE
const openTranscripts = new Set();    // bookmarkIds whose transcript disclosure is open

export function formatTranscriptTime(seconds) {
  const s = Math.max(0, Math.floor(Number(seconds) || 0));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${r}` : `${m}:${r}`;
}

// clipNarrationUpdated SSE: { gameId, bookmarkId, transcriptStatus, chunksDone, chunksTotal }.
export function noteTranscriptProgress(detail) {
  const id = Number(detail?.bookmarkId);
  if (!Number.isSafeInteger(id) || id <= 0) return;
  const done = Number(detail.chunksDone), total = Number(detail.chunksTotal);
  if (detail.transcriptStatus === 'processing' && Number.isInteger(total) && total > 0
    && Number.isInteger(done) && done >= 0) progressByBookmark.set(id, { done: Math.min(done, total), total });
  else progressByBookmark.delete(id);
}

export function transcriptProgressText(bookmarkId) {
  const counts = progressByBookmark.get(Number(bookmarkId));
  return counts ? `Writing transcript (${counts.done} of ${counts.total})...` : 'Writing transcript...';
}

function validSegments(transcript) {
  const segments = Array.isArray(transcript?.segments) ? transcript.segments : [];
  return segments.filter(segment => segment && Number.isFinite(Number(segment.start)) && Number(segment.start) >= 0
    && typeof segment.text === 'string' && segment.text.trim());
}

function element(doc, tag, className, text) {
  const el = doc.createElement(tag);
  if (className) el.className = className;
  if (text !== undefined) el.textContent = text;
  return el;
}

function actionButton(doc, action, label, bookmarkId, callback) {
  const button = element(doc, 'button', `vp-clip-transcript-action`, label);
  button.type = 'button';
  button.dataset.action = action;
  button.dataset.bmId = String(bookmarkId);
  if (typeof callback === 'function') button.addEventListener('click', event => {
    event.preventDefault(); event.stopPropagation(); callback(bookmarkId);
  });
  return button;
}

function statusLine(doc, text) {
  return element(doc, 'p', 'vp-clip-transcript-status', text);
}

export function renderClipTranscript(container, narration, { onSeek, onRetry, onSignIn } = {}) {
  if (!container) return;
  const doc = container.ownerDocument || globalThis.document;
  while (container.firstChild) container.removeChild(container.firstChild);
  const bookmarkId = Number(narration?.bookmarkId) || 0;
  const status = narration?.transcriptStatus || '';
  // A no-op action keeps clicks on transcript chrome from triggering the row's jump.
  container.dataset.action = 'transcript_panel';
  const nodes = [];
  if (status === 'ready') {
    const segments = validSegments(narration.transcript);
    if (!segments.length) nodes.push(statusLine(doc, 'No speech detected.'));
    else {
      const details = element(doc, 'details', 'vp-clip-transcript');
      details.dataset.action = 'transcript_panel';
      details.open = openTranscripts.has(bookmarkId);
      details.addEventListener('toggle', () => {
        if (details.open) openTranscripts.add(bookmarkId); else openTranscripts.delete(bookmarkId);
      });
      details.appendChild(element(doc, 'summary', '', `Transcript (${segments.length} ${segments.length === 1 ? 'line' : 'lines'})`));
      details.appendChild(element(doc, 'p', 'vp-clip-transcript-caption', 'Auto-generated transcript'));
      const list = element(doc, 'div', 'vp-clip-transcript-list');
      for (const segment of segments) {
        const start = Math.round(Number(segment.start) * 1000) / 1000;
        const row = element(doc, 'button', 'vp-clip-transcript-line');
        row.type = 'button';
        row.dataset.action = 'transcript_seek';
        row.dataset.t = String(start);
        row.dataset.bmId = String(bookmarkId);
        row.appendChild(element(doc, 'span', 'vp-clip-transcript-time', formatTranscriptTime(start)));
        row.appendChild(element(doc, 'span', 'vp-clip-transcript-text', segment.text.trim()));
        if (typeof onSeek === 'function') row.addEventListener('click', event => {
          event.preventDefault(); event.stopPropagation(); onSeek(start, bookmarkId);
        });
        list.appendChild(row);
      }
      details.appendChild(list);
      nodes.push(details);
    }
  } else if (status === 'pending') {
    nodes.push(statusLine(doc, 'Writing transcript...'));
  } else if (status === 'processing') {
    nodes.push(statusLine(doc, transcriptProgressText(bookmarkId)));
  } else if (status === 'needs_login') {
    nodes.push(statusLine(doc, 'Sign in to get a transcript of your narration.'));
    nodes.push(actionButton(doc, 'transcript_signin', 'Sign in', bookmarkId, onSignIn));
  } else if (status === 'failed') {
    nodes.push(statusLine(doc, 'Transcript failed. Try again.'));
    nodes.push(actionButton(doc, 'transcript_retry', 'Retry', bookmarkId, onRetry));
  } else if (status === 'quota') {
    nodes.push(statusLine(doc, 'Daily transcript limit reached. Try again tomorrow.'));
    nodes.push(actionButton(doc, 'transcript_retry', 'Retry', bookmarkId, onRetry));
  }
  for (const node of nodes) container.appendChild(node);
  container.hidden = nodes.length === 0;
}
