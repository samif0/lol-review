// Copy, formatting and the start plan for "Share full game" (vod-share.mjs). Pure: no DOM and
// no commands, so the page module stays small and these stay easy to test. All of the
// feature's player-facing copy lives here; the sidecar's `error` sentence is only a fallback.

export const SHARE_LINK = /^https:\/\/revu\.lol\/v\/[0-9A-Za-z]{7}$/;
// wrangler dev serves the watch page on loopback; nothing else is ever shown or opened.
const DEV_SHARE_LINK = /^http:\/\/(?:localhost|127\.0\.0\.1)(?::\d{1,5})?\/v\/[0-9A-Za-z]{7}$/;

export const POOL_720_HINT = 'Not enough sharing space for 1080p right now, so this game will be shared at 720p.';
export const SIZE_720_NOTE = 'This game is long, so it will be shared at 720p.';
export const KEEP_REVIEWING = 'You can close this and keep reviewing while Revu prepares and uploads the video.';
export const STOPPED = 'Sharing stopped. The link no longer works.';
// Stop sharing could not reach Revu's server (offline, signed out, server error): the link still works.
export const STOPPED_QUEUED = "Revu couldn't stop the link yet, so it still works. Try again, or stop sharing at revu.lol/my-clips.";
export const LINK_READY = 'Full game link ready.';
export const STOP_FAILED = "Couldn't stop sharing. Try again.";
export const CHECKING = 'Checking sharing space...';
const FALLBACK = "Sharing didn't finish. Try again.";

const COPY = {
  needs_login: 'Sign in to share this game.',
  busy: 'Another game is still being shared. Try again when it finishes.',
  starting: 'Revu is still starting. Try again in a moment.',
  closing: 'Revu is closing. Share again after it restarts.',
  stopping: 'Stopping the previous share. Try again in a moment.',
  network: "Couldn't reach Revu. Check your connection and try again.",
  sharing_paused: 'Full-game sharing is paused right now. Try again later.',
  too_long: "Games longer than 2 hours can't be shared.",
  too_large: 'This game is too large to share, even at 720p.',
  no_recording: "This game's recording isn't on this PC.",
  disk_full: "There isn't enough free disk space to prepare the video.",
  encode_failed: "Revu couldn't prepare the video. Try again.",
  game_in_progress: 'Finish your game first, then share.',
  game_started: 'Sharing stopped because a game started. Share again after the game.',
  upload_failed: "The upload didn't finish. Check your connection and try again.",
};

/** The share link when it is a revu.lol watch link (or a loopback dev link), else ''. */
export function safeShareUrl(url) {
  const value = typeof url === 'string' ? url.trim() : '';
  return SHARE_LINK.test(value) || DEV_SHARE_LINK.test(value) ? value : '';
}

/** Local date and time: weekday and time within 6 days, otherwise month, day and time. */
export function formatShareTime(unixSeconds, { now = Date.now(), locale, timeZone } = {}) {
  const ms = Number(unixSeconds) * 1000;
  if (!Number.isFinite(ms) || ms <= 0) return '';
  const options = ms - now < 6 * 86_400_000
    ? { weekday: 'long', hour: 'numeric', minute: '2-digit' }
    : { month: 'long', day: 'numeric', hour: 'numeric', minute: '2-digit' };
  if (timeZone) options.timeZone = timeZone;
  try { return new Intl.DateTimeFormat(locale, options).format(new Date(ms)); }
  catch (_) { return new Date(ms).toLocaleString(); }
}

/** Decimal gigabytes with one decimal ("1.3 GB"); never below 0.1 GB. */
export function formatShareSize(bytes) {
  const n = Number(bytes);
  if (!Number.isFinite(n) || n <= 0) return '';
  return `${Math.max(0.1, Math.round(n / 1e8) / 10).toFixed(1)} GB`;
}

