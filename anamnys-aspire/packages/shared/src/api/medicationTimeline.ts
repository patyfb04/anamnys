import api from "@anamnys/shared/api/client";
import type { MedicationDraft, MedicationFromFactRequest, PatientTimeline } from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me";

// Medication records from document facts and the patient timeline (F-09). Only ids and dates travel
// in URLs; drug names and doses stay in bodies.
export const medicationTimelineApi = {
  draft: async (documentId: string, factId: string): Promise<MedicationDraft> => {
    const { data } = await api.get<MedicationDraft>(`${BASE}/external-documents/${documentId}/facts/${factId}/medication-draft`);
    return data;
  },
  record: async (documentId: string, factId: string, request: MedicationFromFactRequest): Promise<string> => {
    const { data } = await api.post<{ id: string }>(`${BASE}/external-documents/${documentId}/facts/${factId}/medication`, request);
    return data.id;
  },
  timeline: async (patientId: string, from?: string, to?: string): Promise<PatientTimeline> => {
    const { data } = await api.get<PatientTimeline>(`${BASE}/patients/${patientId}/timeline`, { params: { from, to } });
    return data;
  },
};
