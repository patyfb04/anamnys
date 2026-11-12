import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type { Appointment, AppointmentModality, OutsideAvailabilityReason, SkippedOccurrence } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import FormField, { inputClass } from "@/components/patients/FormField";
import { useAppointmentMutations, useSeriesMutations } from "@/hooks/useAppointments";
import { PRACTICE_ZONE, formatDate, zonedParts, zonedToInstant } from "./calendarTime";
import PatientPicker from "./PatientPicker";
import { describeReason } from "./outsideReasons";
import { patternText, seriesErrorMessage, skippedDates, weekdayOf, type Repeat, type SeriesEnd } from "./series";

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

// Repeat fields only apply to a new appointment (design/specs/2026-11-09-appointment-series-design.md §5).
const repeatDefaults = { repeat: "none" as Repeat, end: "never" as SeriesEnd, endDate: "", count: 10 };

function initialState(mode: FormMode) {
  if (mode.kind === "create") {
    return { patient: mode.patient ?? null, date: mode.date, time: toTime(mode.minutes), duration: 50, modality: "online" as AppointmentModality, ...repeatDefaults };
  }
  const a = mode.appointment;
  const start = zonedParts(new Date(a.startsAt), PRACTICE_ZONE);
  const duration = (new Date(a.endsAt).getTime() - new Date(a.startsAt).getTime()) / 60000;
  return { patient: { id: a.patientId, name: a.patientName }, date: start.date, time: toTime(start.minutes), duration, modality: a.modality, ...repeatDefaults };
}

