// URLs for published paths. Every name is untrusted: it may hold anything a file name can.

/** The URL of a published file, relative to the publication: `files/` + each segment percent-encoded. */
export function fileUrl(path: string): string {
  return 'files/' + encodeSegments(path);
}

/** The location hash for a folder: `#/` + each segment percent-encoded (`#/` is the root). */
export function folderHash(folder: string): string {
  return '#/' + encodeSegments(folder);
}

/**
 * The folder a location hash points at, or '' (the root) for anything that isn't a well-formed
 * `#/a/b`. A hash that names a folder that doesn't exist is the caller's to handle.
 */
export function folderFromHash(hash: string): string {
  if (!hash.startsWith('#/')) return '';
  const segments = hash.slice(2).split('/').filter((segment) => segment !== '');
  try {
    return segments.map((segment) => decodeURIComponent(segment)).join('/');
  } catch {
    return '';
  }
}

export function baseName(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash === -1 ? path : path.slice(slash + 1);
}

export function parentOf(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash === -1 ? '' : path.slice(0, slash);
}

function encodeSegments(path: string): string {
  return path === '' ? '' : path.split('/').map(encodeURIComponent).join('/');
}
