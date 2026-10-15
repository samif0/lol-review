// "Share full game" on the VOD page (4.2). The sidecar encodes a share copy of this game's
// own recording and uploads it as one background job: start_vod_share answers at once and
// vodShareProgress SSE events (forwarded by shell-outer as `revu:vod-share-progress`) carry
// the work, with a status poll as the fallback. A running job carries its link from the
// upload's start (4.3): the link's page shows the upload's progress until the game plays, so
// it can be copied right away. A game shared as segments (4.4, format "hls") plays at its link
// while it uploads, so Cancel asks first once people can watch it. The page never names a file.
// Its copy and the start plan live in vod-share-copy.mjs (re-exported here).

import {
  CHECKING, CONFIRM_STOP, CONFIRM_STOP_LIVE, ENDED_EARLY, KEEP_REVIEWING, LINK_CHANGED, LINK_CHANGED_SHORT, LINK_EARLY,
  LINK_LIVE, LINK_READY, LINK_REPLACING, PAUSED_GAME, POOL_720_HINT, SIZE_720_NOTE, STOP_FAILED, STOPPED, STOPPED_QUEUED,
  formatShareTime, safeShareUrl, vodShareErrorText, vodShareLiveNote, vodSharePhaseLabel, vodSharePlan, vodShareProgressLabel,
  vodShareTtlText,
} from './vod-share-copy.mjs';

export {
  SHARE_LINK, CHECKING, CONFIRM_STOP, CONFIRM_STOP_LIVE, ENDED_EARLY, KEEP_REVIEWING, LINK_CHANGED, LINK_CHANGED_SHORT,
  LINK_EARLY, LINK_GONE, LINK_LIVE, LINK_READY, LINK_REPLACING, LINK_WATCH_NOW, LINK_WATCH_SOON, PAUSED_GAME, POOL_720_HINT,
  SIZE_720_NOTE, STOP_FAILED, STOPPED, STOPPED_QUEUED, SWITCHED_720, UPLINK_720_HINT, UPLINK_SLOW, formatShareSize,
  formatShareTime, safeShareUrl, shareQualityLabel, vodShareErrorText, vodShareLiveNote, vodSharePhaseLabel, vodSharePlan,
  vodShareProgressLabel, vodShareTtlText,
} from './vod-share-copy.mjs';

const percentOf = value => Math.max(0, Math.min(100, Math.floor(Number(value) || 0)));
const hook = (root, name) => root?.querySelector?.(`[data-vs="${name}"]`) || null;
const viewOf = (root, name) => root?.querySelector?.(`[data-vs-view="${name}"]`) || null;
const setHidden = (el, hidden) => { if (el) el.hidden = !!hidden; };
const setText = (el, text) => { if (el && el.textContent !== text) el.textContent = text; };
const showText = (el, text) => { setText(el, text); setHidden(el, !text); };
const setDisabled = (root, names, off) => names.forEach(name => { const el = hook(root, name); if (el) el.disabled = !!off; });

/**
 * openButton: the header "Share full game" button. row: the quiet row above the player
 * (an existing link, a running share, or a result). sheet: the <dialog>. invoke: the page's
 * command bridge. platform: openExternal and capabilities. target: where shell-outer
 * dispatches `revu:vod-share-progress`.
 */
