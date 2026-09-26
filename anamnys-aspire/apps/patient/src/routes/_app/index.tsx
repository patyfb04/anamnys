import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";

export const Route = createFileRoute("/_app/")({ component: Home });

// Placeholder home. The patient app will own booking, rescheduling and cancelling
// appointments; those routes land once the appointment API exists.
function Home() {
  const { t } = useTranslation();
  const user = useAuthStore((s) => s.user);

  return (
    <div className="max-w-5xl mx-auto px-4 md:px-6 py-10">
      <h1 className="text-headline-lg text-onSurface">
        {t("dashboard.greeting", { name: user?.name ?? "" })}
      </h1>
      <p className="mt-2 text-body-lg text-onSurfaceVariant">{t("patientPortal.comingSoon")}</p>
    </div>
  );
}