export default function AppointmentFormModal({ mode, onClose }: Props) {
  const { t } = useTranslation();
  const { create, update } = useAppointmentMutations();
  const series = useSeriesMutations();
  const [state, setState] = useState(() => initialState(mode));
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  // Reasons of a 409 outside_availability; while shown, "Marcar mesmo assim" resends with the acknowledgement.
  const [reasons, setReasons] = useState<OutsideAvailabilityReason[] | null>(null);
  // Series occurrences not created because their slot was taken; shown after creation instead of closing.
  const [skipped, setSkipped] = useState<SkippedOccurrence[] | null>(null);
  const pending = create.isPending || update.isPending || series.create.isPending;
  const repeating = mode.kind === "create" && state.repeat !== "none";
  // Any change to the slot invalidates the reasons shown for the previous one.
  const edit = (change: (s: ReturnType<typeof initialState>) => ReturnType<typeof initialState>) => {
    setState(change);
    setReasons(null);
  };

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    void save(false);
  };

  const summary = () => {
    const interval = state.repeat === "biweekly" ? 2 : 1;
    const pattern = patternText(t, interval, weekdayOf(state.date), state.time);
    const start = formatDate(state.date);
    if (state.end === "onDate" && state.endDate) return t("series.summaryUntil", { pattern, start, end: formatDate(state.endDate) });
    if (state.end === "afterCount" && state.count >= 1) return t("series.summaryCount", { pattern, start, count: state.count });
    return t("series.summaryNever", { pattern, start });
  };

  const saveSeries = async (patientId: string, startsAt: string, acknowledgeOutsideAvailability: boolean) => {
    if (state.end === "onDate" && !state.endDate) {
      setErrors({ endsOn: [t("series.endDateRequired")] });
      return;
    }
    if (state.end === "afterCount" && !(Number.isInteger(state.count) && state.count >= 1 && state.count <= 520)) {
      setErrors({ occurrenceCount: [t("series.countRequired")] });
      return;
    }
    const result = await series.create.mutateAsync({
      patientId,
      startsAt,
      durationMinutes: Number(state.duration),
      modality: state.modality,
      interval: state.repeat === "biweekly" ? 2 : 1,
      endsOn: state.end === "onDate" ? state.endDate : undefined,
      occurrenceCount: state.end === "afterCount" ? state.count : undefined,
      acknowledgeOutsideAvailability,
    });
    toast.success(t("series.created", { count: result.generated }));
    if (result.skipped.length > 0) setSkipped(result.skipped);
    else onClose();
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
        if (repeating) {
          await saveSeries(state.patient.id, slot.startsAt, acknowledgeOutsideAvailability);
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
      else if (repeating) toast.error(seriesErrorMessage(t, err, t("calendar.form.saveFailed")));
      else if (err instanceof ApiError && (err.status === 409 || err.status === 422)) toast.error(err.message);
      else toast.error(t("calendar.form.saveFailed"));
    }
  };

  const closeButton = "px-4 py-2 rounded-radii-lg text-label-lg text-onSurfaceVariant hover:bg-pastelLavender";

  if (skipped) {
    return (
      <Modal
        title={t("series.skippedTitle")}
        onClose={onClose}
        footer={
          <button type="button" onClick={onClose} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary">
            {t("series.close")}
          </button>
        }
      >
        <p role="status" className="p-5 text-body-md text-onSurface">
          {t("series.skipped", { dates: skippedDates(skipped) })}
        </p>
      </Modal>
    );
  }

  return (
    <Modal
      title={t(mode.kind === "create" ? "calendar.form.createTitle" : "calendar.form.editTitle")}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose} className={closeButton}>
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
        {mode.kind === "create" && (
          <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
            {t("series.repeat")}
            <select className={inputClass} value={state.repeat}
              onChange={(e) => edit((s) => ({ ...s, repeat: e.target.value as Repeat }))}>
              <option value="none">{t("series.repeatOptions.none")}</option>
              <option value="weekly">{t("series.repeatOptions.weekly")}</option>
              <option value="biweekly">{t("series.repeatOptions.biweekly")}</option>
            </select>
          </label>
        )}
        {repeating && (
          <fieldset className="flex flex-col gap-2">
            <legend className="mb-1 text-label-md text-onSurfaceVariant">{t("series.ends")}</legend>
            <label className="flex items-center gap-2 text-body-md text-onSurface">
              <input type="radio" name="series-end" checked={state.end === "never"}
                onChange={() => edit((s) => ({ ...s, end: "never" }))} />
              {t("series.endOptions.never")}
            </label>
            <div className="flex flex-wrap items-center gap-2 text-body-md text-onSurface">
              <label className="flex items-center gap-2">
                <input type="radio" name="series-end" checked={state.end === "onDate"}
                  onChange={() => edit((s) => ({ ...s, end: "onDate" }))} />
                {t("series.endOptions.onDate")}
              </label>
              <input type="date" aria-label={t("series.endDate")} className={`${inputClass} w-auto`} min={state.date}
                value={state.endDate} aria-invalid={errors.endsOn ? true : undefined}
                onChange={(e) => edit((s) => ({ ...s, end: "onDate", endDate: e.target.value }))} />
            </div>
            {errors.endsOn && <span className="text-label-md text-error">{errors.endsOn[0]}</span>}
            <div className="flex flex-wrap items-center gap-2 text-body-md text-onSurface">
              <label className="flex items-center gap-2">
                <input type="radio" name="series-end" checked={state.end === "afterCount"}
                  onChange={() => edit((s) => ({ ...s, end: "afterCount" }))} />
                {t("series.endOptions.afterCount")}
              </label>
              <input type="number" aria-label={t("series.count")} className={`${inputClass} w-24`} min={1} max={520}
                value={state.count} aria-invalid={errors.occurrenceCount ? true : undefined}
                onChange={(e) => edit((s) => ({ ...s, end: "afterCount", count: Number(e.target.value) }))} />
              <span>{t("series.sessions")}</span>
            </div>
            {errors.occurrenceCount && <span className="text-label-md text-error">{errors.occurrenceCount[0]}</span>}
            {state.date && state.time && (
              <p className="mt-1 text-body-md text-onSurfaceVariant">{summary()}</p>
            )}
          </fieldset>
        )}
      </form>
    </Modal>
  );
}
