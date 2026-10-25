import api from "@anamnys/shared/api/client";
import type { NoteTheme, ThemeSelection, ThemeSuggestion, ThemeTerm } from "@anamnys/shared/lib/types";

export const themesApi = {
  vocabulary: async (): Promise<ThemeTerm[]> => {
    const { data } = await api.get<ThemeTerm[]>(`/phi/providers/me/theme-vocabulary`);
    return data;
  },
  list: async (noteId: string): Promise<NoteTheme[]> => {
    const { data } = await api.get<NoteTheme[]>(`/phi/providers/me/notes/${noteId}/themes`);
    return data;
  },
  add: async (
    noteId: string,
    termId: string,
    fromSuggestion = false,
    selection?: ThemeSelection,
  ): Promise<string> => {
    const { data } = await api.post<{ id: string }>(`/phi/providers/me/notes/${noteId}/themes`, {
      termId,
      fromSuggestion,
      ...selection,
    });
    return data.id;
  },
  // Queues the background search for the passages; the caller polls the list.
  locate: async (noteId: string): Promise<void> => {
    await api.post(`/phi/providers/me/notes/${noteId}/themes/locate`);
  },
  remove: async (noteId: string, tagId: string): Promise<void> => {
    await api.delete(`/phi/providers/me/notes/${noteId}/themes/${tagId}`);
  },
  // The model runs on the server's CPU and can take well over the client's default 30 s timeout;
  // closing the dialog still aborts the request through the signal.
  suggest: async (noteId: string, signal?: AbortSignal): Promise<ThemeSuggestion[]> => {
    const { data } = await api.post<{ terms: ThemeSuggestion[] }>(
      `/phi/providers/me/notes/${noteId}/themes/suggest`,
      undefined,
      { signal, timeout: 180_000 },
    );
    return data.terms;
  },
};
