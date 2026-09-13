// Optional authoring tool: export the canonical SVG with Inkscape, then package
// the PNG frames into a Windows ICO. No new runtime or packaging dependency.
import { execFileSync } from 'node:child_process';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const branding = fileURLToPath(new URL('../electron/branding/', import.meta.url));
const inkscape = process.env.INKSCAPE_PATH || 'inkscape';
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const temporary = await mkdtemp(path.join(tmpdir(), 'revu-branding-'));

try {
  const frames = [];
  for (const size of [...sizes, 512]) {
    const destination = path.join(temporary, `${size}.png`);
    execFileSync(inkscape, [path.join(branding, 'revu.svg'), '--export-type=png',
      `--export-filename=${destination}`, `--export-width=${size}`, `--export-height=${size}`],
    { stdio: 'pipe', windowsHide: true });
    const png = await readFile(destination);
    if (!png.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])))
      throw new Error(`Invalid PNG export at ${size}px`);
    if (size === 512) await writeFile(path.join(branding, 'revu.png'), png);
    else frames.push({ size, png });
  }

  const directory = Buffer.alloc(6 + frames.length * 16);
  directory.writeUInt16LE(1, 2); // Windows icon, not a cursor.
  directory.writeUInt16LE(frames.length, 4);
  let offset = directory.length;
  frames.forEach(({ size, png }, index) => {
    const entry = 6 + index * 16;
    directory[entry] = directory[entry + 1] = size === 256 ? 0 : size;
    directory.writeUInt16LE(1, entry + 4);
    directory.writeUInt16LE(32, entry + 6);
    directory.writeUInt32LE(png.length, entry + 8);
    directory.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });
  await writeFile(path.join(branding, 'revu.ico'), Buffer.concat([directory, ...frames.map(frame => frame.png)]));
  console.log(`Exported Revu PNG (512px) and ICO (${sizes.join(', ')}px).`);
} finally {
  // Remove only the absolute scratch directory created above, never an input or
  // caller-supplied path. It must remain directly inside the OS temp directory.
  if (path.dirname(temporary) !== path.resolve(tmpdir()) || !path.basename(temporary).startsWith('revu-branding-'))
    throw new Error('Unexpected branding scratch directory');
  await rm(temporary, { recursive: true, force: true });
}
