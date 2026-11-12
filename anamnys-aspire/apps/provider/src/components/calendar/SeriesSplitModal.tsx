import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type {
  Appointment,
  AppointmentModality,
  OutsideAvailabilityReason,
  SkippedOccurrence,
  SplitPreview,
  SplitSeriesRequest,
} from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import FormField, { inputClass } from "@/components/patients/FormField";
import { useSeriesMutations } from "@/hooks/useAppointments";
import { PRACTICE_ZONE, formatDate, minutesToTime, zonedParts } from "./calendarTime";
import { describeReason } from "./outsideReasons";
import { seriesErrorMessage, skippedDates, weekdayOf } from "./series";

interface Props {
  appointment: Appointment; // the occurrence k the new pattern starts from
  onClose: () => void;
}

const whenFormat = new Intl.DateTimeFormat("pt-BR", {
  weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit", timeZone: PRACTICE_ZONE,
});

// "Remarcar > Esta e as seguintes": a new weekday/time from this occurrence on. The preview lists the sessions that keep
// their time (confirmed or changed one by one) before anything is saved; the split itself answers with the skipped dates.
// See design/specs/2026-11-09-appointment-series-design.md §3 Split and §5.
export default function SeriesSplitModal({ appointment: a, onClose }: Props) {
  const { t } = useTranslation();
  const { splitPreview, split } = useSeriesMutations();
  const start = zonedParts(new Date(a.startsAt), PRACTICE_ZONE);
  const [state, setState] = useState({
    weekday: weekdayOf(start.date),
    time: minutesToTime(start.minutes),
    duration: (new Date(a.endsAt).getTime() - new Date(a.startsAt).getTime()) / 60000,
    modality: a.modality,
  });
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [preview, setPreview] = useState<SplitPreview | null>(null);
  const [reasons, setReasons] = useState<OutsideAvailabilityReason[] | null>(null);
  const [skipped, setSkipped] = useState<SkippedOccurrence[] | null>(null);
  const pending = splitPreview.isPending || split.isPending;

  // Any change makes the preview and the availability reasons stale.
  const edit = (change: Partial<typeof state>) => {
    setState((s) => ({ ...s, ...change }));
    setPreview(null);
    setReasons(null);
  };

  const request = (acknowledgeOutsideAvailability: boolean): SplitSeriesRequest => ({
    fromAppointmentId: a.id,
    weekday: state.weekday,
    startTime: state.time,
    durationMinutes: Number(state.duration),
    modality: state.modality,
    acknowledgeOutsideAvailability,
  });

  const fail = (err: unknown) => {
    if (err instanceof ApiError && err.status === 400 && err.errors) setErrors(err.errors);
    else toast.error(seriesErrorMessage(t, err, t("series.split.failed")));
  };

  const loadPreview = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrors({});
    try {
      setPreview(await splitPreview.mutateAsync({ id: a.seriesId!, request: request(false) }));
    } catch (err) {
      fail(err);
    }
  };

  const save = async (acknowledgeOutsideAvailability: boolean) => {
    setErrors({});
    try {
      const result = await split.mutateAsync({ id: a.seriesId!, request: request(acknowledgeOutsideAvailability) });
      toast.success(t("series.split.done"));
      if (result.skipped.length > 0) setSkipped(result.skipped);
      else onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && err.code === "outside_availability") {
        const body = err.body as { reasons?: OutsideAvailabilityReason[] } | undefined;
        setReasons(body?.reasons?.length ? body.reasons : [{ kind: "outside_hours" }]);
      } else fail(err);
    }
  };

  const primary = "px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60";

  if (skipped) {
    return (
      <Modal title={t("series.skippedTitle")} onClose={onClose}
        footer={<button type="button" onClick={onClose} className={primary}>{t("series.close")}</button>}>
        <p role="status" className="p-5 text-body-md text-onSurface">{t("series.skipped", { dates: skippedDates(skipped) })}</p>
      </Modal>
    );
  }

  return (
    <Modal
      title={t("series.split.title")}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose} className="px-4 py-2 rounded-radii-lg text-label-lg text-onSurfaceVariant hover:bg-pastelLavender">
            {t("common.cancel")}
          </button>
          {!preview ? (
            <button type="submit" form="series-split-form" disabled={pending} className={primary}>{t("series.split.preview")}</button>
          ) : reasons ? (
            <button type="button" onClick={() => void save(true)} disabled={pending} className={primary}>{t("availability.bookAnyway")}</button>
          ) : (
            <button type="button" onClick={() => void save(false)} disabled={pending} className={primary}>{t("series.split.save")}</button>
          )}
        </>
      }
    >
      <form id="series-split-form" onSubmit={loadPreview} className="flex flex-col gap-4 p-5">
        <p className="text-body-md text-onSurfaceVariant">{t("series.split.hint", { date: formatDate(start.date) })}</p>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
            {t("series.weekdayLabel")}
            <select className={inputClass} value={state.weekday} onChange={(e) => edit({ weekday: Number(e.target.value) })}>
              {[1, 2, 3, 4, 5, 6, 0].map((d) => (
                <option key={d} value={d}>{t(`series.weekday.${d}`)}</option>
              ))}
            </select>
          </label>
          <FormField label={t("calendar.form.time")} type="time" step={60} required value={state.time}
            onChange={(e) => edit({ time: e.target.value })} error={errors.startTime} />
          <FormField label={t("calendar.form.duration")} type="number" min={5} max={480} required value={state.duration}
            onChange={(e) => edit({ duration: Number(e.target.value) })} error={errors.durationMinutes} />
        </div>
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("calendar.form.modality")}
          <select className={inputClass} value={state.modality}
            onChange={(e) => edit({ modality: e.target.value as AppointmentModality })}>
            <option value="online">{t("calendar.modality.online")}</option>
            <option value="presencial">{t("calendar.modality.presencial")}</option>
          </select>
        </label>

        {preview && (
          <div role="status" className="flex flex-col gap-2 rounded-radii-md border border-outlineVariant px-3 py-2 text-body-md text-onSurface">
            <span>{t("series.split.newFirst", { when: whenFormat.format(new Date(preview.newFirstStartsAt)) })}</span>
            <span>{t("series.split.moving", { count: preview.moving })}</span>
            {preview.staying.length === 0 ? (
              <span className="text-onSurfaceVariant">{t("series.split.stayingNone")}</span>
            ) : (
              <>
                <span className="text-label-lg">{t("series.split.stayingTitle")}</span>
                <ul className="list-disc pl-5">
                  {preview.staying.map((s) => (
                    <li key={s.id}>
                      {whenFormat.format(new Date(s.startsAt))} ({t(`series.split.reason.${s.reason}`)})
                    </li>
                  ))}
                </ul>
              </>
            )}
          </div>
        )}

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
      </form>
    </Modal>
  );
}
