import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  KEEP_MARKER, commentScanner, derive, domainPattern, domainReplacement, findIdentity, findOccurrences, generatedFiles,
  globPattern, inspect, main, manualSteps, namePattern, repository, rewriteText, validateBrand, wranglerVars, writeWranglerVars,
} from './rebrand.mjs';

const brand = { name: 'Revu', domain: 'revu.lol', formerDomains: [] };
const sample = { name: 'Sample', domain: 'sample.example', formerDomains: [] };
const names = text => [...text.matchAll(namePattern('Revu'))].length;

test('the bare word is the display name; anything shaped like an identifier is not', () => {
  for (const display of ['Restart Revu to continue.', "Revu's window", '<title>Revu: Review</title>', "title: 'Revu'",
    '"Revu"', '>Revu<', 'Revu v4.6.0', '(Revu is closing)', 'Revu.']) assert.equal(names(display), 1, display);
  for (const identity of ['Revu.Sidecar.exe', 'namespace Revu.Core.Data;', '%LOCALAPPDATA%\\Revu', 'Videos\\\\Revu\\\\clips',
    "'Revu/ElectronProfile'", 'Revu-Setup.exe', 'X-Revu-CSRF', 'RevuVodInk', 'root.RevuNotes', '__revuActiveWork',
    'revu-desktop.exe', 'gg.revu.desktop', 'REVU_DATA_ROOT', 'src/Revu.Core', 'Revu.sln']) assert.equal(names(identity), 0, identity);
});

test('the domain is found in any case, under subdomains and with escaped dots, but not inside a longer label', () => {
  const found = text => [...text.matchAll(domainPattern('revu.lol'))].map(match => match[0]);
  assert.deepEqual(found('https://revu.lol/v/Abc1234'), ['revu.lol']);
  assert.deepEqual(found('https://clips.revu.lol/x login@revu.lol'), ['revu.lol', 'revu.lol']);
  assert.deepEqual(found('/^https:\\/\\/revu\\.lol\\/v\\//'), ['revu\\.lol']);
  assert.deepEqual(found('"revu\\\\.lol"'), ['revu\\\\.lol']);
  assert.deepEqual(found('https://REVU.lol'), ['REVU.lol']);
  assert.deepEqual(found('https://revu.lol.evil.example'), ['revu.lol']);
  assert.deepEqual(found('notrevu.lol revu.lolz my-revu.lol revu-proxy.lol-review.workers.dev'), []);
});

test('a rewritten domain keeps the escaping and the case it was written in', () => {
  assert.equal(domainReplacement('revu.lol', 'sample.example'), 'sample.example');
  assert.equal(domainReplacement('revu\\.lol', 'sample.example'), 'sample\\.example');
  assert.equal(domainReplacement('revu\\\\.lol', 'my.sample.example'), 'my\\\\.sample\\\\.example');
  assert.equal(domainReplacement('REVU.lol', 'sample.example'), 'SAMPLE.example');
});

test('rewriting changes display text and URLs and leaves identifiers and kept lines alone', () => {
  const before = [
    "dialog.showErrorBox('Revu could not start');",
    "const profile = path.join(root, 'Revu/ElectronProfile');",
    `const folder = path.join(root, 'Revu'); // ${KEEP_MARKER} (data folder)`,
    'fetch("https://clips.revu.lol/auth/web/me", { headers: { "X-Revu-CSRF": "1" } });',
    'assert.match(text, /Only enter this code on revu\\.lol or in the Revu app\\./);',
    'for (const origin of ["https://revu.lol.evil.example", "https://REVU.lol"]) {}',
    'namespace Revu.Sidecar; // Revu never answers a found match',
  ].join('\n');
  assert.equal(rewriteText(before, brand, sample), [
    "dialog.showErrorBox('Sample could not start');",
    "const profile = path.join(root, 'Revu/ElectronProfile');",
    `const folder = path.join(root, 'Revu'); // ${KEEP_MARKER} (data folder)`,
    'fetch("https://clips.sample.example/auth/web/me", { headers: { "X-Revu-CSRF": "1" } });',
    'assert.match(text, /Only enter this code on sample\\.example or in the Sample app\\./);',
    'for (const origin of ["https://sample.example.evil.example", "https://SAMPLE.example"]) {}',
    'namespace Revu.Sidecar; // Sample never answers a found match',
  ].join('\n'));
  assert.equal(rewriteText(rewriteText(before, brand, sample), sample, brand), before, 'and back again');
});

