import { spawn } from 'node:child_process';
import { access } from 'node:fs/promises';
import { cleanRuntimeEnvironment, desktopRoot, inspectRuntime } from '../runtime/overwolf.mjs';
import { launchOptions } from '../electron/configuration.mjs';

try {
  const args = process.argv.slice(2);
  launchOptions(args); // Validate diagnostic flags before any application data access.
  const runtime = inspectRuntime();
  if (!runtime.installed) throw new Error('Run npm run runtime:install first.');
  if (!args.includes('--sidecar')) {
    const executable = new URL('../../artifacts/desktop-build/bin/Revu.Sidecar/release_win-x64/Revu.Sidecar.exe', import.meta.url);
    try { await access(executable); }
    catch { throw new Error('Run npm run build:sidecar first.'); }
  }
  const child = spawn(runtime.executable, [desktopRoot, ...args], {
    // This is the requested GUI, not a background helper. SW_HIDE can suppress
    // the first BrowserWindow.show() on Windows until a second launch focuses it.
    cwd: desktopRoot, env: cleanRuntimeEnvironment(), stdio: 'inherit', windowsHide: args.includes('--smoke'),
  });
  child.once('error', () => { console.error('Unable to launch Revu.'); process.exitCode = 1; });
  child.once('exit', code => { process.exitCode = code ?? 1; });
} catch (error) { console.error(error.message); process.exitCode = 1; }
