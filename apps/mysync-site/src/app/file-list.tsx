import { useWindowVirtualizer } from '@tanstack/react-virtual';
import { useLayoutEffect, useRef, useState } from 'react';
import type { Row } from '../lib/folder-index';
import { formatBytes, formatCount, formatDateTime } from '../lib/format';
import { fileUrl, folderHash, parentOf } from '../lib/paths';
import { CopyButton } from './copy-button';
import { DownloadIcon, FileIcon, FolderIcon } from './icons';

const ROW_HEIGHT = 56;

interface FileListProps {
  rows: Row[];
  /** Search results show each row's folder under its name. */
  showFolders: boolean;
  emptyText: string;
}

/** The folder or search listing, virtualized: only the rows on screen are in the DOM. */
export function FileList({ rows, showFolders, emptyText }: FileListProps) {
  const listRef = useRef<HTMLDivElement>(null);
  const [scrollMargin, setScrollMargin] = useState(0);

  useLayoutEffect(() => {
    setScrollMargin(listRef.current?.offsetTop ?? 0);
  }, []);

  const virtualizer = useWindowVirtualizer({
    count: rows.length,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12,
    scrollMargin,
  });

  if (rows.length === 0) {
    return <p className="empty">{emptyText}</p>;
  }

  return (
    <div className="file-list" role="table" aria-label="Files" aria-rowcount={rows.length}>
      <div className="file-row file-head" role="row">
        <span role="columnheader" className="col-name">Name</span>
        <span role="columnheader" className="col-size">Size</span>
        <span role="columnheader" className="col-time">Modified</span>
        <span role="columnheader" className="col-hash">SHA-256</span>
        <span role="columnheader" className="col-action"><span className="visually-hidden">Download</span></span>
      </div>
      <div ref={listRef} className="file-rows" style={{ height: virtualizer.getTotalSize() }} role="rowgroup">
        {virtualizer.getVirtualItems().map((item) => {
          const row = rows[item.index];
          return (
            <div
              key={row.kind + row.path}
              className="file-row"
              role="row"
              aria-rowindex={item.index + 1}
              style={{ height: ROW_HEIGHT, transform: `translateY(${item.start - virtualizer.options.scrollMargin}px)` }}
            >
              {row.kind === 'folder' ? <FolderCells row={row} showFolder={showFolders} /> : <FileCells row={row} showFolder={showFolders} />}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function Location({ path, show }: { path: string; show: boolean }) {
  if (!show) return null;
  const folder = parentOf(path);
  return <span className="row-location">{folder === '' ? '/' : folder}</span>;
}

function FolderCells({ row, showFolder }: { row: Extract<Row, { kind: 'folder' }>; showFolder: boolean }) {
  const summary = row.files === 0 ? 'Empty' : formatCount(row.files, 'file', 'files');
  return (
    <>
      <span role="cell" className="col-name">
        <FolderIcon className="row-icon folder" />
        <span className="row-text">
          <a className="row-name" href={folderHash(row.path)}>{row.name}</a>
          <Location path={row.path} show={showFolder} />
          <span className="row-meta-mobile">{summary}{row.files > 0 ? ` · ${formatBytes(row.bytes)}` : ''}</span>
        </span>
      </span>
      <span role="cell" className="col-size">{row.files > 0 ? formatBytes(row.bytes) : '—'}</span>
      <span role="cell" className="col-time muted">{summary}</span>
      <span role="cell" className="col-hash" />
      <span role="cell" className="col-action" />
    </>
  );
}

function FileCells({ row, showFolder }: { row: Extract<Row, { kind: 'file' }>; showFolder: boolean }) {
  const { entry } = row;
  const url = fileUrl(entry.path);
  return (
    <>
      <span role="cell" className="col-name">
        <FileIcon className="row-icon" />
        <span className="row-text">
          {entry.disposition === 'inline' ? (
            <a className="row-name" href={url} target="_blank" rel="noopener noreferrer">{row.name}</a>
          ) : (
            <a className="row-name" href={url} download={row.name}>{row.name}</a>
          )}
          <Location path={row.path} show={showFolder} />
          <span className="row-meta-mobile">{formatBytes(entry.size)} · {formatDateTime(entry.mtimeUtc)}</span>
        </span>
      </span>
      <span role="cell" className="col-size">{formatBytes(entry.size)}</span>
      <span role="cell" className="col-time muted">
        <time dateTime={entry.mtimeUtc}>{formatDateTime(entry.mtimeUtc)}</time>
      </span>
      <span role="cell" className="col-hash">
        <code title={entry.sha256}>{entry.sha256.slice(0, 10)}</code>
        <CopyButton text={entry.sha256} label={`Copy the SHA-256 of ${row.name}`} className="small" />
      </span>
      <span role="cell" className="col-action">
        <a className="icon-button" href={url} download={row.name} aria-label={`Download ${row.name}`} title="Download">
          <DownloadIcon />
        </a>
      </span>
    </>
  );
}
