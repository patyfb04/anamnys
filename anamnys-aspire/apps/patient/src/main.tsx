import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider, createRouter } from '@tanstack/react-router';
import './index.css';
import { routeTree } from './routeTree.gen';

const router = createRouter({ routeTree });

// Gives `<Link to="...">` and the router hooks full type inference over the
// generated route tree.
declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <RouterProvider router={router} />
  </StrictMode>,
);
