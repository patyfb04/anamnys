import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { complianceApi } from "@anamnys/shared/api/compliance";
import { toast } from "@anamnys/shared/ui/Toaster";
import type { RetentionItem } from "@anamnys/shared/lib/types";
import Modal from "@/components/patients/Modal";
import { inputClass } from "@/components/patients/FormField";

const CODES = ["already_discarded", "not_expired", "future_appointment", "name_mismatch", "storage_unavailable"];
const DELETED = ["notes", "documents", "external", "sessions", "appointments", "other"];

// Irreversible discard of a patient's record after the guard period. The provider types the
// patient's name to confirm; the name travels in the POST body, never in the URL.
export default function DiscardDialog({ item, onClose }: { item: RetentionItem; onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [errorCode, setErrorCode] = useState<string | null>(null);

  const discard = useMutation({
    mutationFn: () => complianceApi.discard(item.patientId, name),
    onSuccess: async () => {
      toast.success(t("compliance.discard.done"));
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["compliance"] }),
        queryClient.invalidateQueries({ queryKey: ["patients"] }),
        queryClient.invalidateQueries({ queryKey: ["disposal-terms"] }),
        queryClient.invalidateQueries({ queryKey: ["notifications"] }),
      ]);
      onClose();
    },
    onError: (e) => {
      const code = e instanceof ApiError ? (e.code ?? null) : null;
      setErrorCode(code && CODES.includes(code) ? code : "unknown");
      if (code === "already_discarded" || code === "not_expired" || code === "future_appointment") void queryClient.invalidateQueries({ queryKey: ["compliance"] });
    },
  });

  return (
    <Modal
      title={t("compliance.discard.title")}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose} className="rounded-radii-md px-4 py-2 text-label-lg text-onSurfaceVariant hover:bg-pastelLavender">
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={name.trim() === "" || discard.isPending}
            onClick={() => {
              setErrorCode(null);
              discard.mutate();
            }}
            className="rounded-radii-md bg-error px-4 py-2 text-label-lg text-onError hover:opacity-90 disabled:opacity-50"
          >
            {t("compliance.discard.confirm")}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-4 p-5">
        <p className="text-body-md font-bold text-onSurface">{t("compliance.discard.irreversible", { patient: item.patientName })}</p>
        <div>
          <p className="text-body-md text-onSurface">{t("compliance.discard.willDelete")}</p>
          <ul className="list-disc pl-6 text-body-md text-onSurface">
            {DELETED.map((k) => (
              <li key={k}>{t(`compliance.discard.deleted.${k}`)}</li>
            ))}
          </ul>
        </div>
        <p className="text-body-md text-onSurface">{t("compliance.discard.kept")}</p>
        <label className="flex flex-col gap-2">
          <span className="text-label-lg text-onSurface">{t("compliance.discard.typeName", { patient: item.patientName })}</span>
          <input
            className={inputClass}
            value={name}
            autoComplete="off"
            onChange={(e) => setName(e.target.value)}
            aria-label={t("compliance.discard.nameLabel")}
          />
        </label>
        {errorCode && (
          <p role="alert" className="text-body-md text-error">
            {t(`compliance.discard.errors.${errorCode}`)}
          </p>
        )}
      </div>
    </Modal>
  );
}
