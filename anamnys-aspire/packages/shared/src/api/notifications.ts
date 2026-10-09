import api from "@anamnys/shared/api/client";
import type { NotificationsPage } from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me/notifications";

export const notificationsApi = {
  list: async (page = 1): Promise<NotificationsPage> => {
    const { data } = await api.get<NotificationsPage>(BASE, { params: { page } });
    return data;
  },
  markRead: async (id: string): Promise<void> => {
    await api.post(`${BASE}/${id}/read`);
  },
  markAllRead: async (): Promise<void> => {
    await api.post(`${BASE}/read-all`);
  },
};
