import api from "@anamnys/shared/api/client";
import type {
  AnalysisSettings,
  CaseAnalysis,
  FollowUpRequest,
  PatientQuote,
  PatientQuotePage,
  PlanAdherence,
  RecordQuoteRequest,
} from "@anamnys/shared/lib/types";

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

// Plan adherence and follow-ups (F-14). Only ids in URLs; descriptions travel in bodies.
export const planAdherenceApi = {
  get: async (patientId: string): Promise<PlanAdherence> => {
    const { data } = await api.get<PlanAdherence>(`/phi/providers/me/patients/${patientId}/plan-adherence`);
    return data;
  },
  review: async (patientId: string): Promise<{ planId: string; reviewedAt: string }> => {
    const { data } = await api.post<{ planId: string; reviewedAt: string }>(`/phi/providers/me/patients/${patientId}/plan/review`);
    return data;
  },
  createFollowUp: async (noteId: string, request: FollowUpRequest): Promise<string> => {
    const { data } = await api.post<{ id: string }>(`/phi/providers/me/notes/${noteId}/follow-ups`, request);
    return data.id;
  },
  resolve: async (id: string, noteId?: string): Promise<void> => {
    await api.post(`/phi/providers/me/follow-ups/${id}/resolve`, noteId ? { noteId } : {});
  },
  reopen: async (id: string): Promise<void> => {
    await api.post(`/phi/providers/me/follow-ups/${id}/reopen`);
  },
};

// Patient quotes (F-15). Only ids, dates and a term id in URLs; the quote text travels in bodies and responses.
export const patientQuotesApi = {
  forNote: async (noteId: string): Promise<PatientQuote[]> => {
    const { data } = await api.get<{ items: PatientQuote[] }>(`/phi/providers/me/notes/${noteId}/quotes`);
    return data.items;
  },
  record: async (noteId: string, request: RecordQuoteRequest): Promise<PatientQuote> => {
    const { data } = await api.post<PatientQuote>(`/phi/providers/me/notes/${noteId}/quotes`, request);
    return data;
  },
  remove: async (id: string): Promise<void> => {
    await api.delete(`/phi/providers/me/quotes/${id}`);
  },
  forPatient: async (
    patientId: string,
    params: { from?: string; to?: string; termId?: string; page?: number },
  ): Promise<PatientQuotePage> => {
    const { data } = await api.get<PatientQuotePage>(`/phi/providers/me/patients/${patientId}/quotes`, { params });
    return data;
  },
  // Every quote of one day: the list is paged, and the grid's cell detail filters by note and term afterwards.
  forDay: async (patientId: string, on: string): Promise<PatientQuote[]> => {
    const all: PatientQuote[] = [];
    for (let page = 1; ; page++) {
      const { data } = await api.get<PatientQuotePage>(`/phi/providers/me/patients/${patientId}/quotes`, {
        params: { from: on, to: on, page },
      });
      all.push(...data.items);
      if (data.items.length === 0 || data.page * data.pageSize >= data.total) return all;
    }
  },
};
