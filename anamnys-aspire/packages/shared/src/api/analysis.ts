import api from "@anamnys/shared/api/client";
import type { AnalysisSettings, CaseAnalysis } from "@anamnys/shared/lib/types";

// Case analysis (F-10). Only the patient id and dates travel in the URL; themes and quotes stay in the body.
export const analysisApi = {
  get: async (patientId: string, from?: string, to?: string): Promise<CaseAnalysis> => {
    const { data } = await api.get<CaseAnalysis>(`/phi/providers/me/patients/${patientId}/analysis`, { params: { from, to } });
    return data;
  },
  getSettings: async (): Promise<AnalysisSettings> => {
    const { data } = await api.get<AnalysisSettings>("/phi/providers/me/settings/analysis");
    return data;
  },
  saveSettings: async (settings: AnalysisSettings): Promise<void> => {
    await api.put("/phi/providers/me/settings/analysis", settings);
  },
};
