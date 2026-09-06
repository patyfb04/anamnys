import { fileURLToPath, URL } from 'node:url';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { tanstackRouter } from '@tanstack/router-plugin/vite';

// https://vite.dev/config/
export default defineConfig({
  // Served from this sub-path of the .NET server's wwwroot in production, so all
  // three SPAs stay same-origin (which the BFF cookie auth depends on).
  base: '/admin/',
  plugins: [
    // Must run before the React plugin so generated route modules are transformed.
    tanstackRouter({ target: 'react', autoCodeSplitting: true }),
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    // Fixed, not Vite's auto-picked default: the OIDC redirect_uri the server builds for
    // this realm is derived from the Host header of the request that reaches it, so the
    // dev origin has to be stable across restarts to stay registered in Keycloak.
    port: 5276,
    strictPort: true,
    proxy: {
      // Aspire injects SERVER_HTTP from the AppHost's WithReference(server).
      // Plain HTTP avoids negotiating the ASP.NET dev certificate on a
      // localhost-to-localhost hop. `ws` covers the SignalR hubs.
      '/api': { target: process.env.SERVER_HTTP, changeOrigin: true, ws: true },
      '/hubs': { target: process.env.SERVER_HTTP, changeOrigin: true, ws: true },
      // No changeOrigin here, unlike /api and /hubs above: the server has to see
      // *this* dev origin in the Host header so it builds the OIDC redirect_uri
      // (and lands the correlation/session cookies) against localhost:5276 —
      // where the browser actually is — instead of the container-network alias
      // it would otherwise resolve from its own endpoint reference.
      '/auth': { target: process.env.SERVER_HTTP, ws: false },
      '/signin-oidc-owner': { target: process.env.SERVER_HTTP },
    },
  },
});
