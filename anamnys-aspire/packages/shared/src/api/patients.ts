import api from "@anamnys/shared/api/client";
import type {
  Patient,
  CreatePatientRequest,
  PatientSearchRequest,
  PatientSearchResponse,
} from "@anamnys/shared/lib/types";

export const patientsApi = {
  // POST with a JSON body, not GET with query params: name and email filters are PHI and
  // must never appear in a URL.
  search: async (request: PatientSearchRequest): Promise<PatientSearchResponse> => {
    const { data } = await api.post<PatientSearchResponse>("/phi/providers/me/patients/search", request);
    return data;
  },
  get: async (id: string): Promise<Patient> => {
    const { data } = await api.get<Patient>(`/patients/${id}`);
    return data;
  },
  create: async (patient: CreatePatientRequest): Promise<Patient> => {
    const { data } = await api.post<Patient>("/patients", patient);
    return data;
  },
};
