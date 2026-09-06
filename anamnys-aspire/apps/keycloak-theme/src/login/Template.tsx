import type { ReactNode } from 'react';
import { ShieldCheck } from 'lucide-react';
import { brandingFor } from './branding';
import type { KcContext } from './KcContext';
import type { I18n } from './i18n';

type Props = {
  kcContext: KcContext;
  i18n: I18n;
  headline: ReactNode;
  subhead: ReactNode;
  children: ReactNode;
};

// The Anamnys shell for the three pages this theme overrides (login, login-otp,
// login-reset-password). Ported from apps/provider/src/routes/_auth.tsx.
// Every other Keycloak page falls through to keycloakify's default Template, which
// keeps its own default CSS — deliberately not reskinned here.
export function Template({ kcContext, i18n, headline, subhead, children }: Props) {
  const branding = brandingFor(kcContext.realm.name);
  const { msg } = i18n;

  return (
    <div className="min-h-screen bg-surfaceContainerLow flex flex-col">
      <header className="bg-surface border-b border-surfaceVariant px-4">
        <div className="h-16 max-w-5xl mx-auto w-full flex items-center justify-between">
          <img
            src={branding.logo}
            alt={branding.productName}
            width={140}
            height={30}
            className="w-[140px] h-[30px] object-contain"
          />
        </div>
      </header>

      <div className="flex-1 flex items-start justify-center p-4 pt-10 md:pt-14 pb-8">
        <div className="w-full max-w-5xl flex flex-col md:flex-row md:items-start gap-8">
          <div className="hidden md:flex md:flex-[5] flex-col">
            <h1 className="text-headline-lg text-[30px] text-onSurface mb-2.5">{headline}</h1>
            <p className="text-body-lg text-onSurfaceVariant mb-6 max-w-[360px]">{subhead}</p>
            <div className="rounded-radii-lg overflow-hidden bg-surfaceContainerHigh shadow-xl relative flex-1 min-h-[240px]">
              <img src="/sign-up.jpg" alt="" className="absolute inset-0 w-full h-full object-cover" />
              <div className="absolute inset-x-0 bottom-0 h-[55%] bg-primary/30" />
              <div className="absolute left-4 right-4 bottom-4 flex items-center gap-2 bg-white/85 border border-white/50 rounded-radii-md px-3 py-2.5">
                <ShieldCheck size={16} style={{ color: branding.accent }} />
                <span className="text-label-md text-onSurface normal-case">{msg('secureBadge')}</span>
              </div>
            </div>
          </div>

          <div className="flex-1 md:flex-[7]">
            <div className="bg-surfaceContainerLowest rounded-radii-xl border border-surfaceContainerHighest p-4 md:p-6 shadow-xl">
              {children}
            </div>
          </div>
        </div>
      </div>

      <p className="text-center text-body-md text-onSurfaceVariant/70 pb-8">{msg('footerCopyright')}</p>
    </div>
  );
}
