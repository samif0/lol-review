import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Probe artifacts and locks stay isolated; the SDK and environment policy live at desktop/.
export const probeRoot = path.resolve(fileURLToPath(new URL('..', import.meta.url)));
export { desktopRoot, credentialNames, cleanRuntimeEnvironment, credentialStatus, inspectRuntime,
  installPinnedRuntime } from '../../runtime/overwolf.mjs';
