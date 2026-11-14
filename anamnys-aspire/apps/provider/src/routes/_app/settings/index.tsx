import { createFileRoute, useLocation, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import BookingPolicySection from "../../../components/settings/BookingPolicySection";
import AvailabilitySection from "../../../components/settings/AvailabilitySection";
import AccountExportSection from "../../../components/settings/AccountExportSection";
import DocumentSettingsSection from "../../../components/settings/DocumentSettingsSection";
import RetentionSettingsSection from "../../../components/settings/RetentionSettingsSection";
import AnalysisSettingsSection from "../../../components/settings/AnalysisSettingsSection";
import IntakeTemplatesSection from "../../../components/settings/IntakeTemplatesSection";
import FolderTabs from "../../../components/FolderTabs";

type Tab = "agenda" | "prontuario" | "geral";
const TABS: Tab[] = ["geral", "agenda", "prontuario"];

// Sections reached by a hash link (the bell's account export notice) open on their tab.
const TAB_OF_HASH: Record<string, Tab> = { "exportar-dados": "geral" };

export const Route = createFileRoute("/_app/settings/")({
  validateSearch: (search: Record<string, unknown>): { tab?: Tab } => ({
    tab: TABS.includes(search.tab as Tab) ? (search.tab as Tab) : undefined,
  }),
  component: SettingsPage,
});

function SettingsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { hash } = useLocation();
  const { tab: chosen } = Route.useSearch();
  const tab = chosen ?? TAB_OF_HASH[hash] ?? "geral";

  return (
    <div className="p-4 md:p-8 flex flex-col gap-6">
      <h1 className="text-headline-sm text-onSurface">{t("settings.pageTitle")}</h1>
      <FolderTabs
        label={t("settings.tabs.label")}
        active={tab}
        onSelect={(id) => void navigate({ to: "/settings", search: { tab: id } })}
        tabs={TABS.map((id) => ({ id, label: t(`settings.tabs.${id}`) }))}
      >
        {tab === "agenda" && (
          <>
            <AvailabilitySection />
            <BookingPolicySection />
          </>
        )}
        {tab === "prontuario" && (
          <>
            <DocumentSettingsSection />
            <RetentionSettingsSection />
            <AnalysisSettingsSection />
            <IntakeTemplatesSection />
          </>
        )}
        {tab === "geral" && <AccountExportSection />}
      </FolderTabs>
    </div>
  );
}
