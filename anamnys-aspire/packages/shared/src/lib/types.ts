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
  contactEmail: string;
  dateOfBirth: string | null;
  diagnoses?: DiagnosisInput[];
  medications?: MedicationInput[];
  treatmentObjectives?: string[];
}

export interface UpdatePatientRequest {
  firstName: string;
  lastName: string;
  contactEmail: string;
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

// Patient-portal access for a record (design/specs/2026-10-03-portal-invitation-design.md).
export type PortalStatus = "none" | "invited" | "expired" | "active";

export interface PatientDetail {
  id: string;
  firstName: string;
  lastName: string;
  dateOfBirth: string | null;
  contactEmail: string | null;
  portalEmail: string | null; // login email; set only when hasPortalAccount
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
  portalStatus: PortalStatus;
  invitationSentAt: string | null;
  invitationExpiresAt: string | null;
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
  contactEmail: string | null;
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

// Patient portal (design/specs/2026-10-04-patient-portal-sessions-design.md): schedule
// fields only, never clinical content.
export type SessionStatus = "scheduled" | "confirmed" | "attended" | "cancelled" | "no_show";

export interface PortalSession {
  id: string;
  startsAt: string;
  endsAt: string;
  timezone: string;
  modality: string; // "online" | "presencial"
  status: SessionStatus;
}

export interface PortalProvider {
  providerId: string;
  name: string;
  crp: string | null;
  nextSession: PortalSession | null;
}

export interface PortalSessionsPage {
  items: PortalSession[];
  totalCount: number;
  page: number;
  pageSize: number;
}

// Provider calendar (design/specs/2026-10-05-provider-calendar-design.md).
export type AppointmentStatus = "scheduled" | "confirmed" | "attended" | "cancelled" | "no_show";
export type AppointmentModality = "online" | "presencial";

export interface Appointment {
  id: string;
  patientId: string;
  patientName: string;
  startsAt: string; // ISO instant
  endsAt: string;
  timezone: string;
  modality: AppointmentModality;
  status: AppointmentStatus;
  cancellationReason: string | null;
  confirmationDeadlineAt: string | null;
  patientHasEmail: boolean;
  hasNote: boolean;
}

// Public confirmation link (design/specs/2026-10-07-appointment-notifications-design.md).
export interface ConfirmationView {
  status: "pending" | "confirmed" | "invalid";
  providerName: string | null;
  startsAt: string | null;
  endsAt: string | null;
  timezone: string | null;
  modality: string | null;
}

export interface UpdateAppointmentRequest {
  startsAt: string; // ISO instant
  durationMinutes: number;
  modality: AppointmentModality;
}

export interface CreateAppointmentRequest extends UpdateAppointmentRequest {
  patientId: string;
}

// Provider notifications and booking policy (/api/phi/providers/me/...).
export type NotificationKind =
  | "appointment_confirmed"
  | "appointment_auto_cancelled"
  | "account_export_ready"
  | "unsigned_note"
  | "retention_due";

export interface ProviderNotification {
  id: string;
  kind: NotificationKind;
  appointmentId: string | null;
  patientName: string | null;
  startsAt: string | null; // ISO instant
  exportId: string | null;
  createdAt: string;
  readAt: string | null;
}

// Full account export (design/specs/2026-10-23-account-export-design.md).
export type AccountExportStatus = "queued" | "running" | "done" | "failed" | "expired";

export interface AccountExport {
  id: string;
  status: AccountExportStatus;
  requestedAt: string;
  completedAt: string | null;
  sizeBytes: number | null;
  expiresAt: string | null;
}

export interface AccountExportDownload {
  url: string;
  expiresAt: string;
}

export interface NotificationsPage {
  items: ProviderNotification[];
  unreadCount: number;
  page: number;
  pageSize: number;
}

export type AutoCancelMode = "off" | "after_email" | "before_session";

export interface BookingPolicy {
  autoCancelMode: AutoCancelMode;
  autoCancelHours: number;
}

// Live session (design/specs/2026-10-10-live-session-transcription-design.md).
export type LiveNoteFormat = "DAP" | "SOAP" | "Biopsychosocial";
// Portuguese only while the product serves Brazil.
export type LiveLanguage = "pt";

// Server status strings, as stored in Notes.Status.
export type NoteDetailStatus = "Draft" | "Processing" | "ReadyForReview" | "Signed" | "Exported" | "Failed";
export type NoteFailureReason = "no_speech" | "transcription_error" | "server_restart";

export type StructureStatus = "pending" | "done" | "failed";
export type StructureFailure = "model_error" | "too_long" | "server_restart";

export interface NoteSectionView {
  key: string;
  content: string;
  position: number;
  generatedAt: string;
  editedAt: string | null;
  revision: number;
}

export interface NoteDetail {
  id: string;
  patientId: string;
  patientName: string;
  sessionId: string | null;
  status: NoteDetailStatus;
  inputMode: string;
  format: string;
  rawTranscript: string | null;
  failureReason: NoteFailureReason | null;
  createdAt: string;
  structureStatus: StructureStatus | null;
  structureFailure: StructureFailure | null;
  sections: NoteSectionView[];
  signedAt: string | null;
  signedByCrp: string | null;
  providerHasCrp: boolean;
  sectionsByAi: boolean;
  exportedAt: string | null;
}

export type NoteListFilter = "pending" | "signed" | "failed";
export type NoteMode = "ai" | "manual";

export interface NoteListItem {
  id: string;
  patientId: string;
  patientName: string;
  status: NoteDetailStatus;
  structureStatus: StructureStatus | null;
  inputMode: string;
  format: string;
  createdAt: string;
  signedAt: string | null;
  appointmentStartsAt: string | null;
  excerptId?: string | null;
  excerptKind?: ExcerptKind | null;
  snippet?: string | null;
}

export interface NoteSearchRequest {
  status?: NoteListFilter;
  patientId?: string;
  name?: string;
  termId?: string;
  page?: number;
  pageSize?: number;
}

export interface NoteSearchResponse {
  items: NoteListItem[];
  hasMore: boolean;
}

export type ThemeTermStatus = "active" | "retired" | "superseded";

export interface ThemeTerm {
  id: string;
  code: string;
  label: string;
  parentId: string | null;
  status: ThemeTermStatus;
  successorIds: string[];
}

export interface NoteTheme {
  id: string;
  termId: string;
  code: string;
  label: string;
  parentLabel: string | null;
  termStatus: ThemeTermStatus;
  origin: "ai" | "human";
  proposed: boolean;
  addedAt: string;
  excerptId: string | null;
  excerptKind: ExcerptKind | null;
}

export type ExcerptKind = "exact" | "section";

export interface ThemeSelection {
  sectionKey: string;
  start?: number;
  end?: number;
}

export interface ExcerptPassage {
  id: string;
  /** "note" or "document". */
  sourceType?: "note" | "document";
  kind: ExcerptKind;
  patientId: string;
  datedAt: string;
  start?: number;
  end?: number;
  // Note passage.
  noteId?: string;
  sessionStartsAt?: string | null;
  sectionKey?: string;
  sectionTitle?: string;
  sectionText?: string;
  // Document passage: one page of the read text.
  documentId?: string;
  documentKind?: ExternalDocumentKind;
  pageNumber?: number;
  pageText?: string;
}

export interface ThemeSuggestion {
  id: string;
  code: string;
  label: string;
}

export interface NoteCandidate {
  appointmentId: string;
  startsAt: string;
  modality: AppointmentModality;
  status: AppointmentStatus;
}

export interface CreateNoteRequest {
  patientId: string;
  appointmentId: string | null;
  format: LiveNoteFormat;
  mode: NoteMode;
  text: string | null;
}

export interface NoteSnapshot {
  status: NoteDetailStatus;
  queuePosition: number;
  failureReason: NoteFailureReason | null;
}

export type ScaleRegime = "available" | "needs_opt_in" | "opted_in" | "unavailable";

export interface InstrumentItem {
  id: string;
  code: string;
  name: string;
  minScore: number;
  maxScore: number;
  regime: ScaleRegime;
  explanation: string;
}

export interface ScaleEntry {
  id: string;
  score: number;
  appliedAt: string;
  sessionId: string | null;
  appointmentStartsAt: string | null;
}

export interface ScaleSeries {
  instrumentId: string;
  code: string;
  name: string;
  minScore: number;
  maxScore: number;
  entries: ScaleEntry[];
}

export type DocumentStatus = "draft" | "signed" | "superseded";
export type DocumentElementOrigin = "system" | "pdf" | "provider";
export type DocumentCheckMode = "blocking" | "advisory";

export interface DocumentElement {
  code: string;
  origin: DocumentElementOrigin;
  label: string;
  hint: string | null;
}

export interface DocumentProhibition {
  code: string;
  byConstruction: boolean;
  confirmationText: string | null;
}

export interface DocumentKindInfo {
  kind: string;
  title: string;
  rulesetVersion: string;
  elements: DocumentElement[];
  prohibitions: DocumentProhibition[];
}

export interface DocumentSummary {
  id: string;
  kind: string;
  title: string;
  status: DocumentStatus;
  createdAt: string;
  signedAt: string | null;
  supersededBy: string | null;
}

export interface DocumentListItem {
  id: string;
  patientId: string;
  patientName: string;
  kind: string;
  title: string;
  status: DocumentStatus;
  createdAt: string;
  signedAt: string | null;
  supersededBy: string | null;
}

export interface DocumentSearchRequest {
  status?: DocumentStatus;
  patientId?: string;
  name?: string;
  page?: number;
}

export interface DocumentSearchResponse {
  items: DocumentListItem[];
  hasMore: boolean;
}

export interface DocumentCheck {
  missingElements: string[];
  missingConfirmations: string[];
  elementsComplete: boolean;
  confirmationsComplete: boolean;
}

export interface DocumentTeamMember {
  name: string;
  category: string;
  registry: string;
  analysis: string;
}

export interface DocumentDetail {
  team: DocumentTeamMember[];
  id: string;
  patientId: string;
  patientName: string;
  kind: string;
  title: string;
  status: DocumentStatus;
  fields: Record<string, string>;
  confirmations: string[];
  check: DocumentCheck;
  mode: DocumentCheckMode;
  createdAt: string;
  signedAt: string | null;
  signedByCrp: string | null;
  supersededBy: string | null;
  exportedAt: string | null;
  providerHasCrp: boolean;
  hasPracticeCity: boolean;
  /** Frozen at signing; null for drafts. */
  signedCity: string | null;
  signedByName: string | null;
  signedPatientName: string | null;
}

export interface RecordScaleRequest {
  instrumentId: string;
  score: number;
  appliedAt: string;
  sessionId: string | null;
}

export type ExternalDocumentKind = "laudo" | "receita" | "exame" | "encaminhamento" | "relatorio_externo" | "outro";
export type ExternalScanStatus = "pending" | "clean" | "infected" | "failed";

export interface ExternalDocumentItem {
  id: string;
  patientId: string;
  patientName: string | null;
  /** Only in the provider-wide search; null in a patient's own list. */
  patientArchived?: boolean | null;
  fileName: string;
  kind: ExternalDocumentKind;
  documentDate: string | null;
  description: string | null;
  scanStatus: ExternalScanStatus;
  byteSize: number;
  uploadedAt: string;
  scannedAt: string | null;
  /** Reading of the text (F-08); null until the scan is clean. */
  readStatus?: ExternalReadStatus | null;
  pendingFacts?: number;
  /** Facts are being extracted right now. */
  extracting?: boolean;
}

export type ExternalReadStatus = "pending" | "reading" | "done" | "failed";
export type FactKind = "issuer" | "registration" | "issue_date" | "icd" | "medication" | "conclusion";
export type FactStatus = "pending" | "confirmed" | "corrected" | "discarded";

export interface DocumentFact {
  id: string;
  kind: FactKind;
  /** As read from the document. */
  value: string;
  correctedValue: string | null;
  page: number | null;
  quote: string | null;
  status: FactStatus;
  excerptRefId: string | null;
  createdAt: string;
  confirmedAt: string | null;
  /** True when a medication record was made from this fact (F-09); absent until the server reports it. */
  recorded?: boolean;
}

export interface DocumentFacts {
  documentId: string;
  readStatus: ExternalReadStatus | null;
  pageCount: number | null;
  pagesTruncated: boolean;
  extracting: boolean;
  facts: DocumentFact[];
}

/** A confirmed or corrected fact of the patient record. `value` is the correction when there is one. */
export interface DeclaredFact {
  id: string;
  documentId: string;
  documentKind: ExternalDocumentKind;
  documentDate: string | null;
  uploadedAt: string;
  kind: FactKind;
  value: string;
  readValue: string;
  corrected: boolean;
  page: number | null;
  quote: string | null;
  excerptRefId: string | null;
  confirmedAt: string | null;
}

export interface ExternalDocumentSearchRequest {
  kind?: ExternalDocumentKind;
  patientId?: string;
  name?: string;
  page?: number;
}

export interface ExternalDocumentSearchResponse {
  items: ExternalDocumentItem[];
  hasMore: boolean;
}

export interface ExternalDocumentUploadInput {
  file: File;
  kind: ExternalDocumentKind;
  documentDate?: string;
  description?: string;
}

// Medication timeline (F-09). Dates are yyyy-MM-dd.
export type MedicationFactAction = "new" | "dose_change" | "stop";

export interface MedicationDraft {
  factId: string;
  value: string;
  quote: string | null;
  page: number | null;
  status: FactStatus;
  recorded: boolean;
  prefill: {
    drug: string | null;
    dose: string | null;
    posology: string | null;
    startedOn: string | null;
    endedOn: string | null;
  };
  activeMatches: {
    id: string;
    treatmentId: string;
    drug: string;
    dose: string | null;
    posology: string | null;
    startedOn: string;
  }[];
}

export interface MedicationFromFactRequest {
  action: MedicationFactAction;
  targetMedicationId?: string;
  drug: string;
  dose?: string;
  posology?: string;
  startedOn: string;
  endedOn?: string;
}

export interface TimelineOrigin {
  kind: "manual" | "document" | "document_deleted";
  documentId: string | null;
  documentKind: ExternalDocumentKind | null;
  documentDate: string | null;
  page: number | null;
  excerptId: string | null;
}

export interface TimelineSegment {
  medicationId: string;
  dose: string | null;
  posology: string | null;
  startedOn: string;
  endedOn: string | null;
  origin: TimelineOrigin;
}

export interface TimelineMedication {
  treatmentId: string;
  drug: string;
  segments: TimelineSegment[];
}

export interface TimelineScale {
  instrumentId: string;
  code: string;
  name: string;
  minScore: number;
  maxScore: number;
  points: { applicationId: string; score: number; appliedOn: string }[];
}

export type AttendanceKind =
  | "attended"
  | "attended_unscheduled"
  | "no_show"
  | "cancelled_provider"
  | "cancelled_patient"
  | "unrecorded";

export interface TimelineAppointment {
  id: string;
  on: string;
  kind: AttendanceKind;
}

export interface PatientTimeline {
  appointments: TimelineAppointment[];
  medications: TimelineMedication[];
  scales: TimelineScale[];
}

export interface AttendanceSummary {
  counts: {
    attended: number;
    attendedUnscheduled: number;
    noShow: number;
    cancelledByProvider: number;
    cancelledByPatient: number;
    unrecorded: number;
  };
  cancellationLeadHours: { median: number; min: number; max: number; count: number } | null;
  intervalDays: { median: number; min: number; max: number; last: number } | null;
  daysSinceLast: number | null;
}

// Retention and compliance panel (F-06): /api/phi/providers/me/compliance, settings/retention, disposal terms.
export interface UnsignedNoteItem {
  noteId: string;
  patientId: string;
  patientName: string;
  createdAt: string;
  ageDays: number;
}

export interface RetentionItem {
  patientId: string;
  patientName: string;
  lastRecordAt: string; // yyyy-MM-dd
  retentionUntil: string; // yyyy-MM-dd
  expired: boolean;
  hasFutureAppointment: boolean;
}

export interface ComplianceDocumentItem {
  documentId: string;
  patientId: string;
  patientName: string;
  kind: string;
  status: "draft" | "signed";
  missing: string[]; // element codes
  createdAt: string;
}

export interface ComplianceSummary {
  unsignedNotes: UnsignedNoteItem[];
  retention: RetentionItem[];
  documents: ComplianceDocumentItem[];
}

export interface RetentionSettings {
  guardYears: number;
  unsignedNoteDays: number;
}

export interface DisposalTerm {
  id: string;
  patientId: string;
  patientName: string;
  createdAt: string;
  periodFrom: string;
  periodTo: string;
  guardYears: number;
  basis: string;
  counts: Record<string, number>;
}
