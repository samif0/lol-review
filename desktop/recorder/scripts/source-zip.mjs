import { readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

const crcTable = Array.from({ length: 256 }, (_, value) => {
  let crc = value;
  for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  return crc >>> 0;
});
function crc32(data) {
  let crc = 0xffffffff;
  for (const byte of data) crc = (crc >>> 8) ^ crcTable[(crc ^ byte) & 0xff];
  return (crc ^ 0xffffffff) >>> 0;
}

/** Store exact source bytes in a reproducible ZIP with fixed dates and sorted UTF-8 names.
 * This subset is small; it deliberately contains neither compression nor platform file metadata.
 */
export async function writeSourceZip(directory, destination) {
  async function entries(root, prefix = '') {
    const result = [];
    for (const entry of await readdir(root, { withFileTypes: true })) {
      const name = prefix + entry.name, absolute = path.join(root, entry.name);
      if (entry.isDirectory()) result.push(...await entries(absolute, `${name}/`));
      else if (entry.isFile()) result.push({ name, data: await readFile(absolute) });
      else throw new Error(`Source subset rejects aliases: ${name}`);
    }
    return result;
  }
  const localParts = [], centralParts = [];
  let offset = 0;
  const files = (await entries(directory)).sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
  for (const { name, data } of files) {
    const filename = Buffer.from(name, 'utf8'), crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50); local.writeUInt16LE(20, 4); local.writeUInt16LE(0x800, 6);
    local.writeUInt16LE(33, 12); local.writeUInt32LE(crc, 14); local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22); local.writeUInt16LE(filename.length, 26);
    localParts.push(local, filename, data);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x800, 8); central.writeUInt16LE(33, 14); central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20); central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(filename.length, 28); central.writeUInt32LE(offset, 42);
    centralParts.push(central, filename);
    offset += local.length + filename.length + data.length;
  }
  const centralDirectory = Buffer.concat(centralParts), footer = Buffer.alloc(22);
  footer.writeUInt32LE(0x06054b50); footer.writeUInt16LE(files.length, 8); footer.writeUInt16LE(files.length, 10);
  footer.writeUInt32LE(centralDirectory.length, 12); footer.writeUInt32LE(offset, 16);
  await writeFile(destination, Buffer.concat([...localParts, centralDirectory, footer]));
}
