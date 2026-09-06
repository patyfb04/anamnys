import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider, createRouter } from '@tanstack/react-router';
import './index.css';
import { routeTree } from './routeTree.gen';

// import.meta.env.BASE_URL mirrors vite.config.ts's `base` ("/patient/") — see the same
// fix in apps/provider/src/main.tsx for why this is required rather than cosmetic.
const router = createRouter({ routeTree, basepath: import.meta.env.BASE_URL });

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
