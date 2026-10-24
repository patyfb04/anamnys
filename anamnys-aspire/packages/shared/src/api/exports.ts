import api from "@anamnys/shared/api/client";
import type { AccountExport, AccountExportDownload } from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me/exports";

export const exportsApi = {
  latest: async (): Promise<AccountExport | null> => {
    const { data, status } = await api.get<AccountExport | "">(BASE);
    return status === 204 || !data ? null : data;
  },
  request: async (): Promise<{ id: string; status: string }> => {
    const { data } = await api.post<{ id: string; status: string }>(BASE);
    return data;
  },
  download: async (id: string): Promise<AccountExportDownload> => {
    const { data } = await api.post<AccountExportDownload>(`${BASE}/${id}/download`);
    return data;
  },
};
