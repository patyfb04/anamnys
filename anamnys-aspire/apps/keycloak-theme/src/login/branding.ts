// One theme, three realms. The spec (§6) chose per-realm branding over three
// separate themes because the difference is a palette and a label, not a
// component tree.
//
// logo is imported, not a literal "/logo1.png" string: Keycloak serves this theme's
// assets under a dynamic /resources/<hash>/login/anamnys/dist/... prefix, never from
// site root, so a hardcoded absolute path 404s there even though it looks fine in a
// plain Vite dev server. Importing the file lets Vite bundle+hash it and lets
// Keycloakify's own build-time URL rewriting prefix it correctly, the same way it
// already does for this theme's JS/CSS bundles.
import logo1 from '../assets/logo1.png';

type Branding = {
  logo: string;
  accent: string;
  productName: string;
};

const BRANDING: Record<string, Branding> = {
  'anamnys-providers': {
    logo: logo1,
    accent: 'var(--color-primary)',
    productName: 'Anamnys',
  },
  'anamnys-patients': {
    logo: logo1,
    accent: 'var(--color-primary)',
    productName: 'Anamnys',
  },
  'anamnys-owners': {
    logo: logo1,
    accent: 'var(--color-onSurfaceVariant)',
    productName: 'Anamnys Internal',
  },
};

const FALLBACK: Branding = BRANDING['anamnys-providers'];

export function brandingFor(realmName: string): Branding {
  return BRANDING[realmName] ?? FALLBACK;
}
