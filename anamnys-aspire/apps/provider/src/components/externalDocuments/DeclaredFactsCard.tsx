import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ClipboardCheck } from "lucide-react";
import { documentFactsApi } from "@anamnys/shared/api/documentFacts";
import type { DeclaredFact } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import { formatDate } from "@/components/patients/format";
import DocumentPassageModal from "./DocumentPassageModal";
import { FACT_KINDS } from "./factsShared";

// Patient record: what the provider confirmed or corrected from received documents. Only these
// facts are shown here; each links to the page text where it was read.
export default function DeclaredFactsCard({ patientId }: { patientId: string }) {
  const { t, i18n } = useTranslation();
  const [passageId, setPassageId] = useState<string | null>(null);
  const query = useQuery({ queryKey: ["declared-facts", patientId], queryFn: () => documentFactsApi.declared(patientId) });
  const facts = query.data ?? [];
  const groups = FACT_KINDS.map((k) => ({ kind: k, items: facts.filter((f) => f.kind === k) })).filter((g) => g.items.length > 0);

  const source = (f: DeclaredFact) =>
    [t(`externalDocuments.kinds.${f.documentKind}`), formatDate(f.documentDate ?? f.uploadedAt, i18n.language)].join(" · ");

  return (
    <DetailCard title={t("documentFacts.recordTitle")} icon={<ClipboardCheck size={20} />}>
      {query.isPending ? null : query.isError ? (
        <p className="text-body-md text-error">{t("documentFacts.recordLoadFailed")}</p>
      ) : groups.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("documentFacts.recordEmpty")}</p>
      ) : (
        <div className="flex flex-col gap-4">
          {groups.map((g) => (
            <section key={g.kind}>
              <h3 className="mb-1 text-label-md text-onSurfaceVariant">{t(`documentFacts.kinds.${g.kind}`)}</h3>
              <ul className="flex flex-col divide-y divide-outlineVariant">
                {g.items.map((f) => (
                  <li key={f.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                    <div className="min-w-0 flex-1">
                      <p className="break-words text-body-md text-onSurface">{f.value}</p>
                      <p className="text-body-sm text-onSurfaceVariant">
                        {source(f)}
                        {f.corrected && ` · ${t("documentFacts.correctedFromRead", { read: f.readValue })}`}
                      </p>
                    </div>
                    {f.excerptRefId && (
                      <button
                        type="button"
                        onClick={() => setPassageId(f.excerptRefId)}
                        className="text-label-md text-primary hover:underline"
                      >
                        {t("documentFacts.viewInDocument")}
                      </button>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
      )}
      {passageId && <DocumentPassageModal excerptId={passageId} onClose={() => setPassageId(null)} />}
    </DetailCard>
  );
}
