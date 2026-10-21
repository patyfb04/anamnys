import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { documentsApi } from "@anamnys/shared/api/documents";
import { toast } from "@anamnys/shared/ui/Toaster";
import PatientPicker from "@/components/calendar/PatientPicker";
import Modal from "@/components/patients/Modal";

// Picks one of the available document kinds, creates the draft and opens the editor. Without a
// patientId (the documents list) it also asks for the patient.
export default function NewDocumentModal({ patientId: fixedPatientId, onClose }: { patientId?: string; onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<string | null>(null);
  const [picked, setPicked] = useState<{ id: string; name: string } | null>(null);
  const patientId = fixedPatientId ?? picked?.id;
  const kinds = useQuery({ queryKey: ["document-kinds"], queryFn: documentsApi.kinds });

  const create = useMutation({
    mutationFn: (k: string) => documentsApi.create(patientId!, k),
    onSuccess: async (id) => {
      await queryClient.invalidateQueries({ queryKey: ["documents"] });
      onClose();
      void navigate({ to: "/documents/$documentId", params: { documentId: id } });
    },
    onError: (e) => toast.error(e instanceof ApiError && e.status === 409 ? e.message : t("documents.createFailed")),
  });

  return (
    <Modal
      title={t("documents.newTitle")}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose} className="rounded-radii-md px-4 py-2 text-label-lg text-onSurfaceVariant hover:bg-pastelLavender">
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={!kind || !patientId || create.isPending}
            onClick={() => kind && patientId && create.mutate(kind)}
            className="rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-50"
          >
            {t("documents.create")}
          </button>
        </>
      }
    >
      {fixedPatientId === undefined && (
        <div className="flex flex-col gap-2 px-5 pt-5">
          <span className="text-label-lg text-onSurface">{t("documents.patient")}</span>
          <PatientPicker value={picked} onChange={setPicked} />
        </div>
      )}
      <fieldset className="flex flex-col gap-3 p-5">
        <legend className="mb-2 text-body-md text-onSurfaceVariant">{t("documents.chooseKind")}</legend>
        {kinds.isError && <p className="text-body-md text-error">{t("documents.kindsLoadFailed")}</p>}
        {(kinds.data ?? []).map((k) => (
          <label
            key={k.kind}
            className={`flex cursor-pointer items-center gap-3 rounded-radii-lg border p-4 text-body-md text-onSurface ${
              kind === k.kind ? "border-primary bg-surfaceContainerLow" : "border-outlineVariant"
            }`}
          >
            <input type="radio" name="documentKind" value={k.kind} checked={kind === k.kind} onChange={() => setKind(k.kind)} />
            {k.title}
          </label>
        ))}
      </fieldset>
    </Modal>
  );
}
