import api from "@anamnys/shared/api/client";
import type { DeclaredFact, DocumentFacts, ExternalReadStatus } from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me";

// Facts read from received documents (F-08). Values travel only in bodies, never in URLs.
export const documentFactsApi = {
  list: async (documentId: string): Promise<DocumentFacts> => {
    const { data } = await api.get<DocumentFacts>(`${BASE}/external-documents/${documentId}/facts`);
    return data;
  },
  confirm: async (documentId: string, factId: string): Promise<void> => {
    await api.post(`${BASE}/external-documents/${documentId}/facts/${factId}/confirm`);
  },
  correct: async (documentId: string, factId: string, value: string): Promise<void> => {
    await api.post(`${BASE}/external-documents/${documentId}/facts/${factId}/correct`, { value });
  },
  discard: async (documentId: string, factId: string): Promise<void> => {
    await api.post(`${BASE}/external-documents/${documentId}/facts/${factId}/discard`);
  },
  extract: async (documentId: string): Promise<{ readStatus: ExternalReadStatus }> => {
    const { data } = await api.post<{ readStatus: ExternalReadStatus }>(`${BASE}/external-documents/${documentId}/extract`);
    return data;
  },
  declared: async (patientId: string): Promise<DeclaredFact[]> => {
    const { data } = await api.get<DeclaredFact[]>(`${BASE}/patients/${patientId}/declared-facts`);
    return data;
  },
};
