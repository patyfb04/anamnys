import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import BookingPolicySection from "../../../components/settings/BookingPolicySection";

export const Route = createFileRoute("/_app/settings/")({
  component: SettingsPage,
});

function SettingsPage() {
  const { t } = useTranslation();
  return (
    <div className="p-6 flex flex-col gap-6">
      <h1 className="text-headline-sm text-onSurface">{t("settings.pageTitle")}</h1>
      <BookingPolicySection />
    </div>
  );
}
