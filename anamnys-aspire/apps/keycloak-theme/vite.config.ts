import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { keycloakify } from 'keycloakify/vite-plugin';

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    keycloakify({
      // Emitted next to the Keycloak Dockerfile so the image build can COPY it
      // without reaching across the workspace.
      keycloakifyBuildDirPath: '../../keycloak/theme',
      accountThemeImplementation: 'none',
      themeName: 'anamnys',
      keycloakVersionTargets: {
        '22-to-25': false,
        'all-other-versions': 'keycloak-theme-anamnys.jar',
      },
    }),
  ],
});
