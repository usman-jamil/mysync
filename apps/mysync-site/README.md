# mysync-site

The page `mysync publish` puts in front of a published share: a public, read-only, static site
where anyone with the link can browse the files and download them, one at a time or all at once.

It's a React 19 + TypeScript app built with Vite. The production build is embedded in the mysync
binary, which uploads it next to each publication's files. The page has no server: everything it
shows comes from `site-data.js`, which `mysync publish` writes for each publication.

```text
p/<publishId>/
├── index.html          ← this app's build
├── assets/app.js       ←
├── assets/app.css      ←
├── assets/THIRD-PARTY-NOTICES.txt
├── site-data.js        ← written per publication: window.__MYSYNC_SITE__ = {...};
├── SHA256SUMS.txt, download.sh, download.ps1
└── files/<path>        ← the published files
```

## Build output contract

The CLI embeds the build and depends on its exact shape. `nx build mysync-site` writes
`dist/apps/mysync-site/` with **exactly** these files, and the build fails otherwise
(`tools/build-plugins.mts`, `checkBuildOutput`):

| File | Notes |
| --- | --- |
| `index.html` | Loads `./site-data.js` (a classic script, so it runs first), then `./assets/app.js` and `./assets/app.css`. Carries the Content-Security-Policy meta tag. |
| `assets/app.js` | The whole app in one ES module: no code splitting, no source map, fixed name. |
| `assets/app.css` | All styles, fixed name. |
| `assets/THIRD-PARTY-NOTICES.txt` | The license of every bundled npm package. The build fails if one has none. |

- **Relative URLs** (`base: './'`): every publication is served from its own `/p/<publishId>/`.
- **Unhashed names:** each publication has its own copy, so cache-busting buys nothing, and the CLI must know the names.
- **No other assets:** icons are inline SVG components; there are no images or fonts, and `public/` (dev fixtures) is never copied.
- **Content-Security-Policy**, as a meta tag because the static website can't send headers:
  `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'none'; font-src 'none'; base-uri 'none'; form-action 'none'`.
  It's added by the build only: the dev server's hot reload needs inline scripts. The output has no inline scripts or styles (checked).
- `<meta name="referrer" content="no-referrer">` keeps the secret publication URL out of the `Referer` header.

## Data contract: `window.__MYSYNC_SITE__`

The single source of truth is [`contract/site-data.schema.json`](contract/site-data.schema.json)
(JSON Schema draft 2020-12). The TypeScript types (`src/contract/site-data.ts`) and the C# side
are both tested against it.

```jsonc
{
  "schemaVersion": 1,
  "publication": { "id": "4fQ9xK2mT7bV1nR8sL3pZa", "title": "Q3 reports", "publishedUtc": "2026-11-18T07:30:00Z",
                   "publishedBy": "usman@DEV-LAPTOP", "revision": 18, "generator": "mysync 0.5.0" },
  "totals": { "files": 142, "bytes": 40050611 },
  "downloads": { "sha256sums": "SHA256SUMS.txt", "shell": "download.sh", "powershell": "download.ps1" },
  "entries": [
    { "kind": "file", "path": "docs/readme.md", "size": 1204, "sha256": "9f86d0…", "mtimeUtc": "2026-11-15T18:31:02Z", "disposition": "inline" },
    { "kind": "dir", "path": "logs" }
  ]
}
```

- `entries` is sorted ordinally by `path`; paths are the share manifest's (forward slashes, NFC). `dir` entries exist only for empty directories.
- A file's URL is `files/` plus each path segment through `encodeURIComponent` (`src/lib/paths.ts`, `fileUrl`).
- `disposition` is decided by mysync: `inline` files open in a new tab, `attachment` files download. The page never opens HTML, SVG, XML or script files inline.
- A missing `site-data.js`, an unknown `schemaVersion` or broken data shows an explanation, never a blank page.

## What the page does

- Header with the title, the publication date in the viewer's local time, and the totals.
- Folder navigation in the URL hash (`#/docs/sub`): the static website would answer 404 for a real deep path. Breadcrumbs lead back up; an unknown folder shows the root.
- Search across every path (debounced), with a match count.
- Files: name, size, modified time, short SHA-256 with a copy button, and a download link.
- "Download everything": `curl … | sh` and `irm … | iex` one-liners for this URL, and a link to `SHA256SUMS.txt`.
- Light and dark themes: follows `prefers-color-scheme` until the viewer picks System, Light or Dark in the header (remembered in `localStorage` when the browser allows it, and applied before the first render); keyboard-navigable; narrow screens move size and date under the name.
- Fast with 100,000 entries: the folder index is built once, and the list is virtualized (`@tanstack/react-virtual`), so only the visible rows are in the DOM.
- No requests of its own (no CDN, fonts, analytics or error reporting), and every name is rendered as text: `dangerouslySetInnerHTML` is banned by lint.

Runtime dependencies: `react`, `react-dom` and `@tanstack/react-virtual`. Versions live in the root `package.json`.

## Working on it

```bash
npm run site                  # the mocked share in your browser (same as nx serve mysync-site --open)
npm run site:large            # 100,000 mocked files: writes the large fixture, opens /?fixture=large

nx serve mysync-site          # http://localhost:4200, with public/site-data.js (every awkward case)
node apps/mysync-site/tools/generate-fixture.mjs --files 100000
                              # then http://localhost:4200/?fixture=large (dev only; the file is gitignored)

nx lint mysync-site           # ESLint, no warnings allowed
nx typecheck mysync-site      # tsc -b: the app, the tests and the build config
nx test mysync-site           # Vitest + Testing Library
nx build mysync-site          # dist/apps/mysync-site, checked against the contract above
```

The fixtures are data only: no files exist behind them, so a file or download link answers 404 in
dev. `public/site-data.js` is the committed fixture: Unicode and combining characters, spaces, `#`,
`?`, `%`, `&`, `+`, `[`, quotes, shell metacharacters, deep nesting, empty folders, a zero-byte file,
a multi-GB file, and both dispositions. A test validates it against the schema; keep it valid.

To see the production build as a publication, put the build and a `site-data.js` in a folder like
`p/<id>/` and serve it over HTTP (`python3 -m http.server`); opening `index.html` from disk won't
run the module script.
