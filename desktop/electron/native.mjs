import { spawn } from 'node:child_process';
import { access, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

export function externalUrl(value) {
  if (typeof value !== 'string' || value.length > 8192) throw new Error('Invalid external URL');
  const target = new URL(value);
  if (!['https:', 'http:'].includes(target.protocol) || target.username || target.password)
    throw new Error('Unsupported external URL');
  return target.href;
}

export function updaterPath(executable, packaged) {
  const current = path.dirname(executable);
  if (!packaged || path.basename(current).toLowerCase() !== 'current')
    throw new Error('Updates apply only in the installed app');
  return path.join(path.dirname(current), 'Update.exe');
}

export function nativeCommands({ app, dialog, screen, shell, window, dataRoot, isolated, quit }) {
  return async (command, args) => {
    switch (command) {
      case 'app_version': return app.getVersion();
      case 'pick_folder': {
        const result = await dialog.showOpenDialog(window, { properties: ['openDirectory'] });
        return result.canceled ? null : result.filePaths[0];
      }
      case 'save_export_file': {
        const result = await dialog.showSaveDialog(window, {
          defaultPath: path.basename(args.fileName), filters: [{ name: 'Markdown', extensions: ['md'] }],
        });
        if (result.canceled || !result.filePath) return { ok: true, saved: false };
        await writeFile(result.filePath, args.markdown, 'utf8');
        return { ok: true, saved: true, path: result.filePath };
      }
      case 'open_log_folder': {
        const folder = path.join(dataRoot, 'Revu');
        await mkdir(folder, { recursive: true });
        if (await shell.openPath(folder)) throw new Error('Unable to open the log folder');
        return null;
      }
      case 'set_window_resolution': {
        if (args.resolution === 'maximized') { window.maximize(); return null; }
        const match = /^(\d+)x(\d+)$/.exec(args.resolution);
        const width = match ? Number(match[1]) : 1600, height = match ? Number(match[2]) : 1000;
        if (width < 980 || height < 640 || width > 10000 || height > 10000) return null;
        const area = screen.getDisplayMatching(window.getBounds()).workAreaSize;
        window.unmaximize(); window.setSize(Math.min(width, area.width), Math.min(height, area.height)); window.center();
        return null;
      }
      case 'apply_update': {
        if (isolated) throw new Error('Updates are disabled in isolated mode');
        const updater = updaterPath(process.execPath, app.isPackaged);
        await access(updater);
        quit(async () => {
          const child = spawn(updater, ['apply', '--silent', '--waitPid', String(process.pid)], {
            cwd: path.dirname(updater), detached: true, windowsHide: true, stdio: 'ignore',
          });
          await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
          child.unref();
        });
        return null;
      }
      // Navigation is owned by the renderer; these compatibility commands are no-ops.
      case 'review_vod': case 'open_review': case 'take_next_step': return null;
      default: throw new Error('Unsupported desktop operation');
    }
  };
}
