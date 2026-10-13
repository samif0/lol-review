import { spawn } from 'node:child_process';
import { access, mkdir, mkdtemp } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { cleanRuntimeEnvironment } from '../runtime/overwolf.mjs';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const executable = path.join(repository, 'artifacts/desktop-package/win-unpacked/revu-desktop.exe');
try {
  await access(executable);
  const tests = path.join(repository, 'artifacts/recording-tests');
  await mkdir(tests, { recursive: true });
  const dataRoot = await mkdtemp(path.join(tests, 'practice-'));
  console.log(`Practice Tool test recordings: ${path.join(dataRoot, 'Revu/Recordings')}`);
  const child = spawn(executable, ['--recording-test', '--data-root', dataRoot], {
    cwd: path.dirname(executable), env: cleanRuntimeEnvironment(), stdio: 'inherit', windowsHide: false,
  });
  child.once('error', error => { console.error(error.message); process.exitCode = 1; });
  child.once('exit', code => { process.exitCode = code ?? 1; });
} catch (error) { console.error(`Build the local Revu package before running the recording test: ${error.message}`); process.exitCode = 1; }
