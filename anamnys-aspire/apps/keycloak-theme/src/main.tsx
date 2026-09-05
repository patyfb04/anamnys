import { createRoot } from 'react-dom/client';
import { StrictMode } from 'react';
import { KcPage } from './login/KcPage';

const rootEl = document.getElementById('root');
if (!rootEl) throw new Error('Missing #root');

createRoot(rootEl).render(
  <StrictMode>
    <KcPage />
  </StrictMode>,
);
