import { useTranslation } from "react-i18next";
import { Link2 } from "lucide-react";
import type { AnalysisPair, AnalysisTheme } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";

const TOP = 10;

// Pairs of themes recorded in the same signed session, ordered by the number of sessions (server order).
export default function CooccurrenceCard({ pairs, themes }: { pairs: AnalysisPair[]; themes: AnalysisTheme[] }) {
  const { t } = useTranslation();
  const label = (id: string) => themes.find((x) => x.termId === id)?.label ?? id;
  const top = pairs.slice(0, TOP);
  return (
    <DetailCard title={t("analysis.cooccurrence.title")} icon={<Link2 size={20} />}>
      {top.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("analysis.cooccurrence.empty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-outlineVariant">
          {top.map((p) => (
            <li key={`${p.a}-${p.b}`} className="flex flex-wrap items-center justify-between gap-2 py-2 text-body-md text-onSurface">
              <span className="break-words">
                {label(p.a)} + {label(p.b)}
              </span>
              <span className="text-body-sm text-onSurfaceVariant">{t("analysis.cooccurrence.sessions", { count: p.sessions })}</span>
            </li>
          ))}
        </ul>
      )}
    </DetailCard>
  );
}
