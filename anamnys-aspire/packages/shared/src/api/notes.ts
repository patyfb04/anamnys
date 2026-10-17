import api from "@anamnys/shared/api/client";
import type {
  CreateNoteRequest,
  LiveNoteFormat,
  Note,
  NoteCandidate,
  NoteDetail,
  NoteSearchRequest,
  NoteSearchResponse,
} from "@anamnys/shared/lib/types";

export const notesApi = {
  search: async (request: NoteSearchRequest): Promise<NoteSearchResponse> => {
    const { data } = await api.post<NoteSearchResponse>(`/phi/providers/me/notes/search`, request);
    return data;
  },
  create: async (request: CreateNoteRequest): Promise<string> => {
    const { data } = await api.post<{ noteId: string }>(`/phi/providers/me/notes`, request);
    return data.noteId;
  },
  candidates: async (patientId: string): Promise<NoteCandidate[]> => {
    const { data } = await api.get<NoteCandidate[]>(`/phi/providers/me/patients/${patientId}/note-candidates`);
    return data;
  },
  getDetail: async (noteId: string): Promise<NoteDetail> => {
    const { data } = await api.get<NoteDetail>(`/phi/providers/me/notes/${noteId}`);
    return data;
  },
  restructure: async (noteId: string, format: LiveNoteFormat): Promise<void> => {
    await api.post(`/phi/providers/me/notes/${noteId}/restructure`, { format });
  },
  get: async (noteId: string): Promise<Note> => {
    const { data } = await api.get<Note>(`/notes/${noteId}`);
    return data;
  },
  update: async (noteId: string, patch: Partial<Note>): Promise<Note> => {
    const { data } = await api.patch<Note>(`/notes/${noteId}`, patch);
    return data;
  },
  editSection: async (noteId: string, key: string, content: string, expectedRevision: number): Promise<{ revision: number }> => {
    const { data } = await api.put<{ revision: number }>(`/phi/providers/me/notes/${noteId}/sections/${key}`, {
      content,
      expectedRevision,
    });
    return data;
  },
  createBlankSections: async (noteId: string): Promise<void> => {
    await api.post(`/phi/providers/me/notes/${noteId}/sections/blank`);
  },
  sign: async (noteId: string): Promise<void> => {
    await api.post(`/phi/providers/me/notes/${noteId}/sign`);
  },
  export: async (noteId: string): Promise<{ url: string }> => {
    const { data } = await api.post<{ url: string }>(`/notes/${noteId}/export`);
    return data;
  },
};
