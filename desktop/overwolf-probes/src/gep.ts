import type {} from '@overwolf/ow-electron-packages-types';
import { FEATURES, LEAGUE_ID, ObservationFilter, type Observation } from './policy.js';
import type { Journal } from './journal.js';

type Gep = overwolf.packages.OverwolfPackageManager['gep'];
export type GepConnection = { dispose(): void; refresh(): Promise<void> };

/** Owns all listeners and invalidates asynchronous work on retry, failure or exit. */
export function connectGep(gep: Gep, journal: Journal, status: (text: string) => void): GepConnection {
  const filter = new ObservationFilter();
  let sequence = 0;
  let disposed = false;
  let detected = false;
  let collecting = false;
  let subscribed = false;
  let requested: readonly string[] = [];
  let pendingQueue: Observation | undefined;
  let lastStatus = '';
  const report = (text: string) => {
    if (!disposed && text !== lastStatus) { lastStatus = text; status(text); }
  };
  const invalidate = (reason: string) => {
    sequence++; collecting = false; subscribed = false; requested = []; pendingQueue = undefined;
    filter.reset(reason);
  };
  const coverage = () => {
    if (filter.queue === undefined) {
      report('GEP subscribed; blocked until a fresh supported game queueId update (snapshot schema unverified)');
    } else {
      report('Observing permitted League queue; coverage partial (history and snapshot unverified)');
    }
  };
  const failed = (stage: string, message: string) => {
    invalidate(stage);
    journal.write({ kind: 'gep-error', stage, generation: sequence });
    report(message);
  };
  const configure = async (preserveDetection = false) => {
    if (disposed) return;
    if (!detected) {
      report('Waiting for League detection; launch the probe before starting the game');
      return;
    }
    if (!preserveDetection) invalidate('subscription');
    const current = sequence;
    const stale = () => disposed || current !== sequence;
    // Initial info can arrive as soon as enable() returns, before getFeatures
    // resolves. Only sanitized info is buffered; nothing is published yet.
    collecting = true; requested = FEATURES;
    report('League detected; requesting permitted features');
    try {
      const supported: unknown = await gep.getFeatures(LEAGUE_ID);
      if (stale()) return;
      if (!Array.isArray(supported) || !supported.every(value => typeof value === 'string')) throw new Error('invalid-features');
      requested = FEATURES.filter(feature => supported.includes(feature));
      for (const feature of FEATURES) journal.write({ kind: 'feature-support', feature, supported: requested.includes(feature), generation: current });
      if (!requested.includes('matchState')) {
        collecting = false; pendingQueue = undefined; filter.reset('missing-matchState');
        report('Blocked: GEP does not report the matchState feature required for queue identity');
        return;
      }
      // Do not publish observations until the allowlisted subscription succeeds.
      await gep.setRequiredFeatures(LEAGUE_ID, [...requested]);
      if (stale()) return;
      subscribed = true;
      if (pendingQueue && filter.queue !== undefined) {
        journal.write({ kind: 'observation', source: 'live-info', generation: current, epoch: filter.revision, coverage: 'partial', ...pendingQueue });
      }
      pendingQueue = undefined;
      coverage();
      const info: unknown = await gep.getInfo(LEAGUE_ID);
      if (stale()) return;
      // Electron's documented return type is any, with no documented envelope.
      // Never infer the Native API's res.game_info shape, dump unknown state, or
      // replay it as events. A verified Electron fixture is required to enable it.
      journal.write({ kind: 'initial-state', present: info !== null && info !== undefined,
        schema: 'unverified', usedForQueue: false, generation: current });
      coverage();
    } catch {
      if (!stale()) failed('subscription', 'GEP subscription failed; observations blocked. Retry game events after resolving the provider problem');
    }
  };
  const refresh = () => configure();
  const onDetected = (event: { enable(): void }, gameId: number) => {
    if (disposed || gameId !== LEAGUE_ID) return;
    invalidate('detected'); detected = true;
    const current = sequence;
    collecting = true; requested = FEATURES;
    try { event.enable(); }
    catch {
      detected = false;
      failed('enable', 'Blocked: GEP could not enable League; restart the game with the probe open');
      return;
    }
    if (current === sequence) void configure(true);
  };
  const accept = (source: 'info' | 'event', gameId: number, data: unknown) => {
    if (disposed || !collecting || gameId !== LEAGUE_ID || (!subscribed && source === 'event')) return;
    if (!data || typeof data !== 'object' || !requested.includes((data as { feature?: string }).feature ?? '')) return;
    const revision = filter.revision;
    const observation = filter.accept(data, performance.now(), source);
    if (filter.revision !== revision) {
      pendingQueue = undefined;
      journal.write({ kind: 'observation-reset', reason: filter.resetReason, generation: sequence, epoch: filter.revision });
    }
    if (!subscribed) {
      if (observation?.key === 'queueId') pendingQueue = observation;
      return;
    }
    if (observation) journal.write({ kind: 'observation', source: `live-${source}`, generation: sequence,
      epoch: observation.feature === 'announcer' ? revision : filter.revision, coverage: 'partial', ...observation });
    coverage();
  };
  const onInfo = (_event: unknown, gameId: number, data: unknown) => accept('info', gameId, data);
  const onEvent = (_event: unknown, gameId: number, data: unknown) => accept('event', gameId, data);
  const onExit = (_event: unknown, gameId: number) => {
    if (disposed || gameId !== LEAGUE_ID) return;
    detected = false; invalidate('game-exit');
    journal.write({ kind: 'game-exit', droppedPayloads: filter.dropped, generation: sequence });
    report('League exited; waiting for a new game detection');
  };
  const onPrivileges = (_event: unknown, gameId: number) => {
    if (disposed || gameId !== LEAGUE_ID) return;
    invalidate('privilege-mismatch');
    journal.write({ kind: 'privilege-mismatch', generation: sequence });
    report('Blocked: game/probe privilege mismatch; observations disabled');
  };
  const onError = (_event: unknown, gameId: number) => {
    if (!disposed && gameId === LEAGUE_ID) failed('provider', 'GEP provider error; observations blocked. Retry game events after resolving the provider problem');
  };
  gep.on('game-detected', onDetected);
  gep.on('new-info-update', onInfo);
  gep.on('new-game-event', onEvent);
  gep.on('game-exit', onExit);
  gep.on('elevated-privileges-required', onPrivileges);
  gep.on('error', onError);
  report('Waiting for League detection; launch the probe before starting the game');
  return {
    refresh,
    dispose() {
      if (disposed) return;
      disposed = true; detected = false; invalidate('disposed');
      gep.off('game-detected', onDetected);
      gep.off('new-info-update', onInfo);
      gep.off('new-game-event', onEvent);
      gep.off('game-exit', onExit);
      gep.off('elevated-privileges-required', onPrivileges);
      gep.off('error', onError);
    }
  };
}
