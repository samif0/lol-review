// Entry point for `node --test site/tests`. Node 22 resolves a directory
// argument as a module rather than expanding it into test files, so this file
// loads every *.test.mjs suite beside it.
const { readdirSync } = require('node:fs');
const { join } = require('node:path');
const { pathToFileURL } = require('node:url');

for (const name of readdirSync(__dirname).filter(file => file.endsWith('.test.mjs')).sort()) {
  import(pathToFileURL(join(__dirname, name)).href);
}
