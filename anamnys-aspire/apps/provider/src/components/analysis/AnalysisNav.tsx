import { useTranslation } from "react-i18next";
import { goToCard } from "@/components/patients/detail/cardAnchors";

// Anchors at the top of the Análises page: each opens its card and scrolls to it, in page order.
const LINKS = [
  { id: "analysis-absence", label: "analysis.absence.title" },
  { id: "analysis-trajectory", label: "analysis.trajectory.title" },
  { id: "analysis-plan", label: "planAdherence.title" },
  { id: "analysis-themes", label: "analysis.themes.title" },
  { id: "analysis-quotes", label: "patientQuotes.title" },
  { id: "analysis-cooccurrence", label: "analysis.cooccurrence.title", onlyAvailable: true },
  { id: "timeline", label: "timeline.title" },
];

export default function AnalysisNav({ available }: { available: boolean }) {
  const { t } = useTranslation();

  return (
    <nav aria-label={t("analysis.navLabel")} className="flex flex-wrap gap-2">
      {LINKS.filter((l) => available || !l.onlyAvailable).map((l) => (
        <button key={l.id} type="button" onClick={() => goToCard(l.id)} className="rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-3 py-2 text-label-lg text-primary shadow-sm hover:bg-pastelLavender">
          {t(l.label)}
        </button>
      ))}
    </nav>
  );
}
