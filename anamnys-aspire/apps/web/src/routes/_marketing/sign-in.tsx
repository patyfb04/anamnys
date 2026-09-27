import { createFileRoute } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { Stethoscope, HeartPulse } from 'lucide-react';
import { appUrls } from '@anamnys/shared/lib/appUrls';
import Card from '@anamnys/shared/ui/Card';

export const Route = createFileRoute('/_marketing/sign-in')({
  component: SignInPage,
});

// Entrar's persona picker, the login counterpart of /get-started. Both cards link
// straight into the BFF's /auth/{realm}/login, which challenges into Keycloak's
// (themed) login form. The owners realm is internal and deliberately not offered here.
function SignInPage() {
  const { t } = useTranslation();

  return (
    <div className="max-w-3xl mx-auto px-4 md:px-6 py-16 md:py-24">
      <h1 className="text-headline-lg text-onSurface mb-2 text-center">
        {t('welcome.signInPage.title')}
      </h1>
      <p className="text-body-lg text-onSurfaceVariant mb-10 text-center">
        {t('welcome.signInPage.subtitle')}
      </p>

      <div className="grid sm:grid-cols-2 gap-4">
        <a href={appUrls.providerLogin} className="block">
          <Card padded={false} className="h-full p-7 hover:border-primary/50 transition-colors">
            <Stethoscope size={28} className="text-primary mb-3" />
            <p className="text-headline-sm text-onSurface mb-1">{t('common.provider')}</p>
            <p className="text-body-md text-onSurfaceVariant">{t('welcome.signInPage.providerBody')}</p>
          </Card>
        </a>

        <a href={appUrls.patientLogin} className="block">
          <Card padded={false} className="h-full p-7 hover:border-primary/50 transition-colors">
            <HeartPulse size={28} className="text-primary mb-3" />
            <p className="text-headline-sm text-onSurface mb-1">{t('common.patient')}</p>
            <p className="text-body-md text-onSurfaceVariant">{t('welcome.signInPage.patientBody')}</p>
          </Card>
        </a>
      </div>
    </div>
  );
}
