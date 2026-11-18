import type { Entry, FileEntry } from '../contract/site-data';
import { baseName, parentOf } from './paths';

export interface FolderRow {
  kind: 'folder';
  path: string;
  name: string;
  files: number;
  bytes: number;
}

export interface FileRow {
  kind: 'file';
  path: string;
  name: string;
  entry: FileEntry;
}

export type Row = FolderRow | FileRow;

interface Folder {
  folders: Set<string>;
  files: FileEntry[];
  totalFiles: number;
  totalBytes: number;
}

/** Folders and files by folder, built once per publication (the list can hold 100,000 entries). */
export class FolderIndex {
  private readonly folders = new Map<string, Folder>();
  private readonly collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });
  readonly files: FileEntry[] = [];
  private readonly lowered: string[] = [];

  constructor(entries: readonly Entry[]) {
    this.folder('');
    for (const entry of entries) {
      if (entry.kind === 'dir') {
        this.ensurePath(entry.path);
      } else {
        this.files.push(entry);
        this.lowered.push(entry.path.toLocaleLowerCase());
        const parent = parentOf(entry.path);
        this.ensurePath(parent).files.push(entry);
        for (let folder: string | null = parent; folder !== null; folder = folder === '' ? null : parentOf(folder)) {
          const counted = this.folder(folder);
          counted.totalFiles += 1;
          counted.totalBytes += entry.size;
        }
      }
    }
  }

  has(folder: string): boolean {
    return this.folders.has(folder);
  }

  /** A folder's contents: folders first, then files, each in natural name order. */
  rows(folder: string): Row[] {
    const found = this.folders.get(folder);
    if (!found) return [];

    const folders: FolderRow[] = [...found.folders]
      .map((path) => {
        const child = this.folders.get(path) as Folder;
        return { kind: 'folder' as const, path, name: baseName(path), files: child.totalFiles, bytes: child.totalBytes };
      })
      .sort((a, b) => this.collator.compare(a.name, b.name));
    const files: FileRow[] = found.files
      .map((entry) => ({ kind: 'file' as const, path: entry.path, name: baseName(entry.path), entry }))
      .sort((a, b) => this.collator.compare(a.name, b.name));
    return [...folders, ...files];
  }

  /**
   * Every folder, then every file, whose full path contains the query (case-insensitive). Files keep
   * the publication's path order, so 100,000 of them need no sort.
   */
  search(query: string): Row[] {
    const needle = query.trim().toLocaleLowerCase();
    if (needle === '') return [];

    const folders: FolderRow[] = [];
    for (const [path, folder] of this.folders) {
      if (path !== '' && path.toLocaleLowerCase().includes(needle)) {
        folders.push({ kind: 'folder', path, name: baseName(path), files: folder.totalFiles, bytes: folder.totalBytes });
      }
    }
    folders.sort((a, b) => this.collator.compare(a.path, b.path));

    const rows: Row[] = folders;
    for (let i = 0; i < this.files.length; i++) {
      if (this.lowered[i].includes(needle)) {
        const entry = this.files[i];
        rows.push({ kind: 'file', path: entry.path, name: baseName(entry.path), entry });
      }
    }
    return rows;
  }

  private ensurePath(path: string): Folder {
    let current = '';
    let folder = this.folder('');
    if (path === '') return folder;
    for (const segment of path.split('/')) {
      const next = current === '' ? segment : `${current}/${segment}`;
      folder.folders.add(next);
      folder = this.folder(next);
      current = next;
    }
    return folder;
  }

  private folder(path: string): Folder {
    let folder = this.folders.get(path);
    if (!folder) {
      folder = { folders: new Set(), files: [], totalFiles: 0, totalBytes: 0 };
      this.folders.set(path, folder);
    }
    return folder;
  }
}
