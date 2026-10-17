import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { CalendarClock, UserRound } from "lucide-react";
import { notesApi } from "@anamnys/shared/api/notes";
import PatientPicker from "@/components/calendar/PatientPicker";
import { PRACTICE_ZONE, formatTimeWithDate, todayIn, zonedParts } from "@/components/calendar/calendarTime";
import { inputClass } from "@/components/patients/FormField";

export type PickedPatient = { id: string; name: string };

interface Props {
  patient: PickedPatient | null;
  onPatientChange: (patient: PickedPatient | null) => void;
  appointmentId: string;
  onAppointmentChange: (id: string) => void;
  error?: string[];
}

// Who the typed note is for, and optionally which recent appointment it records.
export default function NoteTargetCard({ patient, onPatientChange, appointmentId, onAppointmentChange, error }: Props) {
  const { t } = useTranslation();
  const candidates = useQuery({
    queryKey: ["note-candidates", patient?.id],
    queryFn: () => notesApi.candidates(patient!.id),
    enabled: !!patient,
  });

  // Preselect today's appointment once the candidates arrive, unless one was chosen already.
  useEffect(() => {
    if (!candidates.data) return;
    if (appointmentId) {
      if (!candidates.data.some((c) => c.appointmentId === appointmentId)) onAppointmentChange("");
      return;
    }
    const today = todayIn(PRACTICE_ZONE);
    const ofToday = candidates.data.find((c) => zonedParts(new Date(c.startsAt), PRACTICE_ZONE).date === today);
    if (ofToday) onAppointmentChange(ofToday.appointmentId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [candidates.data]);

  return (
    <section className="flex flex-col gap-4 bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm">
      <div className="flex flex-col gap-2">
        <h2 className="flex items-center gap-2 text-title-md text-onSurface">
          <UserRound size={20} className="text-primary" aria-hidden />
          {t("notes.create.patient")}
        </h2>
        <PatientPicker
          value={patient}
          onChange={(p) => {
            onPatientChange(p);
            onAppointmentChange("");
          }}
          error={error}
        />
      </div>
      {patient && (
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          <span className="flex items-center gap-2">
            <CalendarClock size={16} className="text-primary" aria-hidden />
            {t("notes.create.appointment")}
          </span>
          <select className={inputClass} value={appointmentId} onChange={(e) => onAppointmentChange(e.target.value)}>
            <option value="">{t("notes.create.noAppointment")}</option>
            {(candidates.data ?? []).map((c) => (
              <option key={c.appointmentId} value={c.appointmentId}>
                {formatTimeWithDate(new Date(c.startsAt), new Date(), PRACTICE_ZONE)} · {t(`calendar.modality.${c.modality}`)}
              </option>
            ))}
          </select>
        </label>
      )}
    </section>
  );
}
