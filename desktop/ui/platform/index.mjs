import { validateCommand } from './commands.mjs';

const WINDOW_ACTIONS = Object.freeze(['minimize', 'toggleMaximize', 'close', 'unminimize', 'show', 'setFocus', 'startDragging']);

// Electron exposes its preload only on the trusted top-level document. Same-
// origin child pages deliberately use that bridge, never subframe Node access.
export function trustedTop(scope) {
  try {
    const top = scope?.top || scope;
    if (top !== scope && (!scope.location?.origin || scope.location.origin === 'null'
      || top.location?.origin !== scope.location.origin)) return null;
    return top;
  } catch (_) { return null; }
}

async function resolveAdapter(scope) {
  const top = trustedTop(scope);
  if (!top) return null;
  const bridge = top.revuDesktop;
  if (bridge !== undefined) {
    if (bridge?.version !== 1 || bridge.kind !== 'electron'
      || typeof bridge.invoke !== 'function') throw new Error('Unsupported desktop bridge');
    return bridge;
  }
  return null; // Deliberate browser preview; no native host is present.
}

// One client per page, shared by all its modules. Disposing a page releases its
// subscriptions even if a native listen registration completes after navigation.
export function createPlatform({ scope = globalThis.window, adapterFactory = resolveAdapter } = {}) {
  let pending = null;
  let disposed = false;
  let hidden = false;
  const subscriptions = new Set();
  const getAdapter = () => {
    if (disposed) return Promise.reject(new Error('Desktop page disposed'));
    if (!pending) pending = Promise.resolve().then(() => adapterFactory(scope)).catch((error) => {
      pending = null; // an actual initialization failure can be retried
      throw error;
    });
    return pending;
  };
  const requireAdapter = async (capability) => {
    const adapter = await getAdapter();
    if (disposed) throw new Error('Desktop page disposed');
    if (!adapter || adapter.capabilities?.[capability] !== true) {
      throw new Error(`Desktop capability unavailable: ${capability}`);
    }
    return adapter;
  };
  const invoke = async (command, args) => {
    validateCommand(command, args);
    const adapter = await requireAdapter('commands');
    return adapter.invoke(command, args);
  };
  const listen = async (event, callback) => {
    if (event !== 'lcu-event') throw new Error('Unsupported desktop event');
    if (typeof callback !== 'function') throw new TypeError('Event callback is required');
    const adapter = await requireAdapter('events');
    let active = true;
    let nativeUnsubscribe = null;
    let generation = 0;
    const pause = () => {
      generation++;
      const remove = nativeUnsubscribe;
      nativeUnsubscribe = null;
      if (typeof remove === 'function') remove();
    };
    const unsubscribe = () => {
      if (!active) return;
      active = false;
      subscriptions.delete(subscription);
      pause();
    };
    const resume = async () => {
      if (!active || disposed || hidden) return;
      const current = ++generation;
      const remove = await adapter.listen(event, (value) => {
        if (active && !disposed && !hidden && current === generation) callback(value);
      });
      if (typeof remove !== 'function') throw new Error('Desktop listener did not return a disposer');
      if (!active || disposed || hidden || current !== generation) remove();
      else nativeUnsubscribe = remove;
    };
    const subscription = { pause, resume, unsubscribe };
    subscriptions.add(subscription);
    try {
      await resume();
      return unsubscribe;
    } catch (error) {
      unsubscribe();
      throw error;
    }
  };
  const onPageHide = () => {
    hidden = true;
    // Review/matchup modules flush drafts in their own pagehide callbacks. Keep
    // commands available for those final writes while releasing native listeners.
    for (const subscription of subscriptions) {
      try { subscription.pause(); } catch (_) { /* release remaining listeners */ }
    }
  };
  const onPageShow = () => {
    if (!hidden || disposed) return;
    hidden = false;
    for (const subscription of subscriptions) {
      subscription.resume().catch(() => subscription.unsubscribe());
    }
  };
  const dispose = () => {
    if (disposed) return;
    disposed = true;
    scope?.removeEventListener?.('pagehide', onPageHide);
    scope?.removeEventListener?.('pageshow', onPageShow);
    for (const subscription of subscriptions) {
      try { subscription.unsubscribe(); } catch (_) { /* release remaining subscriptions */ }
    }
  };
  scope?.addEventListener?.('pagehide', onPageHide);
  scope?.addEventListener?.('pageshow', onPageShow);
  return Object.freeze({
    invoke,
    listen,
    dispose,
    async capabilities() { return (await getAdapter())?.capabilities || Object.freeze({}); },
    async getInvoke() { return (await getAdapter())?.capabilities?.commands ? invoke : null; },
    async getListen() { return (await getAdapter())?.capabilities?.events ? listen : null; },
    async getMedia() {
      const adapter = await getAdapter();
      if (!adapter?.capabilities?.media) return null;
      return Object.freeze({ invoke, resolveMedia: (path) => {
        if (disposed) return null;
        if (typeof path !== 'string' || !path || path.includes('\0')) return null;
        return adapter.resolveMedia(path);
      } });
    },
    async getWindow() {
      const adapter = await getAdapter();
      if (!adapter?.capabilities?.windows) return null;
      return Object.freeze(Object.fromEntries(WINDOW_ACTIONS.map((action) => [action, async () => {
        const current = await requireAdapter('windows');
        if (typeof current.window?.[action] !== 'function') throw new Error(`Window action unavailable: ${action}`);
        return current.window[action]();
      }])));
    },
    pickFolder: () => invoke('pick_folder'),
    saveExport: (fileName, markdown) => invoke('save_export_file', { fileName, markdown }),
    version: () => invoke('app_version'),
    updates: Object.freeze({ check: () => invoke('check_update'), download: () => invoke('download_update'), apply: () => invoke('apply_update') }),
    // Native operations require an explicit capability from the Electron preload.
    async openExternal(url) {
      const target = new URL(url);
      if (!['https:', 'http:'].includes(target.protocol)) throw new Error('Unsupported external URL');
      const adapter = await requireAdapter('externalLinks');
      return adapter.openExternal(target.href);
    },
    async recorderStatus() { return (await requireAdapter('recorder')).recorder.status(); },
  });
}

export const platform = createPlatform();
export const getInvoke = platform.getInvoke;
export const getListen = platform.getListen;
export const getWindow = platform.getWindow;
export const getMedia = platform.getMedia;

export function resolveAssetUrl(media, filePath) {
  return media?.resolveMedia(filePath) || null;
}
