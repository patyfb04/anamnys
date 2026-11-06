import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { dossiersApi } from "@anamnys/shared/api/dossiers";
import type { DossierReason } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import { inputClass } from "@/components/patients/FormField";
import Modal from "@/components/patients/Modal";

const REASONS: DossierReason[] = ["crp", "court", "patient_request", "other"];
const NOTE_MAX = 200;

interface Props {
  patientId: string;
  onClose: () => void;
  /** Called after a successful request, so the card can go back to the first page. */
  onRequested?: () => void;
  /** Retry after a failure: same reason and period as the failed request. */
  initial?: { reason: DossierReason; periodFrom: string | null; periodTo: string | null };
}

// Request dialog: optional period, reason, the delivery warning. The server answers 202 and the card polls.
export default function GenerateDossierModal({ patientId, onClose, onRequested, initial }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [from, setFrom] = useState(initial?.periodFrom ?? "");
  const [to, setTo] = useState(initial?.periodTo ?? "");
  const [reason, setReason] = useState<DossierReason | "">(initial?.reason ?? "");
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);

  const valid = reason !== "" && (reason !== "other" || note.trim().length > 0) && (!from || !to || from <= to);

  const request = useMutation({
    mutationFn: () =>
      dossiersApi.request(patientId, {
        reason: reason as DossierReason,
        reasonNote: reason === "other" ? note.trim() : undefined,
        periodFrom: from || undefined,
        periodTo: to || undefined,
      }),
    onSuccess: async () => {
      onRequested?.();
      await queryClient.invalidateQueries({ queryKey: ["dossiers", patientId] });
      toast.success(t("dossier.requested"));
      onClose();
    },
    onError: (e) => {
      const code = e instanceof ApiError ? e.code : undefined;
      if (code === "dossier_in_progress") {
        toast.error(t("dossier.inProgress"));
        void queryClient.invalidateQueries({ queryKey: ["dossiers", patientId] });
        onClose();
      } else if (code && ["invalid_reason", "reason_note_required", "invalid_period"].includes(code)) {
        setError(t(`dossier.errors.${code}`));
      } else {
        setError(t("dossier.requestFailed"));
      }
    },
  });

  return (
    <Modal
      title={t("dossier.dialogTitle")}
      onClose={request.isPending ? () => undefined : onClose}
      footer={
        <>
          <button
            type="button"
            onClick={onClose}
            disabled={request.isPending}
            className="rounded-radii-md px-4 py-2 text-label-lg text-onSurfaceVariant hover:bg-pastelLavender disabled:opacity-50"
          >
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={!valid || request.isPending}
            onClick={() => {
              setError(null);
              request.mutate();
            }}
            className="rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-50"
          >
            {request.isPending ? t("dossier.submitting") : t("dossier.submit")}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-4 p-5">
        <p className="text-body-md text-onSurfaceVariant">{t("dossier.description")}</p>

        <div className="flex flex-col gap-1">
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
              {t("dossier.periodFrom")}
              <input type="date" value={from} max={to || undefined} onChange={(e) => setFrom(e.target.value)} className={inputClass} />
            </label>
            <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
              {t("dossier.periodTo")}
              <input type="date" value={to} min={from || undefined} onChange={(e) => setTo(e.target.value)} className={inputClass} />
            </label>
          </div>
          <span className="text-body-sm text-onSurfaceVariant">{t("dossier.periodHint")}</span>
        </div>

        <fieldset className="flex flex-col gap-2">
          <legend className="mb-1 text-label-md text-onSurfaceVariant">{t("dossier.reasonLegend")}</legend>
          {REASONS.map((r) => (
            <label key={r} className="flex items-center gap-2 text-body-md text-onSurface">
              <input type="radio" name="dossier-reason" checked={reason === r} onChange={() => setReason(r)} />
              {t(`dossier.reason.${r}`)}
            </label>
          ))}
          {reason === "other" && (
            <input
              type="text"
              value={note}
              maxLength={NOTE_MAX}
              onChange={(e) => setNote(e.target.value)}
              aria-label={t("dossier.reasonNote")}
              placeholder={t("dossier.reasonNote")}
              className={inputClass}
            />
          )}
        </fieldset>

        <p role="note" className="text-body-md font-bold text-onSurface">
          {t("dossier.warning")}
        </p>
        {error && (
          <p role="alert" className="text-body-md text-error">
            {error}
          </p>
        )}
      </div>
    </Modal>
  );
}
