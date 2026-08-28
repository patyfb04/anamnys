// ─── Domain types ─────────────────────────────────────────────────────────────

export interface Patient {
  id: string;
  firstName: string;
  lastName: string;
  dateOfBirth: string;
  diagnoses: string[];
  currentMedications: string[];
  treatmentPlan?: string;
  preferredLanguage?: string; // ISO 639-1 code, e.g. "en" — undefined means auto-detect
  lastVisit?: string;
}

export interface CreatePatientRequest {
  firstName: string;
  lastName: string;
  dateOfBirth: string; // ISO date string, e.g. "1990-01-31"
  diagnoses?: string[];
  currentMedications?: string[];
  treatmentPlan?: string;
  preferredLanguage?: string;
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

export interface AuthUser {
  id: string;
  email: string;
  name: string;
  specialty: Specialty;
  token: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  email: string;
  password: string;
  name: string;
  specialty: Specialty;
}

// Envelope returned by /auth/login, /auth/register, and /auth/login/2fa. Exactly one of
// (challengeToken, user) is set: requiresTwoFactor=true means the password step succeeded and
// the client must call completeTwoFactorLogin with the challengeToken + a code.
export interface LoginResponse {
  requiresTwoFactor: boolean;
  challengeToken?: string | null;
  user?: AuthUser | null;
}

// ─── Account / 2FA types ───────────────────────────────────────────────────────

export interface TwoFactorStatus {
  enabled: boolean;
}

export interface TwoFactorSetup {
  secret: string;
  otpauthUri: string;
}

export interface TwoFactorVerifyResult {
  recoveryCodes: string[];
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
