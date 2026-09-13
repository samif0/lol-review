import { randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile, rename, readdir, realpath, lstat, statfs, unlink } from 'node:fs/promises';
import path from 'node:path';

const presets = new Set(['720p30', '1080p30', '1080p60']);
const defaults = Object.freeze({ enabled: false, preset: '720p30' });
const unfinished = new Set(['starting', 'recording', 'stopping', 'finalizing']);
const validId = value => Number.isSafeInteger(value) && value > 0;
const uuid = value => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
export function recordingSettings(value) {
  if (!value || Object.keys(value).some(key => !['enabled', 'preset'].includes(key))
      || typeof value.enabled !== 'boolean' || !presets.has(value.preset)) throw new Error('Invalid recording settings');
  return { enabled: value.enabled, preset: value.preset };
}

async function atomicJson(file, value) {
  const temporary = `${file}.${randomUUID()}.tmp`;
  try {
    await writeFile(temporary, JSON.stringify(value, null, 2), { flag: 'wx', flush: true });
    await rename(temporary, file);
  } catch (error) { await unlink(temporary).catch(() => {}); throw error; }
}
async function bounded(operation, milliseconds, code = 'recording-timeout') {
  let timer;
  try { return await Promise.race([operation, new Promise((_, reject) => {
    timer = setTimeout(() => reject(Object.assign(new Error(code), { code })), milliseconds);
  })]); } finally { clearTimeout(timer); }
}

