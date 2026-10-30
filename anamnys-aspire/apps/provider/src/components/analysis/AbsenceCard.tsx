import { useState } from "react";
import { useTranslation } from "react-i18next";
import { History } from "lucide-react";
import type { AnalysisAbsence, AnalysisTheme } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import OccurrenceModal from "./OccurrenceModal";

// dd/mm/aaaa from an ISO day (no time zone shift).
function formatShortDate(iso: string): string {
  const [y, m, d] = iso.split("-");
  return `${d}/${m}/${y}`;
}

// "Temas sem registro recente": terms recorded repeatedly and then not recorded for the last N sessions.
// Descriptive only (observation verbs, neutral colour); a line opens the last passage.
export default function AbsenceCard({
  absences,
  absenceSessions,
  themes,
}: {
  absences: AnalysisAbsence[];
  absenceSessions: number;
  themes: AnalysisTheme[];
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState<AnalysisAbsence | null>(null);
  // Server order (by last record date).
  const ordered = absences;

  return (
    <DetailCard title={t("analysis.absence.title")} icon={<History size={20} />}>
      {ordered.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("analysis.absence.empty", { count: absenceSessions })}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-outlineVariant">
          {ordered.map((a) => (
            <li key={a.termId}>
              <button
                type="button"
                onClick={() => setOpen(a)}
                aria-haspopup="dialog"
                className="flex w-full flex-col gap-0.5 rounded-radii-md px-2 py-2 text-left hover:bg-pastelLavender"
              >
                <span className="break-words text-body-md text-onSurface">{a.label}</span>
                <span className="text-body-sm text-onSurfaceVariant">
                  {t("analysis.absence.line", {
                    recorded: a.recordedIn,
                    span: a.spanSessions,
                    since: t("analysis.absence.sessionsSince", { count: a.sessionsSince }),
                    date: formatShortDate(a.lastOn),
                    days: t("analysis.absence.days", { count: a.daysSince }),
                  })}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {open && (
        <OccurrenceModal
          on={open.lastOn}
          noteId={open.lastOccurrence.noteId}
          items={[
            {
              theme: themes.find((x) => x.termId === open.termId) ?? {
                termId: open.termId,
                code: open.code,
                label: open.label,
                parentId: open.parentId,
                active: true,
                supersededBy: [],
                occurrences: [],
              },
              occurrence: { ...open.lastOccurrence, on: open.lastOn },
            },
          ]}
          sameSession={[]}
          onClose={() => setOpen(null)}
        />
      )}
    </DetailCard>
  );
}
