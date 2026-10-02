// ─── Domain types ─────────────────────────────────────────────────────────────

// Patient records (/api/phi/providers/me/patients). See
// design/specs/2026-10-01-patient-records-design.md §4. Dates are "YYYY-MM-DD".
export interface DiagnosisInput {
  description: string;
  icdCode?: string | null;
  resolvedOn?: string | null;
}

export interface MedicationInput {
  drug: string;
  dose?: string | null;
  posology?: string | null;
  startedOn: string | null;
  endedOn?: string | null;
}

export interface ObjectiveInput {
  description: string;
}

export interface CreatePatientRequest {
  firstName: string;
  lastName: string;
  dateOfBirth: string | null;
  diagnoses?: DiagnosisInput[];
  medications?: MedicationInput[];
  treatmentObjectives?: string[];
}

export interface UpdatePatientRequest {
  firstName: string;
  lastName: string;
  dateOfBirth: string | null;
}

export interface DiagnosisItem {
  id: string;
  description: string;
  icdCode: string | null;
  recordedAt: string;
  resolvedOn: string | null;
}

export interface MedicationItem {
  id: string;
  drug: string;
  dose: string | null;
  posology: string | null;
  startedOn: string;
  endedOn: string | null;
}

export interface ObjectiveItem {
  id: string;
  description: string;
  createdAt: string;
}

export interface NoteSummary {
  id: string;
  status: string;
  format: string;
  createdAt: string;
  signedAt: string | null;
}

export interface PatientDetail {
  id: string;
  firstName: string;
  lastName: string;
  dateOfBirth: string | null;
  email: string | null;
  phone: string | null;
  hasPortalAccount: boolean;
  archivedAt: string | null;
  lastVisit: string | null;
  nextAppointmentAt: string | null;
  noteStatus: NoteStatusGroup;
  canDelete: boolean;
  diagnoses: DiagnosisItem[];
  medications: MedicationItem[];
  objectives: ObjectiveItem[];
  recentNotes: NoteSummary[];
}

// Keep in sync with the backend's ClinicalDraft.Domain.SupportedLanguages.
export const SUPPORTED_LANGUAGES: { code: string; label: string }[] = [
  { code: "pt", label: "Portuguese" },
];

export interface Note {
  id: string;
  patientId: string;
  providerId: string;
  status: NoteStatus;
  inputMode: InputMode;
  rawTranscript?: string;
  structuredContent?: StructuredNote;
  billingCodes?: BillingCode[];
  priorAuthLetter?: string;
  auditTrail: AuditEntry[];
  createdAt: string;
  signedAt?: string;
}

export type NoteStatus =
  | "draft"
  | "processing"
  | "ready_for_review"
  | "signed"
  | "exported";

export type InputMode =
  | "voice_batch"
  | "voice_live"
  | "voice_appointment"
  | "text";

export type Specialty = "mental_health" | "physical_therapy";

export type NoteFormat = "DAP" | "SOAP" | "PT_FUNCTIONAL" | "BIOPSYCHOSOCIAL";

export interface StructuredNote {
  format: NoteFormat;
  sections: Record<string, string>;
  generatedAt: string;
}

export interface BillingCode {
  cptCode: string;
  icd10Code: string;
  description: string;
  confidenceScore: number; // 0–1
  denialRiskScore: number; // 0–100
  modifiers?: string[];
  missingDocumentation?: string[];
}

export interface AuditEntry {
  timestamp: string;
  action: "ai_draft" | "provider_edit" | "signed" | "exported";
  fieldChanged?: string;
  previousValue?: string;
  newValue?: string;
  actorId: string;
}

// ─── Pipeline / job types ─────────────────────────────────────────────────────

export interface TranscribeJobResponse {
  jobId: string;
  status: "queued" | "processing" | "completed" | "failed";
  progress?: PipelineProgress;
  noteId?: string;
}

export interface PipelineProgress {
  step: PipelineStep;
  label: string;
  percentage: number;
}

export type PipelineStep =
  | "transcribing"
  | "structuring"
  | "extracting"
  | "billing"
  | "qa"
  | "done";

// ─── Auth types ────────────────────────────────────────────────────────────────

// Mirrors MeResponse from the server. There is deliberately no token field:
// tokens live in the server-side ticket store and never reach JavaScript.
export interface AuthUser {
  id: string;
  email: string;
  name: string;
  realm: string;
  roles: string[];
}

// ─── API response wrapper ──────────────────────────────────────────────────────

export interface ApiResponse<T> {
  data: T;
  success: boolean;
  message?: string;
}

export interface PaginatedResponse<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

// Patient list (POST /api/phi/providers/me/patients/search). See
// design/specs/2026-09-27-patient-list-design.md §4.
export type NoteStatusGroup = "pending" | "signed" | "none";
export type PatientSortBy = "name" | "lastVisit" | "nextVisit" | "noteStatus";
export type SortDir = "asc" | "desc";

export interface PatientFilters {
  name?: string;
  email?: string;
  noteStatus?: NoteStatusGroup[];
  lastVisitFrom?: string; // "YYYY-MM-DD"
  lastVisitTo?: string;
  nextVisitFrom?: string;
  nextVisitTo?: string;
  archived?: boolean; // true: archived patients only
}

export interface PatientSearchRequest extends PatientFilters {
  search?: string;
  sortBy?: PatientSortBy;
  sortDir?: SortDir;
  page?: number;
  pageSize?: number;
}

export interface PatientListItem {
  id: string;
  firstName: string;
  lastName: string;
  email: string | null;
  lastVisit: string | null;
  nextAppointmentAt: string | null;
  noteStatus: NoteStatusGroup;
}

export interface PatientSearchResponse {
  items: PatientListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}
