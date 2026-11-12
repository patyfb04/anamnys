import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { Repeat } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import type { Appointment, AppointmentStatus } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import { inputClass } from "@/components/patients/FormField";
import { useAppointmentMutations, useSeriesMutations, useSeriesQuery } from "@/hooks/useAppointments";
import { PRACTICE_ZONE, formatDate, formatTime, formatTimeWithDate, zonedParts } from "./calendarTime";
import { seriesErrorMessage } from "./series";
import { NEXT_STATUSES, STATUS_STYLE } from "./status";

const NOTE_WINDOW_MS = 14 * 86_400_000;

interface Props {
  appointment: Appointment;
  onClose: () => void;
  onEdit: () => void;
  // "Remarcar > Esta e as seguintes" of a series occurrence.
  onSplit: () => void;
}

// For a series occurrence, "Remarcar" and "Cancelar" first ask for the scope
// (design/specs/2026-11-09-appointment-series-design.md §5).
type Step = "idle" | "rescheduleScope" | "cancelScope" | "cancelling" | "ending";

const ACTION_LABEL: Record<AppointmentStatus, string> = {
  confirmed: "calendar.details.confirm",
  attended: "calendar.details.attended",
  no_show: "calendar.details.noShow",
  cancelled: "calendar.details.cancel",
  scheduled: "calendar.details.undo",
};