const percentOf = value => Math.max(0, Math.min(100, Math.floor(Number(value) || 0)));
export function vodShareProgressLabel(job) {
  switch (job?.phase) {
    case 'queued': case 'preparing': return `Preparing video ${percentOf(job.percent)}%`;
    case 'uploading': return `Uploading ${percentOf(job.percent)}%`;
    case 'finishing': return 'Finishing';
    default: return job?.state === 'running' ? `Preparing video ${percentOf(job.percent)}%` : '';
  }
}

/** The same label without its percent: what a screen reader hears, once per phase. */
export function vodSharePhaseLabel(job) {
  switch (job?.phase) {
    case 'queued': case 'preparing': return 'Preparing video';
    case 'uploading': return 'Uploading';
    case 'finishing': return 'Finishing';
    default: return job?.state === 'running' ? 'Preparing video' : '';
  }
}

/** The copy for one errorCode. '' means no message (cancelled, not_allowed). can720: a 1080p
 * attempt that 720p would still fit, so "Try 720p" is honest advice. */
export function vodShareErrorText(code, options = {}) {
  const { retryAt = null, error = '', limits = null, time = formatShareTime, can720 = true } = options;
  const when = retryAt ? time(retryAt) : '';
  switch (code) {
    case 'cancelled': case 'not_allowed': return '';
    case 'pool_full': {
      const space = when ? `Sharing space is full until ${when}.` : 'Sharing space is full right now.';
      return can720 ? `${space} Try 720p, or save the file instead.`
        : `${space} Try again ${when ? 'then' : 'later'}, or save the file instead.`;
    }
    case 'live_limit': return `You already share ${Number(limits?.maxLive) || 3} games. Stop sharing one to share this game.`;
    case 'daily_limit': {
      const started = `You've started ${Number(limits?.maxStartsPerDay) || 6} shares today.`;
      return when ? `${started} You can share again after ${when}.` : `${started} You can share again tomorrow.`;
    }
    default: return Object.hasOwn(COPY, code) ? COPY[code] : (typeof error === 'string' && error.trim()) || FALLBACK;
  }
}

export function vodShareTtlText(ttlSeconds) {
  const days = Math.max(1, Math.round((Number(ttlSeconds) || 259_200) / 86_400));
  return `Anyone with the link can watch for ${days} ${days === 1 ? 'day' : 'days'}.`;
}

/** The share sheet's start state for a status reply and the quality the player picked. */
export function vodSharePlan(status, requested = '1080p') {
  const recording = status?.recording || {};
  const pool = status?.pool || null, limits = status?.limits || null;
  const fits1080 = !pool || pool.fits1080 !== false, fits720 = !pool || pool.fits720 !== false;
  const disable1080 = !fits1080 && fits720;
  const quality = disable1080 || requested === '720p' ? '720p' : '1080p';
  let block = null;
  // The sidecar reports sharingOn false whenever it could not ask the proxy; only a reply
  // from the proxy can say sharing is paused.
  if (status?.sharingOn === false && status?.reachable !== false) block = { code: 'sharing_paused' };
  else if (recording.tooLong) block = { code: 'too_long' };
  else if (recording.available === false) block = { code: 'no_recording' };
  else if (!fits1080 && !fits720) block = { code: 'pool_full', retryAt: pool.fits720At ?? null };
  else if (Number(status?.busyGameId) > 0) block = { code: 'busy' };
  else if (limits && Number(limits.live) >= Number(limits.maxLive)) block = { code: 'live_limit' };
  else if (limits && Number(limits.startsToday) >= Number(limits.maxStartsPerDay)) {
    block = { code: 'daily_limit', retryAt: limits.dayResetsAt ?? null };
  }
  const size = formatShareSize(quality === '720p' ? status?.estimates?.p720Bytes : status?.estimates?.p1080Bytes);
  return { quality, disable1080, block, canShare: !block, hint: disable1080 ? POOL_720_HINT : '',
    notice: status?.reachable === false && !block ? 'network' : null, sizeText: size ? `About ${size} at ${quality}.` : '',
    builtIn: recording.builtIn === true, imported: recording.builtIn === false };
}