export function createVodShare({ openButton, row, sheet, invoke, platform = null, target = globalThis.window,
  pollMs = 15_000, timers = globalThis, now = () => Date.now(), locale, timeZone } = {}) {
  let gameId = 0, generation = 0, epoch = 0, lastRead = -Infinity, reading = null;
  let status = null, job = null, share = null, denied = false, watching = false, cancelling = false;
  let starting = false, stopping = false, checking = false, sheetOpen = false, confirming = null;
  let quality = '1080p', qualityPicked = false, includeNotes = false, message = null, rowNote = null, canOpen = false;
  let pollTimer = null, polling = false, pollFailures = 0, rowNoteTimer = null, justDone = false;
  let rowMode = 'hidden', rowSaid = '', progressSaid = '';
  const copyTimers = new Map();
  const time = unix => formatShareTime(unix, { now: now(), locale, timeZone });
  const running = () => job?.state === 'running';
  const link = () => safeShareUrl(share?.url);
  // A running share's link, from its upload's start: it already works.
  const jobLink = () => (running() ? job.url || '' : '');
  const anyLink = () => jobLink() || link();
  const textOf = entry => (entry?.text ? entry.text : vodShareErrorText(entry?.code, { retryAt: entry?.retryAt,
    error: entry?.error, limits: status?.limits, time, can720: entry?.quality === '1080p' && status?.pool?.fits720 !== false,
    linkShown: entry?.linkShown === true, kept: entry?.kept === true }));
  // The start plan: the player's pick, or 'auto' (720p for an account whose last upload was too slow) before any.
  const planNow = () => vodSharePlan(status, qualityPicked ? quality : 'auto');
  // While a share runs: its progress, that it waits for the player's game, or that the link changed.
  const runningDetail = () => (job.paused ? `${vodShareProgressLabel(job)}. ${PAUSED_GAME}`
    : job.linkChanged ? `${vodShareProgressLabel(job)}. ${LINK_CHANGED_SHORT}` : vodShareProgressLabel(job));
  // Spoken once per phase, and once more when the link arrives.
  const runningSaid = () => (jobLink() ? `${vodSharePhaseLabel(job)}. ${LINK_LIVE}` : vodSharePhaseLabel(job));
  // A calm result note: in the sheet when it is open, else in the row (where it fades).
  const say = note => { if (sheetOpen) message = note; else showRowNote(note); };

  Promise.resolve().then(() => platform?.capabilities?.()).then(caps => {
    canOpen = caps?.externalLinks === true; paint();
  }, () => { canOpen = false; });

  function stopPolling() { if (pollTimer) timers.clearInterval(pollTimer); pollTimer = null; pollFailures = 0; }
  function startPolling() { if (!pollTimer) pollTimer = timers.setInterval(() => { void poll(); }, pollMs); }
  function reset(id) {
    generation++; gameId = id; status = null; job = null; share = null; watching = false; cancelling = false;
    starting = false; stopping = false; checking = false; confirming = null; message = null; rowNote = null;
    quality = '1080p'; qualityPicked = false; includeNotes = false; lastRead = -Infinity; reading = null; justDone = false;
    denied = false;
    stopPolling();
    closeSheet();
  }

  function takeJob(source) {
    // Events and status reads carry the upload's link (null until it starts); a start reply has none.
    const url = 'url' in source ? safeShareUrl(source.url) : job?.url || '';
    const flag = name => (name in source ? source[name] === true : job?.[name] === true);
    job = { state: 'running', phase: source.phase || 'queued', percent: source.percent,
      quality: source.quality ?? job?.quality ?? null, qualityReason: source.qualityReason ?? job?.qualityReason ?? null,
      url, expiresAt: url ? source.expiresAt ?? job?.expiresAt ?? null : null, linkChanged: flag('linkChanged'),
      format: source.format ?? job?.format ?? 'mp4', watchable: flag('watchable'), uplinkSlow: flag('uplinkSlow'),
      paused: flag('paused') };
    rowNote = null;
    if (!watching) { watching = true; startPolling(); }
  }

  function report(source) {
    const code = String(source?.errorCode || (source?.needsLogin ? 'needs_login' : ''));
    if (code === 'not_allowed') { denied = true; closeSheet(); return; }
    if (code === 'cancelled') { message = null; return; }
    const entry = { code, retryAt: source?.retryAt ?? null, error: typeof source?.error === 'string' ? source.error : '',
      quality: source?.quality ?? null, linkShown: source?.linkShown === true, kept: source?.kept === true };
    if (sheetOpen) message = entry; else rowNote = { ...entry, error: true, retry: true };
  }

  function finishJob(source, { reread = true } = {}) {
    const attempted = source.quality ?? job?.quality ?? null;
    const linkShown = !!job?.url, linkChanged = source.linkChanged === true || job?.linkChanged === true;
    // A segmented share people could already watch keeps its link when it ends early.
    const kept = (source.format ?? job?.format) === 'hls' && (source.watchable === true || job?.watchable === true);
    if (confirming === 'sheet-cancel' || confirming === 'row-cancel') confirming = null;
    watching = false; cancelling = false; job = null; stopPolling();
    if (source.state === 'done') {
      const url = safeShareUrl(source.url) || link();
      if (url) {
        share = { ...(share || {}), url, expiresAt: source.expiresAt ?? share?.expiresAt ?? null,
          markersAttached: source.markersAttached ?? share?.markersAttached ?? null,
          quality: source.quality ?? share?.quality ?? null };
        // The next share starts from the defaults again: 1080p, notes off.
        message = null; rowNote = null; justDone = true; includeNotes = false; quality = '1080p'; qualityPicked = false;
        // A link copied before the upload started over no longer works.
        if (linkChanged) say({ text: LINK_CHANGED, error: false });
        else if (source.truncated === true) say({ text: ENDED_EARLY, error: false });
      }
      epoch++; // a read already in flight predates this link
      if (reread) void refresh(true); // the stored entry carries the id and the exact expiry
    } else if (source.state === 'cancelled') { message = null; rowNote = null; }
    else {
      report({ ...source, quality: attempted, linkShown: (linkShown || linkChanged) && !kept, kept });
      if (kept && reread) { epoch++; void refresh(true); } // the stored link of the part that uploaded
    }
  }

  function applyStatus(reply, startedEpoch) {
    status = reply;
    // A share started, finished or stopped after this read began: what the page saw since wins.
    if (startedEpoch !== epoch) return;
    // The read right after a finished share may predate its stored entry; keep the new link once.
    share = reply.share && safeShareUrl(reply.share.url) ? reply.share : justDone ? share : null;
    justDone = false;
    const current = reply.job;
    if (current?.state !== 'running') cancelling = false;
    if (current?.state === 'running') { if (!cancelling) takeJob(current); }
    else if (watching) finishJob(current || { state: 'error', errorCode: 'upload_failed' }, { reread: false });
  }

  const viewNow = () => (running() ? 'progress' : link() ? 'done' : 'start');
  const focusView = () => focus({ progress: 'cancel', done: 'copy', start: 'share' }[viewNow()]);
  // A job that ends while the sheet is open moves focus to what the new view offers.
  function settle(before) { paint(); if (sheetOpen && viewNow() !== before) focusView(); }

  // One status read. Its reply applies only to the game and generation that asked; it throws when
  // the sidecar does not answer.
  async function readStatus() {
    const id = gameId, gen = generation, startedEpoch = epoch;
    const reply = await invoke('get_vod_share_status', { gameId: id });
    if (gen !== generation || !reply || typeof reply !== 'object') return;
    if (Number(reply.gameId) > 0 && Number(reply.gameId) !== id) return;
    const before = viewNow();
    lastRead = now();
    applyStatus(reply, startedEpoch);
    if (checking) paint(); else settle(before);
  }

  async function refresh(force = false) {
    if (!(gameId > 0)) return;
    if (reading && !force) return reading;
    if (!force && now() - lastRead < 30_000) return;
    const gen = generation;
    const read = readStatus().catch(() => { if (gen === generation) paint(); });
    reading = read;
    try { await read; } finally { if (reading === read) reading = null; }
  }

  async function poll() {
    if (polling || !watching) return;
    const gen = generation;
    polling = true;
    try {
      await readStatus();
      pollFailures = 0;
    } catch (_) {
      if (gen === generation && watching && ++pollFailures >= 4) {
        const before = viewNow();
        finishJob({ state: 'error', errorCode: 'upload_failed' });
        settle(before);
      }
    } finally { polling = false; }
  }

  function onProgress(event) {
    const detail = event?.detail;
    if (!detail || !(gameId > 0) || Number(detail.gameId) !== gameId) return;
    const before = viewNow();
    if (detail.state === 'running') { if (cancelling) return; takeJob(detail); }
    else if (detail.state === 'done' || (watching && ['error', 'cancelled'].includes(detail.state))) finishJob(detail);
    else return;
    settle(before);
  }

  /** vod: the page's snapshot, or null when there is no backend (browser preview). */
  async function check(vod) {
    try {
      const id = Number(vod?.gameId);
      if (!vod || !Number.isSafeInteger(id) || id <= 0 || typeof invoke !== 'function') { reset(0); paint(); return; }
      if (id !== gameId) reset(id);
      await refresh(false);
    } catch (error) { console.warn('[vod-share] status unavailable:', error); }
  }

  function showSheet() {
    try { if (sheet && !sheet.open) sheet.showModal(); } catch (_) { /* a detached dialog stays closed */ }
    sheetOpen = !!sheet;
  }
  function closeSheet() {
    if (!sheetOpen && !sheet?.open) return;
    sheetOpen = false;
    try { sheet.close(); } catch (_) { /* already closed */ }
  }
  const focus = name => { try { hook(sheet, name)?.focus?.(); } catch (_) { /* focus is best effort */ } };

  async function open() {
    // A link or a running share keeps its sheet (and Stop sharing) even after a not_allowed reply.
    if (!(gameId > 0) || checking || (denied && !running() && !link())) return;
    const gen = generation;
    // "Include my notes and bookmarks" starts off for every new share; Try again keeps the choice.
    if (!running() && !link() && !rowNote?.retry) includeNotes = false;
    rowNote = null; confirming = null;
    if (!running() && !link()) message = null;
    showSheet();
    checking = true; paint();
    await refresh(true); // a fresh pool and fresh limits before the player chooses
    if (gen !== generation) return;
    checking = false; paint();
    focusView();
  }

  async function start() {
    const plan = planNow();
    if (!(gameId > 0) || starting || running() || checking || !plan.canShare) return;
    const id = gameId, gen = generation;
    starting = true; message = null; paint();
    let reply = null;
    try { reply = await invoke('start_vod_share', { payload: { gameId: id, quality: plan.quality, includeNotes } }); }
    catch (_) { reply = null; }
    if (gen !== generation) return;
    starting = false;
    if (reply?.ok === true && reply.alreadyShared && safeShareUrl(reply.share?.url)) share = reply.share;
    else if (reply?.ok === true && reply.accepted) {
      epoch++; cancelling = false;
      takeJob({ phase: 'queued', percent: 0, quality: reply.quality || plan.quality, qualityReason: reply.qualityReason });
    } else report({ ...(reply?.ok === false ? reply : { errorCode: '', error: reply?.error }), quality: plan.quality });
    paint();
    if (sheetOpen) focusView();
  }

  // Once people can watch a segmented share, Cancel asks first, like Stop sharing.
  async function cancel(where = 'sheet', confirmed = false) {
    if (!running() || cancelling) return;
    if (job.watchable && !confirmed) {
      confirming = where === 'row' ? 'row-cancel' : 'sheet-cancel'; paint();
      (where === 'row' ? hook(row, 'row-keep') : hook(sheet, 'keep'))?.focus?.();
      return;
    }
    const id = gameId, gen = generation, linkShown = !!jobLink() || job?.linkChanged === true;
    cancelling = true; confirming = null; paint();
    try { await invoke('cancel_vod_share', { payload: { gameId: id } }); } catch (_) { /* the next status read settles it */ }
    if (gen !== generation) return;
    job = null; watching = false; message = null; rowNote = null; stopPolling();
    // The link already shown stops working with the upload.
    if (linkShown) say({ text: STOPPED, error: false });
    paint();
    if (sheetOpen) focus('share');
  }

  function showRowNote(note) {
    rowNote = note;
    if (rowNoteTimer) timers.clearTimeout(rowNoteTimer);
    // A calm result (sharing stopped) fades after a few seconds; an error stays until acted on.
    rowNoteTimer = note && !note.error
      ? timers.setTimeout(() => { rowNoteTimer = null; if (rowNote === note) { rowNote = null; paint(); } }, 6000) : null;
  }

  async function stop(where) {
    if (stopping || !link()) return;
    const id = gameId, gen = generation;
    stopping = true; paint();
    let reply = null;
    try { reply = await invoke('stop_vod_share', { payload: { gameId: id } }); } catch (_) { reply = null; }
    if (gen !== generation) return;
    stopping = false; confirming = null;
    const inRow = where === 'row' || !sheetOpen;
    const stillLive = reply?.ok === true && reply.remoteDeleted !== true && reply.queued === true;
    const stopped = reply?.ok === true && !stillLive;
    const note = stopped ? { text: STOPPED, error: false } : { text: stillLive ? STOPPED_QUEUED : STOP_FAILED, error: true };
    if (inRow) showRowNote(note); else message = note;
    if (stopped) { share = null; includeNotes = false; quality = '1080p'; qualityPicked = false; epoch++; void refresh(true); }
    paint();
    if (!inRow) focusView();
  }

  async function copyLink(button, field) {
    const url = anyLink();
    if (!url || !button) return;
    let ok = false;
    try { ok = (await invoke('copy_text_to_clipboard', { text: url }))?.ok === true; } catch (_) { ok = false; }
    button.textContent = ok ? 'Copied' : "Couldn't copy";
    if (!ok) { try { field?.focus?.(); field?.select?.(); } catch (_) { /* the link stays selectable */ } }
    timers.clearTimeout(copyTimers.get(button));
    copyTimers.set(button, timers.setTimeout(() => { button.textContent = 'Copy'; }, 2000));
  }

  function openLink() {
    const url = anyLink();
    if (url && canOpen) Promise.resolve().then(() => platform.openExternal(url)).catch(() => {});
  }
  function actionVisible() {
    if (running() || link()) return true;
    return !denied && !!status?.signedIn && !!status.allowed && !!status.recording?.available;
  }

  function paintRow() {
    if (!row) return;
    const url = link(), live = jobLink();
    // A result note stands alone, except beside an existing link (a failed stop keeps its actions).
    const mode = running() ? 'running' : url ? 'share' : rowNote ? 'note' : 'hidden';
    const note = mode === 'running' ? null : rowNote;
    row.hidden = mode === 'hidden';
    if (row.dataset) row.dataset.state = mode;
    row.classList?.toggle?.('is-error', !!note?.error);
    showText(hook(row, 'row-label'), mode === 'running' ? 'Sharing full game' : mode === 'share' ? 'Full game link' : '');
    // A running share shows its link as soon as its upload has started.
    showText(hook(row, 'row-url'), mode === 'share' ? url : mode === 'running' ? live : '');
    const detail = mode === 'running' ? runningDetail()
      : note ? textOf(note)
        : mode === 'share' && share.expiresAt ? `Link expires ${time(share.expiresAt)}.` : '';
    setText(hook(row, 'row-detail'), detail);
    // Spoken: each phase once (not every percent), a result note, a link that just arrived.
    const said = mode === 'running' ? runningSaid()
      : note ? textOf(note)
        : mode === 'share' ? (rowMode === 'running' || rowSaid === LINK_READY ? LINK_READY : '') : '';
    rowMode = mode;
    if (said !== rowSaid) { rowSaid = said; setText(hook(row, 'row-live'), said); }
    const confirm = (confirming === 'row' && mode === 'share') || (confirming === 'row-cancel' && mode === 'running');
    const linked = mode === 'share' || !!live;
    setText(hook(row, 'row-confirm-text'), confirming === 'row-cancel' ? CONFIRM_STOP_LIVE : CONFIRM_STOP);
    setHidden(hook(row, 'row-confirm'), !confirm);
    setHidden(hook(row, 'row-actions'), confirm);
    setHidden(hook(row, 'row-copy'), !linked);
    setHidden(hook(row, 'row-open'), !linked || !canOpen);
    setHidden(hook(row, 'row-stop'), mode !== 'share');
    setHidden(hook(row, 'row-cancel'), mode !== 'running');
    setHidden(hook(row, 'row-retry'), !(mode === 'note' && rowNote.retry));
    setDisabled(row, ['row-stop', 'row-confirm-stop', 'row-keep'], stopping);
    setDisabled(row, ['row-cancel'], cancelling);
  }

  function paintSheet() {
    if (!sheet) return;
    const view = viewNow();
    if (sheet.dataset) sheet.dataset.view = view;
    for (const name of ['start', 'progress', 'done']) setHidden(viewOf(sheet, name), name !== view);
    const plan = planNow();
    // Leaving the progress view clears what was spoken, so the next share's phases are heard again.
    if (view !== 'progress' && progressSaid) { progressSaid = ''; setText(hook(sheet, 'progress-live'), ''); }
    if (view === 'start') {
      setText(hook(sheet, 'size'), checking ? CHECKING : plan.sizeText);
      const q1080 = hook(sheet, 'q1080'), q720 = hook(sheet, 'q720');
      if (q1080) { q1080.checked = plan.quality === '1080p'; q1080.disabled = plan.disable1080 || starting; }
      if (q720) { q720.checked = plan.quality === '720p'; q720.disabled = starting; }
      setText(hook(sheet, 'q1080-label'), plan.labels.p1080);
      setText(hook(sheet, 'q720-label'), plan.labels.p720);
      showText(hook(sheet, 'hint'), plan.hint);
      setText(hook(sheet, 'ttl'), vodShareTtlText(status?.ttlSeconds));
      setHidden(hook(sheet, 'imported'), !plan.imported);
      const notes = hook(sheet, 'notes');
      if (notes) { notes.checked = includeNotes; notes.disabled = starting; }
    } else if (view === 'progress') {
      setText(hook(sheet, 'progress-label'), cancelling ? 'Cancelling' : vodShareProgressLabel(job));
      const phase = cancelling ? 'Cancelling' : runningSaid();
      if (phase !== progressSaid) { progressSaid = phase; setText(hook(sheet, 'progress-live'), phase); }
      const bar = hook(sheet, 'progress-bar');
      if (bar?.style) {
        const width = job.phase === 'finishing' ? 100 : percentOf(job.percent);
        // A new phase (or a new share) starts the bar over: jump back instead of sliding backwards.
        bar.style.transition = width < (parseFloat(bar.style.width) || 0) ? 'none' : '';
        bar.style.width = `${width}%`;
      }
      // The link works from the upload's start, so it can be sent while the video is prepared.
      const live = jobLink(), field = hook(sheet, 'progress-url');
      setHidden(hook(sheet, 'progress-link'), !live);
      if (field && field.value !== live) field.value = live;
      setHidden(hook(sheet, 'progress-open'), !live || !canOpen);
      showText(hook(sheet, 'progress-link-note'), job.format === 'hls' ? vodShareLiveNote(job)
        : live ? (job.linkChanged ? LINK_CHANGED : LINK_EARLY) : job.linkChanged ? LINK_REPLACING : '');
      const reason = job.qualityReason === 'pool' ? POOL_720_HINT : job.qualityReason === 'size' ? SIZE_720_NOTE : '';
      setText(hook(sheet, 'progress-note'), reason ? `${reason} ${KEEP_REVIEWING}` : KEEP_REVIEWING);
    } else {
      const url = link(), field = hook(sheet, 'url');
      if (field && field.value !== url) field.value = url;
      setText(hook(sheet, 'expiry'), share.expiresAt ? `Link expires ${time(share.expiresAt)}.` : '');
      setHidden(hook(sheet, 'open'), !canOpen);
      setHidden(hook(sheet, 'markers'), share.markersAttached !== false);
    }
    // One message line: the latest result, else what blocks sharing right now.
    const entry = view === 'start'
      ? (message || (checking ? null : plan.block) || (plan.notice && !checking ? { code: plan.notice } : null))
      : view === 'done' && message?.text ? message : null;
    const text = entry ? textOf(entry) : '';
    const box = hook(sheet, 'message-box');
    setHidden(box, !text);
    box?.classList?.toggle?.('is-error', !!text && entry.error !== false);
    setText(hook(sheet, 'message'), text);
    setHidden(hook(sheet, 'signin'), entry?.code !== 'needs_login');
    setHidden(hook(sheet, 'folder'), !(entry?.code === 'pool_full' && plan.builtIn));
    const confirm = (confirming === 'sheet' && view === 'done') || (confirming === 'sheet-cancel' && view === 'progress');
    setText(hook(sheet, 'confirm-text'), confirming === 'sheet-cancel' ? CONFIRM_STOP_LIVE : CONFIRM_STOP);
    setHidden(hook(sheet, 'confirm'), !confirm);
    setHidden(hook(sheet, 'actions'), confirm);
    setHidden(hook(sheet, 'share'), view !== 'start');
    setDisabled(sheet, ['share'], !plan.canShare || starting || checking);
    setHidden(hook(sheet, 'cancel'), view === 'done');
    setText(hook(sheet, 'cancel'), view === 'progress' ? 'Cancel sharing' : 'Cancel');
    setDisabled(sheet, ['cancel'], view === 'progress' && cancelling);
    setHidden(hook(sheet, 'stop'), view !== 'done');
    setHidden(hook(sheet, 'done'), view !== 'done');
    setDisabled(sheet, ['stop', 'confirm-stop', 'keep'], stopping);
  }

  function paint() {
    setHidden(openButton, !(gameId > 0) || !actionVisible());
    paintRow();
    paintSheet();
  }

  const on = (root, name, handler) => hook(root, name)?.addEventListener?.('click', handler);
  openButton?.addEventListener?.('click', () => { void open(); });
  on(row, 'row-copy', () => copyLink(hook(row, 'row-copy')));
  on(row, 'row-open', openLink);
  on(row, 'row-stop', () => { confirming = 'row'; showRowNote(null); paint(); hook(row, 'row-keep')?.focus?.(); });
  on(row, 'row-keep', () => {
    const was = confirming; confirming = null; paint(); hook(row, was === 'row-cancel' ? 'row-cancel' : 'row-stop')?.focus?.();
  });
  on(row, 'row-confirm-stop', () => (confirming === 'row-cancel' ? cancel('row', true) : stop('row')));
  on(row, 'row-cancel', () => cancel('row'));
  on(row, 'row-retry', () => { void open(); });
  on(sheet, 'close', () => closeSheet());
  on(sheet, 'share', () => start());
  on(sheet, 'cancel', () => (running() ? cancel('sheet') : closeSheet()));
  on(sheet, 'copy', () => copyLink(hook(sheet, 'copy'), hook(sheet, 'url')));
  on(sheet, 'open', openLink);
  on(sheet, 'progress-copy', () => copyLink(hook(sheet, 'progress-copy'), hook(sheet, 'progress-url')));
  on(sheet, 'progress-open', openLink);
  on(sheet, 'stop', () => { confirming = 'sheet'; paint(); focus('keep'); });
  on(sheet, 'keep', () => { const was = confirming; confirming = null; paint(); focus(was === 'sheet-cancel' ? 'cancel' : 'stop'); });
  on(sheet, 'confirm-stop', () => (confirming === 'sheet-cancel' ? cancel('sheet', true) : stop('sheet')));
  on(sheet, 'done', () => closeSheet());
  on(sheet, 'folder', () => { Promise.resolve().then(() => invoke('open_recordings_folder')).catch(() => {}); });
  for (const name of ['q1080', 'q720']) {
    hook(sheet, name)?.addEventListener?.('change', event => {
      if (event?.target?.checked === false) return;
      quality = name === 'q720' ? '720p' : '1080p'; qualityPicked = true; message = null; paint();
    });
  }
  hook(sheet, 'notes')?.addEventListener?.('change', event => { includeNotes = event?.target?.checked === true; });
  // The sheet is modal: keys typed in it never reach the player's shortcuts (Space, F, arrows).
  sheet?.addEventListener?.('keydown', event => event.stopPropagation());
  sheet?.addEventListener?.('close', () => {
    sheetOpen = false;
    if (confirming === 'sheet' || confirming === 'sheet-cancel') confirming = null;
    if (!running()) message = null;
    paint();
  });
  target?.addEventListener?.('revu:vod-share-progress', onProgress);

  return {
    check, refresh: () => refresh(true), open, start, cancel, stop: (where = 'sheet') => stop(where),
    get state() { return { gameId, sheetOpen, view: viewNow(), job: job && { ...job }, share: share && { ...share }, denied }; },
    dispose() { generation++; stopPolling(); target?.removeEventListener?.('revu:vod-share-progress', onProgress); },
  };
}
