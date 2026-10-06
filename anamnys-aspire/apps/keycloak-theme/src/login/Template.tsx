import type { ReactNode } from 'react';
import { ShieldCheck } from 'lucide-react';
import { brandingFor } from './branding';
import signUpImage from '../assets/sign-up.jpg';
import type { KcContext } from './KcContext';
import type { I18n } from './i18n';

type Props = {
  kcContext: KcContext;
  i18n: I18n;
  headline: ReactNode;
  subhead: ReactNode;
  children: ReactNode;
};

// The Anamnys shell for every page this theme overrides (see KcPage.tsx).
// Ported from apps/provider/src/routes/_auth.tsx.
// Every other Keycloak page falls through to keycloakify's default Template, which
// keeps its own default CSS — deliberately not reskinned here.
export function Template({ kcContext, i18n, headline, subhead, children }: Props) {
  const branding = brandingFor(kcContext.realm.name);
  const { msg } = i18n;
  // The marketing site's origin, from the Keycloak client's baseUrl (see the realm
  // JSON). Absent for the owners realm, which is internal: logo only, no site nav.
  const siteUrl = kcContext.client.baseUrl;
  const site = (path: string) => (siteUrl ? new URL(path, siteUrl).toString() : undefined);

  const logo = (
    <img
      src={branding.logo}
      alt={branding.productName}
      width={140}
      height={30}
      className="w-[140px] h-[30px] object-contain"
    />
  );

  return (
    <div className="min-h-screen bg-surfaceContainerLow flex flex-col">
      <header className="bg-surface border-b border-surfaceVariant px-4 md:px-12">
        <div className="h-16 max-w-7xl mx-auto w-full flex items-center justify-between gap-4">
          {siteUrl ? (
            <a href={site('/')} className="shrink-0">
              {logo}
            </a>
          ) : (
            logo
          )}

          {siteUrl && (
            <>
              <nav className="hidden md:flex items-center gap-6">
                {(
                  [
                    ['/product', 'navProduct'],
                    ['/pricing', 'navPricing'],
                    ['/aboutus', 'navAboutUs'],
                  ] as const
                ).map(([path, key]) => (
                  <a
                    key={path}
                    href={site(path)}
                    className="text-onSurfaceVariant font-medium py-4 hover:text-primary transition-colors"
                  >
                    {msg(key)}
                  </a>
                ))}
              </nav>

              <div className="flex items-center gap-2 shrink-0">
                <a
                  href={site('/sign-in')}
                  className="text-label-lg text-primary hover:opacity-80 transition-opacity px-2"
                >
                  {msg('navSignIn')}
                </a>
                <a
                  href={site('/get-started')}
                  className="bg-primaryFixed text-onPrimaryFixedVariant rounded-radii-md px-4 py-2 text-label-lg text-[13px] hover:opacity-80 transition-opacity"
                >
                  {msg('navGetStarted')}
                </a>
              </div>
            </>
          )}
        </div>
      </header>

      <div className="flex-1 flex items-start justify-center p-4 pt-10 md:pt-14 pb-8">
        <div className="w-full max-w-5xl flex flex-col md:flex-row md:items-start gap-8">
          <div className="hidden md:flex md:flex-[5] flex-col">
            <h1 className="text-headline-lg text-[30px] text-onSurface mb-2.5">{headline}</h1>
            <p className="text-body-lg text-onSurfaceVariant mb-6 max-w-[360px]">{subhead}</p>
            <div className="rounded-radii-lg overflow-hidden bg-surfaceContainerHigh shadow-xl relative flex-1 min-h-[240px] border-8 border-white">
              <img src={signUpImage} alt="" className="absolute inset-0 w-full h-full object-cover" />
              <div className="absolute inset-x-0 bottom-0 h-[55%] bg-primary/30" />
              <div className="absolute left-4 right-4 bottom-4 flex items-center gap-2 bg-white/85 border border-white/50 rounded-radii-md px-3 py-2.5">
                <ShieldCheck size={16} style={{ color: branding.accent }} />
                <span className="text-label-md text-onSurface normal-case">{msg('secureBadge')}</span>
              </div>
            </div>
          </div>

          <div className="flex-1 md:flex-[7]">
            <div className="bg-surfaceContainerLowest rounded-radii-xl border border-primary p-4 md:p-6 shadow-xl">
              {children}
            </div>
          </div>
        </div>
      </div>

      <p className="text-center text-body-md text-onSurfaceVariant/70 pb-8">{msg('footerCopyright')}</p>
    </div>
  );
}
