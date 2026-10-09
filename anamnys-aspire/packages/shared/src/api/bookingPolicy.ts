import api from "@anamnys/shared/api/client";
import type { BookingPolicy } from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me/booking-policy";

export const bookingPolicyApi = {
  get: async (): Promise<BookingPolicy> => {
    const { data } = await api.get<BookingPolicy>(BASE);
    return data;
  },
  save: async (policy: BookingPolicy): Promise<void> => {
    await api.put(BASE, policy);
  },
};
