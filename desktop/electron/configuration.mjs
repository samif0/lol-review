import { mkdirSync, readFileSync, realpathSync } from 'node:fs';
import path from 'node:path';

export function launchOptions(argv) {
  const value = name => {
    const index = argv.indexOf(name);
    if (index < 0) return null;
    const result = argv[index + 1];
    if (!result || result.startsWith('--') || !path.isAbsolute(result))
      throw new Error(`${name} requires an absolute path`);
    return result;
  };
  const isolated = argv.includes('--isolated');
  const smoke = argv.includes('--smoke');
  const recordingTest = argv.includes('--recording-test');
  if (smoke && !isolated) throw new Error('--smoke requires --isolated');
  if (recordingTest && isolated) throw new Error('--recording-test cannot be combined with --isolated');
  return { isolated, smoke, recordingTest, dataRoot: value('--data-root'), executable: value('--sidecar') };
}

// Gaming packages this build requests (package.json overwolf.packages). The ordinary
// build declares none; only the credentialed development launcher adds 'recorder'.
export function declaredPackages(desktopRoot) {
  try {
    const packages = JSON.parse(readFileSync(path.join(desktopRoot, 'package.json'), 'utf8')).overwolf?.packages;
    return Array.isArray(packages) ? packages.filter(name => typeof name === 'string') : [];
  } catch { return []; }
}

export function isMaintenanceLaunch(argv) {
  return argv.some(arg => /^--veloapp-(install|updated|obsolete|uninstall)$/.test(arg));
}

// Set Electron's profile before its ready event; asynchronous filesystem setup
// can otherwise initialize Chromium's cache in a different running app's profile.
export function prepareConfiguration({ options, desktopRoot, localAppData, packaged, executablePath, environment = process.env }) {
  if (!localAppData || !path.isAbsolute(localAppData)) throw new Error('Windows LocalAppData is unavailable');
  if (packaged && options.isolated && !options.dataRoot) throw new Error('Packaged isolated launches require --data-root outside LocalAppData');
  if (options.recordingTest && !options.dataRoot) throw new Error('Recording tests require an explicit scratch --data-root');
  const requested = path.resolve(options.dataRoot || (options.isolated
    ? path.join(desktopRoot, '../artifacts/desktop-preview') : environment.REVU_DATA_ROOT || localAppData));
  const local = path.resolve(localAppData).toLowerCase();
  if ((options.isolated || options.recordingTest) && (requested.toLowerCase() === local || requested.toLowerCase().startsWith(local + path.sep)))
    throw new Error('Isolated launches require storage outside LocalAppData');
  mkdirSync(requested, { recursive: true });
  const dataRoot = realpathSync(requested);
  // The backend independently rejects aliases and reparse points for scratch data.
  if ((options.isolated || options.recordingTest) && dataRoot.toLowerCase() !== requested.toLowerCase())
    throw new Error('Isolated data-root aliases are not supported');
  const profile = path.join(dataRoot, 'Revu', options.recordingTest ? 'RecordingTestElectronProfile'
    : options.isolated ? 'IsolatedElectronProfile' : 'ElectronProfile');
  mkdirSync(profile, { recursive: true });
  return {
    ...options, dataRoot, profile,
    windowsAppId: options.recordingTest ? 'gg.revu.desktop.recording-test'
      : options.isolated ? 'gg.revu.desktop.isolated' : 'gg.revu.desktop',
    iconPath: packaged ? path.join(path.dirname(executablePath), 'resources/branding/revu.ico')
      : path.join(desktopRoot, 'electron/branding/revu.ico'),
    uiDirectory: path.join(desktopRoot, 'ui'),
    recorderDirectory: packaged ? path.join(path.dirname(executablePath), 'recorder')
      : path.resolve(desktopRoot, '../artifacts/desktop-recorder'),
    executable: packaged ? path.join(path.dirname(executablePath), 'Revu.Sidecar.exe')
      : options.executable || path.resolve(desktopRoot, '../artifacts/desktop-build/bin/Revu.Sidecar/release_win-x64/Revu.Sidecar.exe'),
  };
}
