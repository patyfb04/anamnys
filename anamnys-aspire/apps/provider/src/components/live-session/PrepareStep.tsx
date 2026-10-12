import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { LiveNoteFormat } from "@anamnys/shared/lib/types";
import PatientPicker from "@/components/calendar/PatientPicker";
import { PRACTICE_ZONE, formatTime, todayIn } from "@/components/calendar/calendarTime";
import { inputClass } from "@/components/patients/FormField";
import { useAppointmentsQuery } from "@/hooks/useAppointments";
import type { StartOptions } from "@/hooks/useLiveRecorder";

const FORMATS: LiveNoteFormat[] = ["DAP", "SOAP", "Biopsychosocial"];
const labelClass = "flex flex-col gap-1 text-label-md text-onSurfaceVariant";

export default function PrepareStep({ onStart }: { onStart: (o: StartOptions) => void }) {
  const { t } = useTranslation();
  const [patient, setPatient] = useState<{ id: string; name: string } | null>(null);
  const [appointmentId, setAppointmentId] = useState<string>("");
  const [format, setFormat] = useState<LiveNoteFormat>("DAP");
  const today = useAppointmentsQuery([todayIn(PRACTICE_ZONE)]);
  const appointments = (today.data ?? []).filter(
    (a) => a.patientId === patient?.id && (a.status === "scheduled" || a.status === "confirmed"),
  );

  return (
    <div className="flex flex-col gap-4">
      <p className="rounded-radii-md bg-surfaceVariant p-3 text-body-md text-onSurfaceVariant">{t("liveSession.consent")}</p>
      <div className="flex flex-col gap-1">
        <span className="text-label-md text-onSurfaceVariant">{t("liveSession.patient")}</span>
        <PatientPicker value={patient} onChange={(p) => { setPatient(p); setAppointmentId(""); }} />
      </div>
      {patient && (
        <label className={labelClass}>
          {t("liveSession.appointment")}
          <select className={inputClass} value={appointmentId} onChange={(e) => setAppointmentId(e.target.value)}>
            <option value="">{t("liveSession.noAppointment")}</option>
            {appointments.map((a) => (
              <option key={a.id} value={a.id}>{formatTime(new Date(a.startsAt), PRACTICE_ZONE)}</option>
            ))}
          </select>
        </label>
      )}
      <label className={labelClass}>
        {t("liveSession.format")}
        <select className={inputClass} value={format} onChange={(e) => setFormat(e.target.value as LiveNoteFormat)}>
          {FORMATS.map((f) => <option key={f} value={f}>{t(`liveSession.formats.${f}`)}</option>)}
        </select>
      </label>
      <button
        type="button"
        disabled={!patient}
        onClick={() => onStart({ patientId: patient!.id, appointmentId: appointmentId || null, format, language: "pt" })}
        className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60"
      >
        {t("liveSession.start")}
      </button>
    </div>
  );
}
