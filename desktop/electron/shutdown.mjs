// Terminal actions (update/relaunch) belong after accepted commands and the
// owned backend have finished. A failed stop must never arm an updater.
export async function shutdownOwnedApp({ pendingCommands, abortEvents, releaseMedia, stopBackend, afterShutdown,
  drainTimeoutMs = Infinity, onAbandoned = () => {} }) {
  abortEvents();
  await drainPending(pendingCommands, drainTimeoutMs, onAbandoned);
  releaseMedia();
  await stopBackend();
  await afterShutdown?.();
}

// Ordinary writes settle within their own deadline, so the bound never cuts them
// short. Only long jobs (clip export, backfill, scans) can outlast it; their replies
// are no longer awaited, and the backend's graceful stop handles the requests.
async function drainPending(pendingCommands, timeoutMs, onAbandoned) {
  const settled = Promise.allSettled([...pendingCommands]).then(() => true);
  if (!Number.isFinite(timeoutMs)) { await settled; return; }
  let timer;
  const drained = await Promise.race([settled,
    new Promise(resolve => { timer = setTimeout(resolve, timeoutMs, false); })]);
  clearTimeout(timer);
  if (!drained) onAbandoned([...pendingCommands]);
}

// A terminal command owns admission until exit. Its command reply can explicitly
// report failure; failures reopen admission so the user can correct and retry.
export function createCommandGate() {
  let terminalActive = false;
  return async ({ terminal, pendingCommands }, operation) => {
    if (terminalActive) throw new Error('An update or database replacement is already in progress');
    if (!terminal) return operation();
    terminalActive = true;
    try {
      // Snapshot before yielding: the caller adds this command to pending only
      // after admission, so it cannot wait on itself.
      await Promise.allSettled([...pendingCommands]);
      const response = await operation();
      if (response.value?.ok === false) terminalActive = false;
      return response;
    } catch (error) {
      terminalActive = false;
      throw error;
    }
  };
}

export function requireCleanBackendExit(child, ready, forced = false) {
  if (ready && (forced || child.exitCode !== 0 || child.signalCode !== null))
    throw new Error('Backend shutdown did not finish cleanly; check the latest save in the logs');
}

// Every quit request is intercepted until the one shared drain completes.
// Final termination uses app.exit(), which does not re-enter before-quit.
export function createQuitHandler({ begin, shutdown, failed, exit }) {
  let completion;
  return event => {
    event.preventDefault();
    if (!completion) {
      begin();
      completion = Promise.resolve().then(shutdown).catch(failed).finally(exit);
    }
    return completion;
  };
}
