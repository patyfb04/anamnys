import api from "@anamnys/shared/api/client";

export type AdminThemeStatus = "active" | "retired" | "superseded";

export interface AdminThemeTerm {
  id: string;
  code: string;
  label: string;
  parentId: string | null;
  status: AdminThemeStatus;
  introducedOn: string;
  successorIds: string[];
  activeTagCount: number;
}

export interface NewThemeTerm {
  code: string;
  label: string;
}

export type MergeTarget = { existingId: string } | NewThemeTerm;

export const adminThemesApi = {
  list: async (): Promise<AdminThemeTerm[]> => {
    const { data } = await api.get<AdminThemeTerm[]>(`/admin/theme-vocabulary`);
    return data;
  },
  create: async (term: NewThemeTerm & { parentId?: string }): Promise<string> => {
    const { data } = await api.post<{ id: string }>(`/admin/theme-vocabulary/terms`, term);
    return data.id;
  },
  rename: async (id: string, label: string): Promise<void> => {
    await api.put(`/admin/theme-vocabulary/terms/${id}/label`, { label });
  },
  retire: async (id: string): Promise<void> => {
    await api.post(`/admin/theme-vocabulary/terms/${id}/retire`);
  },
  split: async (id: string, terms: NewThemeTerm[]): Promise<string[]> => {
    const { data } = await api.post<{ ids: string[] }>(`/admin/theme-vocabulary/terms/${id}/split`, { terms });
    return data.ids;
  },
  merge: async (sourceIds: string[], target: MergeTarget): Promise<string> => {
    const { data } = await api.post<{ id: string }>(`/admin/theme-vocabulary/merge`, { sourceIds, target });
    return data.id;
  },
};
