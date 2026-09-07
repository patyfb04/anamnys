import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider, createRouter } from '@tanstack/react-router';
import './index.css';
import { routeTree } from './routeTree.gen';

// import.meta.env.BASE_URL mirrors vite.config.ts's `base` ("/provider/"). Without it the
// router matches routes against the raw pathname (which still carries that prefix in both
// dev and prod, since the SPA is always served from this sub-path), and every route past
// "/" 404s inside the app even though the asset requests that loaded it succeeded.
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
