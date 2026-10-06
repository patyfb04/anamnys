import api from "@anamnys/shared/api/client";
import type {
  Appointment,
  AppointmentStatus,
  CreateAppointmentRequest,
  UpdateAppointmentRequest,
} from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me/appointments";

export const appointmentsApi = {
  // Repeated `status` keys, not axios's default `status[]=`, which the server does not bind.
  list: async (from: Date, to: Date, statuses: AppointmentStatus[]): Promise<Appointment[]> => {
    const params = new URLSearchParams({ from: from.toISOString(), to: to.toISOString() });
    statuses.forEach((s) => params.append("status", s));
    const { data } = await api.get<{ items: Appointment[] }>(BASE, { params });
    return data.items;
  },
  create: async (request: CreateAppointmentRequest): Promise<string> => {
    const { data } = await api.post<{ id: string }>(BASE, request);
    return data.id;
  },
  update: async (id: string, request: UpdateAppointmentRequest): Promise<void> => {
    await api.put(`${BASE}/${id}`, request);
  },
  setStatus: async (id: string, status: AppointmentStatus, reason?: string): Promise<void> => {
    await api.post(`${BASE}/${id}/status`, { status, reason: reason || undefined });
  },
};
