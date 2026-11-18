import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './app/app';
import './styles.css';
import { applyTheme, storedTheme } from './app/theme';

// Before the first render, so a remembered theme never flashes the other one.
applyTheme(storedTheme());

const root = createRoot(document.getElementById('root') as HTMLElement);

function render() {
  root.render(
    <StrictMode>
      <App data={window.__MYSYNC_SITE__} />
    </StrictMode>,
  );
}

// Dev only (`nx serve mysync-site`, then `?fixture=large`): the 100,000-entry fixture written by
// tools/generate-fixture.mjs. The production build drops this branch entirely.
if (import.meta.env.DEV && new URLSearchParams(window.location.search).get('fixture') === 'large') {
  const script = document.createElement('script');
  script.src = './site-data.large.js';
  script.onload = render;
  script.onerror = () => {
    window.__MYSYNC_SITE__ = undefined;
    render();
  };
  document.head.append(script);
} else {
  render();
}
