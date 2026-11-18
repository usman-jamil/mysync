import { useCallback, useEffect, useRef, useState } from 'react';
import { folderFromHash } from '../lib/paths';

/** The folder in the location hash (`#/docs/sub`), kept in step with back/forward and edited links. */
export function useHashFolder(): string {
  const [folder, setFolder] = useState(() => folderFromHash(window.location.hash));

  useEffect(() => {
    const update = () => setFolder(folderFromHash(window.location.hash));
    window.addEventListener('hashchange', update);
    return () => window.removeEventListener('hashchange', update);
  }, []);

  return folder;
}

export function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);

  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(timer);
  }, [value, delayMs]);

  return debounced;
}

/** Copies text, and says so for a moment. Clipboard access needs HTTPS, which the static website has. */
export function useCopy(resetMs = 1500): [boolean, (text: string) => void] {
  const [copied, setCopied] = useState(false);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => () => window.clearTimeout(timer.current), []);

  const copy = useCallback(
    (text: string) => {
      void navigator.clipboard?.writeText(text).then(() => {
        setCopied(true);
        window.clearTimeout(timer.current);
        timer.current = window.setTimeout(() => setCopied(false), resetMs);
      });
    },
    [resetMs],
  );

  return [copied, copy];
}
