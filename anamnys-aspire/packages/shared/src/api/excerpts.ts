import api from "@anamnys/shared/api/client";
import type { ExcerptPassage } from "@anamnys/shared/lib/types";

export const excerptsApi = {
  get: async (id: string): Promise<ExcerptPassage> => {
    const { data } = await api.get<ExcerptPassage>(`/phi/providers/me/excerpts/${id}`);
    return data;
  },
};
