import { baseName, fileUrl, folderFromHash, folderHash, parentOf } from './paths';

describe('fileUrl', () => {
  it.each([
    ['plain.txt', 'files/plain.txt'],
    ['a b/c d.txt', 'files/a%20b/c%20d.txt'],
    ['docs/[draft] plan #3?.md', 'files/docs/%5Bdraft%5D%20plan%20%233%3F.md'],
    ['docs/100% done & more+plus.txt', 'files/docs/100%25%20done%20%26%20more%2Bplus.txt'],
    [`docs/"quoted" 'name'.txt`, "files/docs/%22quoted%22%20'name'.txt"],
    ['photos/🎉 party.webp', 'files/photos/%F0%9F%8E%89%20party.webp'],
    ['photos/日本語/写真.png', 'files/photos/%E6%97%A5%E6%9C%AC%E8%AA%9E/%E5%86%99%E7%9C%9F.png'],
    ['über/café.jpg', 'files/%C3%BCber/caf%C3%A9.jpg'],
    ['q̇.txt', 'files/q%CC%87.txt'],
    ['a;b `c` $(d)=e,f@g.md', 'files/a%3Bb%20%60c%60%20%24(d)%3De%2Cf%40g.md'],
    ['back\\slash:colon.txt', 'files/back%5Cslash%3Acolon.txt'],
  ])('encodes %j segment by segment', (path, url) => {
    expect(fileUrl(path)).toBe(url);
  });

  it('keeps every name inside files/, whatever it holds', () => {
    const scheme = ['javascript', 'alert(1)'].join(':');
    for (const path of ['..%2F..%2Fsecret', '%2e%2e/x', 'a/%00b', scheme, '//evil.example/x']) {
      const url = fileUrl(path);
      expect(url.startsWith('files/')).toBe(true);
      expect(new URL(url, 'https://site.example/p/id/').href.startsWith('https://site.example/p/id/files/')).toBe(true);
    }
  });

  it('round-trips through the browser: each encoded segment decodes to the original name', () => {
    const path = 'docs/[draft] plan #3?.md';
    const resolved = new URL(fileUrl(path), 'https://site.example/p/id/');
    expect(resolved.pathname.split('/').slice(4).map(decodeURIComponent).join('/')).toBe(path);
    expect(resolved.hash).toBe('');
    expect(resolved.search).toBe('');
  });
});

describe('folder hashes', () => {
  it.each(['', 'docs', 'photos/über café', 'docs/[draft] #3?', 'a/%/b', '日本語/🎉'])('round-trips %j', (folder) => {
    expect(folderFromHash(folderHash(folder))).toBe(folder);
  });

  it('reads anything malformed as the root', () => {
    expect(folderFromHash('')).toBe('');
    expect(folderFromHash('#')).toBe('');
    expect(folderFromHash('#docs')).toBe('');
    expect(folderFromHash('#/%E0%A4%A')).toBe('');
  });

  it('ignores empty segments', () => {
    expect(folderFromHash('#//docs//sub/')).toBe('docs/sub');
  });
});

describe('path parts', () => {
  it('splits names and parents', () => {
    expect(baseName('a/b/c.txt')).toBe('c.txt');
    expect(baseName('c.txt')).toBe('c.txt');
    expect(parentOf('a/b/c.txt')).toBe('a/b');
    expect(parentOf('c.txt')).toBe('');
  });
});
