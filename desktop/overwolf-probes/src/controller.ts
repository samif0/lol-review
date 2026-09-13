export type CaptureState = 'idle' | 'starting' | 'recording' | 'stopping' | 'finalizing' | 'finalized' | 'partial' | 'failed';
export type StopResult = { hasError: boolean; error?: string; reason?: number; filePath?: string; duration?: number; startTimeEpoch?: number; splitCount?: number };
export interface Driver {
  start(onStopped: (result: StopResult) => void, signal: AbortSignal): Promise<void>;
  stop(): Promise<void>;
  validate(result: StopResult, signal: AbortSignal): Promise<boolean>;
}
export interface CaptureDeadlines { startMs: number; stopMs: number; completionMs: number; validationMs: number }
const defaults: CaptureDeadlines = { startMs: 15_000, stopMs: 5_000, completionMs: 5_000, validationMs: 12_000 };
class DeadlineError extends Error {}
function bounded<T>(operation: Promise<T>, ms: number, signal?: AbortSignal): Promise<T> {
  return new Promise((resolve, reject) => {
    const abort = () => finish(() => reject(new Error('cancelled')));
    const timer = setTimeout(() => finish(() => reject(new DeadlineError())), ms);
    let done = false;
    function finish(action: () => void) {
      if (done) return;
      done = true; clearTimeout(timer); signal?.removeEventListener('abort', abort); action();
    }
    // Consume late provider rejection even when cancellation already won the race.
    operation.then(value => finish(() => resolve(value)), error => finish(() => reject(error)));
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) abort();
  });
}

