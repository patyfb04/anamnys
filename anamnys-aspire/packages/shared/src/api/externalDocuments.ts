import api from "@anamnys/shared/api/client";
import type {
  ExternalDocumentItem,
  ExternalDocumentSearchRequest,
  ExternalDocumentSearchResponse,
  ExternalDocumentUploadInput,
} from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me";
// Uploads can be 20 MB over a slow link; the default 30 s would cut them off.
const UPLOAD_TIMEOUT_MS = 180_000;

export const externalDocumentsApi = {
  list: async (patientId: string): Promise<ExternalDocumentItem[]> => {
    const { data } = await api.get<ExternalDocumentItem[]>(`${BASE}/patients/${patientId}/external-documents`);
    return data;
  },
  search: async (request: ExternalDocumentSearchRequest): Promise<ExternalDocumentSearchResponse> => {
    const { data } = await api.post<ExternalDocumentSearchResponse>(`${BASE}/external-documents/search`, request);
    return data;
  },
  upload: async (patientId: string, input: ExternalDocumentUploadInput): Promise<{ id: string; scanStatus: string }> => {
    const form = new FormData();
    form.append("file", input.file);
    form.append("kind", input.kind);
    if (input.documentDate) form.append("documentDate", input.documentDate);
    if (input.description) form.append("description", input.description);
    // `false` drops the instance's JSON default so the browser sets multipart/form-data with its boundary.
    const { data } = await api.post<{ id: string; scanStatus: string }>(
      `${BASE}/patients/${patientId}/external-documents`,
      form,
      { timeout: UPLOAD_TIMEOUT_MS, headers: { "Content-Type": false } },
    );
    return data;
  },
  open: async (id: string): Promise<{ url: string; expiresAt: string }> => {
    const { data } = await api.post<{ url: string; expiresAt: string }>(`${BASE}/external-documents/${id}/open`);
    return data;
  },
  remove: async (id: string): Promise<void> => {
    await api.delete(`${BASE}/external-documents/${id}`);
  },
};
