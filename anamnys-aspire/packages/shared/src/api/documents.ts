import api from "@anamnys/shared/api/client";
import type {
  DocumentCheckMode,
  DocumentDetail,
  DocumentKindInfo,
  DocumentSearchRequest,
  DocumentSearchResponse,
  DocumentSummary,
  DocumentTeamMember,
} from "@anamnys/shared/lib/types";

export const documentsApi = {
  kinds: async (): Promise<DocumentKindInfo[]> => {
    const { data } = await api.get<DocumentKindInfo[]>(`/phi/providers/me/document-kinds`);
    return data;
  },
  list: async (patientId: string): Promise<DocumentSummary[]> => {
    const { data } = await api.get<DocumentSummary[]>(`/phi/providers/me/patients/${patientId}/documents`);
    return data;
  },
  search: async (request: DocumentSearchRequest): Promise<DocumentSearchResponse> => {
    const { data } = await api.post<DocumentSearchResponse>(`/phi/providers/me/documents/search`, request);
    return data;
  },
  create: async (patientId: string, kind: string): Promise<string> => {
    const { data } = await api.post<{ id: string }>(`/phi/providers/me/patients/${patientId}/documents`, { kind });
    return data.id;
  },
  get: async (id: string): Promise<DocumentDetail> => {
    const { data } = await api.get<DocumentDetail>(`/phi/providers/me/documents/${id}`);
    return data;
  },
  save: async (
    id: string,
    fields: Record<string, string>,
    confirmations: string[],
    team: DocumentTeamMember[],
  ): Promise<DocumentDetail> => {
    const { data } = await api.put<DocumentDetail>(`/phi/providers/me/documents/${id}`, { fields, confirmations, team });
    return data;
  },
  sign: async (id: string, acknowledgeMissing: boolean): Promise<void> => {
    await api.post(`/phi/providers/me/documents/${id}/sign`, { acknowledgeMissing });
  },
  correct: async (id: string): Promise<string> => {
    const { data } = await api.post<{ id: string }>(`/phi/providers/me/documents/${id}/correct`);
    return data.id;
  },
  exportPdf: async (id: string): Promise<{ url: string; expiresAt: string }> => {
    const { data } = await api.post<{ url: string; expiresAt: string }>(`/phi/providers/me/documents/${id}/export`);
    return data;
  },
  getSettings: async (): Promise<{ documentCheckMode: DocumentCheckMode }> => {
    const { data } = await api.get<{ documentCheckMode: DocumentCheckMode }>(`/phi/providers/me/document-settings`);
    return data;
  },
  saveSettings: async (documentCheckMode: DocumentCheckMode): Promise<void> => {
    await api.put(`/phi/providers/me/document-settings`, { documentCheckMode });
  },
};
