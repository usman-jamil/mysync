import { cleanup } from '@testing-library/react';
import { afterEach, vi } from 'vitest';

// jsdom doesn't implement scrolling; the virtualized list asks for it.
window.scrollTo = vi.fn() as unknown as typeof window.scrollTo;

afterEach(() => {
  cleanup();
  window.location.hash = '';
  delete window.__MYSYNC_SITE__;
  window.localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
});
