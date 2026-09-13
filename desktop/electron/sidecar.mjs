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

// A failed write is never retried: losing its response does not mean it failed.
export class Sidecar {
  #child; #handshake; #stopping = false; #ready = false; #stopPromise;
  constructor({ dataRoot, executable, isolated = false, onExit = () => {} }) {
    this.dataRoot = dataRoot;
    this.executable = executable;
    this.isolated = isolated;
    this.onExit = onExit;
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
    const child = spawn(this.executable, [], { cwd: path.dirname(this.executable), windowsHide: true,
      stdio: 'ignore', env: { ...environment, REVU_DATA_ROOT: root, REVU_HOST_LAUNCH_ID: launchId } });
    this.#child = child;
    let spawnError;
    child.once('error', () => { spawnError = new Error('Unable to launch the configured sidecar'); });
    child.once('exit', () => { if (this.#ready && !this.#stopping) this.onExit(); });
    const identity = { launchId, processId: child.pid, dataDirectory: path.join(root, 'LoLReviewData') };
    const deadline = Date.now() + 20000;
    try {
      while (Date.now() < deadline) {
        if (spawnError) throw spawnError;
        if (child.exitCode !== null || child.signalCode !== null) throw new Error('Sidecar exited before becoming ready; another data owner or incompatible schema may be present');
        try {
          const text = await readFile(path.join(root, 'Revu', 'sidecar.json'), 'utf8');
          if (text.length > 4096) throw new Error('Invalid handshake size');
          const hs = validateHandshake(JSON.parse(text), identity);
          this.#handshake = hs;
          const host = await this.request('/api/host');
          if (host.launchId !== launchId || host.apiVersion !== 1 || host.processId !== child.pid
            || normalize(host.dataDirectory) !== normalize(identity.dataDirectory)) throw new Error('Incompatible backend');
          const health = await this.request('/api/health');
          if (health.status === 'ready') { this.#ready = true; return; }
        } catch { this.#handshake = null; }
        await delay(100);
      }
      throw new Error('The owned sidecar did not become ready within 20 seconds');
    } catch (error) { await this.stop().catch(() => {}); throw error; }
  }
  async request(route, { method = 'GET', body, timeoutMs = 10000 } = {}) {
    const hs = this.#handshake;
    if (!hs) throw new Error('Sidecar unavailable');
    if (!route.startsWith('/api/') || route.includes('#')) throw new Error('Invalid backend route');
    const response = await fetch(`http://127.0.0.1:${hs.port}${route}`, {
      method, redirect: 'error', signal: AbortSignal.timeout(timeoutMs),
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
    const value = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (!response.ok) throw new Error(typeof value?.error === 'string' ? value.error : `Backend HTTP ${response.status}`);
    return value;
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
