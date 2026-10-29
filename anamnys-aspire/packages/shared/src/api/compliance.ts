import api from "@anamnys/shared/api/client";
import type { ComplianceSummary, DisposalTerm, RetentionSettings } from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me";

// Retention and compliance panel (F-06). Ids travel in the URL; the patient name goes in the discard body.
export const complianceApi = {
  summary: async (): Promise<ComplianceSummary> => {
    const { data } = await api.get<ComplianceSummary>(`${BASE}/compliance`);
    return data;
  },
  getSettings: async (): Promise<RetentionSettings> => {
    const { data } = await api.get<RetentionSettings>(`${BASE}/settings/retention`);
    return data;
  },
  saveSettings: async (settings: RetentionSettings): Promise<void> => {
    await api.put(`${BASE}/settings/retention`, settings);
  },
  discard: async (patientId: string, confirmName: string): Promise<{ termId: string; counts: Record<string, number> }> => {
    const { data } = await api.post<{ termId: string; counts: Record<string, number> }>(`${BASE}/patients/${patientId}/discard`, {
      confirmName,
    });
    return data;
  },
  terms: async (): Promise<DisposalTerm[]> => {
    const { data } = await api.get<DisposalTerm[]>(`${BASE}/disposal-terms`);
    return data;
  },
  termPdf: async (id: string): Promise<{ url: string; expiresAt: string }> => {
    const { data } = await api.get<{ url: string; expiresAt: string }>(`${BASE}/disposal-terms/${id}/pdf`);
    return data;
  },
};
