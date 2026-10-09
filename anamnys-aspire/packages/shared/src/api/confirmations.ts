import api from "@anamnys/shared/api/client";
import type { ConfirmationView } from "@anamnys/shared/lib/types";

// Public, no session: the token in the e-mailed link is the only credential.
export const confirmationsApi = {
  view: async (token: string): Promise<ConfirmationView> => {
    const { data } = await api.get<ConfirmationView>(`/public/appointment-confirmations/${encodeURIComponent(token)}`);
    return data;
  },
  confirm: async (token: string): Promise<void> => {
    await api.post(`/public/appointment-confirmations/${encodeURIComponent(token)}`);
  },
};
