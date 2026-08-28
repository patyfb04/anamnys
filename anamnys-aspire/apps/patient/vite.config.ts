import { fileURLToPath, URL } from 'node:url';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { tanstackRouter } from '@tanstack/router-plugin/vite';

// https://vite.dev/config/
export default defineConfig({
  // Served from this sub-path of the .NET server's wwwroot in production, so all
  // three SPAs stay same-origin (which the BFF cookie auth depends on).
  base: '/patient/',
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
    proxy: {
      // Aspire injects SERVER_HTTP from the AppHost's WithReference(server).
      // Plain HTTP avoids negotiating the ASP.NET dev certificate on a
      // localhost-to-localhost hop. `ws` covers the SignalR hubs.
      '/api': { target: process.env.SERVER_HTTP, changeOrigin: true, ws: true },
      '/hubs': { target: process.env.SERVER_HTTP, changeOrigin: true, ws: true },
    },
  },
});
