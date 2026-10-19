import api from "@anamnys/shared/api/client";
import type { InstrumentItem, RecordScaleRequest, ScaleSeries } from "@anamnys/shared/lib/types";

export const scalesApi = {
  instruments: async (): Promise<InstrumentItem[]> => {
    const { data } = await api.get<InstrumentItem[]>(`/phi/providers/me/instruments`);
    return data;
  },
  optIn: async (instrumentId: string): Promise<void> => {
    await api.post(`/phi/providers/me/instruments/${instrumentId}/opt-in`);
  },
  series: async (patientId: string, sessionId?: string): Promise<ScaleSeries[]> => {
    const { data } = await api.get<ScaleSeries[]>(`/phi/providers/me/patients/${patientId}/scales`, {
      params: sessionId ? { sessionId } : undefined,
    });
    return data;
  },
  record: async (patientId: string, request: RecordScaleRequest): Promise<string> => {
    const { data } = await api.post<{ id: string }>(`/phi/providers/me/patients/${patientId}/scales`, request);
    return data.id;
  },
  remove: async (patientId: string, applicationId: string): Promise<void> => {
    await api.delete(`/phi/providers/me/patients/${patientId}/scales/${applicationId}`);
  },
};
