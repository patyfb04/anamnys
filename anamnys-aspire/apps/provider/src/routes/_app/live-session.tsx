import { useEffect, useState } from "react";
import { createFileRoute, useBlocker } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Cpu, Lock } from "lucide-react";
import type { LiveNoteFormat } from "@anamnys/shared/lib/types";
import { patientsApi } from "@anamnys/shared/api/patients";
import PatientCard, { type PickedPatient } from "@/components/live-session/PatientCard";
import RecordingCard from "@/components/live-session/RecordingCard";
import FormatCards from "@/components/live-session/FormatCards";
import SafeguardsCard from "@/components/live-session/SafeguardsCard";
import ProcessingStep from "@/components/live-session/ProcessingStep";
import { useLiveRecorder } from "@/hooks/useLiveRecorder";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

interface LiveSessionSearch {
  patientId?: string;
}

export const Route = createFileRoute("/_app/live-session")({
  // Only an opaque id (as in /patients/$patientId): the name and appointment stay in state.
  validateSearch: (search: Record<string, unknown>): LiveSessionSearch => ({
    patientId: typeof search.patientId === "string" && UUID.test(search.patientId) ? search.patientId : undefined,
  }),
  component: LiveSessionPage,
});

// Live dictation to a structured note (design/specs/2026-10-10-live-session-transcription-design.md §6).
function LiveSessionPage() {
  const { t } = useTranslation();
  const { patientId } = Route.useSearch();
  const recorder = useLiveRecorder();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [appointmentId, setAppointmentId] = useState("");
  const [format, setFormat] = useState<LiveNoteFormat>("DAP");
  const holdsAudio = recorder.state === "recording" || recorder.state === "paused" || recorder.state === "finalizing";
  const processing = recorder.state === "finalizing" || recorder.state === "done";
  const locked = recorder.state !== "idle" && recorder.state !== "error";

  // Opened from a patient's page: preselect that patient once.
  const preselected = useQuery({
    queryKey: ["patient", patientId],
    queryFn: () => patientsApi.get(patientId!),
    enabled: !!patientId,
  });
  useEffect(() => {
    if (preselected.data && !patient) {
      setPatient({ id: preselected.data.id, name: `${preselected.data.firstName} ${preselected.data.lastName}` });
    }
    // Only when the preselected patient arrives; later changes are the user's.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preselected.data]);

  useBlocker({
    shouldBlockFn: () => holdsAudio && !window.confirm(t("liveSession.leaveWarning")),
    enableBeforeUnload: () => holdsAudio,
  });

  return (
    <div className="flex flex-col gap-6 p-4 md:p-8">
      <header className="flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <h1 className="text-headline-lg text-onSurface">{t("liveSession.title")}</h1>
          <p className="mt-1 text-body-md text-onSurfaceVariant">{t("liveSession.subtitle")}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <span className="inline-flex items-center gap-2 rounded-radii-full bg-pastelMint px-3 py-1 text-label-md text-onPastelMint">
            <Lock size={14} aria-hidden />
            {t("liveSession.chips.memory")}
          </span>
          <span className="inline-flex items-center gap-2 rounded-radii-full bg-pastelLavender px-3 py-1 text-label-md text-onPastelLavender">
            <Cpu size={14} aria-hidden />
            {t("liveSession.chips.local")}
          </span>
        </div>
      </header>

      {recorder.error && recorder.error !== "too_long" && (
        <p className="text-body-md text-error">{t(`liveSession.errors.${recorder.error}`)}</p>
      )}
      {recorder.error === "too_long" && processing && (
        <p className="text-body-md text-onSurfaceVariant">{t("liveSession.errors.too_long")}</p>
      )}

      <div className="grid gap-6 lg:grid-cols-3">
        <div className="flex flex-col gap-6 lg:col-span-2">
          <PatientCard
            patient={patient}
            onPatientChange={setPatient}
            appointmentId={appointmentId}
            onAppointmentChange={setAppointmentId}
            locked={locked}
          />
          <RecordingCard
            state={recorder.state}
            connection={recorder.connection}
            elapsedMs={recorder.elapsedMs}
            level={recorder.level}
            canStart={!!patient}
            onStart={() =>
              patient && void recorder.start({ patientId: patient.id, appointmentId: appointmentId || null, format, language: "pt" })
            }
            onPause={recorder.pause}
            onResume={recorder.resume}
            onStop={() => void recorder.stop()}
            processing={processing ? <ProcessingStep noteId={recorder.noteId} onRecordAgain={recorder.reset} /> : undefined}
          />
        </div>
        <div className="flex flex-col gap-6">
          <FormatCards value={format} onChange={setFormat} disabled={locked} />
          <SafeguardsCard />
        </div>
      </div>
    </div>
  );
}