/** Only session metadata crosses into the sidecar; video remains in native Recorder. */
export class RecordingService {
  constructor({ root, profile, backend, onChange = () => {}, isolated = false,
    now = () => Date.now(), space = async directory => { const fs = await statfs(directory); return fs.bavail * fs.bsize; },
    deadlines = {} }) {
    Object.assign(this, { root, profile, backend, onChange, isolated, now, space });
    this.deadlines = { start: 35_000, stop: 8_000, callback: 8_000, validate: 15_000, registration: 5000, ...deadlines };
    this.settings = { ...defaults }; this.state = 'disabled'; this.message = 'Automatic recording is off.';
    this.driver = null; this.available = false; this.current = null; this.pending = new Map();
    this.game = null; this.context = null; this.serial = Promise.resolve(); this.closing = false;
    this.blocked = false; this.suppressedPid = null; this.timer = null; this.tickPending = false;
    this.startRevision = 0; this.requestedEnabled = false; this.shutdownOperation = null;
    this.windowRetryPid = null;
  }
  getStatus() { return { state: this.state, message: this.message, settings: { ...this.settings }, available: this.available && !this.isolated }; }
  publish(state, message) { this.state = state; this.message = message; this.onChange(this.getStatus()); }
  enqueue(operation) {
    const result = this.serial.then(operation);
    this.serial = result.catch(() => { this.publish('failed', 'Recording could not finish safely. Existing files have been kept.'); });
    return result;
  }
  async initialize() {
    await mkdir(this.profile, { recursive: true });
    await mkdir(this.root, { recursive: true });
    if (path.resolve(this.root).toLowerCase() !== (await realpath(this.root)).toLowerCase()) throw new Error('Recording folder aliases are not supported');
    try { this.settings = recordingSettings(JSON.parse(await readFile(path.join(this.profile, 'recording-settings.json'), 'utf8'))); }
    catch (error) { if (error.code !== 'ENOENT') this.publish('failed', 'Recording settings could not be read. Automatic recording is off.'); }
    this.requestedEnabled = this.settings.enabled;
    // A new host does not adopt a prior process's native capture or assume its
    // raw output passed validation. Keep interrupted output for manual recovery.
    const entries = await readdir(this.root, { withFileTypes: true });
    for (const entry of entries.filter(entry => entry.isDirectory() && uuid(entry.name)).slice(0, 1000)) {
      const directory = path.join(this.root, entry.name);
      if ((await realpath(directory)).toLowerCase() !== directory.toLowerCase()) continue;
      try {
        const file = path.join(directory, 'session.json');
        if ((await lstat(file)).size > 64 * 1024) continue;
        const session = JSON.parse(await readFile(file, 'utf8'));
        if (session.sessionId !== entry.name) continue;
        if (unfinished.has(session.state)) {
          session.state = 'partial'; session.complete = false; session.reason = 'host-interrupted';
          await atomicJson(file, session);
          this.publish('partial', 'Files from an interrupted recording were kept in your recordings folder. Their playback has not been verified.');
        } else if (session.state === 'awaiting-match' && this.pending.size < 100) {
          session.directory = directory; this.pending.set(session.sessionId, session);
        }
      } catch { /* Keep unfamiliar or unreadable media untouched. */ }
    }
    if (this.state !== 'partial' && this.state !== 'failed') this.idleStatus();
    this.schedule();
    return this.getStatus();
  }
  idleStatus() {
    if (this.isolated) this.publish('unavailable', 'Recording is unavailable in this isolated preview.');
    else if (!this.settings.enabled) this.publish('disabled', 'Automatic recording is off.');
    else if (this.blocked) this.publish('partial', 'Recording cannot restart safely yet. Existing files have been kept; restart Revu before recording again.');
    else if (!this.available) this.publish('unavailable', 'Recording is not available in this build yet. Your preference is saved.');
    else this.publish('idle', 'Ready to record your next League match.');
  }
  async saveSettings(value) {
    const settings = recordingSettings(value);
    if (this.isolated && settings.enabled) throw new Error('Recording is disabled in isolated previews');
    if (this.closing) throw new Error('Revu is closing');
    this.requestedEnabled = settings.enabled;
    if (!settings.enabled) {
      this.startRevision++;
      if (this.current && this.current.state !== 'finalizing') this.current.reason = 'recording-disabled';
      if (this.current?.state === 'starting') this.current.abort.abort();
    }
    return this.enqueue(async () => {
      try {
        await atomicJson(path.join(this.profile, 'recording-settings.json'), settings);
        this.settings = settings;
      } finally {
        // Opting out still stops capture when the settings disk cannot be written.
        if (!settings.enabled) {
          this.settings = { ...this.settings, enabled: false };
          if (this.current) await this.stopCurrent('recording-disabled');
        }
      }
      if (!this.current) this.idleStatus();
      this.schedule();
      return this.getStatus();
    });
  }
  async attachDriver(driver) {
    if (this.isolated || this.closing || this.driver) { await driver.dispose?.(); return; }
    this.driver = driver;
    try {
      await bounded(driver.initialize(), 15_000);
      if (this.closing) { await driver.dispose?.(); return; }
      this.available = true;
    }
    catch { this.available = false; await driver.dispose?.(); this.driver = null; }
    if (!['partial', 'failed'].includes(this.state)) this.idleStatus();
    this.schedule();
  }
  unavailable() {
    this.available = false; this.startRevision++;
    if (this.current) {
      this.current.reason = 'recorder-unavailable';
      if (this.current.state === 'starting') this.current.abort.abort();
    }
    void this.enqueue(async () => { if (this.current) await this.stopCurrent('recorder-unavailable'); else this.idleStatus(); }).catch(() => {});
  }
  onGame(game) {
    if (!validId(game?.pid)) return;
    if (game.running) {
      if (this.game?.pid !== game.pid) { this.suppressedPid = null; this.windowRetryPid = null; this.startRevision++; }
      this.game = { ...game, firstSeen: this.game?.pid === game.pid ? this.game.firstSeen : this.now() };
    } else {
      if (this.game?.pid === game.pid) { this.game = null; this.startRevision++; }
      if (this.current?.pid === game.pid && this.current.state === 'starting') this.current.abort.abort();
    }
    this.schedule();
    void this.tick();
  }
  schedule() {
    const candidate = this.settings.enabled && this.requestedEnabled && this.driver && this.available && !this.blocked
      && this.game && this.game.pid !== this.suppressedPid;
    const needed = !this.closing && !this.isolated && (candidate || this.current || this.pending.size);
    if (needed && !this.timer) { this.timer = setInterval(() => { void this.tick(); }, 1000); this.timer.unref(); }
    if (!needed && this.timer) { clearInterval(this.timer); this.timer = null; }
  }
  tick() {
    if (this.tickPending || this.closing || this.isolated) return Promise.resolve();
    this.tickPending = true;
    return this.enqueue(async () => {
      try {
        const context = await this.backend.request('/api/recording/context', { timeoutMs: 2500 });
        this.context = context;
      } catch { this.context = null; }
      const current = this.current;
      if (current) {
        try { await this.identify(current); }
        catch { await this.stopCurrent('journal-write-failed'); }
        if (!this.game || this.game.pid !== current.pid) await this.stopCurrent('game-exit');
        else if (this.context?.lastEndedGameId === current.gameId && current.gameId) await this.stopCurrent('match-ended');
        else {
          try {
            if (await bounded(this.space(this.root), 3000) < 2 * 1024 ** 3) await this.stopCurrent('low-disk');
          } catch { await this.stopCurrent('storage-unavailable'); }
        }
      }
      for (const session of [...this.pending.values()].filter(value => !value.nextRetryAt || value.nextRetryAt <= this.now()).slice(0, 5))
        await this.reconcile(session);
      if (!this.closing && !this.current && this.settings.enabled && this.requestedEnabled && this.available && !this.blocked && this.game && this.game.pid !== this.suppressedPid && this.pending.size < 100)
        await this.startCurrent(this.game);
      this.schedule();
    }).catch(() => {}).finally(() => { this.tickPending = false; });
  }
  async persist(session) {
    const { abort, completion, finish, startOperation, stoppedResult, ...metadata } = session;
    await atomicJson(path.join(session.directory, 'session.json'), metadata);
  }
  async preserve(session) {
    try { await this.persist(session); return true; }
    catch {
      this.blocked = true; session.reason ||= 'journal-write-failed';
      this.publish('partial', 'Recording metadata could not be saved. Existing video files have been kept; restart Revu after checking storage.');
      return false;
    }
  }
  async identify(session) {
    const context = this.context;
    if (!context || !context.isGameInProgress || !validId(context.gameId)) return;
    if (!session.gameId) {
      session.gameId = context.gameId;
      // Multiple native runs belonging to one match are retained as separate,
      // explicitly partial segments; never overwrite the first segment as full.
      for (const pending of this.pending.values()) if (pending.gameId === session.gameId) {
        session.reason = 'reconnect';
        if (!pending.registration) {
          pending.reason = 'reconnect'; pending.complete = false;
          await this.persist(pending);
        }
      }
      await this.persist(session);
    } else if (session.gameId !== context.gameId) {
      session.reason = 'match-identity-changed';
      await this.stopCurrent('match-identity-changed');
      return;
    }
    if (session.gameClockAnchor == null && Number.isFinite(context.gameTimeSeconds) && context.gameTimeSeconds > 0
        && Math.abs(this.now() - Date.parse(context.observedAt)) < 5000) {
      session.gameClockAnchor = { gameTimeSeconds: context.gameTimeSeconds, observedAt: context.observedAt };
      await this.persist(session);
    }
  }
  async startCurrent(game) {
    const revision = this.startRevision, driver = this.driver;
    const canStart = () => !this.closing && this.requestedEnabled && this.settings.enabled && this.available && !this.blocked
      && this.driver === driver && this.startRevision === revision && this.game?.pid === game.pid;
    if (!canStart()) return;
    if (this.windowRetryPid === game.pid && this.now() - game.firstSeen >= 30_000) {
      this.suppressedPid = game.pid;
      this.publish('failed', 'The League game window was not ready. Recording will try again for your next match.');
      return;
    }
    try { if (await bounded(this.space(this.root), 3000) < 2 * 1024 ** 3) {
      this.suppressedPid = game.pid; this.publish('failed', 'Not enough disk space to start recording. Free space before the next match.'); return;
    } } catch { this.publish('failed', 'The recordings folder is unavailable.'); this.suppressedPid = game.pid; return; }
    if (!canStart()) return;
    const session = { sessionId: randomUUID(), pid: game.pid, gameId: null, state: 'starting', preset: this.settings.preset,
      startedAt: new Date(this.now()).toISOString(), complete: false, reason: null, gameClockAnchor: null, abort: new AbortController() };
    session.directory = path.join(this.root, session.sessionId);
    let nativeEntered = false;
    try {
      await mkdir(session.directory);
      await this.persist(session); // Intent must exist before native capture begins.
      this.current = session;
      await this.identify(session);
      if (!canStart()) {
        session.state = 'partial'; session.reason ||= this.closing ? 'app-exit' : 'start-cancelled';
        await this.preserve(session); this.current = null; return;
      }
      this.publish('starting', 'Starting your League recording…');
      session.completion = new Promise(resolve => { session.finish = resolve; });
      nativeEntered = true;
      session.startOperation = driver.start({ pid: game.pid, preset: session.preset, outputDirectory: session.directory,
        signal: session.abort.signal, onStopped: result => {
          session.stoppedResult = result;
          session.finish(result);
          void this.enqueue(() => this.finalize(session, result)).catch(() => {});
        } });
      // Even a provider which resolves start after our deadline still owes a stop.
      void Promise.resolve(session.startOperation).then(async () => {
        if (session.abort.signal.aborted && this.current !== session && this.blocked) {
          try { await bounded(driver.stop(), this.deadlines.stop); } catch { /* Native reuse remains blocked. */ }
        }
      }, () => {});
      session.resolvedSettings = await bounded(session.startOperation, this.deadlines.start, 'start-timeout');
      if (session.stoppedResult !== undefined) { await this.finalize(session, session.stoppedResult); return; }
      if (!canStart() || session.abort.signal.aborted) { await this.stopCurrent(session.reason || (this.closing ? 'app-exit' : 'game-exit')); return; }
      session.state = 'recording'; await this.persist(session);
      this.publish('recording', 'Recording League gameplay and game audio.');
    } catch (error) {
      session.abort.abort();
      // A rejected start can have entered native code. Stop it before allowing
      // another session; a timeout stays blocked until the app is restarted.
      if (['start-timeout', 'recorder-start-failed', 'recorder-stop-failed'].includes(error.code)) this.blocked = true;
      if (nativeEntered) {
        try { await bounded(driver.stop(), this.deadlines.stop); }
        catch { this.blocked = true; }
      }
      session.state = 'partial'; session.reason ||= error.code === 'source-window-not-ready' ? 'window-not-ready' : 'start-failed';
      await this.preserve(session); if (this.current === session) this.current = null;
      if (session.reason === 'window-not-ready') this.windowRetryPid = game.pid;
      if (session.reason !== 'window-not-ready' || this.now() - game.firstSeen >= 30_000) this.suppressedPid = game.pid;
      this.publish('failed', session.reason === 'window-not-ready' ? 'Waiting for the League game window…' : 'Recording could not start. Existing files have been kept.');
    }
  }
  async stopCurrent(reason) {
    const session = this.current;
    if (!session || session.state === 'finalizing') return;
    if (!['game-exit', 'match-ended'].includes(reason)) session.reason = reason;
    if (session.state === 'starting') session.abort.abort();
    session.state = 'stopping';
    await this.preserve(session); this.publish('stopping', 'Finishing your recording…');
    const before = this.now();
    let result;
    try {
      await bounded(this.driver.stop(), this.deadlines.stop);
      result = await bounded(session.completion, this.deadlines.callback);
      session.finalizationMilliseconds = this.now() - before;
    } catch {
      this.blocked = true; session.state = 'partial'; session.reason = 'stop-unconfirmed';
      await this.preserve(session); this.current = null; this.suppressedPid = session.pid;
      this.publish('partial', 'Recording did not finish cleanly. Files have been kept; restart Revu before recording again.');
      return;
    }
    await this.finalize(session, result);
  }
  async finalize(session, result) {
    if (this.current !== session || !unfinished.has(session.state)) return;
    session.finalStats = result?.stats ?? null;
    session.requestToCallbackMs = result?.requestToCallbackMs ?? null;
    session.state = 'finalizing'; await this.preserve(session); this.publish('finalizing', 'Checking the saved recording…');
    const validation = new AbortController();
    try {
      if (result?.hasError !== false || (result.reason !== undefined && result.reason !== 0) || result.splitCount > 0) session.reason ||= 'recording-interrupted';
      const media = await bounded(this.driver.validate(result, validation.signal), this.deadlines.validate);
      Object.assign(session, media);
      session.state = 'awaiting-match'; session.stoppedAt = new Date(this.now()).toISOString();
    } catch { session.state = 'partial'; session.reason ||= 'media-validation-failed'; }
    finally { validation.abort(); }
    if (!await this.preserve(session)) session.state = 'partial';
    if (session.state === 'awaiting-match') this.pending.set(session.sessionId, session);
    this.current = null; this.suppressedPid = session.pid;
    this.publish(session.state === 'partial' ? 'partial' : 'finalizing', session.state === 'partial'
      ? 'The recording is incomplete. Its files have been kept.' : 'Video saved. Waiting for the match result…');
    if (session.state === 'awaiting-match') await this.reconcile(session);
  }
  async reconcile(session) {
    if (!session.registration) {
      const ended = validId(session.gameId) && this.context?.lastEndedGameId === session.gameId;
      if (!ended && !session.reason && !this.closing && this.now() - Date.parse(session.stoppedAt) < 180_000) return;
      if (!validId(session.gameId)) {
        session.state = 'partial'; session.reason ||= 'match-identity-unavailable';
        await this.preserve(session); this.pending.delete(session.sessionId);
        if (!this.current) this.publish('partial', 'Video saved, but its match could not be identified. The file is in your recordings folder.');
        return;
      }
      const anchor = session.gameClockAnchor;
      const gameTimeAtVideoStart = anchor && session.startedAtVerified === true
        ? anchor.gameTimeSeconds + (Date.parse(session.startedAt) - Date.parse(anchor.observedAt)) / 1000 : null;
      session.gameTimeAtVideoStart = Number.isFinite(gameTimeAtVideoStart)
        && gameTimeAtVideoStart >= -600 && gameTimeAtVideoStart <= 7200 ? gameTimeAtVideoStart : null;
      const endClock = this.context?.lastEndedGameTimeSeconds;
      const endObserved = Date.parse(this.context?.lastEndedObservedAt);
      const endConfirmed = Date.parse(this.context?.lastEndedConfirmedAt);
      const freshEndClock = Number.isFinite(endClock) && endClock > 0
        && Number.isFinite(endObserved) && Number.isFinite(endConfirmed)
        && endConfirmed >= endObserved && endConfirmed - endObserved <= 10_000;
      const coversEnd = ended && freshEndClock && session.gameTimeAtVideoStart !== null
        && Number.isFinite(session.durationSeconds)
        && session.gameTimeAtVideoStart + session.durationSeconds >= endClock - 2;
      session.matchEndEvidence = ended ? { gameTimeSeconds: endClock ?? null,
        observedAt: this.context.lastEndedObservedAt ?? null,
        confirmedAt: this.context.lastEndedConfirmedAt ?? null, coversEnd: !!coversEnd } : null;
      session.complete = !!coversEnd && !session.reason && session.gameTimeAtVideoStart <= 2;
      if (!session.complete) session.reason ||= !ended ? 'match-end-unverified'
        : !coversEnd ? 'match-end-coverage-unverified' : 'late-or-unverified-start';
      session.registration = { sessionId: session.sessionId, gameId: session.gameId, filePath: session.filePath,
        fileSize: session.fileSize, durationSeconds: session.durationSeconds, startedAt: session.startedAt,
        complete: session.complete, gameTimeAtVideoStart: session.gameTimeAtVideoStart };
    }
    // Once attempted, completion, timing and identity come only from the frozen
    // durable request, including after a lost response or a subsequent match.
    session.complete = session.registration.complete;
    session.gameTimeAtVideoStart = session.registration.gameTimeAtVideoStart;
    try {
      await this.persist(session);
      // The endpoint is explicitly idempotent by session ID. Persist the exact
      // body before the first attempt so a lost reply never changes its meaning.
      const result = await bounded(this.backend.request('/api/recording/register', { method: 'POST', body: session.registration,
        timeoutMs: this.deadlines.registration }), this.deadlines.registration);
      if (!result?.ok) throw new Error('registration-failed');
      session.registrationStatus = result.status;
      session.state = session.complete ? 'saved' : 'partial';
      await this.persist(session); this.pending.delete(session.sessionId);
      if (!this.current) this.publish(session.state, session.complete
        ? (result.status === 'retained-existing' ? 'Recording saved. The match keeps its existing video.' : 'Recording saved for this match.')
        : 'An incomplete recording was kept in your recordings folder.');
    } catch {
      session.retryAttempts = Math.min((session.retryAttempts || 0) + 1, 10);
      session.nextRetryAt = this.now() + Math.min(60_000, 1000 * 2 ** session.retryAttempts);
      if (!this.current) this.publish('partial', 'Video saved. Match attachment is pending; Revu will retry.');
    }
  }
  shutdown() {
    if (this.shutdownOperation) return this.shutdownOperation;
    this.closing = true; this.startRevision++; clearInterval(this.timer); this.timer = null;
    if (this.current && this.current.state !== 'finalizing') this.current.reason ||= 'app-exit';
    if (this.current?.state === 'starting') this.current.abort.abort();
    this.shutdownOperation = this.enqueue(async () => {
      try {
        if (this.current) await this.stopCurrent('app-exit');
        for (const session of [...this.pending.values()].slice(0, 5)) await this.reconcile(session);
      } finally { await bounded(Promise.resolve(this.driver?.dispose?.()), this.deadlines.stop); }
    });
    return this.shutdownOperation;
  }
}
