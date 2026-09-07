import { createRoot } from 'react-dom/client';
import { StrictMode } from 'react';
import { KcPage } from './kc.gen';

if (!window.kcContext) {
  throw new Error('No Keycloak context');
}

const rootEl = document.getElementById('root');
if (!rootEl) throw new Error('Missing #root');

createRoot(rootEl).render(
  <StrictMode>
    <KcPage kcContext={window.kcContext} />
  </StrictMode>,
);
