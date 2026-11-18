import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { fixture } from '../testing/fixture';
import App from './app';
import { storedTheme } from './theme';

const theme = () => document.documentElement.getAttribute('data-theme');

describe('the colour theme', () => {
  it('follows the system until the viewer chooses', () => {
    render(<App data={fixture()} />);
    expect(theme()).toBeNull();
    expect(screen.getByRole('radio', { name: 'Match the system' }).getAttribute('aria-checked')).toBe('true');
  });

  it('switches to dark and light, and remembers the choice', async () => {
    render(<App data={fixture()} />);

    await userEvent.click(screen.getByRole('radio', { name: 'Dark' }));
    expect(theme()).toBe('dark');
    expect(storedTheme()).toBe('dark');
    expect(screen.getByRole('radio', { name: 'Dark' }).getAttribute('aria-checked')).toBe('true');

    await userEvent.click(screen.getByRole('radio', { name: 'Light' }));
    expect(theme()).toBe('light');

    await userEvent.click(screen.getByRole('radio', { name: 'Match the system' }));
    expect(theme()).toBeNull();
    expect(window.localStorage.getItem('mysync-site:theme')).toBeNull();
  });

  it('starts from a remembered choice', () => {
    window.localStorage.setItem('mysync-site:theme', 'dark');
    render(<App data={fixture()} />);
    expect(theme()).toBe('dark');
  });

  it('ignores a stored value it does not know', () => {
    window.localStorage.setItem('mysync-site:theme', 'neon');
    expect(storedTheme()).toBe('system');
  });

  it('still works when storage is blocked', async () => {
    const blocked = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    try {
      render(<App data={fixture()} />);
      await userEvent.click(screen.getByRole('radio', { name: 'Dark' }));
      expect(theme()).toBe('dark');
    } finally {
      vi.restoreAllMocks();
    }
    expect(blocked).toHaveBeenCalled();
  });

  it('is offered on the error page too', () => {
    render(<App data={undefined} />);
    expect(screen.getByRole('radiogroup', { name: 'Colour theme' })).toBeTruthy();
  });
});
