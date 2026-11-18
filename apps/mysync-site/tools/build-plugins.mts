// Build-time plugins for mysync-site. The CLI embeds the build output and depends on its exact
// shape (README.md, "Build output contract"); these keep it that way.

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import type { Plugin } from 'vite';

/** Exactly these files, nothing else: the CLI's static index.html and embedding rely on it. */
export const BUILD_OUTPUT = ['assets/THIRD-PARTY-NOTICES.txt', 'assets/app.css', 'assets/app.js', 'index.html'] as const;

export const CONTENT_SECURITY_POLICY = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self'",
  "img-src 'self' data:",
  "connect-src 'none'",
  "font-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
].join('; ');

/**
 * The static website can't send response headers, so the policy is a meta tag. Build only: the
 * dev server's hot reload uses inline scripts the policy would block.
 */
export function contentSecurityPolicy(): Plugin {
  return {
    name: 'mysync-site:csp',
    apply: 'build',
    transformIndexHtml: {
      order: 'post',
      handler: (html) =>
        html.replace(
          '<meta charset="utf-8" />',
          `<meta charset="utf-8" />\n    <meta http-equiv="Content-Security-Policy" content="${CONTENT_SECURITY_POLICY}" />`,
        ),
    },
  };
}

interface BundledPackage {
  name: string;
  version: string;
  license: string;
  text: string;
}

/**
 * assets/THIRD-PARTY-NOTICES.txt: the license of every npm package bundled into app.js, since
 * every publication redistributes it. Fails the build if a bundled package has no license.
 */
export function thirdPartyNotices(): Plugin {
  return {
    name: 'mysync-site:third-party-notices',
    apply: 'build',
    generateBundle(_options, bundle) {
      const roots = new Set<string>();
      for (const output of Object.values(bundle)) {
        if (output.type !== 'chunk') continue;
        for (const id of output.moduleIds) {
          const root = packageRoot(id);
          if (root) roots.add(root);
        }
      }

      const packages = [...roots].map(readPackage).sort((a, b) => a.name.localeCompare(b.name));
      if (packages.length === 0) {
        this.error('No bundled npm packages were found; the notices would be empty.');
      }

      const body = packages
        .map((p) => `${p.name}@${p.version} (${p.license})\n${'-'.repeat(72)}\n${p.text.trim()}\n`)
        .join('\n\n');
      this.emitFile({
        type: 'asset',
        fileName: 'assets/THIRD-PARTY-NOTICES.txt',
        source: `Third-party software bundled in this page (assets/app.js)\n\n${body}`,
      });
    },
  };
}

/** Fails the build if the output isn't exactly BUILD_OUTPUT, or index.html breaks the contract. */
export function buildOutputContract(outDir: string): Plugin {
  return {
    name: 'mysync-site:build-output-contract',
    apply: 'build',
    closeBundle() {
      const problems = checkBuildOutput(outDir);
      if (problems.length > 0) {
        throw new Error(`mysync-site build output breaks its contract:\n  - ${problems.join('\n  - ')}`);
      }
    },
  };
}

/** Every way the folder differs from the contract; empty when it matches. */
export function checkBuildOutput(outDir: string): string[] {
  if (!existsSync(outDir)) return [`${outDir} does not exist`];

  const problems: string[] = [];
  const files = listFiles(outDir);
  const expected = new Set<string>(BUILD_OUTPUT);
  for (const file of files) {
    if (!expected.has(file)) problems.push(`unexpected file ${file}`);
  }
  for (const file of BUILD_OUTPUT) {
    if (!files.includes(file)) problems.push(`missing ${file}`);
  }

  if (files.includes('index.html')) {
    const html = readFileSync(join(outDir, 'index.html'), 'utf8');
    const required = [
      '<script src="./site-data.js"></script>',
      'src="./assets/app.js"',
      'href="./assets/app.css"',
      `<meta http-equiv="Content-Security-Policy" content="${CONTENT_SECURITY_POLICY}" />`,
      '<meta name="referrer" content="no-referrer" />',
    ];
    for (const text of required) {
      if (!html.includes(text)) problems.push(`index.html lacks ${text}`);
    }
    for (const script of html.match(/<script\b[^>]*>/g) ?? []) {
      if (!/\bsrc=/.test(script)) problems.push(`index.html has an inline script: ${script}`);
    }
    if (/<style\b/i.test(html) || /\sstyle=/i.test(html)) problems.push('index.html has inline styles');
  }

  if (files.includes('assets/app.js')) {
    const js = readFileSync(join(outDir, 'assets/app.js'), 'utf8');
    if (/sourceMappingURL/.test(js)) problems.push('assets/app.js references a source map');
    if (/\bimport\s*\(/.test(js)) problems.push('assets/app.js has a dynamic import (a second chunk)');
  }

  return problems;
}

function listFiles(folder: string): string[] {
  const found: string[] = [];
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const full = join(dir, name);
      if (statSync(full).isDirectory()) walk(full);
      else found.push(relative(folder, full).split(sep).join('/'));
    }
  };
  walk(folder);
  return found.sort();
}

function packageRoot(id: string): string | null {
  const path = id.replace(/\\/g, '/').replace(/\?.*$/, '');
  const at = path.lastIndexOf('/node_modules/');
  if (at === -1 || path.startsWith('\0')) return null;
  const rest = path.slice(at + '/node_modules/'.length).split('/');
  const name = rest[0].startsWith('@') ? `${rest[0]}/${rest[1]}` : rest[0];
  return path.slice(0, at + '/node_modules/'.length) + name;
}

function readPackage(root: string): BundledPackage {
  const manifest = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')) as {
    name: string;
    version: string;
    license?: string;
  };
  const licenseFile = readdirSync(root).find((name) => /^(licen[cs]e|copying)(\.|$)/i.test(name));
  if (!manifest.license && !licenseFile) {
    throw new Error(`${manifest.name}@${manifest.version} has no license; it can't be bundled into a publication.`);
  }
  return {
    name: manifest.name,
    version: manifest.version,
    license: manifest.license ?? 'see below',
    text: licenseFile ? readFileSync(join(root, licenseFile), 'utf8') : `License: ${manifest.license}`,
  };
}
