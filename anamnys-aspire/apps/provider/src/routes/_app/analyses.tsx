import { useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { BarChart3 } from "lucide-react";
import { analysisApi } from "@anamnys/shared/api/analysis";
import { patientsApi } from "@anamnys/shared/api/patients";
import DetailCard from "@/components/patients/detail/DetailCard";
import PatientPicker from "@/components/analysis/PatientPicker";
import ThemeGridCard from "@/components/analysis/ThemeGridCard";
import AbsenceCard from "@/components/analysis/AbsenceCard";
import CooccurrenceCard from "@/components/analysis/CooccurrenceCard";
import TrajectoryCard from "@/components/analysis/TrajectoryCard";
import TimelineCard from "@/components/timeline/TimelineCard";
import { PERIODS, periodFrom, todayIso, type TimelinePeriod } from "@/components/timeline/timelineGeometry";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export const Route = createFileRoute("/_app/analyses")({
  // Only the patient id in the URL (no PHI); everything else is loaded.
  validateSearch: (search: Record<string, unknown>): { patientId?: string } => ({
    patientId: typeof search.patientId === "string" && UUID.test(search.patientId) ? search.patientId : undefined,
  }),
  component: AnalysesPage,
});

// Case analysis (F-11): themes per signed session, co-occurrence and the patient timeline.
// Observation only: no ranking by need, no evaluative wording.
function AnalysesPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { patientId } = Route.useSearch();
  const setPatient = (id: string | undefined) => navigate({ to: "/analyses", search: { patientId: id } });

  return (
    <div className="flex flex-col gap-6 p-4 md:p-8">
      <div>
        <h1 className="text-headline-lg text-onSurface">{t("analysis.title")}</h1>
        <p className="text-body-md text-onSurfaceVariant">{t("analysis.subtitle")}</p>
      </div>
      {patientId ? (
        <PatientAnalysis key={patientId} patientId={patientId} onChange={() => setPatient(undefined)} />
      ) : (
        <div className="flex flex-col gap-3">
          <p className="text-body-md text-onSurfaceVariant">{t("analysis.chooseHint")}</p>
          <PatientPicker onPick={setPatient} />
        </div>
      )}
    </div>
  );
}

function PatientAnalysis({ patientId, onChange }: { patientId: string; onChange: () => void }) {
  const { t } = useTranslation();
  const [period, setPeriod] = useState<TimelinePeriod>("1y");
  const today = todayIso();
  const patient = useQuery({ queryKey: ["patient", patientId], queryFn: () => patientsApi.get(patientId) });
  const query = useQuery({
    queryKey: ["analysis", patientId, period, today],
    queryFn: () => analysisApi.get(patientId, periodFrom(period, today)),
  });
  const data = query.data;
  const themes = data?.themes ?? [];

  const periodButton = (p: TimelinePeriod) =>
    `rounded-radii-full px-3 py-1 text-label-md transition-colors ${
      period === p ? "bg-primary text-onPrimary" : "text-onSurfaceVariant hover:bg-pastelLavender"
    }`;

  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <p className="break-words text-title-md text-onSurface">
            {patient.data ? `${patient.data.firstName} ${patient.data.lastName}` : ""}
          </p>
          <button type="button" onClick={onChange} className="text-label-lg text-primary hover:underline">
            {t("analysis.changePatient")}
          </button>
        </div>
        <div role="group" aria-label={t("analysis.periodLabel")} className="flex items-center gap-1">
          {PERIODS.map((p) => (
            <button key={p} type="button" aria-pressed={period === p} className={periodButton(p)} onClick={() => setPeriod(p)}>
              {t(`analysis.period.${p}`)}
            </button>
          ))}
        </div>
      </div>

      {query.isPending ? null : query.isError || !data ? (
        <p role="alert" className="text-body-md text-error">
          {t("analysis.loadFailed")}
        </p>
      ) : !data.available ? (
        <>
          <AbsenceCard absences={data.absences ?? []} absenceSessions={data.absenceSessions ?? 4} themes={themes} />
          <TrajectoryCard patientId={patientId} period={period} minimumSessions={data.minimumSessions} />
          <DetailCard title={t("analysis.themes.title")} icon={<BarChart3 size={20} />}>
            <p className="text-body-md text-onSurfaceVariant">
              {t("analysis.unavailable", { count: data.signedSessions, min: data.minimumSessions })}
            </p>
          </DetailCard>
        </>
      ) : (
        <>
          <AbsenceCard absences={data.absences ?? []} absenceSessions={data.absenceSessions ?? 4} themes={themes} />
          <TrajectoryCard patientId={patientId} period={period} />
          <ThemeGridCard sessions={data.sessions ?? []} themes={themes} absences={data.absences ?? []} absenceSessions={data.absenceSessions ?? 4} />
          <CooccurrenceCard pairs={data.cooccurrence ?? []} themes={themes} />
        </>
      )}
      <TimelineCard patientId={patientId} period={period} />
    </>
  );
}
