import { createFileRoute } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { Stethoscope, HeartPulse } from 'lucide-react';
import { appUrls } from '@anamnys/shared/lib/appUrls';
import Card from '@anamnys/shared/ui/Card';

export const Route = createFileRoute('/_marketing/get-started')({
  component: GetStartedPage,
});

// Comecar's persona picker. Profissional links straight into Keycloak's (themed)
// registration form for the providers realm — see design/specs/2026-09-07-provider-
// patient-registration-design.md. Paciente is phase 2: its realm still has
// registrationAllowed: false, so it renders disabled rather than linking anywhere that
// would just fail.
function GetStartedPage() {
  const { t } = useTranslation();

  return (
    <div className="max-w-3xl mx-auto px-4 md:px-6 py-16 md:py-24">
      <h1 className="text-headline-lg text-onSurface mb-2 text-center">
        {t('welcome.getStartedPage.title')}
      </h1>
      <p className="text-body-lg text-onSurfaceVariant mb-10 text-center">
        {t('welcome.getStartedPage.subtitle')}
      </p>

      <div className="grid sm:grid-cols-2 gap-4">
        <a href={appUrls.providerRegister} className="block">
          <Card padded={false} className="h-full p-7 hover:border-primary/50 transition-colors">
            <Stethoscope size={28} className="text-primary mb-3" />
            <p className="text-headline-sm text-onSurface mb-1">{t('common.provider')}</p>
            <p className="text-body-md text-onSurfaceVariant">{t('welcome.getStartedPage.providerBody')}</p>
          </Card>
        </a>

        <Card padded={false} className="h-full p-7 opacity-60 cursor-not-allowed" aria-disabled="true">
          <div className="flex items-center justify-between mb-3">
            <HeartPulse size={28} className="text-primary" />
            <span className="text-label-sm text-onSurfaceVariant bg-surfaceContainerHigh rounded-radii-full px-2.5 py-1">
              {t('welcome.comingSoon')}
            </span>
          </div>
          <p className="text-headline-sm text-onSurface mb-1">{t('common.patient')}</p>
          <p className="text-body-md text-onSurfaceVariant">{t('welcome.getStartedPage.patientBody')}</p>
        </Card>
      </div>
    </div>
  );
}
