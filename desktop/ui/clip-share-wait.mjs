// Clip shares run as a sidecar background job (C6 6.2). share_clip returns
// `accepted` at once; the outcome arrives as clipShareProgress SSE events, which
// shell-outer forwards into the VOD frame as `revu:clip-share-progress`. A status
// poll covers a missed event. There is no overall deadline while the job is
// queued or running: long clips can take many minutes to upload.

const MAX_POLL_FAILURES = 4;

function shareError(message, { retryable = false, needsLogin = false } = {}) {
  const error = new Error(message || 'Share failed.');
  error.retryable = !!retryable;
  error.needsLogin = !!needsLogin;
  return error;
}

export function waitForShareJob({ bookmarkId, invoke, target = globalThis.window, pollMs = 15000, onProgress } = {}) {
  const id = Number(bookmarkId);
  let settled = false, timer = null, polling = false, failures = 0;
  let resolveJob, rejectJob;
  const promise = new Promise((resolve, reject) => { resolveJob = resolve; rejectJob = reject; });
  const finish = (error, value) => {
    if (settled) return;
    settled = true;
    clearInterval(timer);
    target?.removeEventListener?.('revu:clip-share-progress', onEvent);
    if (error) rejectJob(error); else resolveJob(value);
  };
  const done = source => finish(null, { url: String(source?.url || ''), narrated: !!source?.narrated,
    transcriptAttached: !!source?.transcriptAttached });
  const failed = source => finish(shareError(source?.error ? String(source.error) : 'Share failed.', source));
  const progress = detail => {
    if (typeof onProgress !== 'function') return;
    try { onProgress(detail); } catch (_) { /* a display error never ends the job */ }
  };
  function onEvent(event) {
    const detail = event?.detail;
    if (settled || !detail || Number(detail.bookmarkId) !== id) return;
    progress(detail);
    if (detail.phase === 'done') {
      if (detail.url) done(detail);
      else failed({ error: 'Share finished, but no link came back.', retryable: false });
    } else if (detail.phase === 'error') failed(detail);
  }
  async function poll() {
    if (settled || polling) return;
    polling = true;
    try {
      const status = await invoke('get_clip_share_status', { bookmarkId: id });
      failures = 0;
      if (settled || !status) return;
      if (status.state === 'done') {
        if (status.url) done(status);
        else failed({ error: 'Share finished, but no link came back.', retryable: false });
      } else if (status.state === 'error') failed(status);
      else if (status.state === 'idle') {
        // The sidecar no longer knows this job (for example it restarted).
        failed({ error: 'The share stopped before it finished. Trying again.', retryable: true });
      } else if (status.phase) {
        progress({ bookmarkId: id, phase: status.phase, sentBytes: status.sentBytes, totalBytes: status.totalBytes });
      }
    } catch (_) {
      failures += 1;
      if (failures >= MAX_POLL_FAILURES) failed({ error: 'Revu lost track of this share. Trying again.', retryable: true });
    } finally { polling = false; }
  }
  if (!Number.isSafeInteger(id) || id <= 0 || typeof invoke !== 'function') {
    finish(shareError('Share failed.'));
  } else {
    target?.addEventListener?.('revu:clip-share-progress', onEvent);
    timer = setInterval(poll, pollMs);
  }
  return { promise, cancel: () => finish(shareError('Share cancelled.')) };
}

export function shareProgressLabel(detail) {
  switch (detail?.phase) {
    case 'uploading': {
      const sent = Number(detail.sentBytes), total = Number(detail.totalBytes);
      const percent = total > 0 && sent >= 0 ? Math.min(100, Math.floor(sent * 100 / total)) : 0;
      return `Uploading ${percent}%`;
    }
    case 'finishing': return 'Finishing...';
    case 'transcript': return 'Adding transcript...';
    case 'queued': case 'preparing': return 'Queued';
    default: return '';
  }
}