test('comments are told apart from code without mistaking globs and URLs for them', () => {
  const comments = (file, lines) => { const spans = commentScanner(file); return lines.map(line => spans(line).length > 0); };
  assert.deepEqual(comments('a.mjs', ['// Revu', "const a = 'Revu'; // Revu", "run('tests/*.test.mjs', 'https://revu.lol')",
    '/** Revu', ' * Revu', ' */', "const b = 'Revu';"]), [true, true, false, true, true, true, false]);
  assert.deepEqual(comments('a.css', ['/* Revu', 'still Revu', 'done */', '.a{color:red} /* Revu */', '.revu{}']),
    [true, true, true, true, false]);
  assert.deepEqual(comments('a.html', ['<!-- Revu', 'Revu -->', '<p>Revu</p>']), [true, true, false]);
  assert.deepEqual(comments('a.toml', ['# Revu', 'APP_NAME = "Revu"', 'A = "1" # Revu']), [true, false, true]);
  assert.deepEqual(comments('a.cs', ['/// <summary>Revu</summary>', 'var a = "Revu";']), [true, false]);
  const found = findOccurrences("const a = 'Revu'; // Revu\n", brand, 'a.mjs');
  assert.deepEqual(found.map(item => item.comment), [false, true]);
});

test('identifiers are listed apart from display text', () => {
  assert.deepEqual(findIdentity('Open Revu at revu.lol with revu-desktop.exe from %LOCALAPPDATA%\\Revu', brand),
    ['revu-desktop.exe', '%LOCALAPPDATA%\\Revu']);
});

test('path rules: ** crosses directories and * does not', () => {
  assert.ok(globPattern('site/**').test('site/tests/vod.test.mjs'));
  assert.ok(globPattern('**/*.md').test('README.md'));
  assert.ok(globPattern('**/*.md').test('proxy/README.md'));
  assert.ok(globPattern('desktop/ui/*.html').test('desktop/ui/index.html'));
  assert.ok(!globPattern('desktop/ui/*.html').test('desktop/ui/nested/index.html'));
  assert.ok(!globPattern('desktop/ui/*.html').test('desktop/ui/index.html.bak'));
});

test('brand values are validated before anything is written', () => {
  assert.throws(() => validateBrand({ name: 'Bad"Name', domain: 'a.example' }), /name/);
  assert.throws(() => validateBrand({ name: 'Fine', domain: 'https://a.example' }), /domain/);
  assert.throws(() => validateBrand({ name: 'Fine', domain: 'A.example' }), /domain/);
  assert.throws(() => validateBrand({ name: 'Fine', domain: 'a.example', formerDomains: ['a.example'] }), /formerDomains/);
  assert.deepEqual(validateBrand({ name: 'Two Words', domain: 'a.example' }), { name: 'Two Words', domain: 'a.example', formerDomains: [] });
});

test('Worker vars follow the brand, and a former domain stays an allowed origin', () => {
  assert.deepEqual(wranglerVars(brand), {
    ALLOWED_ORIGINS: 'https://revu.lol,https://www.revu.lol,http://localhost:8788,http://127.0.0.1:8788',
    MAGIC_LINK_FROM: 'login@revu.lol', APP_NAME: 'Revu', PUBLIC_BASE: 'https://clips.revu.lol', WATCH_BASE: 'https://revu.lol',
    AUTH_BASE: 'https://clips.revu.lol', CREDENTIALED_ORIGINS: 'https://revu.lol,https://www.revu.lol',
    TURNSTILE_HOSTNAMES: 'revu.lol,www.revu.lol',
  });
  const renamed = wranglerVars({ ...sample, formerDomains: ['revu.lol'] });
  assert.equal(renamed.CREDENTIALED_ORIGINS, 'https://sample.example,https://www.sample.example,https://revu.lol,https://www.revu.lol');
  assert.equal(renamed.TURNSTILE_HOSTNAMES, 'sample.example,www.sample.example,revu.lol,www.revu.lol');
  assert.equal(writeWranglerVars('# note\nAPP_NAME = "Old"\nOTHER = "x"\n', { APP_NAME: 'New' }), '# note\nAPP_NAME = "New"\nOTHER = "x"\n');
  assert.throws(() => writeWranglerVars('OTHER = "x"\n', { APP_NAME: 'New' }), /no APP_NAME var/);
});

