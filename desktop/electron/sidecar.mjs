import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { readFile, realpath } from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { cleanRuntimeEnvironment } from '../runtime/overwolf.mjs';
import { requireCleanBackendExit } from './shutdown.mjs';

const normalize = (value) => path.resolve(value).replace(/[\\/]+$/, '').toLowerCase();
export function validateHandshake(value, { launchId, processId, dataDirectory }) {
  if (value?.apiVersion !== 1 || value.launchId !== launchId || value.processId !== processId
    || typeof value.dataDirectory !== 'string' || normalize(value.dataDirectory) !== normalize(dataDirectory)
    || !Number.isInteger(value.port) || value.port < 1 || value.port > 65535
    || !/^[A-F0-9]{64}$/.test(value.token)) throw new Error('Sidecar identity mismatch');
  return value;
}

// One authenticated request to the owned backend. A rejected request carries the
// HTTP status so callers can tell a definitive refusal from a lost response.
// Aborting `signal` disconnects, which the backend sees as the request being cancelled.
export async function backendRequest(hs, route, { method = 'GET', body, timeoutMs = 10000, signal } = {}) {
  if (!route.startsWith('/api/') || route.includes('#')) throw new Error('Invalid backend route');
  const timeout = AbortSignal.timeout(timeoutMs);
  const response = await fetch(`http://127.0.0.1:${hs.port}${route}`, {
    method, redirect: 'error', signal: signal ? AbortSignal.any([timeout, signal]) : timeout,
    headers: { Authorization: `Bearer ${hs.token}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  // Bound even streamed responses; never log bodies (they can contain user data).
  const reader = response.body.getReader();
  const chunks = []; let bytes = 0;
  try {
    while (true) {
      const { done, value } = await reader.read(); if (done) break;
      bytes += value.byteLength;
      if (bytes > 16 * 1024 * 1024) throw new Error('Backend response limit exceeded');
      chunks.push(value);
    }
  } finally { await reader.cancel(); }
  // Status first: an empty or non-JSON error body still reports its HTTP status.
  const text = Buffer.concat(chunks).toString('utf8');
  let value = null, unreadable = false;
  if (text.trim()) {
    try { value = JSON.parse(text); } catch { unreadable = true; }
  }
  if (!response.ok || unreadable) {
    const message = typeof value?.error === 'string' && value.error.trim() ? value.error
      : response.ok ? 'The backend returned an unreadable response' : `Backend HTTP ${response.status}`;
    const e = new Error(message);
    e.status = response.status;
    throw e;
  }
  return value;
}

// The sidecar publishes this before a schema upgrade (migrations can take a while).
export function isStartingHandshake(value, { launchId, processId }) {
  return value?.state === 'starting' && value.apiVersion === 1
    && value.launchId === launchId && value.processId === processId;
}

// /api/health answers ready, or 503 when the database cannot be opened. That is an
// answer, not a reason to wait: the authenticated probe says why.
export async function readHealth(request) {
  try { return await request('/api/health'); }
  catch (error) {
    if (error?.status !== 503) throw error;
    const detail = await request('/api/host/health').catch(() => null);
    if (detail?.status === 'ready') return detail;
    return { status: 'degraded', error: typeof detail?.error === 'string' && detail.error.trim()
      ? detail.error : "Revu couldn't open its database." };
  }
}

const quiet = Object.freeze({ info() {}, warn() {}, error() {} });

// A failed write is never retried: losing its response does not mean it failed.
export class Sidecar {
  #child; #handshake; #stopping = false; #ready = false; #stopPromise; #requests = new AbortController();
  constructor({ dataRoot, executable, isolated = false, onExit = () => {}, onPreparing = () => {}, log = quiet, stderrLog = null,
    preparingTimeoutMs = 10 * 60_000, spawnProcess = spawn }) {
    this.dataRoot = dataRoot;
    this.executable = executable;
    this.isolated = isolated;
    this.onExit = onExit;
    this.onPreparing = onPreparing;
    this.log = log;
    this.stderrLog = stderrLog;
    this.preparingTimeoutMs = preparingTimeoutMs;
    this.spawnProcess = spawnProcess;
  }
  async start() {
    if (this.#child) throw new Error('Sidecar already started');
    const root = await realpath(this.dataRoot);
    const launchId = randomUUID();
    const environment = cleanRuntimeEnvironment();
    for (const name of Object.keys(environment)) {
      if (/^REVU_(ISOLATED_HOST_TEST|HOST_LAUNCH_ID|DATA_ROOT)$/i.test(name)) delete environment[name];
    }
    if (this.isolated) environment.REVU_ISOLATED_HOST_TEST = '1';
    // stderr carries the runtime's report of a fatal crash; keep a capped copy of it.
    const child = this.spawnProcess(this.executable, [], { cwd: path.dirname(this.executable), windowsHide: true,
      stdio: ['ignore', 'ignore', this.stderrLog ? 'pipe' : 'ignore'],
      env: { ...environment, REVU_DATA_ROOT: root, REVU_HOST_LAUNCH_ID: launchId } });
    this.#child = child;
    if (child.stderr) {
      child.stderr.on('data', chunk => this.stderrLog.append(chunk));
      child.stderr.on('error', () => {});
    }
    let spawnError;
    child.once('error', () => { spawnError = new Error('Unable to launch the configured sidecar'); });
    child.once('spawn', () => this.log.info(`Sidecar started (process ${child.pid})`));
    child.once('exit', (code, signal) => {
      const unexpected = this.#ready && !this.#stopping;
      this.log[unexpected ? 'error' : 'info'](`Sidecar exited (code ${code ?? 'none'}, signal ${signal ?? 'none'})`);
      if (unexpected) this.onExit({ code, signal });
    });
    const identity = { launchId, processId: child.pid, dataDirectory: path.join(root, 'LoLReviewData') };
    const startedAt = Date.now();
    // 20 s covers an ordinary launch. Only a launch that announced a schema upgrade gets
    // the longer preparing bound, measured from that announcement: still finite, so a
    // hung backend cannot trap the user. Each finished migration set is recorded and the
    // rest re-run safely, so the next launch continues an upgrade this one gave up on.
    let deadline = startedAt + 20000, preparing = false;
    try {
      while (Date.now() < deadline) {
        if (spawnError) throw spawnError;
        if (child.exitCode !== null || child.signalCode !== null) throw new Error('Sidecar exited before becoming ready; another data owner or incompatible schema may be present');
        let verdict;
        try {
          const text = await readFile(path.join(root, 'Revu', 'sidecar.json'), 'utf8');
          if (text.length > 4096) throw new Error('Invalid handshake size');
          const value = JSON.parse(text);
          if (isStartingHandshake(value, identity)) {
            if (!preparing) {
              preparing = true;
              deadline = Date.now() + this.preparingTimeoutMs;
              this.log.info('Sidecar is upgrading its data');
              this.onPreparing();
            }
          } else {
            const hs = validateHandshake(value, identity);
            if (preparing) { preparing = false; deadline = Date.now() + 20000; }
            this.#handshake = hs;
            const host = await this.request('/api/host');
            if (host.launchId !== launchId || host.apiVersion !== 1 || host.processId !== child.pid
              || normalize(host.dataDirectory) !== normalize(identity.dataDirectory)) throw new Error('Incompatible backend');
            const health = await readHealth(route => this.request(route));
            if (health.status === 'ready') {
              this.#ready = true;
              this.log.info(`Sidecar ready in ${Date.now() - startedAt} ms`);
              return;
            }
            if (health.status === 'degraded') verdict = new Error(health.error);
          }
        } catch { this.#handshake = null; }
        // A verified backend that cannot open its database is an answer, not a timeout.
        if (verdict) throw verdict;
        await delay(100);
      }
      if (preparing) {
        const minutes = Math.round(this.preparingTimeoutMs / 60_000);
        throw new Error(`Updating your Revu data did not finish within ${minutes} minutes. Revu continues the update the next time it opens.`);
      }
      throw new Error('The owned sidecar did not become ready within 20 seconds');
    } catch (error) { await this.stop().catch(() => {}); throw error; }
  }
  async request(route, options = {}) {
    const hs = this.#handshake;
    if (!hs) throw new Error('Sidecar unavailable');
    if (!route.startsWith('/api/') || route.includes('#')) throw new Error('Invalid backend route');
    return backendRequest(hs, route, { ...options, signal: this.#requests.signal });
  }
  /**
   * Abort every request still in flight. Quit calls this for long jobs it stopped
   * waiting on: the backend sees the disconnect, stops ffmpeg or the backfill through
   * the request's cancellation, and keeps nothing partial. Later requests are unaffected.
   */
  cancelRequests() {
    const inFlight = this.#requests;
    this.#requests = new AbortController();
    inFlight.abort();
  }
  async events(signal, onEvent) {
    while (!signal.aborted) {
      try {
        const hs = this.#handshake;
        if (!hs) return;
        const response = await fetch(`http://127.0.0.1:${hs.port}/api/events`, {
          headers: { Authorization: `Bearer ${hs.token}` }, redirect: 'error', signal });
        if (!response.ok) throw new Error('Event stream unavailable');
        const decoder = new SseDecoder(onEvent);
        for await (const chunk of response.body) decoder.push(chunk);
      } catch { if (signal.aborted) return; }
      await delay(1000, undefined, { signal }).catch(() => {});
    }
  }
  stop() {
    return this.#stopPromise ??= this.#stop();
  }
  async #stop() {
    this.#stopping = true;
    const child = this.#child;
    if (!child) return;
    if (child.exitCode !== null || child.signalCode !== null) {
      this.#handshake = null;
      requireCleanBackendExit(child, this.#ready);
      return;
    }
    const exited = new Promise(resolve => child.once('exit', resolve));
    // The authenticated endpoint completes its response before stopping Kestrel.
    // The backend then drains accepted writes and releases its database lease.
    let graceful = false;
    if (this.#handshake) {
      try { await this.request('/api/host/shutdown', { method: 'POST', body: {}, timeoutMs: 2000 }); graceful = true; }
      catch { /* Failed startup or an unresponsive owned child still needs reaping. */ }
    }
    this.#handshake = null;
    if (graceful) await Promise.race([exited, delay(20000, undefined, { ref: false })]);
    const forced = child.exitCode === null && child.signalCode === null;
    if (forced) {
      this.log.warn(graceful ? 'Sidecar did not exit within 20 seconds; ending it' : 'Ending the sidecar without a graceful shutdown');
      child.kill(); // Never target a PID learned from a stale handshake.
      await Promise.race([exited, delay(3000, undefined, { ref: false })]);
    }
    if (child.exitCode === null && child.signalCode === null) throw new Error('Owned sidecar did not exit');
    requireCleanBackendExit(child, this.#ready, forced);
  }
}

export class SseDecoder {
  #buffer = ''; #decoder = new TextDecoder();
  constructor(onEvent) { this.onEvent = onEvent; }
  push(chunk) {
    this.#buffer += this.#decoder.decode(chunk, { stream: true });
    if (this.#buffer.length > 256 * 1024) throw new Error('SSE record limit exceeded');
    let match;
    while ((match = /\r?\n\r?\n/.exec(this.#buffer))) {
      const record = this.#buffer.slice(0, match.index);
      this.#buffer = this.#buffer.slice(match.index + match[0].length);
      let event = 'message'; const data = [];
      for (const line of record.split(/\r?\n/)) {
        if (line.startsWith('event:')) event = line.slice(6).trim();
        if (line.startsWith('data:')) data.push(line.slice(5).trimStart());
      }
      if (data.length) this.onEvent({ type: event, payload: JSON.parse(data.join('\n')) });
    }
  }
}
