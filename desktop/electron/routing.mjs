import { validateCommand } from '../ui/platform/commands.mjs';

export const APP_ORIGIN = 'revu-app://ui';
export function isAppUrl(value) {
  try { const url = new URL(value); return url.protocol === 'revu-app:' && url.host === 'ui' && !url.username && !url.password; }
  catch { return false; }
}
export function validateSender(event, contents) {
  if (event.sender !== contents || event.senderFrame !== contents.mainFrame || !isAppUrl(event.senderFrame?.url))
    throw new Error('Untrusted desktop sender');
}
export function commandRequest(command, args = {}) {
  const definition = validateCommand(command, args);
  const serialized = JSON.stringify(args);
  // Copy and file export carry the same generated review markdown. Both keep
  // a 4 MiB serialized-envelope cap; other commands retain the 64 KiB limit.
  const limit = ['save_export_file', 'copy_text_to_clipboard'].includes(command) ? 4 * 1024 * 1024 : 64 * 1024;
  if (Buffer.byteLength(serialized) > limit)
    throw new Error('Command exceeds payload limit');
  const query = new URLSearchParams();
  for (const [arg, key] of Object.entries(definition.query || {})) {
    const value = args[arg];
    if (value == null || value === '' || (definition.positiveQuery?.includes(arg) && value <= 0)) continue;
    query.set(key, String(value));
  }
  return { definition, route: definition.route + (query.size ? `?${query}` : ''),
    method: definition.method, body: definition.method === 'POST' ? args.payload || {} : undefined,
    timeoutMs: definition.timeoutMs };
}

export function snapshotMedia(command, value) {
  if (!['get_vod', 'get_patterns'].includes(command)) return null;
  const paths = [];
  // Snapshot DTO fields only; never infer filesystem permission from arbitrary
  // strings, config folders, commands, or frontend-provided paths.
  const visit = (node, depth = 0) => {
    if (!node || typeof node !== 'object' || depth > 12 || paths.length >= 512) return;
    for (const [key, value] of Object.entries(node)) {
      if (['filePath', 'vodPath', 'clipPath'].includes(key) && typeof value === 'string' && value) paths.push(value);
      else if (value && typeof value === 'object') visit(value, depth + 1);
    }
  };
  visit(value);
  return paths;
}
