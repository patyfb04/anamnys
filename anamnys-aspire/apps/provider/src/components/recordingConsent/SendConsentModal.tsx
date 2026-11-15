import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { recordingConsentApi } from "@anamnys/shared/api/recordingConsent";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import { consentKey, errorCode } from "./consentModel";

const NOTE_MAX = 1000;

// Preview of the term as the patient will read it, with the optional "Observações do profissional" typed here
// shown as the term's last paragraph, then "Enviar por e-mail" (design/specs/2026-11-15-recording-consent-design.md §6).
export default function SendConsentModal({ patientId, onClose }: { patientId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [note, setNote] = useState("");
  const preview = useQuery({
    queryKey: ["recording-consent-preview", patientId],
    queryFn: () => recordingConsentApi.preview(patientId),
    staleTime: 0,
    gcTime: 0,
  });

  const send = useMutation({
    mutationFn: () => recordingConsentApi.send(patientId, note.trim() || null),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: consentKey(patientId) });
      toast.success(t("recordingConsent.card.sent"));
      onClose();
    },
    onError: (e) => {
      const code = errorCode(e);
      const key = `recordingConsent.send.errors.${code ?? "unknown"}`;
      toast.error(t(key, { defaultValue: t("recordingConsent.send.errors.unknown") }));
      // A 409 means the state moved on; show the new one.
      void queryClient.invalidateQueries({ queryKey: consentKey(patientId) });
    },
  });

  const blocked = preview.data?.sendBlockedBy ?? null;
  const trimmed = note.trim();

  return (
    <Modal
      title={t("recordingConsent.send.title")}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose} className="rounded-radii-md px-4 py-2 text-label-lg text-onSurfaceVariant hover:bg-pastelLavender">
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={!preview.isSuccess || blocked !== null || send.isPending || note.length > NOTE_MAX}
            onClick={() => send.mutate()}
            className="inline-flex items-center gap-2 rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-50"
          >
            {send.isPending && <Loader2 size={16} className="animate-spin" aria-hidden />}
            {send.isPending ? t("recordingConsent.send.sending") : t("recordingConsent.send.confirm")}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-4 p-5">
        <p className="text-body-md text-onSurfaceVariant">{t("recordingConsent.send.intro")}</p>

        {preview.isPending && <p role="status" className="text-body-md text-onSurfaceVariant">{t("recordingConsent.send.loading")}</p>}
        {preview.isError && <p role="alert" className="text-body-md text-error">{t("recordingConsent.send.loadFailed")}</p>}
        {blocked && (
          <p role="alert" className="rounded-radii-md bg-errorContainer px-3 py-2 text-body-md text-onErrorContainer">
            {t(`recordingConsent.blocked.${blocked}`)}
          </p>
        )}

        {preview.data && (
          <div className="max-h-80 overflow-y-auto rounded-radii-lg border border-outlineVariant bg-surfaceContainerLow p-4 text-body-md text-onSurface">
            <p className="whitespace-pre-wrap">{preview.data.text}</p>
            {trimmed && (
              <p className="mt-4 whitespace-pre-wrap">
                <strong>{t("recordingConsent.send.noteHeading")}</strong>
                {"\n"}
                {trimmed}
              </p>
            )}
          </div>
        )}

        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("recordingConsent.send.noteLabel")}
          <textarea
            value={note}
            onChange={(e) => setNote(e.target.value)}
            rows={3}
            maxLength={NOTE_MAX}
            className="w-full resize-y rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-3 py-2 text-body-md text-onSurface outline-none focus:border-primary"
          />
          <span className="text-body-sm">{t("recordingConsent.send.noteHint")}</span>
        </label>
      </div>
    </Modal>
  );
}
