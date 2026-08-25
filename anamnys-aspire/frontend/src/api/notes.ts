import api from "@/api/client";
import type { Note } from "@/lib/types";

export const notesApi = {
  list: async (patientId: string): Promise<Note[]> => {
    const { data } = await api.get<Note[]>(`/notes`, { params: { patientId } });
    return data;
  },
  get: async (noteId: string): Promise<Note> => {
    const { data } = await api.get<Note>(`/notes/${noteId}`);
    return data;
  },
  update: async (noteId: string, patch: Partial<Note>): Promise<Note> => {
    const { data } = await api.patch<Note>(`/notes/${noteId}`, patch);
    return data;
  },
  sign: async (noteId: string): Promise<Note> => {
    const { data } = await api.post<Note>(`/notes/${noteId}/sign`);
    return data;
  },
  export: async (noteId: string): Promise<{ url: string }> => {
    const { data } = await api.post<{ url: string }>(`/notes/${noteId}/export`);
    return data;
  },
};
