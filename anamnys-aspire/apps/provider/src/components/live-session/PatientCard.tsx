import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { CalendarClock, UserRound } from "lucide-react";
import { patientsApi } from "@anamnys/shared/api/patients";
import Avatar from "@anamnys/shared/ui/Avatar";
import PatientPicker from "@/components/calendar/PatientPicker";
import { PRACTICE_ZONE, formatTime, todayIn } from "@/components/calendar/calendarTime";
import { inputClass } from "@/components/patients/FormField";
import { ageFrom } from "@/components/patients/format";
import { useAppointmentsQuery } from "@/hooks/useAppointments";

export type PickedPatient = { id: string; name: string };

interface Props {
  patient: PickedPatient | null;
  onPatientChange: (patient: PickedPatient | null) => void;
  appointmentId: string;
  onAppointmentChange: (id: string) => void;
  locked: boolean;
}

// Who the note is for: search first, then the patient's header and today's appointments.
export default function PatientCard({ patient, onPatientChange, appointmentId, onAppointmentChange, locked }: Props) {
  const { t } = useTranslation();
  const detail = useQuery({
    queryKey: ["patient", patient?.id],
    queryFn: () => patientsApi.get(patient!.id),
    enabled: !!patient,
  });
  const today = useAppointmentsQuery([todayIn(PRACTICE_ZONE)]);
  const appointments = (today.data ?? []).filter(
    (a) => a.patientId === patient?.id && (a.status === "scheduled" || a.status === "confirmed"),
  );
  const age = ageFrom(detail.data?.dateOfBirth ?? null);

  return (
    <section className="bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm">
      {!patient ? (
        <div className="flex flex-col gap-2">
          <h2 className="flex items-center gap-2 text-title-md text-onSurface">
            <UserRound size={20} className="text-primary" aria-hidden />
            {t("liveSession.patient")}
          </h2>
          <PatientPicker value={null} onChange={onPatientChange} />
        </div>
      ) : (
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex items-center gap-4 min-w-0">
            <Avatar name={patient.name} size={56} />
            <div className="min-w-0">
              <p className="truncate text-headline-sm text-onSurface">{patient.name}</p>
              {age !== null && <p className="text-body-md text-onSurfaceVariant">{t("liveSession.age", { age })}</p>}
            </div>
          </div>
          {!locked && (
            <button
              type="button"
              onClick={() => {
                onPatientChange(null);
                onAppointmentChange("");
              }}
              className="self-start text-label-lg text-primary hover:underline sm:self-center"
            >
              {t("liveSession.changePatient")}
            </button>
          )}
        </div>
      )}
      {patient && (
        <label className="mt-4 flex flex-col gap-1 rounded-radii-lg bg-surfaceContainerLow p-3 text-label-md text-onSurfaceVariant">
          <span className="flex items-center gap-2">
            <CalendarClock size={16} className="text-primary" aria-hidden />
            {t("liveSession.appointment")}
          </span>
          <select
            className={inputClass}
            value={appointmentId}
            disabled={locked}
            onChange={(e) => onAppointmentChange(e.target.value)}
          >
            <option value="">{t("liveSession.noAppointment")}</option>
            {appointments.map((a) => (
              <option key={a.id} value={a.id}>
                {formatTime(new Date(a.startsAt), PRACTICE_ZONE)} · {t(`calendar.modality.${a.modality}`)}
              </option>
            ))}
          </select>
        </label>
      )}
    </section>
  );
}