test('the desktop UI keeps accepting share links on a former site host', async () => {
  const files = generatedFiles({ ...sample, formerDomains: ['revu.lol'] });
  assert.match(files['desktop/ui/brand.mjs'], /export const SITE_HOST = 'sample\.example';/);
  assert.match(files['desktop/ui/brand.mjs'], /export const FORMER_SITE_HOSTS = Object\.freeze\(\['revu\.lol'\]\);/);
  assert.match(files['src/Revu.Core/Constants/Brand.cs'], /namespace Revu\.Core\.Constants;[\s\S]*public const string Name = "Sample";/);
  assert.equal(derive(sample).sender, 'login@sample.example');
});

test('this repository has no unreviewed name or domain, and its brand files match brand.json', () => {
  const result = inspect();
  assert.deepEqual(result.problems, []);
  assert.deepEqual(result.unreviewed.map(item => `${item.file}:${item.line}: ${item.source.trim()}`), []);
});

test('the manual steps cover what a script cannot do, with the redirect last among the domain steps', () => {
  const steps = manualSteps(brand, { ...sample, formerDomains: ['revu.lol'] });
  const titles = steps.map(step => step.title).join('\n');
  for (const topic of [/Cloudflare/, /Email sender/, /Turnstile/, /Worker/, /Pages/, /Riot/, /Redirect revu\.lol/])
    assert.match(titles, topic);
  const index = pattern => steps.findIndex(step => pattern.test(step.title));
  assert.ok(index(/Ship the desktop app/) < index(/Redirect revu\.lol/));
  assert.ok(index(/Email sender/) < index(/Worker/));
  const nameOnly = manualSteps(brand, { ...brand, name: 'Sample' }).map(step => step.title).join('\n');
  assert.doesNotMatch(nameOnly, /Redirect|Turnstile|Email sender/);
});

// A throwaway repository shaped like this one: generated files, Worker vars, a static
// page, a code file with a kept line and a file under an identity path rule.
function scratchRepository() {
  const root = mkdtempSync(path.join(os.tmpdir(), 'rebrand-test-'));
  const write = (file, text) => { mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); writeFileSync(path.join(root, file), text); };
  write('brand.json', `${JSON.stringify(brand, null, 2)}\n`);
  for (const [file, text] of Object.entries(generatedFiles(brand))) write(file, text);
  write('proxy/wrangler.toml', `# Vars for revu.lol\n[vars]\n${Object.entries(wranglerVars(brand)).map(([key, value]) => `${key} = "${value}"`).join('\n')}\n`);
  write('scripts/rebrand.reviewed.json', JSON.stringify({
    paths: [{ glob: 'site/**', kind: 'display', why: 'static site' }, { glob: 'release.yml', kind: 'identity', why: 'by hand' }],
    lines: [{ file: 'package.json', has: '"productName": "Revu"', why: 'manifest' }],
  }));
  write('site/index.html', '<title>Revu</title>\n<a href="https://revu.lol/v/Abc1234">Watch on revu.lol</a>\n<script src="/revu.svg"></script>\n');
  write('package.json', '{\n  "name": "revu-desktop",\n  "productName": "Revu"\n}\n');
  write('host.mjs', `// Revu host\nconst folder = join(root, 'Revu'); // ${KEEP_MARKER}\n`);
  write('release.yml', '--packTitle "Revu"\n');
  const git = (...args) => execFileSync('git', args, { cwd: root, stdio: 'pipe' });
  git('init', '-q');
  git('add', '-A');
  git('-c', 'user.name=test', '-c', 'user.email=test@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '-q', '-m', 'fixture');
  return { root, read: file => readFileSync(path.join(root, file), 'utf8'), write, remove: () => rmSync(root, { recursive: true, force: true }) };
}

async function withScratch(run) {
  const scratch = scratchRepository();
  const original = process.cwd();
  try { await run(scratch); } finally { process.chdir(original); scratch.remove(); }
}

const repositoryAt = root => repository(root);