/** One deliberate diagnostic capture per process. No domain/database association is implied. */
export class CaptureController {
  state: CaptureState = 'idle';
  lastFailure: string | null = null;
  persistenceHealthy = true;
  // A terminal manifest is not proof that an unresponsive provider actually stopped.
  mayBeActive = false;
  private serial: Promise<void> = Promise.resolve();
  private completed = false;
  private startRequested = false;
  private startAttempted = false;
  private startSettled = false;
  private receivedStop = false;
  private interrupted: 'stop' | 'crash' | undefined;
  private startAbort?: AbortController;
  private stopAbort?: AbortController;
  private validationAbort?: AbortController;
  private stopOperation?: Promise<void>;
  private stopInFlight = false;
  private completionTimer?: ReturnType<typeof setTimeout>;
  private completionWait?: Promise<void>;
  private resolveCompletion?: () => void;
  private deadlines: CaptureDeadlines;
  constructor(private driver: Driver, private persist: (state: CaptureState) => void, deadlines: Partial<CaptureDeadlines> = {}) {
    this.deadlines = { ...defaults, ...deadlines };
    if (Object.values(this.deadlines).some(ms => !Number.isFinite(ms) || ms < 1 || ms > 120_000)) throw new Error('invalid-capture-deadline');
  }
  private enqueue(action: () => Promise<void>): Promise<void> {
    this.serial = this.serial.then(action).catch(async () => {
      this.fail('controller-failed', this.startAttempted ? 'partial' : 'failed');
      await this.stopProvider();
    });
    return this.serial;
  }
  private set(state: CaptureState): boolean {
    this.state = state;
    try { this.persist(state); return true; }
    catch {
      this.persistenceHealthy = false; this.lastFailure = 'persistence-failed'; this.completed = true;
      this.state = this.startAttempted ? 'partial' : 'failed';
      // One best-effort terminal write, with no recursive persistence/error handler.
      try { this.persist(this.state); } catch { /* Main reports the unhealthy manifest. */ }
      this.finishCompletion();
      return false;
    }
  }
  private fail(code: string, state: 'partial' | 'failed' = 'partial') {
    if (this.completed) return;
    this.lastFailure = code; this.completed = true; this.finishCompletion(); this.set(state);
  }
  private finishCompletion() {
    clearTimeout(this.completionTimer); this.completionTimer = undefined;
    this.resolveCompletion?.(); this.resolveCompletion = undefined; this.completionWait = undefined;
  }
  private wasCrashed(): boolean { return this.interrupted === 'crash'; }
  private awaitCompletion() {
    if (this.receivedStop || this.completed || this.completionTimer) return;
    this.completionWait = new Promise(resolve => { this.resolveCompletion = resolve; });
    this.completionTimer = setTimeout(() => {
      void this.enqueue(async () => { if (!this.receivedStop) this.fail('completion-timeout'); });
    }, this.deadlines.completionMs);
    this.completionTimer.unref();
  }
  private async stopProvider(): Promise<void> {
    if (!this.startAttempted || !this.mayBeActive || this.receivedStop) return;
    if (this.stopOperation) return this.stopOperation;
    this.stopAbort = new AbortController();
    const abort = this.stopAbort;
    this.stopInFlight = true;
    this.stopOperation = (async () => {
      try {
        await bounded(Promise.resolve().then(() => this.driver.stop()), this.deadlines.stopMs, abort.signal);
        // A start still pending in the provider can succeed after this stop.
        this.mayBeActive = !this.startSettled && !this.receivedStop;
      } catch (error) {
        if (!this.receivedStop) this.fail(error instanceof DeadlineError ? 'stop-timeout' : 'stop-failed');
      } finally { this.stopInFlight = false; }
    })();
    await this.stopOperation;
  }
  start(): Promise<void> {
    if (this.state === 'idle') this.startRequested = true;
    return this.enqueue(async () => {
      if (this.state !== 'idle') return;
      if (this.interrupted) { this.fail(this.interrupted === 'crash' ? 'provider-crash' : 'start-cancelled', 'failed'); return; }
      if (!this.set('starting')) return;
      const abort = this.startAbort = new AbortController();
      try {
        const pendingStart = Promise.resolve().then(() => {
          abort.signal.throwIfAborted();
          this.startAttempted = true; this.mayBeActive = true;
          return this.driver.start(result => { void this.onStopped(result); }, abort.signal);
        });
        const lateStartSettled = () => {
          this.startSettled = true;
          if (this.startAttempted && abort.signal.aborted && !this.receivedStop) {
            this.mayBeActive = true;
            if (!this.stopInFlight) this.stopOperation = undefined;
            void this.enqueue(() => this.stopProvider());
          }
        };
        void pendingStart.then(lateStartSettled, lateStartSettled);
        await bounded(pendingStart, this.deadlines.startMs, abort.signal);
        if (this.completed || this.receivedStop || this.interrupted) return;
        if (!this.set('recording')) await this.stopProvider();
      } catch (error) {
        abort.abort(); // Prevent a timed-out adapter from continuing to the next setup stage.
        if (this.receivedStop) return;
        if (!this.interrupted) this.fail(error instanceof DeadlineError ? 'start-timeout' : 'start-failed', error instanceof DeadlineError ? 'partial' : 'failed');
        await this.stopProvider();
      }
    });
  }
  stop(): Promise<void> {
    if (this.state === 'starting' || (this.state === 'idle' && this.startRequested)) { this.interrupted = 'stop'; this.startAbort?.abort(); }
    return this.enqueue(async () => {
      if (this.interrupted === 'stop' && !this.receivedStop) this.fail('start-cancelled', this.startAttempted ? 'partial' : 'failed');
      if (this.completed) { await this.stopProvider(); return; }
      if (this.state !== 'recording') return;
      this.set('stopping');
      await this.stopProvider();
      this.awaitCompletion(); // Resolved stop cannot replace completion metadata.
    });
  }
  onStopped(result: StopResult): Promise<void> {
    if (this.state === 'idle' || this.receivedStop) return this.serial;
    this.receivedStop = true; this.mayBeActive = false; this.finishCompletion();
    this.startAbort?.abort(); this.stopAbort?.abort();
    return this.enqueue(async () => {
      if (this.completed || this.wasCrashed()) return;
      if (!this.set('finalizing')) return;
      if (!result || result.hasError !== false || result.error || (result.reason !== undefined && result.reason !== 0) ||
          (result.splitCount !== undefined && result.splitCount !== 0) || !result.filePath ||
          (result.startTimeEpoch !== undefined && (!Number.isFinite(result.startTimeEpoch) || result.startTimeEpoch <= 0)) ||
          !Number.isFinite(result.duration) || result.duration! <= 0) { this.fail('incomplete-stop'); return; }
      const abort = this.validationAbort = new AbortController();
      try {
        const valid = await bounded(Promise.resolve().then(() => { abort.signal.throwIfAborted(); return this.driver.validate(result, abort.signal); }), this.deadlines.validationMs, abort.signal);
        if (this.completed || this.wasCrashed()) return;
        if (valid) { this.completed = true; this.set('finalized'); }
        else this.fail('media-validation-failed');
      } catch (error) {
        abort.abort();
        if (!this.wasCrashed()) this.fail(error instanceof DeadlineError ? 'validation-timeout' : 'media-validation-failed');
      }
    });
  }
  crash(): Promise<void> {
    if (this.state === 'idle' && !this.startRequested) return this.serial;
    this.interrupted = 'crash'; this.startAbort?.abort(); this.validationAbort?.abort();
    return this.enqueue(async () => { this.fail('provider-crash', this.startAttempted ? 'partial' : 'failed'); await this.stopProvider(); });
  }
  async drained() {
    while (true) {
      const pending = this.serial; await pending;
      if (this.completionWait) { this.completionTimer?.ref(); await this.completionWait; continue; }
      if (pending === this.serial) return;
    }
  }
}
