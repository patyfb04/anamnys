import { createFileRoute } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@anamnys/shared/lib/store/authStore';

export const Route = createFileRoute('/_app/dashboard')({
  component: DashboardPage,
});

// Placeholder landing page. The real dashboard (specs/UI/Dashboard/screen.png — stats
// tiles, today's schedule, recent activity) depends on features that don't exist
// server-side yet (transcription, notes, scheduling) — building that is its own design
// pass. This just gives a freshly registered or logged-in provider somewhere real to
// land instead of /patients.
function DashboardPage() {
  const { t } = useTranslation();
  const { user } = useAuthStore();

  return (
    <div className="p-4 md:p-8">
      <h1 className="text-headline-lg text-onSurface mb-2">
        {t('dashboard.greeting', { name: user?.name ?? '' })}
      </h1>
      <p className="text-body-lg text-onSurfaceVariant">{t('dashboard.comingSoon')}</p>
    </div>
  );
}
