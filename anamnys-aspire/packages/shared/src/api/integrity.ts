import api from "@anamnys/shared/api/client";
import type { IntegritySummary, RecordIntegrity } from "@anamnys/shared/lib/types";

const notFound = (e: unknown) => (e as { response?: { status?: number } }).response?.status === 404;

// Document integrity chain (F-45). Ids only in the URLs; a draft or unknown record answers 404 (no badge).
export const integrityApi = {
  forNote: async (noteId: string): Promise<RecordIntegrity | null> => {
    try {
      const { data } = await api.get<RecordIntegrity>(`/phi/notes/${noteId}/integrity`);
      return data;
    } catch (e) {
      if (notFound(e)) return null;
      throw e;
    }
  },
  forDocument: async (documentId: string): Promise<RecordIntegrity | null> => {
    try {
      const { data } = await api.get<RecordIntegrity>(`/phi/documents/${documentId}/integrity`);
      return data;
    } catch (e) {
      if (notFound(e)) return null;
      throw e;
    }
  },
  summary: async (): Promise<IntegritySummary> => {
    const { data } = await api.get<IntegritySummary>("/phi/providers/me/integrity");
    return data;
  },
  /** The verification report, audited on each download; the file comes back in the response. */
  reportPdf: async (): Promise<Blob> => {
    const { data } = await api.get<Blob>("/phi/providers/me/integrity/report.pdf", { responseType: "blob" });
    return data;
  },
};
