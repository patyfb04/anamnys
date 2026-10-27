import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Paperclip } from "lucide-react";
import { externalDocumentsApi } from "@anamnys/shared/api/externalDocuments";
import DetailCard from "@/components/patients/detail/DetailCard";
import ExternalDocumentRow from "./ExternalDocumentRow";
import { hasPending } from "./fileCheck";
import UploadExternalDocumentModal from "./UploadExternalDocumentModal";

const POLL_MS = 3000;

// Patient record card: documents received from outside (laudos, exames...), with the antivirus
// status. Polls while any file is still being scanned. Archived patients: read-only.
export default function ExternalDocumentsCard({ patientId, readOnly }: { patientId: string; readOnly: boolean }) {
  const { t } = useTranslation();
  const [uploading, setUploading] = useState(false);
  const list = useQuery({
    queryKey: ["external-documents", "patient", patientId],
    queryFn: () => externalDocumentsApi.list(patientId),
    refetchInterval: (q) => (hasPending(q.state.data) ? POLL_MS : false),
  });
  const items = list.data ?? [];

  return (
    <DetailCard collapsibleId="externalDocuments"
      title={t("externalDocuments.title")}
      icon={<Paperclip size={20} />}
      action={
        !readOnly && (
          <button type="button" onClick={() => setUploading(true)} className="inline-flex items-center gap-1 px-3 py-1.5 text-label-lg text-primary hover:underline">
            {t("externalDocuments.upload")}
          </button>
        )
      }
    >
      {list.isPending ? null : list.isError ? (
        <p className="text-body-md text-error">{t("externalDocuments.loadFailed")}</p>
      ) : items.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("externalDocuments.empty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-outlineVariant">
          {items.map((d) => (
            <ExternalDocumentRow key={d.id} doc={d} readOnly={readOnly} />
          ))}
        </ul>
      )}

      {uploading && <UploadExternalDocumentModal patientId={patientId} onClose={() => setUploading(false)} />}
    </DetailCard>
  );
}
