import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import path from 'node:path';
import { checkRecordingAbort, recorderError } from './recorder-policy.mjs';

const runFile = promisify(execFile);
const powershell = () => path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
async function query(script, signal, timeout = 5000) {
  checkRecordingAbort(signal);
  if (process.platform !== 'win32') throw recorderError('game-process-inspection-unavailable');
  try {
    const { stdout } = await runFile(powershell(), ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')],
      { signal, timeout, maxBuffer: 8192, windowsHide: true, encoding: 'utf8' });
    checkRecordingAbort(signal);
    return JSON.parse(stdout);
  } catch (error) {
    checkRecordingAbort(signal);
    throw recorderError(error?.code === 'ETIMEDOUT' ? 'game-process-inspection-timeout' : 'game-process-inspection-unavailable');
  }
}

export async function scanLeaguePids(signal) {
  const value = await query("$ErrorActionPreference='Stop'; ConvertTo-Json -Compress -InputObject @((Get-CimInstance Win32_Process -Filter \"Name = 'League of Legends.exe'\" -Property Name,ProcessId) | Where-Object { $_.Name -ieq 'League of Legends.exe' } | ForEach-Object { [int]$_.ProcessId })", signal);
  if (!Array.isArray(value) || value.length > 16 || value.some(pid => !Number.isSafeInteger(pid) || pid <= 0))
    throw recorderError('game-process-inspection-unavailable');
  return [...new Set(value)];
}
export function leaguePidAlive(pid) {
  try { process.kill(pid, 0); return true; }
  catch (error) { if (error?.code === 'ESRCH') return false; throw recorderError('game-process-inspection-unavailable'); }
}

// Recorder GameInfo has no dimensions. Query the exact process's client rectangle
// rather than enabling Overlay or guessing from a display. This is one bounded
// helper per start attempt, never a recurring operation during a recording.
// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getclientrect
// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid
// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setthreaddpiawarenesscontext
export async function inspectLeagueWindow(pid, signal) {
  if (!Number.isSafeInteger(pid) || pid <= 0) throw recorderError('game-process-invalid');
  const script = `$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class RevuRecorderWindow {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumProc(IntPtr window, IntPtr state);
  [DllImport("user32.dll", SetLastError=true)] static extern bool EnumWindows(EnumProc callback, IntPtr state);
  [DllImport("user32.dll", SetLastError=true)] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
  [DllImport("user32.dll", SetLastError=true)] static extern bool GetClientRect(IntPtr window, out RECT rectangle);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
  [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
  public static string Read(int target) {
    using (Process process = Process.GetProcessById(target)) {
      if (!String.Equals(process.ProcessName, "League of Legends", StringComparison.OrdinalIgnoreCase)) return "{\\"status\\":\\"wrong-process\\"}";
      IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
      if (previous == IntPtr.Zero) throw new InvalidOperationException();
      try {
        var sizes = new List<int[]>();
        bool ok = EnumWindows(delegate(IntPtr window, IntPtr state) {
          uint owner;
          if (GetWindowThreadProcessId(window, out owner) == 0 || owner != (uint)target || !IsWindowVisible(window) || IsIconic(window)) return true;
          RECT rectangle;
          if (!GetClientRect(window, out rectangle)) return false;
          int width = rectangle.Right - rectangle.Left, height = rectangle.Bottom - rectangle.Top;
          if (width >= 64 && height >= 64) sizes.Add(new int[] { width, height });
          return true;
        }, IntPtr.Zero);
        if (!ok) throw new InvalidOperationException();
        if (sizes.Count == 0) return "{\\"status\\":\\"not-ready\\"}";
        if (sizes.Count != 1) return "{\\"status\\":\\"ambiguous\\"}";
        return "{\\"status\\":\\"ready\\",\\"pid\\":" + target + ",\\"width\\":" + sizes[0][0] + ",\\"height\\":" + sizes[0][1] + "}";
      } finally { SetThreadDpiAwarenessContext(previous); }
    }
  }
}
'@
[RevuRecorderWindow]::Read(${pid})`;
  const result = await query(script, signal);
  if (result?.status === 'not-ready') throw recorderError('source-window-not-ready');
  if (result?.status === 'ambiguous') throw recorderError('source-window-ambiguous');
  if (result?.status !== 'ready' || result.pid !== pid) throw recorderError('game-process-invalid');
  if (![result.width, result.height].every(value => Number.isInteger(value) && value >= 64 && value <= 7680))
    throw recorderError('source-dimensions-unavailable');
  return { pid, width: result.width, height: result.height };
}
