import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { documentFactsApi } from "@anamnys/shared/api/documentFacts";
import type { ExternalDocumentItem } from "@anamnys/shared/lib/types";
import Badge from "@anamnys/shared/ui/Badge";
import { toast } from "@anamnys/shared/ui/Toaster";
import DocumentFactsModal from "./DocumentFactsModal";
import { useFactsRefresh } from "./factsShared";

// Read status next to the scan status: "lendo…", "N fatos a conferir", "lido", or a failure with
// "Ler de novo"; "Ver fatos" opens the review panel once the text is read. Nothing shows until the
// scan is clean.
export default function DocumentReadStatus({
  doc,
  readOnly,
  onOpenOriginal,
}: {
  doc: ExternalDocumentItem;
  readOnly: boolean;
  onOpenOriginal: () => void;
}) {
  const { t } = useTranslation();
  const [viewing, setViewing] = useState(false);
  const refresh = useFactsRefresh(doc);
  const reread = useMutation({
    mutationFn: () => documentFactsApi.extract(doc.id),
    onSuccess: async () => {
      toast.success(t("documentFacts.rereadStarted"));
      await refresh();
    },
    onError: (e) =>
      toast.error(e instanceof ApiError && e.code === "archived" ? t("documentFacts.errors.archived") : t("documentFacts.errors.failed")),
  });
  if (doc.scanStatus !== "clean" || !doc.readStatus) return null;

  const reading = doc.readStatus === "pending" || doc.readStatus === "reading" || doc.extracting === true;
  const pendingFacts = doc.pendingFacts ?? 0;
  const link = "text-label-md text-primary hover:underline disabled:opacity-60";

  return (
    <div className="flex flex-wrap items-center gap-2" onClick={(e) => e.stopPropagation()}>
      {reading ? (
        <Badge label={t("documentFacts.reading")} tone="blue" />
      ) : doc.readStatus === "failed" ? (
        <>
          <Badge label={t("documentFacts.readFailedShort")} tone="pink" />
          {!readOnly && (
            <button type="button" disabled={reread.isPending} onClick={() => reread.mutate()} className={link}>
              {t("documentFacts.reread")}
            </button>
          )}
        </>
      ) : pendingFacts > 0 ? (
        <Badge label={t("documentFacts.pendingFacts", { count: pendingFacts })} tone="lavender" />
      ) : (
        <Badge label={t("documentFacts.read")} tone="mint" />
      )}
      {!reading && doc.readStatus === "done" && (
        <button type="button" onClick={() => setViewing(true)} className={link}>
          {t("documentFacts.view")}
        </button>
      )}
      {viewing && <DocumentFactsModal doc={doc} readOnly={readOnly} onOpenOriginal={onOpenOriginal} onClose={() => setViewing(false)} />}
    </div>
  );
}
