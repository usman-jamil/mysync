/// <reference types='vitest' />
import { resolve } from 'node:path';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { buildOutputContract, contentSecurityPolicy, thirdPartyNotices } from './tools/build-plugins.mts';

const outDir = resolve(import.meta.dirname, '../../dist/apps/mysync-site');

// The build output is embedded in the mysync binary, which depends on its exact shape:
// index.html, assets/app.js, assets/app.css, assets/THIRD-PARTY-NOTICES.txt (README.md).
export default defineConfig(() => ({
  root: import.meta.dirname,
  cacheDir: '../../node_modules/.vite/apps/mysync-site',
  // Relative URLs: every publication is served from its own /p/<publishId>/.
  base: './',
  server: { port: 4200, host: 'localhost' },
  preview: { port: 4200, host: 'localhost' },
  plugins: [react(), contentSecurityPolicy(), thirdPartyNotices(), buildOutputContract(outDir)],
  build: {
    outDir,
    emptyOutDir: true,
    // public/ holds dev fixtures only; they must never reach a publication.
    copyPublicDir: false,
    sourcemap: false,
    modulePreload: false,
    cssCodeSplit: false,
    // Nothing is emitted as a separate image or font: icons are inline SVG components.
    assetsInlineLimit: Number.MAX_SAFE_INTEGER,
    reportCompressedSize: false,
    rolldownOptions: {
      output: {
        // Fixed names: each publication gets its own copy, so hashes buy nothing, and the CLI must know them.
        entryFileNames: 'assets/app.js',
        chunkFileNames: 'assets/app.js',
        assetFileNames: (asset: { names?: string[] }) =>
          (asset.names ?? []).some((name) => name.endsWith('.css')) ? 'assets/app.css' : 'assets/[name][extname]',
        codeSplitting: false,
      },
    },
  },
  define: {
    'import.meta.vitest': undefined,
  },
  test: {
    name: 'mysync-site',
    watch: false,
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
    include: ['{src,tests,tools}/**/*.{test,spec}.{ts,mts,tsx}'],
    reporters: ['default'],
    coverage: {
      reportsDirectory: '../../coverage/apps/mysync-site',
      provider: 'v8' as const,
    },
  },
}));
