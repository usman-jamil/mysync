import type { SiteData } from '../contract/site-data';
import { CopyButton } from './copy-button';
import { TerminalIcon } from './icons';

interface DownloadPanelProps {
  downloads: SiteData['downloads'];
  /** The publication's URL, ending in `/` (from the page's own location). */
  baseUrl: string;
}

export function DownloadPanel({ downloads, baseUrl }: DownloadPanelProps) {
  const commands = [
    { label: 'macOS / Linux', command: `curl -fsSL ${baseUrl}${downloads.shell} | sh` },
    { label: 'Windows PowerShell', command: `irm ${baseUrl}${downloads.powershell} | iex` },
  ];

  return (
    <section className="panel download-panel" aria-labelledby="download-everything">
      <h2 id="download-everything">
        <TerminalIcon /> Download everything
      </h2>
      <p className="muted">
        Run one of these in an empty folder. It downloads every file and checks it against{' '}
        <a href={downloads.sha256sums}>{downloads.sha256sums}</a>.
      </p>
      <ul className="commands">
        {commands.map(({ label, command }) => (
          <li key={label}>
            <span className="command-label">{label}</span>
            <span className="command">
              <code>{command}</code>
              <CopyButton text={command} label={`Copy the ${label} command`} />
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}

/** The folder this page is served from: `https://host/p/<id>/` whether the URL ends in `/` or `/index.html`. */
export function publicationBaseUrl(location: Pick<Location, 'href'>): string {
  const url = new URL('.', location.href);
  url.hash = '';
  url.search = '';
  return url.href;
}
