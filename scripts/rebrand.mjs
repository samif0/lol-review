#!/usr/bin/env node
// Keeps the product's display name and public domain cheap to change.
//
// brand.json is the source of truth. Four small generated files hand it to the
// stacks that can read a constant (the Electron host, the desktop UI modules, the
// C# core and sidecar, the Worker), and proxy/wrangler.toml carries it as vars.
// Everything else is served as written (site/, the desktop UI's HTML), so this
// script finds and rewrites it instead.
//
//   node scripts/rebrand.mjs --check              list what is outside the source of truth; exit 1 on
//                                                 anything unreviewed (CI runs this). Add --verbose for lines.
//   node scripts/rebrand.mjs --identity [--of <Name>]
//                                                 list the name-bearing identifiers a rename leaves alone
//   node scripts/rebrand.mjs --sync               regenerate the brand files from brand.json
//   node scripts/rebrand.mjs --to <Name> --domain <domain>
//                                                 rename, then print the steps a script cannot do
//
// What a rename never touches: anything an install or a stored file depends on
// (packId LoLReview, revu-desktop.exe, gg.revu.desktop, %LOCALAPPDATA%\Revu, C#
// namespaces, storage keys, protocol schemes, the Worker, bucket and database
// names). Those are shaped differently from prose, or carry a `rebrand: keep` comment.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const BRAND_FILE = 'brand.json';
const REVIEWED_FILE = 'scripts/rebrand.reviewed.json';
const WRANGLER = 'proxy/wrangler.toml';
export const KEEP_MARKER = 'rebrand: keep';

