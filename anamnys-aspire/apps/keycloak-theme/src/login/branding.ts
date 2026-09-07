// One theme, three realms. The spec (§6) chose per-realm branding over three
// separate themes because the difference is a palette and a label, not a
// component tree.
type Branding = {
  logo: string;
  accent: string;
  productName: string;
};

const BRANDING: Record<string, Branding> = {
  'anamnys-providers': {
    logo: '/logo1.png',
    accent: 'var(--color-primary)',
    productName: 'Anamnys',
  },
  'anamnys-patients': {
    logo: '/logo1.png',
    accent: 'var(--color-primary)',
    productName: 'Anamnys',
  },
  'anamnys-owners': {
    logo: '/logo1.png',
    accent: 'var(--color-onSurfaceVariant)',
    productName: 'Anamnys Internal',
  },
};

const FALLBACK: Branding = BRANDING['anamnys-providers'];

export function brandingFor(realmName: string): Branding {
  return BRANDING[realmName] ?? FALLBACK;
}
