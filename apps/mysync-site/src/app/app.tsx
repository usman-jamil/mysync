import { useEffect, useMemo, useState } from 'react';
import { loadSiteData, type LoadResult, type SiteData } from '../contract/site-data';
import { FolderIndex } from '../lib/folder-index';
import { formatBytes, formatCount, formatDateTime } from '../lib/format';
import { Breadcrumbs } from './breadcrumbs';
import { DownloadPanel, publicationBaseUrl } from './download-panel';
import { FileList } from './file-list';
import { useDebounced, useHashFolder } from './hooks';
import { AlertIcon, SearchIcon } from './icons';
import { ThemeSwitcher } from './theme';

export function App({ data }: { data: unknown }) {
  const loaded: LoadResult = useMemo(() => loadSiteData(data), [data]);
  return loaded.ok ? <Publication data={loaded.data} /> : <ErrorState reason={loaded.reason} detail={loaded.detail} />;
}

function Publication({ data }: { data: SiteData }) {
  const { publication, totals } = data;
  const index = useMemo(() => new FolderIndex(data.entries), [data.entries]);
  const hashFolder = useHashFolder();
  const folder = index.has(hashFolder) ? hashFolder : '';
  const [query, setQuery] = useState('');
  const search = useDebounced(query, 150).trim();

  const rows = useMemo(() => (search === '' ? index.rows(folder) : index.search(search)), [index, folder, search]);
  const baseUrl = useMemo(() => publicationBaseUrl(window.location), []);

  useEffect(() => {
    document.title = publication.title;
  }, [publication.title]);

  return (
    <div className="page">
      <header className="site-header">
        <div className="header-inner">
          <div className="header-top">
            <h1>{publication.title}</h1>
            <ThemeSwitcher />
          </div>
          <p className="header-meta">
            <span>
              Published <time dateTime={publication.publishedUtc}>{formatDateTime(publication.publishedUtc)}</time>
            </span>
            <span aria-hidden="true">·</span>
            <span>{formatCount(totals.files, 'file', 'files')}</span>
            <span aria-hidden="true">·</span>
            <span>{formatBytes(totals.bytes)}</span>
          </p>
        </div>
      </header>

      <main className="content">
        <DownloadPanel downloads={data.downloads} baseUrl={baseUrl} />

        <div className="toolbar">
          {search === '' ? (
            <Breadcrumbs folder={folder} rootLabel="All files" />
          ) : (
            <p className="search-summary" role="status">
              {formatCount(rows.length, 'match', 'matches')} for “{search}”
            </p>
          )}
          <label className="search">
            <SearchIcon />
            <span className="visually-hidden">Search all files</span>
            <input
              type="search"
              placeholder="Search all files"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              autoComplete="off"
              spellCheck={false}
            />
          </label>
        </div>

        <FileList
          key={search === '' ? `folder:${folder}` : 'search'}
          rows={rows}
          showFolders={search !== ''}
          emptyText={search === '' ? 'This folder is empty.' : 'No file or folder matches your search.'}
        />
      </main>

      <footer className="site-footer">
        <span>Revision {publication.revision}</span>
        <span aria-hidden="true">·</span>
        <span>{publication.generator}</span>
      </footer>
    </div>
  );
}

const errorTitles: Record<Exclude<LoadResult, { ok: true }>['reason'], string> = {
  missing: 'This publication could not be loaded',
  unsupported: 'This publication needs a newer page',
  invalid: 'This publication is damaged',
};

function ErrorState({ reason, detail }: { reason: keyof typeof errorTitles; detail: string }) {
  return (
    <main className="error-state" role="alert">
      <ThemeSwitcher />
      <AlertIcon className="error-icon" />
      <h1>{errorTitles[reason]}</h1>
      <p>{detail}</p>
      <p className="muted">If it was just published, try again in a minute. Otherwise, ask whoever shared the link.</p>
    </main>
  );
}

export default App;
