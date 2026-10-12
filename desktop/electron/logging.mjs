import { appendFileSync, mkdirSync, renameSync, statSync, unlinkSync } from 'node:fs';
import path from 'node:path';

// Revu's own logs live beside the handshake, under the data root (respects --data-root
// and REVU_DATA_ROOT). The sidecar writes sidecar.log here; the host writes host.log
// and a capped copy of the sidecar's stderr. Overwolf's runtime keeps RuntimeLogs.
export const logDirectory = dataRoot => path.join(dataRoot, 'Revu', 'logs');

const SECRETS = [
  [/[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/g, '[email]'],
  [/\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+/gi, '$1 [redacted]'],
  [/\b(access_token|token|password|secret|api_?key|code)=[^&\s"']+/gi, '$1=[redacted]'],
  [/\bRGAPI-[A-Za-z0-9-]+/g, '[redacted]'],
  [/\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*/g, '[redacted]'],
  [/\b[A-Fa-f0-9]{32,}\b/g, '[redacted]'],
];

/** Mask emails, credentials and long hex tokens; logs never carry request bodies. */
export function redact(text) {
  let value = String(text);
  for (const [pattern, replacement] of SECRETS) value = value.replace(pattern, replacement);
  return value;
}

export function describeError(error) {
  if (error instanceof Error) return error.stack || `${error.name}: ${error.message}`;
  return String(error);
}

/**
 * A small synchronous rolling log: name.log plus older generations (name.1.log ...),
 * each capped at maxBytes. Writes before open() are kept (bounded) and flushed on
 * open, so early startup failures still reach the file. Logging never throws.
 */
export function createRollingLog({ name, maxBytes = 1024 * 1024, maxFiles = 3, now = () => new Date() }) {
  let directory = null, size = null, rotateAfter = 0;
  const early = [];
  const file = () => path.join(directory, `${name}.log`);
  const generation = index => index === 0 ? file() : path.join(directory, `${name}.${index}.log`);
  const rotate = () => {
    for (let index = maxFiles - 1; index > 0; index--) {
      try { renameSync(generation(index - 1), generation(index)); } catch { /* blocked or missing generation */ }
    }
    if (maxFiles === 1) { try { unlinkSync(file()); } catch { /* keep appending */ } }
    try { size = statSync(file()).size; } catch { size = 0; }
    // Rotation was blocked by a reader: keep appending and retry after another slice.
    rotateAfter = size > 0 ? size + Math.ceil(maxBytes / 8) : 0;
  };
  const append = text => {
    if (!text) return;
    if (!directory) { if (early.length < 200) early.push(text); return; }
    const bytes = Buffer.from(redact(text), 'utf8');
    try {
      if (size === null) {
        mkdirSync(directory, { recursive: true });
        try { size = statSync(file()).size; } catch { size = 0; }
      }
      if (size > 0 && size + bytes.length > maxBytes && size >= rotateAfter) rotate();
      appendFileSync(file(), bytes);
      size += bytes.length;
    } catch { size = null; /* A full or locked disk must never break the host. */ }
  };
  const write = (level, message, error) => {
    const detail = error === undefined ? '' : `\n  ${describeError(error).replace(/\r?\n/g, '\n  ')}`;
    append(`${now().toISOString()} ${level.toUpperCase().padEnd(5)} ${message}${detail}\n`);
  };
  return {
    get file() { return directory ? file() : null; },
    open(target) {
      if (directory) return;
      directory = target;
      for (const text of early.splice(0)) append(text);
    },
    append: chunk => append(Buffer.isBuffer(chunk) ? chunk.toString('utf8') : String(chunk)),
    info: (message, error) => write('info', message, error),
    warn: (message, error) => write('warn', message, error),
    error: (message, error) => write('error', message, error),
  };
}
