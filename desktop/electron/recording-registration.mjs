import { stat } from 'node:fs/promises';

const validId = value => Number.isSafeInteger(value) && value > 0;
const REJECTION_LIMIT = 5;

/** The sidecar answers 409 for a conflicting identity, which repeating the same frozen
 * body cannot change. Its 400 also covers a file that is briefly locked or still settling,
 * so a 400 is final only once the file is gone or resized, or after five answers in a row.
 * Timeouts, an unavailable sidecar, 503 and other 5xx responses remain retryable.
 */
export async function registrationRejected(error, session, inspect = stat) {
  if (error?.status === 409) return true;
  if (error?.status !== 400) return false;
  session.rejections = (session.rejections ?? 0) + 1;
  if (session.rejections >= REJECTION_LIMIT) return true;
  try { return (await inspect(session.filePath)).size !== session.fileSize; }
  catch (failure) { return failure?.code === 'ENOENT'; }
}

/** Freeze one registration body from durable session facts and the latest match context.
 * Completion requires verified start timing plus fresh exact-match end-clock coverage.
 */
export function registrationFor(session, context) {
  const ended = validId(session.gameId) && context?.lastEndedGameId === session.gameId;
  const anchor = session.gameClockAnchor;
  const offset = anchor && session.startedAtVerified === true
    ? anchor.gameTimeSeconds + (Date.parse(session.startedAt) - Date.parse(anchor.observedAt)) / 1000 : null;
  const gameTimeAtVideoStart = Number.isFinite(offset) && offset >= -600 && offset <= 7200 ? offset : null;
  const endClock = context?.lastEndedGameTimeSeconds;
  const endObserved = Date.parse(context?.lastEndedObservedAt);
  const endConfirmed = Date.parse(context?.lastEndedConfirmedAt);
  const freshEndClock = Number.isFinite(endClock) && endClock > 0
    && Number.isFinite(endObserved) && Number.isFinite(endConfirmed)
    && endConfirmed >= endObserved && endConfirmed - endObserved <= 10_000;
  const coversEnd = ended && freshEndClock && gameTimeAtVideoStart !== null
    && Number.isFinite(session.durationSeconds)
    && gameTimeAtVideoStart + session.durationSeconds >= endClock - 2;
  const matchEndEvidence = ended ? { gameTimeSeconds: endClock ?? null,
    observedAt: context.lastEndedObservedAt ?? null,
    confirmedAt: context.lastEndedConfirmedAt ?? null, coversEnd: !!coversEnd } : null;
  const complete = !!coversEnd && !session.reason && gameTimeAtVideoStart <= 2;
  const reason = complete ? session.reason : session.reason || (!ended ? 'match-end-unverified'
    : !coversEnd ? 'match-end-coverage-unverified' : 'late-or-unverified-start');
  return { gameTimeAtVideoStart, matchEndEvidence, complete, reason,
    registration: { sessionId: session.sessionId, gameId: session.gameId, filePath: session.filePath,
      fileSize: session.fileSize, durationSeconds: session.durationSeconds, startedAt: session.startedAt,
      complete, gameTimeAtVideoStart } };
}
