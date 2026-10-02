import api from "@anamnys/shared/api/client";
import type {
  CreatePatientRequest,
  DiagnosisInput,
  MedicationInput,
  ObjectiveInput,
  PatientDetail,
  PatientSearchRequest,
  PatientSearchResponse,
  UpdatePatientRequest,
} from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me/patients";

// CRUD for one kind of clinical item under a patient.
function itemsApi<TInput>(kind: "diagnoses" | "medications" | "objectives") {
  return {
    add: async (patientId: string, input: TInput): Promise<string> => {
      const { data } = await api.post<{ id: string }>(`${BASE}/${patientId}/${kind}`, input);
      return data.id;
    },
    update: async (patientId: string, itemId: string, input: TInput): Promise<void> => {
      await api.put(`${BASE}/${patientId}/${kind}/${itemId}`, input);
    },
    remove: async (patientId: string, itemId: string): Promise<void> => {
      await api.delete(`${BASE}/${patientId}/${kind}/${itemId}`);
    },
  };
}

export const patientsApi = {
  // POST with a JSON body, not GET with query params: name and email filters are PHI and
  // must never appear in a URL.
  search: async (request: PatientSearchRequest): Promise<PatientSearchResponse> => {
    const { data } = await api.post<PatientSearchResponse>(`${BASE}/search`, request);
    return data;
  },
  create: async (request: CreatePatientRequest): Promise<string> => {
    const { data } = await api.post<{ id: string }>(BASE, request);
    return data.id;
  },
  get: async (id: string): Promise<PatientDetail> => {
    const { data } = await api.get<PatientDetail>(`${BASE}/${id}`);
    return data;
  },
  update: async (id: string, request: UpdatePatientRequest): Promise<void> => {
    await api.put(`${BASE}/${id}`, request);
  },
  remove: async (id: string): Promise<void> => {
    await api.delete(`${BASE}/${id}`);
  },
  archive: async (id: string): Promise<void> => {
    await api.post(`${BASE}/${id}/archive`);
  },
  unarchive: async (id: string): Promise<void> => {
    await api.post(`${BASE}/${id}/unarchive`);
  },
  diagnoses: itemsApi<DiagnosisInput>("diagnoses"),
  medications: itemsApi<MedicationInput>("medications"),
  objectives: itemsApi<ObjectiveInput>("objectives"),
};
