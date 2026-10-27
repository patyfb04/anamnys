import { useEffect, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { excerptsApi } from "@anamnys/shared/api/excerpts";
import Modal from "@/components/patients/Modal";
import { formatDate } from "@/components/patients/format";

// "Ver no documento": the read text of the page that holds the fact, with the sentence highlighted
// (same mark style as a note passage). Only the opaque excerpt id is sent; the text is loaded, never
// put in a URL. Without exact offsets (the page changed) the whole page is shown unmarked.
export default function DocumentPassageModal({ excerptId, onClose }: { excerptId: string; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const markRef = useRef<HTMLElement>(null);
  const query = useQuery({ queryKey: ["excerpt", excerptId], queryFn: () => excerptsApi.get(excerptId) });
  const p = query.data;
  const text = p?.sourceType === "document" ? (p.pageText ?? "") : "";
  const exact =
    p && p.start != null && p.end != null && p.start >= 0 && p.end > p.start && p.end <= text.length ? p : null;

  useEffect(() => {
    markRef.current?.scrollIntoView({ block: "center" });
  }, [exact?.start, exact?.end, text]);

  return (
    <Modal
      title={p?.pageNumber ? t("documentFacts.passageTitle", { page: p.pageNumber }) : t("documentFacts.passage")}
      onClose={onClose}
      footer={
        <button type="button" onClick={onClose} className="rounded-radii-lg px-4 py-2 text-label-lg text-primary hover:bg-pastelLavender">
          {t("common.close")}
        </button>
      }
    >
      <div className="flex flex-col gap-3 p-5">
        {query.isPending ? (
          <p className="text-body-md text-onSurfaceVariant">{t("documentFacts.loading")}</p>
        ) : query.isError || !p || p.sourceType !== "document" ? (
          <p className="text-body-md text-error">{t("documentFacts.passageFailed")}</p>
        ) : (
          <>
            <p className="text-body-sm text-onSurfaceVariant">
              {[p.documentKind ? t(`externalDocuments.kinds.${p.documentKind}`) : null, formatDate(p.datedAt, i18n.language)]
                .filter(Boolean)
                .join(" · ")}
            </p>
            <p className="whitespace-pre-wrap break-words text-body-md text-onSurface">
              {exact ? (
                <>
                  {text.slice(0, exact.start)}
                  <mark
                    ref={markRef}
                    className="rounded-sm bg-pastelLavender text-onSurface underline decoration-primary decoration-2 underline-offset-2"
                  >
                    {text.slice(exact.start, exact.end)}
                  </mark>
                  {text.slice(exact.end)}
                </>
              ) : (
                text
              )}
            </p>
          </>
        )}
      </div>
    </Modal>
  );
}
