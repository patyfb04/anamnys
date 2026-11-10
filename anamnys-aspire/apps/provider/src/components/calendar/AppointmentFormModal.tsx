import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type { Appointment, AppointmentModality, OutsideAvailabilityReason } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import FormField, { inputClass } from "@/components/patients/FormField";
import { useAppointmentMutations } from "@/hooks/useAppointments";
import { PRACTICE_ZONE, zonedParts, zonedToInstant } from "./calendarTime";
import PatientPicker from "./PatientPicker";
import { describeReason } from "./outsideReasons";

export type FormMode =
  | { kind: "create"; date: string; minutes: number; patient?: { id: string; name: string } }
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
    return { patient: mode.patient ?? null, date: mode.date, time: toTime(mode.minutes), duration: 50, modality: "online" as AppointmentModality };
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
  // Reasons of a 409 outside_availability; while shown, "Marcar mesmo assim" resends with the acknowledgement.
  const [reasons, setReasons] = useState<OutsideAvailabilityReason[] | null>(null);
  const pending = create.isPending || update.isPending;
  // Any change to the slot invalidates the reasons shown for the previous one.
  const edit = (change: (s: ReturnType<typeof initialState>) => ReturnType<typeof initialState>) => {
    setState(change);
    setReasons(null);
  };

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    void save(false);
  };

  const save = async (acknowledgeOutsideAvailability: boolean) => {
    setErrors({});
    const slot = {
      startsAt: zonedToInstant(state.date, fromTime(state.time), PRACTICE_ZONE).toISOString(),
      durationMinutes: Number(state.duration),
      modality: state.modality,
      acknowledgeOutsideAvailability,
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
      if (err instanceof ApiError && err.status === 409 && err.code === "outside_availability") {
        const body = err.body as { reasons?: OutsideAvailabilityReason[] } | undefined;
        setReasons(body?.reasons?.length ? body.reasons : [{ kind: "outside_hours" }]);
      } else if (err instanceof ApiError && err.status === 400 && err.errors) setErrors(err.errors);
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
          <button type="button" onClick={onClose} className="px-4 py-2 rounded-radii-lg text-label-lg text-onSurfaceVariant hover:bg-pastelLavender">
            {t("common.cancel")}
          </button>
          {reasons ? (
            <button type="button" onClick={() => void save(true)} disabled={pending} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60">
              {t("availability.bookAnyway")}
            </button>
          ) : (
            <button type="submit" form="appointment-form" disabled={pending} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60">
              {t("calendar.form.save")}
            </button>
          )}
        </>
      }
    >
      <form id="appointment-form" onSubmit={submit} className="flex flex-col gap-4 p-5">
        {reasons && (
          <div role="alert" className="flex flex-col gap-1 rounded-radii-md bg-secondaryContainer border border-secondary px-3 py-2 text-body-md text-onSecondaryContainer">
            <span className="text-label-lg">{t("availability.outsideTitle")}</span>
            <ul className="list-disc pl-5">
              {reasons.map((r, i) => (
                <li key={i}>{describeReason(t, r)}</li>
              ))}
            </ul>
            <span className="text-body-sm text-onSurfaceVariant">{t("availability.outsideHint")}</span>
          </div>
        )}
        <div className="flex flex-col gap-1">
          <span className="text-label-md text-onSurfaceVariant">{t("calendar.form.patient")}</span>
          {mode.kind === "create" ? (
            <PatientPicker value={state.patient} onChange={(patient) => edit((s) => ({ ...s, patient }))} error={errors.patientId} />
          ) : (
            <span className="text-body-md text-onSurface">{state.patient?.name}</span>
          )}
        </div>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <FormField label={t("calendar.form.date")} type="date" required value={state.date}
            onChange={(e) => edit((s) => ({ ...s, date: e.target.value }))} error={errors.startsAt} />
          <FormField label={t("calendar.form.time")} type="time" step={60} required value={state.time}
            onChange={(e) => edit((s) => ({ ...s, time: e.target.value }))} />
          <FormField label={t("calendar.form.duration")} type="number" min={5} max={480} required value={state.duration}
            onChange={(e) => edit((s) => ({ ...s, duration: Number(e.target.value) }))} error={errors.durationMinutes} />
        </div>
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("calendar.form.modality")}
          <select className={inputClass} value={state.modality}
            onChange={(e) => edit((s) => ({ ...s, modality: e.target.value as AppointmentModality }))}>
            <option value="online">{t("calendar.modality.online")}</option>
            <option value="presencial">{t("calendar.modality.presencial")}</option>
          </select>
        </label>
      </form>
    </Modal>
  );
}
