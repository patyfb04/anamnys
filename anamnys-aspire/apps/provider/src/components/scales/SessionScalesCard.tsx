import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Activity } from "lucide-react";
import { patientsApi } from "@anamnys/shared/api/patients";
import { scalesApi } from "@anamnys/shared/api/scales";
import DetailCard from "@/components/patients/detail/DetailCard";
import ScaleRecordModal from "./ScaleRecordModal";

// Note page card: the scores recorded for this note's session, with a shortcut to record another.
export default function SessionScalesCard({ patientId, sessionId }: { patientId: string; sessionId: string }) {
  const { t } = useTranslation();
  const [recording, setRecording] = useState(false);
  const series = useQuery({
    queryKey: ["scales", patientId, sessionId],
    queryFn: () => scalesApi.series(patientId, sessionId),
  });
  // Same query key as the patient record page, so the cache is shared.
  const patient = useQuery({ queryKey: ["patient", patientId], queryFn: () => patientsApi.get(patientId) });
  const canRecord = patient.isSuccess && patient.data.archivedAt == null;
  const lines = (series.data ?? []).flatMap((s) => s.entries.map((e) => ({ id: e.id, text: `${s.code}: ${e.score}` })));

  return (
    <DetailCard
      title={t("scales.sessionTitle")}
      icon={<Activity size={20} />}
      action={
        canRecord ? (
          <button
            type="button"
            onClick={() => setRecording(true)}
            className="rounded-radii-full px-3 py-1.5 text-label-lg text-primary hover:bg-pastelLavender"
          >
            {t("scales.record")}
          </button>
        ) : undefined
      }
    >
      {series.isPending ? null : series.isError ? (
        <p className="text-body-md text-error">{t("scales.seriesLoadFailed")}</p>
      ) : lines.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("scales.sessionEmpty")}</p>
      ) : (
        <ul className="flex flex-col gap-1 text-body-md text-onSurface">
          {lines.map((l) => (
            <li key={l.id}>{l.text}</li>
          ))}
        </ul>
      )}
      {recording && <ScaleRecordModal patientId={patientId} sessionId={sessionId} onClose={() => setRecording(false)} />}
    </DetailCard>
  );
}
