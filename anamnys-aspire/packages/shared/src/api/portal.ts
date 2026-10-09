import api from "@anamnys/shared/api/client";
import type { PortalProvider, PortalSessionsPage } from "@anamnys/shared/lib/types";

// Patient-portal reads: the providers linked to the signed-in account and their sessions.
export const portalApi = {
  providers: async (): Promise<PortalProvider[]> => {
    const { data } = await api.get<PortalProvider[]>("/phi/patients/me/providers");
    return data;
  },
  sessions: async (providerId: string, scope: "upcoming" | "past", page: number): Promise<PortalSessionsPage> => {
    const { data } = await api.get<PortalSessionsPage>(`/phi/patients/me/providers/${providerId}/sessions`, {
      params: { scope, page },
    });
    return data;
  },
  confirm: async (appointmentId: string): Promise<void> => {
    await api.post(`/phi/patients/me/appointments/${appointmentId}/confirm`);
  },
};
