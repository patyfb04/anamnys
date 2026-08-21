import api from "@/api/client";
import { Patient, CreatePatientRequest, PaginatedResponse } from "@/lib/types";

export const patientsApi = {
  list: async (page = 1, pageSize = 20): Promise<PaginatedResponse<Patient>> => {
    const { data } = await api.get<PaginatedResponse<Patient>>("/patients", {
      params: { page, pageSize },
    });
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
