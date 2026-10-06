import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type { Appointment, AppointmentModality } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import FormField, { inputClass } from "@/components/patients/FormField";
import { useAppointmentMutations } from "@/hooks/useAppointments";
import { PRACTICE_ZONE, zonedParts, zonedToInstant } from "./calendarTime";
import PatientPicker from "./PatientPicker";

export type FormMode =
  | { kind: "create"; date: string; minutes: number }
  | { kind: "edit"; appointment: Appointment };

interface Props {
  mode: FormMode;
  onClose: () => void;
}

const toTime = (minutes: number) => `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;
const fromTime = (time: string) => {
  const [h, m] = time.split(":").map(Number);
  return h * 60 + m;
};

function initialState(mode: FormMode) {
  if (mode.kind === "create") {
    return { patient: null, date: mode.date, time: toTime(mode.minutes), duration: 50, modality: "online" as AppointmentModality };
  }
  const a = mode.appointment;
  const start = zonedParts(new Date(a.startsAt), PRACTICE_ZONE);
  const duration = (new Date(a.endsAt).getTime() - new Date(a.startsAt).getTime()) / 60000;
  return { patient: { id: a.patientId, name: a.patientName }, date: start.date, time: toTime(start.minutes), duration, modality: a.modality };
}

export default function AppointmentFormModal({ mode, onClose }: Props) {
  const { t } = useTranslation();
  const { create, update } = useAppointmentMutations();
  const [state, setState] = useState(() => initialState(mode));
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const pending = create.isPending || update.isPending;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrors({});
    const slot = {
      startsAt: zonedToInstant(state.date, fromTime(state.time), PRACTICE_ZONE).toISOString(),
      durationMinutes: Number(state.duration),
      modality: state.modality,
    };
    try {
      if (mode.kind === "create") {
        if (!state.patient) {
          setErrors({ patientId: [t("calendar.form.patientRequired")] });
          return;
        }
        await create.mutateAsync({ ...slot, patientId: state.patient.id });
        toast.success(t("calendar.form.created"));
      } else {
        await update.mutateAsync({ id: mode.appointment.id, request: slot });
        toast.success(t("calendar.form.updated"));
      }
      onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 400 && err.errors) setErrors(err.errors);
      else if (err instanceof ApiError && (err.status === 409 || err.status === 422)) toast.error(err.message);
      else toast.error(t("calendar.form.saveFailed"));
    }
  };

  return (
    <Modal
      title={t(mode.kind === "create" ? "calendar.form.createTitle" : "calendar.form.editTitle")}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose} className="px-4 py-2 rounded-radii-lg text-label-lg text-onSurfaceVariant hover:bg-surfaceContainer">
            {t("common.cancel")}
          </button>
          <button type="submit" form="appointment-form" disabled={pending} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60">
            {t("calendar.form.save")}
          </button>
        </>
      }
    >
      <form id="appointment-form" onSubmit={submit} className="flex flex-col gap-4 p-5">
        <div className="flex flex-col gap-1">
          <span className="text-label-md text-onSurfaceVariant">{t("calendar.form.patient")}</span>
          {mode.kind === "create" ? (
            <PatientPicker value={state.patient} onChange={(patient) => setState((s) => ({ ...s, patient }))} error={errors.patientId} />
          ) : (
            <span className="text-body-md text-onSurface">{state.patient?.name}</span>
          )}
        </div>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <FormField label={t("calendar.form.date")} type="date" required value={state.date}
            onChange={(e) => setState((s) => ({ ...s, date: e.target.value }))} error={errors.startsAt} />
          <FormField label={t("calendar.form.time")} type="time" step={900} required value={state.time}
            onChange={(e) => setState((s) => ({ ...s, time: e.target.value }))} />
          <FormField label={t("calendar.form.duration")} type="number" min={5} max={480} required value={state.duration}
            onChange={(e) => setState((s) => ({ ...s, duration: Number(e.target.value) }))} error={errors.durationMinutes} />
        </div>
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("calendar.form.modality")}
          <select className={inputClass} value={state.modality}
            onChange={(e) => setState((s) => ({ ...s, modality: e.target.value as AppointmentModality }))}>
            <option value="online">{t("calendar.modality.online")}</option>
            <option value="presencial">{t("calendar.modality.presencial")}</option>
          </select>
        </label>
      </form>
    </Modal>
  );
}
