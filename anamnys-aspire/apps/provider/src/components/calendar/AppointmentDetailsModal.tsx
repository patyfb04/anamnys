import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type { Appointment, AppointmentStatus } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import { inputClass } from "@/components/patients/FormField";
import { useAppointmentMutations } from "@/hooks/useAppointments";
import { formatTime, formatTimeWithDate } from "./calendarTime";
import { NEXT_STATUSES, STATUS_STYLE } from "./status";

interface Props {
  appointment: Appointment;
  onClose: () => void;
  onEdit: () => void;
}

const ACTION_LABEL: Record<AppointmentStatus, string> = {
  confirmed: "calendar.details.confirm",
  attended: "calendar.details.attended",
  no_show: "calendar.details.noShow",
  cancelled: "calendar.details.cancel",
  scheduled: "calendar.details.undo",
};

export default function AppointmentDetailsModal({ appointment: a, onClose, onEdit }: Props) {
  const { t } = useTranslation();
  const { setStatus } = useAppointmentMutations();
  const [cancelling, setCancelling] = useState(false);
  const [reason, setReason] = useState("");

  const started = new Date(a.startsAt) <= new Date();
  const actions = NEXT_STATUSES[a.status].filter((s) => (s === "attended" || s === "no_show" ? started : true));
  const editable = a.status === "scheduled" || a.status === "confirmed";
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

  const button = "px-3 py-2 rounded-radii-lg border border-outlineVariant text-label-lg text-onSurface hover:bg-surfaceContainer disabled:opacity-60";

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
          {a.cancellationReason && (
            <span className="text-onSurfaceVariant">{t("calendar.details.reason")}: {a.cancellationReason}</span>
          )}
          <Link to="/patients/$patientId" params={{ patientId: a.patientId }} className="self-start text-label-lg text-primary">
            {t("calendar.details.openRecord")}
          </Link>
        </div>

        {cancelling ? (
          <div className="flex flex-col gap-2">
            <textarea className={inputClass} maxLength={500} rows={3} value={reason}
              placeholder={t("calendar.details.reasonPlaceholder")} onChange={(e) => setReason(e.target.value)} />
            <div className="flex gap-2">
              <button type="button" className={button} onClick={() => setCancelling(false)}>{t("common.cancel")}</button>
              <button type="button" disabled={setStatus.isPending} onClick={() => change("cancelled")}
                className="px-3 py-2 rounded-radii-lg bg-error text-label-lg text-onError disabled:opacity-60">
                {t("calendar.details.cancelConfirm")}
              </button>
            </div>
          </div>
        ) : (
          <div className="flex flex-wrap gap-2">
            {editable && <button type="button" className={button} onClick={onEdit}>{t("calendar.details.edit")}</button>}
            {actions.map((s) => (
              <button key={s} type="button" disabled={setStatus.isPending} className={button}
                onClick={() => (s === "cancelled" ? setCancelling(true) : change(s))}>
                {t(ACTION_LABEL[s])}
              </button>
            ))}
          </div>
        )}
      </div>
    </Modal>
  );
}
