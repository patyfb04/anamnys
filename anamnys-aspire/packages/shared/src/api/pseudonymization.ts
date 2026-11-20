import api from "@anamnys/shared/api/client";
import type { PseudonymizationBackfillStatus } from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me";

export const pseudonymizationApi = {
  // 202 accepted; 404 unknown note; 409 not failed; 410 the held text is gone.
  retry: async (noteId: string): Promise<void> => {
    await api.post(`${BASE}/notes/${noteId}/pseudonymization/retry`);
  },
  backfillStatus: async (): Promise<PseudonymizationBackfillStatus> => {
    const { data } = await api.get<PseudonymizationBackfillStatus>(`${BASE}/pseudonymization/backfill`);
    return data;
  },
  // 202 with the status; 409 when already running.
  startBackfill: async (): Promise<PseudonymizationBackfillStatus> => {
    const { data } = await api.post<PseudonymizationBackfillStatus>(`${BASE}/pseudonymization/backfill`);
    return data;
  },
};
