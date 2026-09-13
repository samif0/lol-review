import { lstat, readdir, statfs } from 'node:fs/promises';
import path from 'node:path';

export interface CaptureStorage { availableBytes: number; captureBytes: number }
type StopReason = 'capture-limit-stop' | 'storage-unavailable';
export interface StorageWatchdogOptions {
  root: string;
  active(): boolean;
  elapsedMs(): number;
  onStop(reason: StopReason): void;
  inspect?: (root: string) => Promise<CaptureStorage>;
  timeoutMs?: number;
}

/** Read only directory metadata; capture payloads never enter the host's memory. */
export async function inspectCaptureStorage(root: string): Promise<CaptureStorage> {
  const [disk, names] = await Promise.all([statfs(root), readdir(root)]);
  if (names.length > 128) throw new Error('unexpected-output-count');
  const files = await Promise.all(names.filter(name => /^capture/i.test(name) && name !== 'capture-settings.json')
    .map(name => lstat(path.join(root, name))));
  if (files.some(info => info.isSymbolicLink() || !info.isFile())) throw new Error('unexpected-capture-path');
  return { availableBytes: disk.bavail * disk.bsize, captureBytes: files.reduce((bytes, file) => bytes + file.size, 0) };
}

/** Called on the one-second safety tick. A slow scan never queues another scan. */
export function storageWatchdog(options: StorageWatchdogOptions) {
  const inspect = options.inspect ?? inspectCaptureStorage;
  const timeoutMs = options.timeoutMs ?? 3000;
  if (!Number.isFinite(timeoutMs) || timeoutMs < 1 || timeoutMs > 30_000) throw new Error('invalid-storage-timeout');
  let disposed = false, busy = false;
  let deadline: ReturnType<typeof setTimeout> | undefined;
  const active = () => !disposed && options.active();
  return {
    async check(): Promise<void> {
      if (!active()) return;
      // Enforce duration even when the preceding filesystem call has not returned.
      if (options.elapsedMs() > 2 * 60 * 60 * 1000) { options.onStop('capture-limit-stop'); return; }
      if (busy) return;
      busy = true;
      let timedOut = false;
      deadline = setTimeout(() => {
        timedOut = true;
        if (active()) options.onStop('storage-unavailable');
      }, timeoutMs);
      deadline.unref();
      let reason: StopReason | undefined;
      try {
        const storage = await inspect(options.root);
        if (!active() || timedOut) return;
        if (![storage.availableBytes, storage.captureBytes].every(value => Number.isFinite(value) && value >= 0))
          throw new Error('invalid-storage-metadata');
        if (storage.captureBytes > 20 * 1024 ** 3 || storage.availableBytes < 2 * 1024 ** 3)
          reason = 'capture-limit-stop';
      } catch {
        if (active() && !timedOut) reason = 'storage-unavailable';
      } finally {
        clearTimeout(deadline); deadline = undefined; busy = false;
      }
      if (reason) options.onStop(reason);
    },
    dispose() { disposed = true; clearTimeout(deadline); deadline = undefined; }
  };
}
