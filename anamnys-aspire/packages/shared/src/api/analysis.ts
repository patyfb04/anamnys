import api from "@anamnys/shared/api/client";
import type { CaseAnalysis } from "@anamnys/shared/lib/types";

// Case analysis (F-10). Only the patient id and dates travel in the URL; themes and quotes stay in the body.
export const analysisApi = {
  get: async (patientId: string, from?: string, to?: string): Promise<CaseAnalysis> => {
    const { data } = await api.get<CaseAnalysis>(`/phi/providers/me/patients/${patientId}/analysis`, { params: { from, to } });
    return data;
  },
};
