import { fixture } from '../testing/fixture';
import { FolderIndex } from './folder-index';

describe('FolderIndex', () => {
  const index = new FolderIndex(fixture().entries);

  it('lists folders first, then files, in natural name order', () => {
    const rows = index.rows('');
    expect(rows.map((row) => `${row.kind}:${row.name}`)).toEqual([
      'folder:a b',
      'folder:archive',
      'folder:bin',
      'folder:data',
      'folder:docs',
      'folder:logs',
      'folder:notes',
      'folder:photos',
      'folder:site',
      'file:README.md',
    ]);
  });

  it('counts files and bytes under each folder, recursively', () => {
    const data = index.rows('').find((row) => row.name === 'data');
    expect(data).toMatchObject({ kind: 'folder', files: 3, bytes: 8053063680 + 1048576 });
    const docs = index.rows('').find((row) => row.name === 'docs');
    expect(docs).toMatchObject({ files: 6 });
  });

  it('keeps empty directories, nested ones included', () => {
    expect(index.rows('logs')).toEqual([]);
    expect(index.has('archive/2025/empty')).toBe(true);
    expect(index.rows('archive/2025').map((row) => row.name)).toEqual(['empty']);
    expect(index.rows('').find((row) => row.name === 'archive')).toMatchObject({ files: 0, bytes: 0 });
  });

  it('knows which folders exist', () => {
    expect(index.has('')).toBe(true);
    expect(index.has('docs/specs/v2/drafts/deep/nesting/is/fine')).toBe(true);
    expect(index.has('nope')).toBe(false);
    expect(index.has('README.md')).toBe(false);
  });

  it('searches whole paths, case-insensitively, folders before files', () => {
    expect(index.search('CAFÉ').map((row) => `${row.kind}:${row.path}`)).toEqual([
      'folder:photos/über café',
      'file:photos/über café/IMG_0001.jpg',
      'file:photos/über café/IMG_0002.jpg',
    ]);
    expect(index.search('[draft]').map((row) => row.path)).toEqual(['docs/[draft] plan #3?.md']);
    expect(index.search('   ')).toEqual([]);
    expect(index.search('no such thing')).toEqual([]);
  });

  it('stays quick with 100,000 entries', () => {
    const entries = Array.from({ length: 100_000 }, (_, i) => ({
      kind: 'file' as const,
      path: `group-${String(i % 100).padStart(3, '0')}/file-${String(i).padStart(6, '0')}.txt`,
      size: i,
      sha256: '0'.repeat(64),
      mtimeUtc: '2026-01-01T00:00:00Z',
      disposition: 'inline' as const,
    }));
    const started = performance.now();
    const large = new FolderIndex(entries);
    expect(large.rows('group-007')).toHaveLength(1000);
    expect(large.search('file-09999')).toHaveLength(10);
    expect(performance.now() - started).toBeLessThan(2000);
  });
});
