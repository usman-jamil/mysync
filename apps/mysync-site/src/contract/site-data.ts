// The shape of window.__MYSYNC_SITE__, as contract/site-data.schema.json defines it.
// site-data.schema.spec.ts checks the fixture against the schema; keep these types in step with it.

export const SUPPORTED_SCHEMA_VERSION = 1;

export interface SiteData {
  schemaVersion: 1;
  publication: Publication;
  totals: { files: number; bytes: number };
  downloads: { sha256sums: string; shell: string; powershell: string };
  entries: Entry[];
}

export interface Publication {
  id: string;
  title: string;
  publishedUtc: string;
  revision: number;
  generator: string;
}

export type Disposition = 'inline' | 'attachment';

export interface FileEntry {
  kind: 'file';
  path: string;
  size: number;
  sha256: string;
  mtimeUtc: string;
  disposition: Disposition;
}

export interface DirEntry {
  kind: 'dir';
  path: string;
}

export type Entry = FileEntry | DirEntry;

declare global {
  interface Window {
    __MYSYNC_SITE__?: unknown;
  }
}

export type LoadResult =
  | { ok: true; data: SiteData }
  | { ok: false; reason: 'missing' | 'unsupported' | 'invalid'; detail: string };

/**
 * Checks what site-data.js left on window enough to render it safely. The full rules are the
 * schema's; this only guards the app against a missing, newer or broken file.
 */
export function loadSiteData(value: unknown): LoadResult {
  if (value === undefined || value === null) {
    return { ok: false, reason: 'missing', detail: 'site-data.js did not load.' };
  }
  if (typeof value !== 'object') {
    return { ok: false, reason: 'invalid', detail: 'The publication data is not an object.' };
  }

  const data = value as Partial<SiteData> & { schemaVersion?: unknown };
  if (data.schemaVersion !== SUPPORTED_SCHEMA_VERSION) {
    return {
      ok: false,
      reason: 'unsupported',
      detail: `This page understands publication format ${SUPPORTED_SCHEMA_VERSION}, but the data is format ${String(data.schemaVersion)}.`,
    };
  }

  const publication = data.publication;
  if (
    !publication ||
    typeof publication.title !== 'string' ||
    typeof publication.publishedUtc !== 'string' ||
    !data.totals ||
    !data.downloads ||
    !Array.isArray(data.entries)
  ) {
    return { ok: false, reason: 'invalid', detail: 'The publication data is incomplete.' };
  }

  for (const entry of data.entries as unknown[]) {
    if (!isEntry(entry)) {
      return { ok: false, reason: 'invalid', detail: 'The publication data contains an unreadable entry.' };
    }
  }

  return { ok: true, data: data as SiteData };
}

function isEntry(value: unknown): value is Entry {
  if (typeof value !== 'object' || value === null) return false;
  const entry = value as Record<string, unknown>;
  if (typeof entry['path'] !== 'string' || entry['path'] === '') return false;
  if (entry['kind'] === 'dir') return true;
  return (
    entry['kind'] === 'file' &&
    typeof entry['size'] === 'number' &&
    typeof entry['sha256'] === 'string' &&
    typeof entry['mtimeUtc'] === 'string' &&
    (entry['disposition'] === 'inline' || entry['disposition'] === 'attachment')
  );
}