const NAME_SHAPE = /^[A-Za-z][A-Za-z0-9]*(?: [A-Za-z0-9]+)*$/;
const DOMAIN_SHAPE = /^(?=.{4,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/;
const escapeRegExp = text => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

export function validateBrand(brand) {
  if (!brand || typeof brand !== 'object') throw new Error('brand.json must hold an object');
  if (typeof brand.name !== 'string' || !NAME_SHAPE.test(brand.name))
    throw new Error('The name must start with a letter and use only letters, digits and single spaces');
  if (typeof brand.domain !== 'string' || !DOMAIN_SHAPE.test(brand.domain))
    throw new Error('The domain must be a lowercase host name such as example.com');
  const former = brand.formerDomains ?? [];
  if (!Array.isArray(former) || former.some(item => typeof item !== 'string' || !DOMAIN_SHAPE.test(item)))
    throw new Error('formerDomains must be a list of lowercase host names');
  if (former.includes(brand.domain)) throw new Error('formerDomains must not repeat the current domain');
  return { name: brand.name, domain: brand.domain, formerDomains: [...new Set(former)] };
}

// ── Finding occurrences ──────────────────────────────────────────────────────

/**
 * The display name is the bare word in prose or a string. A path separator, a dot,
 * an @, a hyphen or a word character on either side makes it part of an identifier
 * (Revu.Sidecar, %LOCALAPPDATA%\Revu, Revu/ElectronProfile, Revu-Setup.exe,
 * X-Revu-CSRF, RevuVod), which a rename leaves alone. Regular expressions are prose
 * too: /Revu is closing/, /restart Revu/, Revu\. and \bRevu all hold the display name.
 */
export function namePattern(name) {
  const before = '(?:(?<![\\\\/\\w.@-])'           // not glued to an identifier or a path
    + '|(?<=(?:^|[\\s(,=:!&|?{\\[;])/)'            // the opening slash of a regex literal
    + '|(?<=(?<!\\\\)\\\\[nrtbsSB]))';              // straight after \n, \b, \s and the like
  const after = '(?![\\w-]|\\\\*\\.[A-Za-z]'     // not Revu.Core (or Revu\.sln in a regex), Revu-Setup, RevuVod
    + '|/[A-Za-z0-9_]{3}'                           // not Revu/Recordings (a regex ends /, /i or /gi)
    + '|\\\\(?:\\\\|[A-Za-z0-9_]{2}))';             // not Revu\logs or Revu\\clips (a regex has \. \? \s)
  return new RegExp(before + escapeRegExp(name) + after, 'g');
}

/** The domain in any letter case, with its dots plain or backslash-escaped (inside a regex or a string holding one). */
export function domainPattern(domain) {
  return new RegExp(`(?<![A-Za-z0-9-])${domain.split('.').map(escapeRegExp).join('(\\\\*)\\.')}(?![A-Za-z0-9-])`, 'gi');
}

function recase(original, replacement) {
  const letters = original.replace(/[^A-Za-z]/g, '');
  if (letters && letters === letters.toUpperCase()) return replacement.toUpperCase();
  return replacement;
}

/** The new domain written the way the old one was: same escaping, and upper case where the old first label was. */
export function domainReplacement(match, newDomain) {
  const [firstOld] = match.split(/\\*\./);
  const escape = /(\\+)\./.exec(match)?.[1] ?? '';
  const labels = newDomain.split('.');
  labels[0] = recase(firstOld, labels[0]);
  return labels.join(`${escape}.`);
}

const SLASH_COMMENTS = new Set(['.js', '.mjs', '.cjs', '.ts', '.cs', '.cpp', '.hpp', '.h', '.c', '.css']);
const HASH_COMMENTS = new Set(['.yml', '.yaml', '.toml', '.ps1', '.py', '.sh']);
const MARKUP_COMMENTS = new Set(['.html', '.svg', '.xml', '.csproj', '.props', '.targets']);

/**
 * Returns a function that gives the comment spans of each line of `file`, as
 * [start, end) pairs; call it once per line, in order. Deliberately simple:
 * whole-line comments, a trailing `//` or `#` that follows whitespace, one-line
 * block comments in CSS and markup, and block comments that open at the start of a
 * line. A string that merely contains comment characters ('tests/*.mjs',
 * 'https://…') is not mistaken for one.
 */
export function commentScanner(file) {
  const extension = path.posix.extname(file).toLowerCase();
  const slash = SLASH_COMMENTS.has(extension), markup = MARKUP_COMMENTS.has(extension);
  const hash = HASH_COMMENTS.has(extension) || path.posix.basename(file) === 'CMakeLists.txt';
  const block = slash ? ['/*', '*/'] : markup ? ['<!--', '-->'] : null;
  const inline = extension === '.css' ? /\/\*.*?\*\//g : markup ? /<!--.*?-->/g : null;
  let closer = null;
  return line => {
    const spans = [];
    let from = 0;
    if (closer) {
      const end = line.indexOf(closer);
      if (end < 0) return [[0, line.length]];
      from = end + closer.length;
      spans.push([0, from]);
      closer = null;
    }
    const start = from + (line.length - from - line.slice(from).trimStart().length);
    if (block && line.startsWith(block[0], start)) {
      const end = line.indexOf(block[1], start + block[0].length);
      if (end < 0) { closer = block[1]; spans.push([start, line.length]); return spans; }
      spans.push([start, end + block[1].length]);
    }
    if (slash) {
      if (line.startsWith('//', start) || line.startsWith('*', start)) spans.push([start, line.length]);
      else {
        const tail = /(?:^|\s)\/\/(?=\s|$)/.exec(line);
        if (tail) spans.push([tail.index, line.length]);
      }
    }
    if (inline) for (const match of line.matchAll(inline)) spans.push([match.index, match.index + match[0].length]);
    if (hash) {
      const tail = /(?:^|\s)#/.exec(line);
      if (tail) spans.push([tail.index, line.length]);
    }
    if (extension === '.sql' && line.includes('--')) spans.push([line.indexOf('--'), line.length]);
    return spans;
  };
}

// A quoted name handed to a path builder is a folder on disk, not display text.
const PATH_CALL = /\bpath\.(?:join|resolve)\(|\bPath\.Combine\(|\bJoin-Path\b/;

/** The occurrences in one line. `keep` marks the ones a rename leaves alone. */
function lineOccurrences(line, name, domain) {
  const hits = [];
  for (const match of line.matchAll(domain)) hits.push({ kind: 'domain', index: match.index, text: match[0] });
  for (const match of line.matchAll(name)) hits.push({ kind: 'name', index: match.index, text: match[0] });
  if (!hits.length) return hits;
  hits.sort((a, b) => a.index - b.index);
  const marked = line.includes(KEEP_MARKER), pathCall = PATH_CALL.test(line);
  for (const hit of hits) {
    const quote = line[hit.index - 1];
    const folder = hit.kind === 'name' && pathCall && (quote === "'" || quote === '"') && line[hit.index + hit.text.length] === quote;
    hit.keep = marked ? `marked "${KEEP_MARKER}"` : folder ? 'folder name in a path' : null;
  }
  return hits;
}

/** Every display-name and domain occurrence in `text`, with its line and whether it sits in a comment. */
export function findOccurrences(text, brand, file = '') {
  const commentSpans = commentScanner(file);
  const name = namePattern(brand.name), domain = domainPattern(brand.domain);
  const found = [];
  const lines = text.split('\n');
  for (let number = 0; number < lines.length; number += 1) {
    const line = lines[number], spans = commentSpans(line);
    for (const hit of lineOccurrences(line, name, domain)) {
      found.push({ ...hit, line: number + 1, source: line,
        comment: spans.some(([start, end]) => hit.index >= start && hit.index < end) });
    }
  }
  return found;
}

/** `text` with every occurrence renamed, except the ones a rename leaves alone. */
export function rewriteText(text, from, to) {
  const name = namePattern(from.name), domain = domainPattern(from.domain);
  return text.split('\n').map(line => {
    let next = line;
    for (const hit of lineOccurrences(line, name, domain).reverse()) {
      if (hit.keep) continue;
      const replacement = hit.kind === 'domain' ? domainReplacement(hit.text, to.domain) : to.name;
      next = next.slice(0, hit.index) + replacement + next.slice(hit.index + hit.text.length);
    }
    return next;
  }).join('\n');
}

/** Name-bearing identifiers: every case-insensitive hit of the name that is neither display text nor the domain. */
export function findIdentity(text, brand) {
  const token = new RegExp(`[A-Za-z0-9_.:%@\\\\/-]*${escapeRegExp(brand.name)}[A-Za-z0-9_.:\\\\/-]*`, 'gi');
  const name = namePattern(brand.name), domain = domainPattern(brand.domain);
  const found = [];
  for (const line of text.split('\n')) {
    const taken = [...line.matchAll(name), ...line.matchAll(domain)].map(match => [match.index, match.index + match[0].length]);
    for (const match of line.matchAll(token)) {
      const at = match.index + match[0].toLowerCase().indexOf(brand.name.toLowerCase());
      if (line.includes(KEEP_MARKER) || !taken.some(([start, end]) => at >= start && at < end)) found.push(match[0]);
    }
  }
  return found;
}

// ── The reviewed list ────────────────────────────────────────────────────────

/** `**` crosses directories, `*` stays inside one. */
export function globPattern(glob) {
  const body = glob.split(/(\*\*\/|\*\*|\*)/).map(part => {
    if (part === '**/') return '(?:.*/)?';
    if (part === '**') return '.*';
    if (part === '*') return '[^/]*';
    return escapeRegExp(part);
  }).join('');
  return new RegExp(`^${body}$`);
}

export function parseReviewed(json) {
  const reviewed = JSON.parse(json);
  const kinds = new Set(['display', 'identity']);
  for (const rule of reviewed.paths) {
    if (typeof rule.glob !== 'string' || !kinds.has(rule.kind) || !rule.why) throw new Error(`Bad path rule: ${JSON.stringify(rule)}`);
  }
  for (const rule of reviewed.lines) {
    if (typeof rule.file !== 'string' || typeof rule.has !== 'string' || !rule.has || !rule.why)
      throw new Error(`Bad line rule: ${JSON.stringify(rule)}`);
  }
  return { paths: reviewed.paths.map(rule => ({ ...rule, pattern: globPattern(rule.glob) })), lines: reviewed.lines };
}

// ── The generated brand files ────────────────────────────────────────────────

const GENERATED = 'Generated from brand.json by scripts/rebrand.mjs. Do not edit by hand: change brand.json, then run\n'
  + '`node scripts/rebrand.mjs --sync`. CI fails while this file and brand.json disagree.';
const comment = (prefix, text) => text.split('\n').map(line => `${prefix} ${line}`).join('\n');
const jsString = text => `'${text.replace(/[\\']/g, '\\$&')}'`;

/** The public hosts and addresses that follow from the domain. */
export function derive(brand) {
  const { domain } = brand;
  return {
    siteHost: domain, siteUrl: `https://${domain}`, wwwHost: `www.${domain}`,
    apiHost: `clips.${domain}`, apiUrl: `https://clips.${domain}`, sender: `login@${domain}`,
  };
}

export function generatedFiles(brand) {
  const d = derive(brand);
  const note = 'This is the display name and the public site only. Names an install or a stored file depends on\n'
    + '(folders, the executable, the app id, storage keys) never come from here.';
  return {
    'desktop/electron/brand.mjs': `${comment('//', GENERATED)}\n${comment('//', note)}\n`
      + `export const APP_NAME = ${jsString(brand.name)};\n`,
    'desktop/ui/brand.mjs': `${comment('//', GENERATED)}\n${comment('//', note)}\n`
      + `export const APP_NAME = ${jsString(brand.name)};\n`
      + `export const SITE_HOST = ${jsString(d.siteHost)};\n`
      + `export const SITE_URL = ${jsString(d.siteUrl)};\n`
      + '// Earlier site hosts. Share links saved before a rename still point at them.\n'
      + `export const FORMER_SITE_HOSTS = Object.freeze([${brand.formerDomains.map(jsString).join(', ')}]);\n`,
    'src/Revu.Core/Constants/Brand.cs': `${comment('//', GENERATED)}\n${comment('//', note)}\n`
      + 'namespace Revu.Core.Constants;\n\n'
      + 'public static class Brand\n{\n'
      + `    public const string Name = ${JSON.stringify(brand.name)};\n}\n`,
    'proxy/src/brand.ts': `${comment('//', GENERATED)}\n`
      + '// Defaults for the vars of the same name in wrangler.toml, which a deployed Worker reads first.\n'
      + 'import type { Env } from "./types";\n\n'
      + `export const APP_NAME = ${JSON.stringify(brand.name)};\n`
      + `export const SITE_HOST = ${JSON.stringify(d.siteHost)};\n`
      + `const SITE_URL = ${JSON.stringify(d.siteUrl)};\n`
      + `const API_URL = ${JSON.stringify(d.apiUrl)};\n\n`
      + 'const origin = (value: string): string => value.replace(/\\/+$/, "");\n\n'
      + '/** The product name shown in emails and pages. */\n'
      + 'export const appName = (env: Pick<Env, "APP_NAME">): string => env.APP_NAME || APP_NAME;\n'
      + '/** Origin of the static site that serves the watch pages. No trailing slash. */\n'
      + 'export const watchBase = (env: Pick<Env, "WATCH_BASE">): string => origin(env.WATCH_BASE || SITE_URL);\n'
      + '/** Origin shareable clip links are built on. No trailing slash. */\n'
      + 'export const publicBase = (env: Pick<Env, "PUBLIC_BASE">): string => origin(env.PUBLIC_BASE || API_URL);\n'
      + '/** The one origin the cookie-authenticated web account API answers on. */\n'
      + 'export const authBase = (env: Pick<Env, "AUTH_BASE">): string => env.AUTH_BASE || API_URL;\n'
      + '/** The site host as people type it, for copy such as "enter this code on …". */\n'
      + 'export const siteHost = (env: Pick<Env, "WATCH_BASE">): string => {\n'
      + '  try { return new URL(watchBase(env)).host; } catch { return new URL(SITE_URL).host; }\n'
      + '};\n',
  };
}

const LOCAL_ORIGINS = ['http://localhost:8788', 'http://127.0.0.1:8788'];
/** The Worker vars that carry the name and domain. Former domains stay allowed while their redirects are live. */
export function wranglerVars(brand) {
  const d = derive(brand);
  const hosts = [brand.domain, ...brand.formerDomains].flatMap(host => [host, `www.${host}`]);
  const origins = hosts.map(host => `https://${host}`);
  return {
    ALLOWED_ORIGINS: [...origins, ...LOCAL_ORIGINS].join(','),
    MAGIC_LINK_FROM: d.sender,
    APP_NAME: brand.name,
    PUBLIC_BASE: d.apiUrl,
    WATCH_BASE: d.siteUrl,
    AUTH_BASE: d.apiUrl,
    CREDENTIALED_ORIGINS: origins.join(','),
    TURNSTILE_HOSTNAMES: hosts.join(','),
  };
}

const varLine = key => new RegExp(`^${key} = "([^"\\n]*)"[ \\t]*$`, 'm');
export function readWranglerVars(toml, keys) {
  return Object.fromEntries(keys.map(key => [key, varLine(key).exec(toml)?.[1]]));
}
export function writeWranglerVars(toml, vars) {
  let next = toml;
  for (const [key, value] of Object.entries(vars)) {
    if (!varLine(key).test(next)) throw new Error(`${WRANGLER} has no ${key} var`);
    next = next.replace(varLine(key), `${key} = "${value}"`);
  }
  return next;
}
const isWranglerVarLine = (line, vars) => Object.keys(vars).some(key => line.startsWith(`${key} = "`));

// ── The repository ───────────────────────────────────────────────────────────

const NEVER_SCANNED = [/(?:^|\/)package-lock\.json$/, /(?:^|\/)packages\.lock\.json$/, /^site\/vendor\//];

export function repository(root = repositoryRoot) {
  const read = relative => readFileSync(path.join(root, relative), 'utf8');
  const tracked = () => execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8', maxBuffer: 64 << 20 })
    .split('\0').filter(Boolean);
  return {
    root, read, tracked,
    write: (relative, text) => {
      mkdirSync(path.dirname(path.join(root, relative)), { recursive: true });
      writeFileSync(path.join(root, relative), text);
    },
    brand: () => validateBrand(JSON.parse(read(BRAND_FILE))),
    reviewed: () => parseReviewed(read(REVIEWED_FILE)),
    /** Tracked text files, minus lockfiles, vendored code and the files this script owns. */
    *texts(owned) {
      for (const file of tracked()) {
        if (owned.has(file) || NEVER_SCANNED.some(pattern => pattern.test(file))) continue;
        let buffer;
        try { buffer = readFileSync(path.join(root, file)); } catch { continue; }
        if (buffer.subarray(0, 8192).includes(0)) continue;
        yield [file, buffer.toString('utf8')];
      }
    },
  };
}

const HINTS = [
  [/^desktop\/electron\//, "import { APP_NAME } from './brand.mjs'"],
  [/^desktop\/ui\//, "import { APP_NAME, SITE_HOST, SITE_URL } from './brand.mjs'"],
  [/^desktop\//, "import { APP_NAME } from desktop/electron/brand.mjs"],
  [/^src\//, 'use Brand.Name (Revu.Core.Constants)'],
  [/^proxy\/src\//, 'use APP_NAME or the helpers in ./brand'],
];

/**
 * Sorts every occurrence outside the source of truth. `unreviewed` is what fails CI:
 * a name or domain written into code that could have read the brand file.
 */
export function inspect(repo = repository()) {
  const brand = repo.brand(), reviewed = repo.reviewed();
  const generated = generatedFiles(brand), vars = wranglerVars(brand);
  const owned = new Set([BRAND_FILE, REVIEWED_FILE, ...Object.keys(generated)]);
  const problems = [], files = [], unreviewed = [];
  const lineRuleHits = new Map(reviewed.lines.map(rule => [rule, 0]));

  for (const [file, expected] of Object.entries(generated)) {
    let actual = null;
    try { actual = repo.read(file).replace(/\r\n/g, '\n'); } catch { /* reported below */ }
    if (actual !== expected) problems.push(`${file} does not match brand.json. Run: node scripts/rebrand.mjs --sync`);
  }
  const toml = repo.read(WRANGLER);
  for (const [key, value] of Object.entries(readWranglerVars(toml, Object.keys(vars)))) {
    if (value !== vars[key]) problems.push(`${WRANGLER}: ${key} should be "${vars[key]}". Run: node scripts/rebrand.mjs --sync`);
  }

  for (const [file, text] of repo.texts(owned)) {
    const found = findOccurrences(text, brand, file);
    if (!found.length) continue;
    const pathRule = reviewed.paths.find(rule => rule.pattern.test(file));
    const tally = { file, name: 0, domain: 0, kept: 0, reason: new Set(), occurrences: [] };
    for (const occurrence of found) {
      let verdict;
      if (occurrence.keep) verdict = { kind: 'identity', why: occurrence.keep };
      else if (file === WRANGLER && isWranglerVarLine(occurrence.source, vars)) verdict = { kind: 'display', why: 'Worker var set from brand.json' };
      else if (pathRule) verdict = pathRule;
      else if (occurrence.comment) verdict = { kind: 'display', why: 'code comment' };
      else {
        const lineRule = reviewed.lines.find(rule => rule.file === file && occurrence.source.includes(rule.has));
        if (lineRule) { lineRuleHits.set(lineRule, lineRuleHits.get(lineRule) + 1); verdict = { kind: 'display', why: lineRule.why }; }
      }
      if (!verdict) { unreviewed.push({ file, ...occurrence }); continue; }
      if (verdict.kind === 'identity') tally.kept += 1; else tally[occurrence.kind] += 1;
      tally.reason.add(verdict.why);
      tally.occurrences.push({ ...occurrence, verdict });
    }
    files.push(tally);
  }
  for (const [rule, hits] of lineRuleHits) {
    if (!hits) problems.push(`${REVIEWED_FILE}: no line in ${rule.file} contains ${JSON.stringify(rule.has)} any more. Remove the entry.`);
  }
  return { brand, reviewed, files, unreviewed, problems };
}

function hintFor(file) { return HINTS.find(([pattern]) => pattern.test(file))?.[1] ?? null; }

function printCheck(result, { verbose, log = console.log }) {
  const { brand, files, unreviewed, problems } = result;
  const total = files.reduce((sum, file) => ({ name: sum.name + file.name, domain: sum.domain + file.domain, kept: sum.kept + file.kept }),
    { name: 0, domain: 0, kept: 0 });
  log(`Display name "${brand.name}" and domain ${brand.domain}: occurrences outside brand.json and its generated files.`);
  log('A rename rewrites every one of these except the "kept" column.\n');
  log('  name  domain  kept  file');
  for (const file of files) {
    log(`${String(file.name).padStart(6)}${String(file.domain).padStart(8)}${String(file.kept).padStart(6)}  ${file.file}  (${[...file.reason].join('; ')})`);
    if (verbose) for (const item of file.occurrences) log(`          ${item.line}: ${item.source.trim().slice(0, 160)}`);
  }
  log(`\n${total.name} name and ${total.domain} domain occurrences in ${files.length} files are reviewed; ${total.kept} are kept as identity.`);
  if (problems.length) { log(`\n${problems.length} problem(s):`); for (const problem of problems) log(`  ${problem}`); }
  if (unreviewed.length) {
    log(`\n${unreviewed.length} occurrence(s) are not on the reviewed list:`);
    for (const item of unreviewed) {
      log(`  ${item.file}:${item.line}: ${item.source.trim().slice(0, 180)}`);
    }
    const hints = new Set(unreviewed.map(item => hintFor(item.file)).filter(Boolean));
    log('\nRead the name or domain from the brand file instead of writing it out:');
    for (const hint of hints) log(`  ${hint}`);
    log(`If the text is an identifier an install or a stored file depends on, end the line with a "${KEEP_MARKER}" comment.`);
    log(`If it must stay written out (no build step, a log template), add it to ${REVIEWED_FILE} with the reason.`);
  }
  return problems.length || unreviewed.length ? 1 : 0;
}

function sync(repo, brand) {
  const written = [];
  for (const [file, text] of Object.entries(generatedFiles(brand))) {
    let current = null;
    try { current = repo.read(file); } catch { /* new file */ }
    if (current?.replace(/\r\n/g, '\n') !== text) { repo.write(file, text); written.push(file); }
  }
  const toml = repo.read(WRANGLER), next = writeWranglerVars(toml, wranglerVars(brand));
  if (next !== toml) { repo.write(WRANGLER, next); written.push(WRANGLER); }
  return written;
}

const INLINE_SCRIPT = /<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/g;
/**
 * The CSP hashes of a page's inline scripts, one for LF and one for CRLF line
 * endings. site/_headers pins them, so a rename that edits such a script must
 * carry the pinned hashes along.
 */
export function inlineScriptHashes(html) {
  const hashes = [];
  for (const match of html.matchAll(INLINE_SCRIPT)) {
    const lf = match[1].replace(/\r\n/g, '\n');
    for (const body of [lf, lf.replace(/\n/g, '\r\n')]) hashes.push(`sha256-${createHash('sha256').update(body, 'utf8').digest('base64')}`);
  }
  return hashes;
}

/** Rewrites the tree from the current brand to `target`. Returns what changed and what was left alone. */
export function rename(repo, target) {
  const from = repo.brand(), reviewed = repo.reviewed();
  const formerDomains = target.domain === from.domain ? from.formerDomains
    : [...new Set([from.domain, ...from.formerDomains])].filter(domain => domain !== target.domain);
  const to = validateBrand({ name: target.name, domain: target.domain, formerDomains });
  if (to.name === from.name && to.domain === from.domain) throw new Error('That is already the name and domain');
  // A name inside the other brand's own text would be rewritten twice on the way back.
  if (namePattern(from.name).test(to.name) || domainPattern(from.domain).test(to.domain))
    throw new Error('The new name or domain contains the old one; rename through an unrelated name first');

  const owned = new Set([BRAND_FILE, REVIEWED_FILE, ...Object.keys(generatedFiles(from))]);
  const changed = [], kept = new Map(), collisions = [], hashes = new Map(), headerFiles = [];
  const keep = (file, why) => { const key = `${file}\n${why}`; kept.set(key, (kept.get(key) ?? 0) + 1); };
  for (const [file, text] of repo.texts(owned)) {
    // The new name as a word that is already here: after the rename it reads as the product's name.
    for (const item of findOccurrences(text, to, file)) collisions.push({ file, line: item.line, source: item.source });
    if (path.posix.basename(file) === '_headers') headerFiles.push(file);
    const pathRule = reviewed.paths.find(rule => rule.pattern.test(file));
    const found = findOccurrences(text, from, file);
    if (!found.length) continue;
    if (pathRule?.kind === 'identity') { for (const item of found) keep(file, pathRule.why); continue; }
    for (const item of found) if (item.keep) keep(file, item.keep);
    const next = rewriteText(text, from, to);
    if (next === text) continue;
    if (file.endsWith('.html')) {
      const before = inlineScriptHashes(text), after = inlineScriptHashes(next);
      before.forEach((hash, index) => { if (hash !== after[index]) hashes.set(hash, after[index]); });
    }
    repo.write(file, next);
    changed.push(file);
  }
  for (const file of headerFiles) {
    const text = repo.read(file);
    let next = text;
    for (const [before, after] of hashes) next = next.split(before).join(after);
    if (next !== text) { repo.write(file, next); changed.push(file); }
  }
  const rawReviewed = JSON.parse(repo.read(REVIEWED_FILE));
  rawReviewed.lines = rawReviewed.lines.map(rule => ({ ...rule, has: rewriteText(rule.has, from, to) }));
  repo.write(REVIEWED_FILE, `${JSON.stringify(rawReviewed, null, 2)}\n`);
  repo.write(BRAND_FILE, `${JSON.stringify(to, null, 2)}\n`);
  changed.push(BRAND_FILE, ...sync(repo, to));
  return {
    from, to, changed: [...new Set(changed)], collisions, rehashed: hashes.size,
    kept: [...kept].map(([key, count]) => { const [file, why] = key.split('\n'); return { file, why, count }; }),
  };
}

export function manualSteps(from, to) {
  const was = derive(from), now = derive(to);
  const domainChanged = from.domain !== to.domain;
  const steps = [];
  const add = (title, ...lines) => steps.push({ title, lines });
  add('Review and verify the rewrite',
    'Read `git diff`. Prose is rewritten word for word, so check grammar, line lengths and anything that reads oddly.',
    'Run every verify command in CLAUDE.md, the proxy and site tests, and `node scripts/rebrand.mjs --check`.',
    'The hashes site/_headers pins for inline scripts are updated for you; site/tests/headers.test.mjs confirms them.');
  add('Replace the artwork, which no script can redraw',
    'site/og-image.jpg, the icons and favicon in site/, site/revu.svg and site/revu-mark.png, the shot-*.webp screenshots,',
    'and desktop/electron/branding (run desktop/scripts/build-branding.mjs after editing the SVG). File names stay as they are.',
    'A new og-image needs OG_IMAGE_VERSION bumped so chat apps refetch it.');
  if (domainChanged) {
    add(`Register ${to.domain} and add it to Cloudflare`,
      `Create the zone, then DNS records for ${now.siteHost}, ${now.wwwHost} and ${now.apiHost}.`,
      `Keep ${from.domain} registered and on Cloudflare for as long as any old link should work. If it ever lapses, first remove it`,
      `from formerDomains in brand.json and run --sync, so a new owner of ${from.domain} is not a trusted origin of the account API.`);
    add('Email sender',
      `Verify ${to.domain} in Resend (SPF, DKIM, DMARC records) before the Worker starts sending from ${now.sender}.`,
      `Until it is verified, sign-in emails from ${now.sender} bounce: verify first, deploy second.`);
    add('Turnstile',
      `Add ${now.siteHost} and ${now.wwwHost} to the Turnstile widget's hostnames. Keep ${was.siteHost} and ${was.wwwHost} until the redirects below are live.`);
    add('Worker (proxy/)',
      `Add a custom domain or route for ${now.apiHost} to the Worker and keep the one for ${was.apiHost}: old clip links (${was.apiUrl}/<id>) and`,
      'installs that have not updated yet still call it. Then deploy the Worker from a clean checkout of main.',
      `The web sign-in cookie is bound to ${was.apiHost}, so everyone signed in on the site signs in once more on ${now.siteHost}.`);
    add('Pages (site/)',
      `Add ${now.siteHost} and ${now.wwwHost} as custom domains of the Pages project, then deploy with node scripts/deploy-site.mjs.`,
      `Keep ${was.siteHost} attached until the redirect rule below is in place.`);
    add('Riot and search verification',
      `site/riot.txt must be reachable at ${now.siteUrl}/riot.txt. Update the product URL in the Riot developer portal application and tell Riot about the new name and domain.`,
      `Re-verify ${to.domain} in Google Search Console (site/google*.html is tied to the old property) and submit ${now.siteUrl}/sitemap.xml.`);
    add('Ship the desktop app before switching share links',
      `Tag a release. The new build shows the new name and accepts share links on both ${now.siteHost} and ${was.siteHost}.`,
      `Installs that have not updated only recognise ${was.siteHost} links, so give the update a few days to spread before the redirect step,`,
      'or accept that an old build hides the link of a newly shared game until it updates.');
    add(`Redirect ${from.domain} so every old link keeps working`,
      `Once the new site is live, add one Cloudflare redirect rule on the ${from.domain} zone: https://${was.siteHost}/* and https://${was.wwwHost}/* to`,
      `${now.siteUrl}/$1, status 301, preserving the path and query string. Browsers carry the #notes= fragment across the redirect themselves.`,
      `Leave ${was.apiHost} served by the Worker (no redirect): media, uploads and old builds use it directly.`,
      `Check after the rule is on: an old clip link (${was.apiUrl}/<id>), an old full-game link (${was.siteUrl}/v/<id>), and a link with #notes=.`,
      `Coach notes and the "viewed" marks a browser saved under ${was.siteHost} stay with that origin and do not follow the redirect; notes inside a link do.`);
  }
  add('Places that keep the old name on purpose, each a separate decision',
    '.github/workflows/release.yml: the installer shortcut title (--packTitle), the GitHub release name and the Revu-Setup.exe asset the site downloads.',
    'Changing --packTitle renames Start menu and desktop shortcuts on update; test an in-place update from the current release before shipping that.',
    'The GitHub repository, the Discord server, the Azure signing profile and the Worker, bucket and database names do not change with the display name.');
  add('Tell people',
    'Update the README badge and links if the repository moves, the Discord server name, and the Riot application text.');
  return steps;
}

function printSteps(steps, log = console.log) {
  log('\nWhat this script cannot do, in order:\n');
  steps.forEach((step, index) => {
    log(`${index + 1}. ${step.title}`);
    for (const line of step.lines) log(`   ${line}`);
    log('');
  });
}

function parseArguments(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    if (['--check', '--sync', '--identity', '--verbose', '--allow-dirty'].includes(flag)) options[flag.slice(2)] = true;
    else if (flag === '--to' || flag === '--domain' || flag === '--of') {
      const value = argv[index + 1];
      if (value === undefined || value.startsWith('--')) throw new Error(`${flag} needs a value`);
      options[flag.slice(2)] = value; index += 1;
    } else throw new Error(`Unknown argument: ${flag}`);
  }
  return options;
}

export function main(argv, repo = repository(), log = console.log) {
  const options = parseArguments(argv);
  if (options.check) return printCheck(inspect(repo), { verbose: options.verbose, log });
  if (options.sync) {
    const written = sync(repo, repo.brand());
    log(written.length ? `Updated: ${written.join(', ')}` : 'The brand files already match brand.json.');
    return 0;
  }
  if (options.identity) {
    const brand = { ...repo.brand(), ...(options.of ? { name: options.of } : {}) }, counts = new Map();
    for (const [, text] of repo.texts(new Set([BRAND_FILE, REVIEWED_FILE])))
      for (const token of findIdentity(text, brand)) counts.set(token, (counts.get(token) ?? 0) + 1);
    log('Name-bearing identifiers a rename leaves alone (count, text):');
    for (const [token, count] of [...counts].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))) log(`${String(count).padStart(6)}  ${token}`);
    return 0;
  }
  if (options.to !== undefined || options.domain !== undefined) {
    const current = repo.brand();
    if (!options['allow-dirty']) {
      const dirty = execFileSync('git', ['status', '--porcelain', '--untracked-files=no'], { cwd: repo.root, encoding: 'utf8' }).trim();
      if (dirty) throw new Error('Commit or set aside your changes first, so the rename is the only thing in the diff (or pass --allow-dirty)');
    }
    const result = rename(repo, { name: options.to ?? current.name, domain: options.domain ?? current.domain });
    log(`Renamed "${result.from.name}" (${result.from.domain}) to "${result.to.name}" (${result.to.domain}) in ${result.changed.length} files.`);
    if (result.rehashed) log(`Updated ${result.rehashed} pinned inline-script hash(es) in site/_headers.`);
    if (result.kept.length) {
      log('\nLeft alone:');
      for (const item of result.kept) log(`  ${item.file}: ${item.count} (${item.why})`);
    }
    log(`\nIdentifiers that contain the old name were not touched. List them with: node scripts/rebrand.mjs --identity --of ${JSON.stringify(result.from.name)}`);
    if (result.collisions.length) {
      log(`\n"${result.to.name}" or ${result.to.domain} was already written in ${result.collisions.length} place(s) before the rename.`);
      log('They now read as the product name, the check may flag the ones in code, and a later rename would rewrite them too:');
      for (const item of result.collisions.slice(0, 40)) log(`  ${item.file}:${item.line}: ${item.source.trim().slice(0, 140)}`);
      if (result.collisions.length > 40) log(`  ... and ${result.collisions.length - 40} more`);
    }
    printSteps(manualSteps(result.from, result.to), log);
    const after = inspect(repo);
    if (after.problems.length || after.unreviewed.length) {
      log('The check does not pass on the renamed tree:');
      return printCheck(after, { log });
    }
    log('node scripts/rebrand.mjs --check passes on the renamed tree.');
    return 0;
  }
  log('Usage: node scripts/rebrand.mjs --check [--verbose] | --identity [--of <Name>] | --sync | --to <Name> --domain <domain>');
  return 2;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { process.exitCode = main(process.argv.slice(2)); }
  catch (error) { console.error(error.message); process.exitCode = 2; }
}
