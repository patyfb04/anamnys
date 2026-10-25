import { useRef, useState, type DragEvent } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Upload } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import { externalDocumentsApi } from "@anamnys/shared/api/externalDocuments";
import type { ExternalDocumentKind } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import { inputClass } from "@/components/patients/FormField";
import Modal from "@/components/patients/Modal";
import { ACCEPT, KINDS, checkFile, formatBytes } from "./fileCheck";

const SERVER_CODES = ["unsupported_type", "too_large", "type_mismatch", "empty", "archived"];

function todayIso(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

// Upload dialog: file (picker or drag and drop), kind, optional date and description. The same
// limits as the server are checked here first. The server answers 201 with the scan still pending.
export default function UploadExternalDocumentModal({ patientId, onClose }: { patientId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const inputRef = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [dragging, setDragging] = useState(false);
  const [kind, setKind] = useState<ExternalDocumentKind | "">("");
  const [documentDate, setDocumentDate] = useState("");
  const [description, setDescription] = useState("");

  const pick = (f: File | undefined) => {
    if (!f) return;
    const problem = checkFile(f);
    setFileError(problem ? t(`externalDocuments.errors.${problem}`) : null);
    setFile(problem ? null : f);
  };
  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setDragging(false);
    pick(e.dataTransfer.files[0]);
  };

  const upload = useMutation({
    mutationFn: () =>
      externalDocumentsApi.upload(patientId, {
        file: file!,
        kind: kind as ExternalDocumentKind,
        documentDate: documentDate || undefined,
        description: description.trim() || undefined,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["external-documents"] });
      toast.success(t("externalDocuments.uploaded"));
      onClose();
    },
    onError: (e) => {
      const code = e instanceof ApiError ? (e.status === 413 ? "too_large" : e.code) : undefined;
      if (code && SERVER_CODES.includes(code)) setFileError(t(`externalDocuments.errors.${code}`));
      else {
        const fieldMessages = e instanceof ApiError && e.errors ? Object.values(e.errors).flat() : [];
        toast.error(fieldMessages.length > 0 ? fieldMessages.join(" ") : t("externalDocuments.uploadFailed"));
      }
    },
  });

  return (
    <Modal
      title={t("externalDocuments.uploadTitle")}
      onClose={upload.isPending ? () => undefined : onClose}
      footer={
        <>
          <button
            type="button"
            onClick={onClose}
            disabled={upload.isPending}
            className="rounded-radii-md px-4 py-2 text-label-lg text-onSurfaceVariant hover:bg-pastelLavender disabled:opacity-50"
          >
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={!file || !kind || upload.isPending}
            onClick={() => upload.mutate()}
            className="rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-50"
          >
            {upload.isPending ? t("externalDocuments.uploading") : t("externalDocuments.send")}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-4 p-5">
        <div
          onDragOver={(e) => {
            e.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={onDrop}
          className={`flex flex-col items-center gap-2 rounded-radii-lg border-2 border-dashed p-6 text-center ${
            dragging ? "border-primary bg-pastelLavender" : "border-outlineVariant"
          }`}
        >
          <Upload size={24} className="text-primary" aria-hidden />
          <p className="text-body-md text-onSurface break-all">
            {file ? `${file.name} (${formatBytes(file.size)})` : t("externalDocuments.dropHint")}
          </p>
          <button type="button" onClick={() => inputRef.current?.click()} className="text-label-lg text-primary hover:underline">
            {t("externalDocuments.chooseFile")}
          </button>
          <p className="text-body-sm text-onSurfaceVariant">{t("externalDocuments.limits")}</p>
          <input
            ref={inputRef}
            type="file"
            accept={ACCEPT}
            className="sr-only"
            tabIndex={-1}
            aria-label={t("externalDocuments.chooseFile")}
            onChange={(e) => {
              pick(e.target.files?.[0]);
              e.target.value = "";
            }}
          />
          {fileError && (
            <p role="alert" className="text-label-md text-error">
              {fileError}
            </p>
          )}
        </div>

        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("externalDocuments.kind")}
          <select className={inputClass} value={kind} onChange={(e) => setKind(e.target.value as ExternalDocumentKind | "")}>
            <option value="">{t("externalDocuments.kindPlaceholder")}</option>
            {KINDS.map((k) => (
              <option key={k} value={k}>
                {t(`externalDocuments.kinds.${k}`)}
              </option>
            ))}
          </select>
        </label>

        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("externalDocuments.date")}
          <input type="date" className={inputClass} value={documentDate} max={todayIso()} onChange={(e) => setDocumentDate(e.target.value)} />
        </label>

        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("externalDocuments.description")}
          <textarea className={inputClass} rows={2} maxLength={200} value={description} onChange={(e) => setDescription(e.target.value)} />
        </label>
      </div>
    </Modal>
  );
}
