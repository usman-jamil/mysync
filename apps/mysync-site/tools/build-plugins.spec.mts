import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { BUILD_OUTPUT, checkBuildOutput, CONTENT_SECURITY_POLICY } from './build-plugins.mts';

const goodHtml = `<!doctype html><html><head>
<meta charset="utf-8" />
<meta http-equiv="Content-Security-Policy" content="${CONTENT_SECURITY_POLICY}" />
<meta name="referrer" content="no-referrer" />
<script src="./site-data.js"></script>
<script type="module" crossorigin src="./assets/app.js"></script>
<link rel="stylesheet" crossorigin href="./assets/app.css">
</head><body><div id="root"></div></body></html>`;

describe('the build output contract check', () => {
  let folder: string;

  beforeEach(() => {
    folder = mkdtempSync(join(tmpdir(), 'mysync-site-'));
    write('index.html', goodHtml);
    write('assets/app.js', 'console.log(1)');
    write('assets/app.css', 'body{}');
    write('assets/THIRD-PARTY-NOTICES.txt', 'react');
  });

  afterEach(() => rmSync(folder, { recursive: true, force: true }));

  function write(name: string, text: string) {
    mkdirSync(dirname(join(folder, name)), { recursive: true });
    writeFileSync(join(folder, name), text);
  }

  it('accepts exactly the four files', () => {
    expect(BUILD_OUTPUT).toHaveLength(4);
    expect(checkBuildOutput(folder)).toEqual([]);
  });

  it.each([
    ['an extra chunk', () => write('assets/chunk-1.js', 'x'), 'unexpected file assets/chunk-1.js'],
    ['a dev fixture', () => write('site-data.js', 'x'), 'unexpected file site-data.js'],
    ['a source map', () => write('assets/app.js.map', '{}'), 'unexpected file assets/app.js.map'],
    ['a missing notices file', () => rmSync(join(folder, 'assets/THIRD-PARTY-NOTICES.txt')), 'missing assets/THIRD-PARTY-NOTICES.txt'],
    ['an inline script', () => write('index.html', goodHtml.replace('</body>', '<script>alert(1)</script></body>')), 'inline script'],
    ['inline styles', () => write('index.html', goodHtml.replace('<div id="root">', '<div id="root" style="x">')), 'inline styles'],
    ['no CSP', () => write('index.html', goodHtml.replace(/<meta http-equiv[^>]*>/, '')), 'Content-Security-Policy'],
    ['no site-data.js', () => write('index.html', goodHtml.replace('<script src="./site-data.js"></script>', '')), 'site-data.js'],
    ['a dynamic import', () => write('assets/app.js', 'import("./x.js")'), 'dynamic import'],
    ['a source map comment', () => write('assets/app.js', '//# sourceMappingURL=app.js.map'), 'source map'],
  ])('rejects %s', (_name, change, problem) => {
    change();
    expect(checkBuildOutput(folder).join('\n')).toContain(problem);
  });
});
