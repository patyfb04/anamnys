import api from "@anamnys/shared/api/client";
import type {
  CreateSeriesRequest,
  SeriesCreated,
  SeriesDetail,
  SeriesSummary,
  SplitPreview,
  SplitResult,
  SplitSeriesRequest,
} from "@anamnys/shared/lib/types";

// Recurring appointments (design/specs/2026-11-09-appointment-series-design.md §4).
const BASE = "/phi/providers/me";

export const appointmentSeriesApi = {
  create: async (request: CreateSeriesRequest): Promise<SeriesCreated> => {
    const { data } = await api.post<SeriesCreated>(`${BASE}/appointment-series`, request);
    return data;
  },
  get: async (id: string): Promise<SeriesDetail> => {
    const { data } = await api.get<SeriesDetail>(`${BASE}/appointment-series/${id}`);
    return data;
  },
  // The patient's active series (a bare array).
  listForPatient: async (patientId: string): Promise<SeriesSummary[]> => {
    const { data } = await api.get<SeriesSummary[]>(`${BASE}/patients/${patientId}/appointment-series`);
    return data;
  },
  splitPreview: async (id: string, request: SplitSeriesRequest): Promise<SplitPreview> => {
    const { data } = await api.post<SplitPreview>(`${BASE}/appointment-series/${id}/split/preview`, request);
    return data;
  },
  split: async (id: string, request: SplitSeriesRequest): Promise<SplitResult> => {
    const { data } = await api.post<SplitResult>(`${BASE}/appointment-series/${id}/split`, request);
    return data;
  },
  end: async (id: string, fromDate: string): Promise<number> => {
    const { data } = await api.post<{ cancelled: number }>(`${BASE}/appointment-series/${id}/end`, { fromDate });
    return data.cancelled;
  },
};
