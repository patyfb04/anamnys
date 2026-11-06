import api from "@anamnys/shared/api/client";
import type { AccountExportDownload, DossierPage, DossierRequest } from "@anamnys/shared/lib/types";

export const dossiersApi = {
  list: async (patientId: string, page: number): Promise<DossierPage> => {
    const { data } = await api.get<DossierPage>(`/phi/patients/${patientId}/dossiers`, { params: { page } });
    return data;
  },
  request: async (patientId: string, body: DossierRequest): Promise<{ id: string; status: string }> => {
    const { data } = await api.post<{ id: string; status: string }>(`/phi/patients/${patientId}/dossiers`, body);
    return data;
  },
  /** A fresh presigned URL on every call, audited. */
  download: async (id: string): Promise<AccountExportDownload> => {
    const { data } = await api.post<AccountExportDownload>(`/phi/dossiers/${id}/download`);
    return data;
  },
  receiptPdf: async (id: string): Promise<Blob> => {
    const { data } = await api.get<Blob>(`/phi/dossiers/${id}/receipt.pdf`, { responseType: "blob" });
    return data;
  },
  timestampToken: async (id: string): Promise<Blob> => {
    const { data } = await api.get<Blob>(`/phi/dossiers/${id}/timestamp.tsr`, { responseType: "blob" });
    return data;
  },
};
