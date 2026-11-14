import api from "@anamnys/shared/api/client";
import type {
  IntakeAnswer,
  IntakeAnswerInput,
  IntakeDetail,
  IntakeListItem,
  IntakeTemplateDetail,
  IntakeTemplateList,
  IntakeTemplateSummary,
  IntakeTransfer,
  UpdateIntakeTemplateRequest,
} from "@anamnys/shared/lib/types";

// Structured intake (design/specs/2026-11-12-structured-intake-design.md §4). Only ids travel in URLs;
// answers and question text stay in bodies.
const BASE = "/phi/providers/me";

export const intakeApi = {
  templates: async (): Promise<IntakeTemplateList> => {
    const { data } = await api.get<IntakeTemplateList>(`${BASE}/intake-templates`);
    return data;
  },
  template: async (id: string): Promise<IntakeTemplateDetail> => {
    const { data } = await api.get<IntakeTemplateDetail>(`${BASE}/intake-templates/${id}`);
    return data;
  },
  adapt: async (id: string, name?: string): Promise<IntakeTemplateDetail> => {
    const { data } = await api.post<IntakeTemplateDetail>(`${BASE}/intake-templates/${id}/adapt`, name ? { name } : {});
    return data;
  },
  // Returns the template as saved: a new id and version when the template was already in use.
  updateTemplate: async (id: string, request: UpdateIntakeTemplateRequest): Promise<IntakeTemplateDetail> => {
    const { data } = await api.put<IntakeTemplateDetail>(`${BASE}/intake-templates/${id}`, request);
    return data;
  },
  archiveTemplate: async (id: string): Promise<IntakeTemplateSummary> => {
    const { data } = await api.post<IntakeTemplateSummary>(`${BASE}/intake-templates/${id}/archive`);
    return data;
  },
  // null = the Anamnys default template.
  setDefault: async (defaultTemplateId: string | null): Promise<string> => {
    const { data } = await api.put<{ defaultTemplateId: string }>(`${BASE}/intake-settings`, { defaultTemplateId });
    return data.defaultTemplateId;
  },
  listForPatient: async (patientId: string): Promise<IntakeListItem[]> => {
    const { data } = await api.get<IntakeListItem[]>(`${BASE}/patients/${patientId}/intakes`);
    return data;
  },
  start: async (patientId: string): Promise<string> => {
    const { data } = await api.post<{ id: string }>(`${BASE}/patients/${patientId}/intakes`);
    return data.id;
  },
  get: async (id: string): Promise<IntakeDetail> => {
    const { data } = await api.get<IntakeDetail>(`${BASE}/intakes/${id}`);
    return data;
  },
  saveAnswers: async (id: string, answers: IntakeAnswerInput[]): Promise<IntakeAnswer[]> => {
    const { data } = await api.put<IntakeAnswer[]>(`${BASE}/intakes/${id}/answers`, answers);
    return data;
  },
  // Creates the Anamnese note (or returns the one the intake already has).
  generateNote: async (id: string): Promise<string> => {
    const { data } = await api.post<{ noteId: string }>(`${BASE}/intakes/${id}/note`);
    return data.noteId;
  },
  // "Editar respostas": deletes the unsigned note so the answers can change again.
  unlock: async (id: string): Promise<IntakeDetail> => {
    const { data } = await api.post<IntakeDetail>(`${BASE}/intakes/${id}/unlock`);
    return data;
  },
  discard: async (id: string): Promise<void> => {
    await api.delete(`${BASE}/intakes/${id}`);
  },
  transfers: async (id: string): Promise<IntakeTransfer[]> => {
    const { data } = await api.get<IntakeTransfer[]>(`${BASE}/intakes/${id}/transfers`);
    return data;
  },
  confirmTransfer: async (id: string, transferId: string): Promise<IntakeTransfer> => {
    const { data } = await api.post<IntakeTransfer>(`${BASE}/intakes/${id}/transfers/${transferId}/confirm`);
    return data;
  },
  ignoreTransfer: async (id: string, transferId: string): Promise<IntakeTransfer> => {
    const { data } = await api.post<IntakeTransfer>(`${BASE}/intakes/${id}/transfers/${transferId}/ignore`);
    return data;
  },
};
