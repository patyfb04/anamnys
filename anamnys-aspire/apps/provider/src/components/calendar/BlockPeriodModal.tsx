import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type { AffectedAppointment, BlockKind, BlockRequest } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import FormField, { inputClass } from "@/components/patients/FormField";
import { useBlockMutations } from "@/hooks/useAvailability";
import { PRACTICE_ZONE, formatTime, timeToMinutes, todayIn, zonedParts, zonedToInstant } from "./calendarTime";

const KINDS: BlockKind[] = ["vacation", "block", "holiday"];
const MAX_REASON = 200;

interface Props {
  onClose: () => void;
}

type Step = { kind: "form" } | { kind: "affected"; appointments: AffectedAppointment[]; changed: boolean };

// "Bloquear período" (spec §5): the period, then the preview of affected appointments with an explicit choice. Nothing
// is cancelled unless the provider picks "Cancelar e avisar os pacientes"; the ids shown go back as
// expectedAppointmentIds, and a 409 `changed` shows the new list and asks again.
export default function BlockPeriodModal({ onClose }: Props) {
  const { t } = useTranslation();
  const { preview, create } = useBlockMutations();
  const today = todayIn(PRACTICE_ZONE);
  const [kind, setKind] = useState<BlockKind>("vacation");
  const [from, setFrom] = useState(today);
  const [to, setTo] = useState(today);
  const [allDay, setAllDay] = useState(true);
  const [startTime, setStartTime] = useState("08:00");
  const [endTime, setEndTime] = useState("18:00");
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [step, setStep] = useState<Step>({ kind: "form" });
  const pending = preview.isPending || create.isPending;

  // All-day blocks go as local midnights of the first and last dates (both included); timed ones as the exact instants.
  const request = (): BlockRequest => ({
    kind,
    allDay,
    startsAt: zonedToInstant(from, allDay ? 0 : timeToMinutes(startTime), PRACTICE_ZONE).toISOString(),
    endsAt: zonedToInstant(to, allDay ? 0 : timeToMinutes(endTime), PRACTICE_ZONE).toISOString(),
    reason: reason.trim() || undefined,
  });

  const showError = (err: unknown) => {
    if (err instanceof ApiError && err.status === 400) setError(err.message);
    else toast.error(t("blocks.saveFailed"));
  };

  const save = async (cancelAffected: boolean, shown: AffectedAppointment[]) => {
    setError(null);
    try {
      const result = await create.mutateAsync({
        ...request(),
        cancelAffected,
        expectedAppointmentIds: cancelAffected ? shown.map((a) => a.id) : undefined,
      });
      toast.success(
        result.cancelledAppointmentIds.length > 0
          ? t("blocks.savedCancelled", { count: result.cancelledAppointmentIds.length })
          : t("blocks.saved"),
      );
      onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && err.code === "changed") {
        const body = err.body as { appointments?: AffectedAppointment[] } | undefined;
        setStep({ kind: "affected", appointments: body?.appointments ?? [], changed: true });
        return;
      }
      showError(err);
    }
  };

  const continueToPreview = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    if (to < from || (!allDay && from === to && timeToMinutes(endTime) <= timeToMinutes(startTime))) {
      setError(t("blocks.invalidRange"));
      return;
    }
    try {
      const affected = await preview.mutateAsync(request());
      if (affected.length === 0) await save(false, []);
      else setStep({ kind: "affected", appointments: affected, changed: false });
    } catch (err) {
      showError(err);
    }
  };

  const cancelButton = (
    <button type="button" onClick={onClose} className="px-4 py-2 rounded-radii-lg text-label-lg text-onSurfaceVariant hover:bg-pastelLavender">
      {t("common.cancel")}
    </button>
  );

  if (step.kind === "affected") {
    const list = step.appointments;
    return (
      <Modal
        title={t("blocks.title")}
        onClose={onClose}
        footer={
          <>
            <button type="button" onClick={() => setStep({ kind: "form" })} className="px-4 py-2 rounded-radii-lg text-label-lg text-onSurfaceVariant hover:bg-pastelLavender">
              {t("blocks.back")}
            </button>
            {list.length === 0 ? (
              <button type="button" disabled={pending} onClick={() => save(false, [])} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60">
                {t("blocks.save")}
              </button>
            ) : (
              <>
                <button type="button" disabled={pending} onClick={() => save(false, list)} className="px-4 py-2 rounded-radii-lg border border-outlineVariant text-label-lg text-onSurface hover:bg-pastelLavender disabled:opacity-60">
                  {t("blocks.keep")}
                </button>
                <button type="button" disabled={pending} onClick={() => save(true, list)} className="px-4 py-2 rounded-radii-lg bg-error text-label-lg text-onError disabled:opacity-60">
                  {t("blocks.cancelAndNotify")}
                </button>
              </>
            )}
          </>
        }
      >
        <div className="flex flex-col gap-3 p-5">
          {step.changed && (
            <p role="alert" className="rounded-radii-md bg-errorContainer px-3 py-2 text-body-md text-onErrorContainer">
              {t("blocks.changed")}
            </p>
          )}
          {list.length === 0 ? (
            <p className="text-body-md text-onSurface">{t("blocks.noneAffectedNow")}</p>
          ) : (
            <>
              <p className="text-body-md text-onSurface">{t("blocks.affectedIntro", { count: list.length })}</p>
              <ul className="flex flex-col divide-y divide-outlineVariant rounded-radii-md border border-outlineVariant">
                {list.map((a) => {
                  const start = new Date(a.startsAt);
                  const date = zonedParts(start, PRACTICE_ZONE).date;
                  return (
                    <li key={a.id} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2 text-body-md">
                      <span className="text-onSurface">{a.patientName}</span>
                      <span className="text-onSurfaceVariant">
                        {new Intl.DateTimeFormat("pt-BR", { weekday: "short", day: "2-digit", month: "2-digit", year: "numeric", timeZone: "UTC" }).format(new Date(`${date}T12:00:00Z`))}{" "}
                        {formatTime(start, PRACTICE_ZONE)}–{formatTime(new Date(a.endsAt), PRACTICE_ZONE)}
                      </span>
                    </li>
                  );
                })}
              </ul>
              <p className="text-body-sm text-onSurfaceVariant">{t("blocks.affectedHint")}</p>
            </>
          )}
          {error && <p role="alert" className="text-label-md text-error">{error}</p>}
        </div>
      </Modal>
    );
  }

  return (
    <Modal
      title={t("blocks.title")}
      onClose={onClose}
      footer={
        <>
          {cancelButton}
          <button type="submit" form="block-form" disabled={pending} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60">
            {t("blocks.continue")}
          </button>
        </>
      }
    >
      <form id="block-form" onSubmit={continueToPreview} className="flex flex-col gap-4 p-5">
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("blocks.kindLabel")}
          <select className={inputClass} value={kind} onChange={(e) => setKind(e.target.value as BlockKind)}>
            {KINDS.map((k) => (
              <option key={k} value={k}>{t(`blocks.kind.${k}`)}</option>
            ))}
          </select>
        </label>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <FormField label={t("blocks.from")} type="date" required value={from}
            onChange={(e) => {
              setFrom(e.target.value);
              if (e.target.value > to) setTo(e.target.value);
            }} />
          <FormField label={t("blocks.to")} type="date" required min={from} value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
        <label className="flex items-center gap-2 text-body-md text-onSurface">
          <input type="checkbox" checked={allDay} onChange={(e) => setAllDay(e.target.checked)} />
          {t("blocks.allDay")}
        </label>
        {!allDay && (
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <FormField label={t("blocks.startTime")} type="time" required value={startTime} onChange={(e) => setStartTime(e.target.value)} />
            <FormField label={t("blocks.endTime")} type="time" required value={endTime} onChange={(e) => setEndTime(e.target.value)} />
          </div>
        )}
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("blocks.reason")}
          <textarea className={inputClass} rows={2} maxLength={MAX_REASON} value={reason} onChange={(e) => setReason(e.target.value)} />
          <span className="self-end text-label-sm">{reason.length}/{MAX_REASON}</span>
        </label>
        {!allDay && <p className="text-body-sm text-onSurfaceVariant">{t("blocks.timedHint")}</p>}
        {error && <p role="alert" className="text-label-md text-error">{error}</p>}
      </form>
    </Modal>
  );
}
