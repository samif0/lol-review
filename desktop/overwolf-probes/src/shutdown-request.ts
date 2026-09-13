import { readFile } from 'node:fs/promises';

/** Poll the owned launcher's request without blocking the host or queuing slow reads. */
export function shutdownRequestPoller(file: string, token: string, onShutdown: () => void,
  read: (file: string, signal: AbortSignal) => Promise<string> = (file, signal) => readFile(file, { encoding: 'utf8', signal }),
  timeoutMs = 1000) {
  let disposed = false, busy = false;
  let pending: AbortController | undefined;
  let deadline: ReturnType<typeof setTimeout> | undefined;
  return {
    async check(): Promise<void> {
      if (disposed || busy) return;
      busy = true;
      const abort = pending = new AbortController();
      deadline = setTimeout(() => abort.abort(), timeoutMs);
      deadline.unref();
      let requested = false;
      try {
        const request = JSON.parse(await read(file, abort.signal));
        requested = !disposed && !abort.signal.aborted && request?.token === token;
      } catch { /* Absence, timeout and malformed requests do not authorize shutdown. */ }
      finally { clearTimeout(deadline); deadline = undefined; pending = undefined; busy = false; }
      if (requested) onShutdown();
    },
    dispose() { disposed = true; pending?.abort(); clearTimeout(deadline); deadline = undefined; }
  };
}
