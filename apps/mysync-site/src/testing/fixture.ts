import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import type { SiteData } from '../contract/site-data';

/** Runs a site-data.js file the way the browser does, and returns what it assigned to window. */
export function runSiteDataScript(source: string): unknown {
  const window: { __MYSYNC_SITE__?: unknown } = {};
  runInNewContext(source, { window });
  return window.__MYSYNC_SITE__;
}

/** public/site-data.js, the committed dev fixture. */
export function fixture(): SiteData {
  return runSiteDataScript(readFileSync(resolve(__dirname, '../../public/site-data.js'), 'utf8')) as SiteData;
}
