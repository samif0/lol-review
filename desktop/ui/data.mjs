import { getInvoke } from './platform/index.mjs';

/** A host failure must remain visible: sample data is only for browser previews. */
export function createSnapshotReader({ resolveInvoke = getInvoke, fetchJson = (...args) => globalThis.fetch(...args) } = {}) {
  return async function readSnapshot(command, sample, args) {
    const invoke = await resolveInvoke();
    if (invoke) return invoke(command, args);
    const response = await fetchJson(`./${sample}`);
    if (!response.ok) throw new Error(`${sample} ${response.status}`);
    return response.json();
  };
}

export const readSnapshot = createSnapshotReader();
