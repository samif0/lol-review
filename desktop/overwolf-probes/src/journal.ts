import { appendFileSync, mkdirSync, openSync, closeSync } from 'node:fs';
import path from 'node:path';
import { MAX_JOURNAL_BYTES, MAX_ROWS } from './policy.js';

/** Fixed record and byte caps. Callers pass only selected primitive metadata. */
export class Journal {
  rows = 0;
  bytes = 0;
  dropped = 0;
  readonly file: string;
  constructor(root: string) {
    mkdirSync(root, { recursive: true });
    this.file = path.join(root, 'observations.jsonl');
    closeSync(openSync(this.file, 'wx'));
  }
  write(row: Record<string, string | number | boolean | null>) {
    const line = JSON.stringify({ utc: new Date().toISOString(), monoMs: performance.now(), ...row }) + '\n';
    const bytes = Buffer.byteLength(line);
    if (this.rows >= MAX_ROWS || this.bytes + bytes > MAX_JOURNAL_BYTES || bytes > 2048) { this.dropped++; return; }
    appendFileSync(this.file, line);
    this.rows++; this.bytes += bytes;
  }
}
