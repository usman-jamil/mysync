#!/usr/bin/env node
// Writes a large dev fixture to public/site-data.large.js (gitignored), to try the site with many
// entries: `nx serve mysync-site`, then open http://localhost:4200/?fixture=large.
//
//   node apps/mysync-site/tools/generate-fixture.mjs --files 100000

import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const at = args.indexOf('--files');
const count = at === -1 ? 100_000 : Number(args[at + 1]);
if (!Number.isInteger(count) || count < 1) {
  console.error('usage: generate-fixture.mjs --files <count>');
  process.exit(2);
}

const folders = ['src', 'docs', 'assets/images', 'assets/fonts', 'data/2025', 'data/2026', 'build/output', 'vendor/lib'];
const extensions = ['.txt', '.md', '.json', '.png', '.bin', '.tar.gz', '.html', '.csv'];
const inline = new Set(['.txt', '.md', '.log', '.csv', '.json', '.pdf', '.png', '.jpg', '.jpeg', '.gif', '.webp']);

let seed = 42;
const random = () => ((seed = (seed * 1103515245 + 12345) % 2 ** 31) / 2 ** 31);

const entries = [];
let bytes = 0;
for (let i = 0; i < count; i++) {
  const folder = `${folders[i % folders.length]}/group-${String(Math.floor(i / 500)).padStart(3, '0')}`;
  const extension = extensions[i % extensions.length];
  const path = `${folder}/file ${String(i).padStart(6, '0')}${extension}`;
  const size = Math.floor(random() ** 3 * 50_000_000);
  bytes += size;
  entries.push({
    kind: 'file',
    path,
    size,
    sha256: createHash('sha256').update(path).digest('hex'),
    mtimeUtc: new Date(Date.UTC(2026, 0, 1) + Math.floor(random() * 300 * 86_400_000)).toISOString().replace(/\.\d{3}Z$/, 'Z'),
    disposition: inline.has(extension) ? 'inline' : 'attachment',
  });
}
// Ordinal (UTF-16 code unit) order, as the contract requires.
entries.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));

const data = {
  schemaVersion: 1,
  publication: {
    id: 'LargeFixture0000000000',
    title: `Large fixture (${count.toLocaleString('en')} files)`,
    publishedUtc: '2026-11-18T07:30:00Z',
    revision: 1,
    generator: 'generate-fixture.mjs',
  },
  totals: { files: count, bytes },
  downloads: { sha256sums: 'SHA256SUMS.txt', shell: 'download.sh', powershell: 'download.ps1' },
  entries,
};

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'public', 'site-data.large.js');
writeFileSync(out, `window.__MYSYNC_SITE__ = ${JSON.stringify(data)};\n`);
console.log(`Wrote ${count.toLocaleString('en')} entries to ${out}`);