export default function AppointmentDetailsModal({ appointment: a, onClose, onEdit, onSplit }: Props) {
  const { t } = useTranslation();
  const { setStatus } = useAppointmentMutations();
  const { end } = useSeriesMutations();
  const seriesQuery = useSeriesQuery(a.seriesId);
  const [step, setStep] = useState<Step>("idle");
  const [reason, setReason] = useState("");
  const occurrenceDate = zonedParts(new Date(a.startsAt), PRACTICE_ZONE).date;

  const started = new Date(a.startsAt) <= new Date();
  const actions = NEXT_STATUSES[a.status].filter((s) => (s === "attended" || s === "no_show" ? started : true));
  const editable = a.status === "scheduled" || a.status === "confirmed";
  // Scope choices only while the session is still ahead and its series is running (the server answers 409 otherwise).
  const seriesScope = !!a.seriesId && editable && !started && seriesQuery.data?.active !== false;
  const day = new Intl.DateTimeFormat("pt-BR", { weekday: "long", day: "numeric", month: "long", timeZone: a.timezone })
    .format(new Date(a.startsAt));

  const change = async (status: AppointmentStatus) => {
    try {
      await setStatus.mutateAsync({ id: a.id, status, reason: status === "cancelled" ? reason.trim() : undefined });
      toast.success(t("calendar.details.statusChanged"));
      onClose();
    } catch (err) {
      toast.error(err instanceof ApiError && err.status === 409 ? err.message : t("calendar.details.statusFailed"));
    }
  };

  // "Encerrar a série a partir desta": cancels this and the following sessions, earlier ones stay.
  const endSeries = async () => {
    try {
      const cancelled = await end.mutateAsync({ id: a.seriesId!, fromDate: occurrenceDate });
      toast.success(t("series.end.done", { count: cancelled }));
      onClose();
    } catch (err) {
      toast.error(seriesErrorMessage(t, err, t("series.end.failed")));
    }
  };

  const seriesLine = () => {
    const description = a.seriesDescription ?? seriesQuery.data?.description ?? "";
    const total = seriesQuery.data?.occurrenceCount;
    if (a.seriesIndex == null) return t("series.partOfPlain", { description });
    return total
      ? t("series.partOf", { description, index: a.seriesIndex, total })
      : t("series.partOfOpen", { description, index: a.seriesIndex });
  };

  const button = "px-3 py-2 rounded-radii-lg border border-outlineVariant text-label-lg text-onSurface hover:bg-pastelLavender disabled:opacity-60";

  return (
    <Modal title={a.patientName} onClose={onClose}>
      <div className="flex flex-col gap-4 p-5">
        <div className="flex flex-col gap-1 text-body-md text-onSurface">
          <span className="capitalize">{day}</span>
          <span>{formatTime(new Date(a.startsAt), a.timezone)}–{formatTime(new Date(a.endsAt), a.timezone)} · {t(`calendar.modality.${a.modality}`)}</span>
          <span className={`self-start rounded-radii-full border-l-4 px-3 py-0.5 text-label-md ${STATUS_STYLE[a.status]}`}>
            {t(`calendar.status.${a.status}`)}
          </span>
          {a.confirmationDeadlineAt && a.status === "scheduled" && (
            <span className="text-onSurfaceVariant">
              {t("calendar.details.awaitingConfirmation", { time: formatTimeWithDate(new Date(a.confirmationDeadlineAt), new Date(a.startsAt), a.timezone) })}
            </span>
          )}
          {!a.patientHasEmail && (a.status === "scheduled" || a.status === "confirmed") && (
            <span className="text-onSurfaceVariant">{t("calendar.details.noEmail")}</span>
          )}
          {a.seriesId && (
            <span className="flex items-start gap-1 text-onSurfaceVariant">
              <Repeat size={14} className="mt-1 shrink-0" aria-hidden />
              <span>
                {seriesLine()}
                {a.detached && <> {t("series.partOfDetached")}</>}
              </span>
            </span>
          )}
          {a.cancellationReason && (
            <span className="text-onSurfaceVariant">{t("calendar.details.reason")}: {a.cancellationReason}</span>
          )}
          <Link to="/patients/$patientId" params={{ patientId: a.patientId }} className="self-start text-label-lg text-primary">
            {t("calendar.details.openRecord")}
          </Link>
        </div>

        {step === "rescheduleScope" || step === "cancelScope" ? (
          <div role="group" aria-label={t(step === "rescheduleScope" ? "series.scope.rescheduleTitle" : "series.scope.cancelTitle")}
            className="flex flex-col gap-2">
            <span className="text-label-lg text-onSurface">
              {t(step === "rescheduleScope" ? "series.scope.rescheduleTitle" : "series.scope.cancelTitle")}
            </span>
            <div className="flex flex-wrap gap-2">
              <button type="button" className={button} onClick={() => (step === "rescheduleScope" ? onEdit() : setStep("cancelling"))}>
                {t("series.scope.onlyThis")}
              </button>
              <button type="button" className={button} onClick={() => (step === "rescheduleScope" ? onSplit() : setStep("ending"))}>
                {t(step === "rescheduleScope" ? "series.scope.thisAndFollowing" : "series.scope.endFromHere")}
              </button>
              <button type="button" className={button} onClick={() => setStep("idle")}>{t("common.cancel")}</button>
            </div>
          </div>
        ) : step === "ending" ? (
          <div role="alert" className="flex flex-col gap-2">
            <span className="text-label-lg text-onSurface">{t("series.end.title")}</span>
            <p className="text-body-md text-onSurfaceVariant">{t("series.end.body", { date: formatDate(occurrenceDate) })}</p>
            <div className="flex gap-2">
              <button type="button" className={button} onClick={() => setStep("idle")}>{t("common.cancel")}</button>
              <button type="button" disabled={end.isPending} onClick={() => void endSeries()}
                className="px-3 py-2 rounded-radii-lg bg-error text-label-lg text-onError disabled:opacity-60">
                {t("series.end.confirm")}
              </button>
            </div>
          </div>
        ) : step === "cancelling" ? (
          <div className="flex flex-col gap-2">
            <textarea className={inputClass} maxLength={500} rows={3} value={reason}
              placeholder={t("calendar.details.reasonPlaceholder")} onChange={(e) => setReason(e.target.value)} />
            <div className="flex gap-2">
              <button type="button" className={button} onClick={() => setStep("idle")}>{t("common.cancel")}</button>
              <button type="button" disabled={setStatus.isPending} onClick={() => change("cancelled")}
                className="px-3 py-2 rounded-radii-lg bg-error text-label-lg text-onError disabled:opacity-60">
                {t("calendar.details.cancelConfirm")}
              </button>
            </div>
          </div>
        ) : (
          <div className="flex flex-wrap gap-2">
            {editable && (
              <button type="button" className={button} onClick={() => (seriesScope ? setStep("rescheduleScope") : onEdit())}>
                {t(seriesScope ? "series.scope.rescheduleTitle" : "calendar.details.edit")}
              </button>
            )}
            {actions.map((s) => (
              <button key={s} type="button" disabled={setStatus.isPending} className={button}
                onClick={() => (s === "cancelled" ? setStep(seriesScope ? "cancelScope" : "cancelling") : change(s))}>
                {t(ACTION_LABEL[s])}
              </button>
            ))}
            {started && a.status !== "cancelled" && a.status !== "no_show" && !a.hasNote
              && Date.now() - Date.parse(a.startsAt) <= NOTE_WINDOW_MS && (
              <Link
                to="/note-editor/new"
                search={{ patientId: a.patientId, appointmentId: a.id }}
                className={button}
                onClick={onClose}
              >
                {t("calendar.details.writeNote")}
              </Link>
            )}
          </div>
        )}
      </div>
    </Modal>
  );
}
