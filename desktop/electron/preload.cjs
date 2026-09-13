const { contextBridge, ipcRenderer } = require('electron');

// Sandboxed preload intentionally has no Node filesystem or bearer access.
if (process.isMainFrame && location.protocol === 'revu-app:' && location.host === 'ui') {
  const isolated = process.argv.includes('--revu-isolated');
  let media = new Map();
  ipcRenderer.on('revu:media-clear', () => { media = new Map(); });
  const invoke = async (command, args = {}) => {
    const result = await ipcRenderer.invoke('revu:command', command, args);
    if (result.grants) media = new Map(result.grants);
    return result.value;
  };
  contextBridge.exposeInMainWorld('revuDesktop', Object.freeze({
    version: 1, kind: 'electron',
    capabilities: Object.freeze({ commands: true, events: true, media: true, windows: true,
      dialogs: true, updates: !isolated, externalLinks: !isolated, recorder: !isolated }),
    invoke,
    openExternal: url => ipcRenderer.invoke('revu:open-external', url),
    listen: async (event, callback) => {
      if (event !== 'lcu-event' || typeof callback !== 'function') throw new Error('Invalid event subscription');
      const listener = (_event, payload) => callback({ payload });
      ipcRenderer.on('revu:lcu-event', listener);
      return () => ipcRenderer.removeListener('revu:lcu-event', listener);
    },
    resolveMedia: (filePath) => media.get(filePath) || null,
    window: Object.freeze(Object.fromEntries(['minimize', 'toggleMaximize', 'close', 'unminimize', 'show', 'setFocus', 'startDragging']
      .map(action => [action, () => ipcRenderer.invoke('revu:window', action)]))),
  }));
}