test('a rename rewrites the tree, keeps identity, records the old domain and leaves the check passing', () => withScratch(async scratch => {
  const repo = repositoryAt(scratch.root);
  const lines = [];
  assert.equal(main(['--check'], repo, line => lines.push(line)), 0, lines.join('\n'));
  lines.length = 0;
  assert.equal(main(['--to', 'Sample', '--domain', 'sample.example'], repo, line => lines.push(line)), 0, lines.join('\n'));
  const output = lines.join('\n');

  assert.deepEqual(JSON.parse(scratch.read('brand.json')), { name: 'Sample', domain: 'sample.example', formerDomains: ['revu.lol'] });
  assert.equal(scratch.read('site/index.html'),
    '<title>Sample</title>\n<a href="https://sample.example/v/Abc1234">Watch on sample.example</a>\n<script src="/revu.svg"></script>\n');
  assert.equal(scratch.read('package.json'), '{\n  "name": "revu-desktop",\n  "productName": "Sample"\n}\n');
  assert.equal(scratch.read('host.mjs'), `// Sample host\nconst folder = join(root, 'Revu'); // ${KEEP_MARKER}\n`);
  assert.equal(scratch.read('release.yml'), '--packTitle "Revu"\n', 'identity paths are not rewritten');
  assert.match(scratch.read('desktop/electron/brand.mjs'), /APP_NAME = 'Sample'/);
  assert.match(scratch.read('proxy/src/brand.ts'), /API_URL = "https:\/\/clips\.sample\.example"/);
  const toml = scratch.read('proxy/wrangler.toml');
  assert.match(toml, /^# Vars for sample\.example$/m);
  assert.match(toml, /^WATCH_BASE = "https:\/\/sample\.example"$/m);
  assert.match(toml, /^CREDENTIALED_ORIGINS = "https:\/\/sample\.example,https:\/\/www\.sample\.example,https:\/\/revu\.lol,https:\/\/www\.revu\.lol"$/m);
  assert.equal(JSON.parse(scratch.read('scripts/rebrand.reviewed.json')).lines[0].has, '"productName": "Sample"');

  assert.match(output, /Renamed "Revu" \(revu\.lol\) to "Sample" \(sample\.example\)/);
  assert.match(output, /release\.yml: 1 \(by hand\)/);
  assert.match(output, /host\.mjs: 1 \(marked "rebrand: keep"\)/);
  assert.match(output, /Redirect revu\.lol so every old link keeps working/);
  assert.match(output, /--check passes on the renamed tree/);
}));

test('the check fails on a new hard-coded name, a stale brand file and a stale reviewed line', () => withScratch(async scratch => {
  const repo = repositoryAt(scratch.root);
  scratch.write('app.mjs', "export const title = 'Revu';\nexport const link = 'https://revu.lol/x';\n// Revu in a comment is fine\n");
  scratch.write('desktop/electron/brand.mjs', "export const APP_NAME = 'Other';\n");
  scratch.write('package.json', '{ "name": "revu-desktop" }\n');
  execFileSync('git', ['add', '-A'], { cwd: scratch.root });
  const lines = [];
  assert.equal(main(['--check'], repo, line => lines.push(line)), 1);
  const output = lines.join('\n');
  assert.match(output, /2 occurrence\(s\) are not on the reviewed list/);
  assert.match(output, /app\.mjs:1: export const title = 'Revu';/);
  assert.match(output, /app\.mjs:2: export const link = 'https:\/\/revu\.lol\/x';/);
  assert.doesNotMatch(output, /app\.mjs:3/);
  assert.match(output, /desktop\/electron\/brand\.mjs does not match brand\.json/);
  assert.match(output, /no line in package\.json contains "\\"productName\\": \\"Revu\\"" any more/);
}));

test('a rename refuses a name it could not undo, and --sync repairs a brand file', () => withScratch(async scratch => {
  const repo = repositoryAt(scratch.root);
  assert.throws(() => main(['--to', 'Revu Pro', '--allow-dirty'], repo, () => {}), /contains the old one/);
  assert.throws(() => main(['--to', 'Revu', '--domain', 'revu.lol', '--allow-dirty'], repo, () => {}), /already the name/);
  assert.throws(() => main(['--to'], repo, () => {}), /needs a value/);
  scratch.write('desktop/ui/brand.mjs', 'stale\n');
  const lines = [];
  assert.equal(main(['--sync'], repo, line => lines.push(line)), 0);
  assert.match(lines.join('\n'), /Updated: desktop\/ui\/brand\.mjs/);
  assert.equal(scratch.read('desktop/ui/brand.mjs'), generatedFiles(brand)['desktop/ui/brand.mjs']);
}));
