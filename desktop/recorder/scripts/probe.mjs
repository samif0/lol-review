import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyRecorderRuntime } from '../runtime/libobs.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const runtime = path.join(root, 'artifacts/desktop-recorder');
await verifyRecorderRuntime(runtime);
const child = spawn(path.join(runtime, 'revu-recorder.exe'), ['--probe'], {
  cwd: runtime, windowsHide: true, stdio: 'inherit',
});
child.on('error', error => { console.error(error.message); process.exitCode = 1; });
child.on('exit', code => { process.exitCode = code ?? 1; });
