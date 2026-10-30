import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import BookingPolicySection from "../../../components/settings/BookingPolicySection";
import AccountExportSection from "../../../components/settings/AccountExportSection";
import DocumentSettingsSection from "../../../components/settings/DocumentSettingsSection";
import RetentionSettingsSection from "../../../components/settings/RetentionSettingsSection";
import AnalysisSettingsSection from "../../../components/settings/AnalysisSettingsSection";

export const Route = createFileRoute("/_app/settings/")({
  component: SettingsPage,
});

function SettingsPage() {
  const { t } = useTranslation();
  return (
    <div className="p-4 md:p-8 flex flex-col gap-6">
      <h1 className="text-headline-sm text-onSurface">{t("settings.pageTitle")}</h1>
      <BookingPolicySection />
      <DocumentSettingsSection />
      <RetentionSettingsSection />
      <AnalysisSettingsSection />
      <AccountExportSection />
    </div>
  );
}
