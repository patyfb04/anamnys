import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { tanstackRouter } from '@tanstack/router-plugin/vite';

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    // Must run before the React plugin so generated route modules are transformed.
    tanstackRouter({ target: 'react', autoCodeSplitting: true }),
    react(),
    tailwindcss(),
  ],
  server: {
    proxy: {
      // Proxy API calls to the server resource. Aspire injects SERVER_HTTP from
      // the AppHost's WithReference(server). Plain HTTP avoids negotiating the
      // ASP.NET dev certificate on a localhost-to-localhost hop.
      // `ws` is enabled for the SignalR hubs the app will add later.
      '/api': {
        target: process.env.SERVER_HTTP,
        changeOrigin: true,
        ws: true,
      },
    },
  },
});
