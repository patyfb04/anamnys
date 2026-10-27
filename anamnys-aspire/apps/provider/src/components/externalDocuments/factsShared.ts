import { useQueryClient } from "@tanstack/react-query";
import type { ExternalDocumentItem, FactKind } from "@anamnys/shared/lib/types";

export const FACT_KINDS: FactKind[] = ["issuer", "registration", "issue_date", "icd", "medication", "conclusion"];

// Reloads everything a fact change touches: the panel, the document lists and the patient record.
export function useFactsRefresh(doc: Pick<ExternalDocumentItem, "id" | "patientId">) {
  const queryClient = useQueryClient();
  return async () => {
    await queryClient.invalidateQueries({ queryKey: ["document-facts", doc.id] });
    await queryClient.invalidateQueries({ queryKey: ["external-documents"] });
    await queryClient.invalidateQueries({ queryKey: ["declared-facts", doc.patientId] });
  };
}
