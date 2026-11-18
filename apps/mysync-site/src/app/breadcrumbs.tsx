import { folderHash } from '../lib/paths';

interface BreadcrumbsProps {
  folder: string;
  rootLabel: string;
}

export function Breadcrumbs({ folder, rootLabel }: BreadcrumbsProps) {
  const segments = folder === '' ? [] : folder.split('/');
  const crumbs = [{ label: rootLabel, path: '' }, ...segments.map((label, i) => ({ label, path: segments.slice(0, i + 1).join('/') }))];

  return (
    <nav className="breadcrumbs" aria-label="Folder">
      <ol>
        {crumbs.map((crumb, i) => {
          const current = i === crumbs.length - 1;
          return (
            <li key={crumb.path}>
              {current ? (
                <span aria-current="page">{crumb.label}</span>
              ) : (
                <a href={folderHash(crumb.path)}>{crumb.label}</a>
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
