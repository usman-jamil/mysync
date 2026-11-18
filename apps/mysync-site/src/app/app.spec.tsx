import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { fixture } from '../testing/fixture';
import { publicationBaseUrl } from './download-panel';
import App from './app';

function navigate(hash: string) {
  act(() => {
    window.location.hash = hash;
    window.dispatchEvent(new HashChangeEvent('hashchange'));
  });
}

function rowNames() {
  return screen.getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[0].querySelector('.row-name')?.textContent);
}

describe('the publication page', () => {
  it('shows the title, date, totals and the root folder', () => {
    render(<App data={fixture()} />);

    expect(screen.getByRole('heading', { level: 1, name: 'Q3 reports' })).toBeTruthy();
    const header = within(screen.getByRole('banner'));
    expect(header.getByText('20 files')).toBeTruthy();
    expect(header.getByText('7.5 GB')).toBeTruthy();
    expect(document.querySelector('header time')?.getAttribute('datetime')).toBe('2026-11-18T07:30:00Z');
    expect(rowNames()).toEqual(['a b', 'archive', 'bin', 'data', 'docs', 'logs', 'notes', 'photos', 'site', 'README.md']);
    expect(document.title).toBe('Q3 reports');
  });

  it('opens folders from the hash, with breadcrumbs back up', () => {
    render(<App data={fixture()} />);

    navigate('#/photos/%C3%BCber%20caf%C3%A9');

    expect(rowNames()).toEqual(['IMG_0001.jpg', 'IMG_0002.jpg']);
    const crumbs = screen.getByRole('navigation', { name: 'Folder' });
    expect(within(crumbs).getByRole('link', { name: 'All files' }).getAttribute('href')).toBe('#/');
    expect(within(crumbs).getByRole('link', { name: 'photos' }).getAttribute('href')).toBe('#/photos');
    expect(within(crumbs).getByText('über café').getAttribute('aria-current')).toBe('page');
  });

  it('links folders by hash and files by their encoded URL', () => {
    render(<App data={fixture()} />);
    navigate('#/docs');

    expect(screen.getByRole('link', { name: 'specs' }).getAttribute('href')).toBe('#/docs/specs');
    const draft = screen.getByRole('link', { name: '[draft] plan #3?.md' });
    expect(draft.getAttribute('href')).toBe('files/docs/%5Bdraft%5D%20plan%20%233%3F.md');
    expect(screen.getByRole('link', { name: 'Download [draft] plan #3?.md' }).getAttribute('download')).toBe('[draft] plan #3?.md');
  });

  it('opens inline files in a new tab, and downloads the rest', () => {
    render(<App data={fixture()} />);
    navigate('#/site');

    const html = screen.getByRole('link', { name: 'index.html' });
    expect(html.getAttribute('download')).toBe('index.html');
    expect(html.getAttribute('target')).toBeNull();

    navigate('#/data');
    const pdf = screen.getByRole('link', { name: 'report 2026 (final).pdf' });
    expect(pdf.getAttribute('target')).toBe('_blank');
    expect(pdf.getAttribute('rel')).toBe('noopener noreferrer');
  });

  it('treats an unknown folder in the hash as the root', () => {
    render(<App data={fixture()} />);
    navigate('#/no/such/folder');
    expect(rowNames()[0]).toBe('a b');
  });

  it('says when a folder is empty', () => {
    render(<App data={fixture()} />);
    navigate('#/logs');
    expect(screen.getByText('This folder is empty.')).toBeTruthy();
  });

  it('searches every path and counts the matches', async () => {
    render(<App data={fixture()} />);
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search all files' }), 'jpg');

    await waitFor(() => expect(screen.getByRole('status').textContent).toBe('2 matches for “jpg”'));
    expect(rowNames()).toEqual(['IMG_0001.jpg', 'IMG_0002.jpg']);
    expect(screen.getAllByText('photos/über café')).toHaveLength(2);

    await userEvent.clear(screen.getByRole('searchbox'));
    await waitFor(() => expect(screen.queryByRole('status')).toBeNull());
  });

  it('renders hostile names as text, never as markup', () => {
    const data = fixture();
    data.entries = [
      { kind: 'file', path: '<img src=x onerror=alert(1)>.txt', size: 1, sha256: 'a'.repeat(64), mtimeUtc: '2026-01-01T00:00:00Z', disposition: 'inline' },
    ];
    render(<App data={data} />);
    expect(screen.getByRole('link', { name: '<img src=x onerror=alert(1)>.txt' })).toBeTruthy();
    expect(document.querySelector('img')).toBeNull();
  });

  it('offers the download-everything commands for this URL', () => {
    render(<App data={fixture()} />);
    const base = publicationBaseUrl(window.location);
    expect(screen.getByText(`curl -fsSL ${base}download.sh | sh`)).toBeTruthy();
    expect(screen.getByText(`irm ${base}download.ps1 | iex`)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'SHA256SUMS.txt' }).getAttribute('href')).toBe('SHA256SUMS.txt');
  });

  it.each([
    ['https://site.example/p/abc/', 'https://site.example/p/abc/'],
    ['https://site.example/p/abc/index.html#/docs', 'https://site.example/p/abc/'],
    ['https://site.example/p/abc/?x=1#/a', 'https://site.example/p/abc/'],
  ])('works out the publication URL from %s', (href, base) => {
    expect(publicationBaseUrl({ href })).toBe(base);
  });
});

describe('the error state', () => {
  it('explains a missing site-data.js instead of showing a blank page', () => {
    render(<App data={undefined} />);
    expect(screen.getByRole('alert').textContent).toContain('This publication could not be loaded');
  });

  it('explains a newer format', () => {
    render(<App data={{ ...fixture(), schemaVersion: 2 }} />);
    expect(screen.getByRole('heading', { name: 'This publication needs a newer page' })).toBeTruthy();
  });

  it('explains damaged data', () => {
    render(<App data={{ schemaVersion: 1 }} />);
    expect(screen.getByRole('heading', { name: 'This publication is damaged' })).toBeTruthy();
  });
});
