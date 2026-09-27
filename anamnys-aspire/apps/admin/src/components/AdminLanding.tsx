import { useTranslation } from "react-i18next";
import Card from "@anamnys/shared/ui/Card";

// Plain <a href> on purpose: /auth/owner/* answer with a 302 to Keycloak, which the
// whole document has to follow. Relative URLs work in dev too — this app's Vite
// server proxies /auth to the BFF with the Host header Keycloak expects.
// Renders inside routes/index.tsx's centered <main>; no min-h-screen of its own.
export default function AdminLanding() {
  const { t } = useTranslation();

  return (
    <Card className="w-full max-w-md text-center">
      <h1 className="text-headline-md text-onSurface mb-2">{t("admin.landing.title")}</h1>
      <p className="text-body-lg text-onSurfaceVariant mb-8">{t("admin.landing.subtitle")}</p>
      <div className="flex flex-col gap-3">
        <a
          href="/auth/owner/login?returnUrl=/admin/"
          className="block rounded-radii-full bg-primary text-onPrimary py-3.5 px-6 text-label-lg hover:opacity-90 transition-opacity"
        >
          {t("admin.landing.signIn")}
        </a>
        <a
          href="/auth/owner/register?returnUrl=/admin/"
          className="block rounded-radii-full border border-primary text-primary py-3.5 px-6 text-label-lg hover:bg-primary/5 transition-colors"
        >
          {t("admin.landing.register")}
        </a>
      </div>
    </Card>
  );
}
