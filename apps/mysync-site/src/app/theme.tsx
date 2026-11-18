import { useEffect, useState } from 'react';
import { MonitorIcon, MoonIcon, SunIcon } from './icons';

export type Theme = 'system' | 'light' | 'dark';

const STORAGE_KEY = 'mysync-site:theme';
const themes: { value: Theme; label: string; Icon: typeof SunIcon }[] = [
  { value: 'system', label: 'Match the system', Icon: MonitorIcon },
  { value: 'light', label: 'Light', Icon: SunIcon },
  { value: 'dark', label: 'Dark', Icon: MoonIcon },
];

/** The remembered choice; storage can be missing or blocked (private windows), so it's never required. */
export function storedTheme(): Theme {
  try {
    const value = window.localStorage.getItem(STORAGE_KEY);
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch {
    return 'system';
  }
}

/** `data-theme` on <html> picks the palette in styles.css; without it, prefers-color-scheme decides. */
export function applyTheme(theme: Theme): void {
  const root = document.documentElement;
  if (theme === 'system') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', theme);
}

export function ThemeSwitcher() {
  const [theme, setTheme] = useState<Theme>(storedTheme);

  useEffect(() => {
    applyTheme(theme);
    try {
      if (theme === 'system') window.localStorage.removeItem(STORAGE_KEY);
      else window.localStorage.setItem(STORAGE_KEY, theme);
    } catch {
      // Not remembered; the choice still applies to this page.
    }
  }, [theme]);

  return (
    <div className="theme-switcher" role="radiogroup" aria-label="Colour theme">
      {themes.map(({ value, label, Icon }) => (
        <button
          key={value}
          type="button"
          role="radio"
          aria-checked={theme === value}
          aria-label={label}
          title={label}
          className={theme === value ? 'selected' : undefined}
          onClick={() => setTheme(value)}
        >
          <Icon />
        </button>
      ))}
    </div>
  );
}
