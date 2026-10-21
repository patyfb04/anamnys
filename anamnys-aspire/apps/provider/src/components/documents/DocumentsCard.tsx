import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { FileSignature, HelpCircle } from "lucide-react";
import { documentsApi } from "@anamnys/shared/api/documents";
import DetailCard from "@/components/patients/detail/DetailCard";
import { formatDate } from "@/components/patients/format";
import DocumentHelpPanel from "./DocumentHelpPanel";
import DocumentStatusBadge from "./DocumentStatusBadge";
import NewDocumentModal from "./NewDocumentModal";

const actionClass = "inline-flex items-center gap-1 px-3 py-1.5 text-label-lg text-primary hover:underline";

// Patient record card: the psychological documents (declaração, atestado, relatório) and their status.
export default function DocumentsCard({ patientId, readOnly }: { patientId: string; readOnly: boolean }) {
  const { t, i18n } = useTranslation();
  const [creating, setCreating] = useState(false);
  const [helping, setHelping] = useState(false);
  const list = useQuery({ queryKey: ["documents", patientId], queryFn: () => documentsApi.list(patientId) });
  const items = list.data ?? [];

  return (
    <DetailCard
      title={t("documents.title")}
      icon={<FileSignature size={20} />}
      action={
        <div className="flex items-center gap-1">
          <button type="button" onClick={() => setHelping(true)} aria-label={t("documents.helpLabel")} className="rounded-radii-full p-1.5 text-onSurfaceVariant hover:bg-surfaceContainer">
            <HelpCircle size={18} />
          </button>
          {!readOnly && (
            <button type="button" onClick={() => setCreating(true)} className={actionClass}>
              {t("documents.new")}
            </button>
          )}
        </div>
      }
    >
      {list.isPending ? null : list.isError ? (
        <p className="text-body-md text-error">{t("documents.listLoadFailed")}</p>
      ) : items.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("documents.empty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-outlineVariant">
          {items.map((d) => (
            <li key={d.id}>
              <Link
                to="/documents/$documentId"
                params={{ documentId: d.id }}
                className="flex flex-wrap items-center justify-between gap-2 py-3 hover:bg-surfaceContainerLow"
              >
                <span className="text-label-lg text-onSurface">{d.title}</span>
                <span className="flex items-center gap-3">
                  <span className="text-body-sm text-onSurfaceVariant">
                    {d.signedAt
                      ? t("documents.signedOn", { date: formatDate(d.signedAt, i18n.language) })
                      : t("documents.createdOn", { date: formatDate(d.createdAt, i18n.language) })}
                  </span>
                  <DocumentStatusBadge status={d.status} />
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}

      {creating && <NewDocumentModal patientId={patientId} onClose={() => setCreating(false)} />}
      {helping && <DocumentHelpPanel onClose={() => setHelping(false)} />}
    </DetailCard>
  );
}
