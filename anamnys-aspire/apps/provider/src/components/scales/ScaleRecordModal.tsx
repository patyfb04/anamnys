import { useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import { scalesApi } from "@anamnys/shared/api/scales";
import { toast } from "@anamnys/shared/ui/Toaster";
import type { InstrumentItem } from "@anamnys/shared/lib/types";
import Modal from "@/components/patients/Modal";
import FormField, { inputClass } from "@/components/patients/FormField";
import { PRACTICE_ZONE, todayIn, zonedToInstant } from "@/components/calendar/calendarTime";

interface Props {
  patientId: string;
  sessionId?: string;
  onClose: () => void;
  onRecorded?: () => void;
}

const buttonClass =
  "inline-flex items-center justify-center gap-2 rounded-radii-full px-4 py-2 text-label-lg disabled:opacity-50";

// Records one score for a patient. Unavailable instruments show only their explanation; opt-in
// instruments need a one-time confirmation first (design/specs/2026-10-18-psychometric-scales-design.md §6).
export default function ScaleRecordModal({ patientId, sessionId, onClose, onRecorded }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const instruments = useQuery({ queryKey: ["instruments"], queryFn: scalesApi.instruments });
  const [instrumentId, setInstrumentId] = useState("");
  const [score, setScore] = useState("");
  const [date, setDate] = useState(() => todayIn(PRACTICE_ZONE));
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [formError, setFormError] = useState<string | null>(null);

  const all = instruments.data ?? [];
  const selected = all.find((i) => i.id === instrumentId);
  const groups: { key: string; label: string; items: InstrumentItem[] }[] = [
    { key: "available", label: t("scales.groups.available"), items: all.filter((i) => i.regime === "available") },
    { key: "needsOptIn", label: t("scales.groups.needsOptIn"), items: all.filter((i) => i.regime === "needs_opt_in" || i.regime === "opted_in") },
    { key: "unavailable", label: t("scales.groups.unavailable"), items: all.filter((i) => i.regime === "unavailable") },
  ];

  const optIn = useMutation({
    mutationFn: (id: string) => scalesApi.optIn(id),
    onSuccess: () => {
      setFormError(null);
      return queryClient.invalidateQueries({ queryKey: ["instruments"] });
    },
    onError: (e) => setFormError(e instanceof ApiError ? e.message : t("scales.optInFailed")),
  });

  const record = useMutation({
    mutationFn: () =>
      scalesApi.record(patientId, {
        instrumentId,
        score: Number(score),
        // Noon of today is still in the future in the morning and the server rejects future
        // dates, so today is sent as "now"; past days use noon practice time.
        appliedAt:
          date === todayIn(PRACTICE_ZONE)
            ? new Date().toISOString()
            : zonedToInstant(date, 12 * 60, PRACTICE_ZONE).toISOString(),
        sessionId: sessionId ?? null,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["scales", patientId] });
      toast.success(t("scales.recorded"));
      onRecorded?.();
      onClose();
    },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 400 && e.errors) {
        setFieldErrors(e.errors);
        if (!["instrumentId", "score", "appliedAt"].some((k) => e.errors?.[k])) setFormError(e.message);
        return;
      }
      setFormError(e instanceof ApiError ? e.message : t("scales.recordFailed"));
    },
  });

  const canRecord = selected?.regime === "available" || selected?.regime === "opted_in";

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setFieldErrors({});
    setFormError(null);
    record.mutate();
  };

  return (
    <Modal
      title={t("scales.record")}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose} className={`${buttonClass} text-onSurfaceVariant hover:bg-pastelLavender`}>
            {t("common.cancel")}
          </button>
          {canRecord && (
            <button type="submit" form="scale-record-form" disabled={record.isPending || score === "" || !date} className={`${buttonClass} bg-primary text-onPrimary`}>
              {record.isPending && <Loader2 size={16} className="animate-spin" />}
              {t("scales.record")}
            </button>
          )}
        </>
      }
    >
      <form id="scale-record-form" onSubmit={submit} className="flex flex-col gap-4 p-5">
        {instruments.isError && <p className="text-body-md text-error">{t("scales.loadFailed")}</p>}

        <div className="flex flex-col gap-1">
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("scales.instrument")}
          <select
            className={inputClass}
            aria-describedby={fieldErrors.instrumentId ? "scale-instrument-error" : undefined}
            value={instrumentId}
            onChange={(e) => {
              setInstrumentId(e.target.value);
              setScore("");
              setFieldErrors({});
              setFormError(null);
            }}
            aria-invalid={fieldErrors.instrumentId ? true : undefined}
          >
            <option value="">{t("scales.choose")}</option>
            {groups.map(
              (g) =>
                g.items.length > 0 && (
                  <optgroup key={g.key} label={g.label} className={g.key === "unavailable" ? "opacity-60" : undefined}>
                    {g.items.map((i) => (
                      <option key={i.id} value={i.id} className={g.key === "unavailable" ? "opacity-60" : undefined}>
                        {i.name} ({i.code})
                        {g.key === "unavailable" ? ` (${t("scales.unavailableSuffix")})` : i.regime === "opted_in" ? ` (${t("scales.optedInSuffix")})` : ""}
                      </option>
                    ))}
                  </optgroup>
                ),
            )}
          </select>
        </label>
        {fieldErrors.instrumentId && (
          <span id="scale-instrument-error" className="text-label-md text-error">
            {fieldErrors.instrumentId[0]}
          </span>
        )}
        </div>

        {selected?.regime === "unavailable" && (
          <p className="rounded-radii-lg bg-surfaceContainerLow p-4 text-body-md text-onSurfaceVariant">{selected.explanation}</p>
        )}

        {selected?.regime === "needs_opt_in" && (
          <div className="flex flex-col items-start gap-3 rounded-radii-lg bg-surfaceContainerLow p-4">
            <p className="text-body-md text-onSurface">{selected.explanation}</p>
            <button
              type="button"
              disabled={optIn.isPending}
              onClick={() => optIn.mutate(selected.id)}
              className={`${buttonClass} bg-primary text-onPrimary`}
            >
              {optIn.isPending && <Loader2 size={16} className="animate-spin" />}
              {t("scales.optInButton")}
            </button>
          </div>
        )}

        {selected && canRecord && (
          <div className="flex flex-wrap gap-3">
            <FormField
              className="w-40"
              type="number"
              inputMode="numeric"
              step={1}
              min={selected.minScore}
              max={selected.maxScore}
              label={`${t("scales.score")} (${t("scales.range", { min: selected.minScore, max: selected.maxScore })})`}
              value={score}
              onChange={(e) => setScore(e.target.value)}
              error={fieldErrors.score}
              autoFocus
            />
            <FormField
              className="w-48"
              type="date"
              max={todayIn(PRACTICE_ZONE)}
              label={t("scales.date")}
              value={date}
              onChange={(e) => setDate(e.target.value)}
              error={fieldErrors.appliedAt}
            />
          </div>
        )}

        {formError && (
          <p role="alert" className="text-body-md text-error">
            {formError}
          </p>
        )}
      </form>
    </Modal>
  );
}
