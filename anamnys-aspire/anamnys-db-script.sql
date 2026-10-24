CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE TABLE "AccessLogs" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"ActorType" text NOT NULL,
	"Actor" text NOT NULL,
	"Scope" text NOT NULL,
	"TicketRef" text,
	"AuthorizedAt" timestamp with time zone,
	"At" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "AccessLogs_ActorType_ck" CHECK (("ActorType" = ANY (ARRAY['provider'::text, 'patient'::text, 'staff'::text, 'system'::text])))
);
CREATE TABLE "AccountExports" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"RequestedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"CompletedAt" timestamp with time zone,
	"ObjectKey" text,
	"Format" text DEFAULT 'zip' NOT NULL,
	"Scope" text DEFAULT 'full' NOT NULL
);
CREATE TABLE "AppointmentConfirmations" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"AppointmentId" uuid NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ExpiresAt" timestamp with time zone NOT NULL,
	"DeadlineAt" timestamp with time zone,
	"ConfirmedAt" timestamp with time zone,
	"ConfirmedBy" text,
	"ClosedAt" timestamp with time zone,
	CONSTRAINT "AppointmentConfirmations_ConfirmedBy_ck" CHECK ((("ConfirmedBy" IS NULL) = ("ConfirmedAt" IS NULL)) AND (("ConfirmedBy" IS NULL) OR ("ConfirmedBy" = ANY (ARRAY['patient'::text, 'provider'::text])))),
	CONSTRAINT "AppointmentConfirmations_Closed_ck" CHECK (("ConfirmedAt" IS NULL) OR ("ClosedAt" IS NULL))
);
CREATE TABLE "AppointmentConfirmationTokens" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ConfirmationId" uuid NOT NULL,
	"TokenHash" bytea NOT NULL CONSTRAINT "AppointmentConfirmationTokens_TokenHash_key" UNIQUE,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE TABLE "Appointments" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"PatientId" uuid NOT NULL,
	"SeriesId" uuid,
	"SessionId" uuid,
	"HoldId" uuid,
	"OfferingId" uuid,
	"ConnectionId" uuid,
	"StartsAt" timestamp with time zone NOT NULL,
	"EndsAt" timestamp with time zone NOT NULL,
	"Timezone" text DEFAULT 'America/Sao_Paulo' NOT NULL,
	"Modality" text DEFAULT 'online' NOT NULL,
	"Status" text DEFAULT 'scheduled' NOT NULL,
	"CreatedBy" text DEFAULT 'provider' NOT NULL,
	"PriceCentsSnapshot" integer,
	"OverridesSeries" boolean DEFAULT false NOT NULL,
	"ExternalEventId" text,
	"CancelledAt" timestamp with time zone,
	"CancelledBy" text,
	"CancellationReason" text,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"Slot" tstzrange GENERATED ALWAYS AS (CASE WHEN "Status" <> 'cancelled' THEN tstzrange("StartsAt", "EndsAt", '[)') END) STORED,
	CONSTRAINT "Appointments_Cancel_ck" CHECK ((("Status" = 'cancelled'::text) = ("CancelledAt" IS NOT NULL))),
	CONSTRAINT "Appointments_CancelledBy_ck" CHECK ((("CancelledBy" IS NULL) OR ("CancelledBy" = ANY (ARRAY['provider'::text, 'patient'::text, 'system'::text])))),
	CONSTRAINT "Appointments_CreatedBy_ck" CHECK (("CreatedBy" = ANY (ARRAY['provider'::text, 'patient'::text]))),
	CONSTRAINT "Appointments_no_overlap" UNIQUE ("ProviderId", "Slot" WITHOUT OVERLAPS),
	CONSTRAINT "Appointments_Price_ck" CHECK ((("PriceCentsSnapshot" IS NULL) OR ("PriceCentsSnapshot" >= 0))),
	CONSTRAINT "Appointments_Range_ck" CHECK (("EndsAt" > "StartsAt")),
	CONSTRAINT "Appointments_Status_ck" CHECK (("Status" = ANY (ARRAY['scheduled'::text, 'confirmed'::text, 'attended'::text, 'cancelled'::text, 'no_show'::text])))
);
CREATE TABLE "AppointmentSeries" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"ProviderId" uuid NOT NULL,
	"RecurrenceRule" text NOT NULL,
	"StartsOn" date NOT NULL,
	"EndsOn" date,
	"DefaultMinutes" integer DEFAULT 50 NOT NULL,
	"DefaultModality" text DEFAULT 'online' NOT NULL,
	"CancelledAt" timestamp with time zone,
	CONSTRAINT "AppointmentSeries_Modality_ck" CHECK (("DefaultModality" = ANY (ARRAY['presencial'::text, 'online'::text])))
);
CREATE TABLE "AudioDestructionLogs" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"SessionId" uuid NOT NULL,
	"DestroyedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"Outcome" text NOT NULL,
	CONSTRAINT "AudioDestructionLogs_Outcome_ck" CHECK (("Outcome" = ANY (ARRAY['transcribed'::text, 'failed'::text])))
);
CREATE TABLE "AuditEntries" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"NoteId" uuid,
	"DocumentId" uuid,
	"ActorId" uuid,
	"Action" text NOT NULL,
	"FieldChanged" text,
	"PreviousValue" text,
	"NewValue" text,
	"At" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL)))
);
CREATE TABLE "AvailabilityExceptions" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"StartsAt" timestamp with time zone NOT NULL,
	"EndsAt" timestamp with time zone NOT NULL,
	"AllDay" boolean DEFAULT false NOT NULL,
	"Kind" text DEFAULT 'block' NOT NULL,
	"Reason" text,
	CONSTRAINT "AvailabilityExceptions_Kind_ck" CHECK (("Kind" = ANY (ARRAY['block'::text, 'vacation'::text, 'holiday'::text]))),
	CONSTRAINT "AvailabilityExceptions_Range_ck" CHECK (("EndsAt" > "StartsAt"))
);
CREATE TABLE "AvailabilityRules" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"Weekday" integer NOT NULL,
	"StartsAtTime" time NOT NULL,
	"EndsAtTime" time NOT NULL,
	"SlotMinutes" integer DEFAULT 50 NOT NULL,
	"EffectiveFrom" date NOT NULL,
	"EffectiveTo" date,
	"Timezone" text DEFAULT 'America/Sao_Paulo' NOT NULL,
	CONSTRAINT "AvailabilityRules_Range_ck" CHECK ((("EffectiveTo" IS NULL) OR ("EffectiveTo" >= "EffectiveFrom"))),
	CONSTRAINT "AvailabilityRules_Slot_ck" CHECK (("SlotMinutes" > 0)),
	CONSTRAINT "AvailabilityRules_Time_ck" CHECK (("EndsAtTime" > "StartsAtTime")),
	CONSTRAINT "AvailabilityRules_Weekday_ck" CHECK ((("Weekday" >= 0) AND ("Weekday" <= 6)))
);
CREATE TABLE "BillingCodes" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"NoteId" uuid NOT NULL,
	"System" integer DEFAULT 2 NOT NULL,
	"ProcedureCode" text NOT NULL,
	"DiagnosisCode" text,
	"Description" text,
	"Confidence" numeric(4, 3),
	"DenialRisk" integer,
	"Modifiers" text[] DEFAULT '{}' NOT NULL,
	"ConfirmedBy" uuid,
	"ConfirmedAt" timestamp with time zone,
	CONSTRAINT "BillingCodes_DenialRisk_ck" CHECK ((("DenialRisk" IS NULL) OR (("DenialRisk" >= 0) AND ("DenialRisk" <= 100))))
);
CREATE TABLE "BookingHolds" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"PatientId" uuid,
	"StartsAt" timestamp with time zone NOT NULL,
	"EndsAt" timestamp with time zone NOT NULL,
	"ExpiresAt" timestamp with time zone NOT NULL,
	"CreatedBy" text DEFAULT 'patient' NOT NULL,
	"ConvertedAppointmentId" uuid,
	"ReleasedAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"Slot" tstzrange GENERATED ALWAYS AS (CASE WHEN "ReleasedAt" IS NULL THEN tstzrange("StartsAt", "EndsAt", '[)') END) STORED,
	CONSTRAINT "BookingHolds_CreatedBy_ck" CHECK (("CreatedBy" = ANY (ARRAY['provider'::text, 'patient'::text]))),
	CONSTRAINT "BookingHolds_no_overlap" UNIQUE ("ProviderId", "Slot" WITHOUT OVERLAPS),
	CONSTRAINT "BookingHolds_Range_ck" CHECK (("EndsAt" > "StartsAt"))
);
CREATE TABLE "BookingPolicies" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL CONSTRAINT "BookingPolicies_ProviderId_key" UNIQUE,
	"MinLeadHours" integer DEFAULT 24 NOT NULL,
	"MaxHorizonDays" integer DEFAULT 60 NOT NULL,
	"CancelLeadHours" integer DEFAULT 24 NOT NULL,
	"RescheduleLeadHours" integer DEFAULT 24 NOT NULL,
	"MaxOpenAppointments" integer DEFAULT 4 NOT NULL,
	"AllowNewPatients" boolean DEFAULT false NOT NULL,
	"AutoCancelMode" text DEFAULT 'after_email' NOT NULL,
	"AutoCancelHours" integer DEFAULT 1 NOT NULL,
	"RequiresConfirmation" boolean DEFAULT true NOT NULL,
	CONSTRAINT "BookingPolicies_Windows_ck" CHECK ((("MinLeadHours" >= 0) AND ("MaxHorizonDays" > 0) AND ("CancelLeadHours" >= 0) AND ("RescheduleLeadHours" >= 0) AND ("MaxOpenAppointments" > 0))),
	CONSTRAINT "BookingPolicies_AutoCancel_ck" CHECK (("AutoCancelMode" = ANY (ARRAY['off'::text, 'after_email'::text, 'before_session'::text])) AND ("AutoCancelHours" >= 1) AND ("AutoCancelHours" <= 168))
);
CREATE TABLE "BreakGlassGrants" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"StaffId" uuid NOT NULL,
	"ProviderId" uuid NOT NULL,
	"TicketRef" text NOT NULL,
	"Reason" text NOT NULL,
	"AuthorizedBy" uuid NOT NULL,
	"AuthorizedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ExpiresAt" timestamp with time zone NOT NULL,
	"RevokedAt" timestamp with time zone,
	CONSTRAINT "BreakGlassGrants_TwoPerson_ck" CHECK (("AuthorizedBy" <> "StaffId")),
	CONSTRAINT "BreakGlassGrants_Window_ck" CHECK (("ExpiresAt" > "AuthorizedAt"))
);
CREATE TABLE "CalendarConnections" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL CONSTRAINT "CalendarConnections_ProviderId_key" UNIQUE,
	"AccountEmail" text NOT NULL,
	"RefreshToken" text NOT NULL,
	"SyncToken" text,
	"ChannelId" text,
	"ChannelResourceId" text,
	"ChannelExpiresAt" timestamp with time zone,
	"Direction" text DEFAULT 'two_way' NOT NULL,
	"ConnectedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"RevokedAt" timestamp with time zone,
	CONSTRAINT "CalendarConnections_Direction_ck" CHECK (("Direction" = ANY (ARRAY['push_only'::text, 'two_way'::text])))
);
CREATE TABLE "ClinicalDocuments" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"ProviderId" uuid NOT NULL,
	"NoteId" uuid,
	"Kind" text NOT NULL,
	"Body" text DEFAULT '' NOT NULL,
	"SignedAt" timestamp with time zone,
	"RetentionUntil" date,
	"SupersededBy" uuid,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"Fields" jsonb DEFAULT '{}' NOT NULL,
	"Confirmations" jsonb DEFAULT '[]' NOT NULL,
	"Team" jsonb DEFAULT '[]'::jsonb NOT NULL,
	"RulesetId" uuid,
	"SignedByCrp" text,
	"SignedCity" text,
	"SignedByName" text,
	"SignedPatientName" text,
	"PdfObjectKey" text,
	"PdfSha256" text,
	"ExportedAt" timestamp with time zone,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ClinicalDocuments_Signed_ck" CHECK ((("SignedAt" IS NULL) = ("SignedByCrp" IS NULL) AND ("SignedAt" IS NULL) = ("SignedCity" IS NULL) AND ("SignedAt" IS NULL) = ("SignedByName" IS NULL) AND ("SignedAt" IS NULL) = ("SignedPatientName" IS NULL))),
	CONSTRAINT "ClinicalDocuments_Pdf_ck" CHECK ((("PdfObjectKey" IS NULL AND "PdfSha256" IS NULL AND "ExportedAt" IS NULL) OR ("PdfObjectKey" IS NOT NULL AND "PdfSha256" IS NOT NULL AND "ExportedAt" IS NOT NULL AND "SignedAt" IS NOT NULL))),
	CONSTRAINT "ClinicalDocuments_Kind_ck" CHECK (("Kind" = ANY (ARRAY['declaracao'::text, 'atestado'::text, 'relatorio'::text, 'relatorio_multiprofissional'::text, 'laudo'::text, 'parecer'::text]))),
	CONSTRAINT "ClinicalDocuments_Team_array" CHECK (jsonb_typeof("Team") = 'array')
);
CREATE TABLE "ConformityChecks" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"DocumentId" uuid NOT NULL,
	"RulesetId" uuid NOT NULL,
	"MissingElements" jsonb DEFAULT '[]' NOT NULL,
	"ForbiddenFound" jsonb DEFAULT '[]' NOT NULL,
	"Mode" text DEFAULT 'advisory' NOT NULL,
	"Passed" boolean NOT NULL,
	"CheckedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ConformityChecks_Mode_ck" CHECK (("Mode" = ANY (ARRAY['blocking'::text, 'advisory'::text])))
);
CREATE TABLE "ConformityRulesets" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"DocumentKind" text NOT NULL,
	"Version" text NOT NULL,
	"Elements" jsonb NOT NULL,
	"Prohibitions" jsonb DEFAULT '[]' NOT NULL,
	"SourceRef" text DEFAULT 'Res. CFP 006/2019' NOT NULL,
	"ArticleRef" text,
	"EffectiveFrom" date NOT NULL,
	CONSTRAINT "ConformityRulesets_Kind_Version_key" UNIQUE("DocumentKind","Version"),
	CONSTRAINT "ConformityRulesets_Elements_ck" CHECK ((jsonb_typeof("Elements") = 'array'::text)),
	CONSTRAINT "ConformityRulesets_Kind_ck" CHECK (("DocumentKind" = ANY (ARRAY['declaracao'::text, 'atestado'::text, 'relatorio'::text, 'relatorio_multiprofissional'::text, 'laudo'::text, 'parecer'::text]))),
	CONSTRAINT "ConformityRulesets_Prohibitions_ck" CHECK ((jsonb_typeof("Prohibitions") = 'array'::text))
);
CREATE TABLE "ConsentEvents" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ConsentId" uuid NOT NULL,
	"FromState" text NOT NULL,
	"ToState" text NOT NULL,
	"At" timestamp with time zone DEFAULT now() NOT NULL,
	"ActorId" uuid,
	"Reason" text
);
CREATE TABLE "ContactMessages" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"FullName" text NOT NULL,
	"Email" text NOT NULL,
	"Phone" text,
	"Message" text NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE TABLE "DenialFlags" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"GuideId" uuid NOT NULL,
	"RuleKey" text NOT NULL,
	"Message" text NOT NULL,
	"RaisedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"DismissedAt" timestamp with time zone
);
CREATE TABLE "Denials" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"GuideId" uuid NOT NULL,
	"ReasonCode" text NOT NULL,
	"ReasonText" text,
	"ReceivedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ResolvedAt" timestamp with time zone
);
CREATE TABLE "DisclosureAuthorizations" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"DocumentId" uuid NOT NULL,
	"PatientId" uuid NOT NULL,
	"RecipientName" text NOT NULL,
	"RecipientRole" text NOT NULL,
	"Purpose" text NOT NULL,
	"AuthorizedAt" timestamp with time zone NOT NULL,
	"RecordedBy" uuid NOT NULL
);
CREATE TABLE "DisposalRecords" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"DocumentId" uuid,
	"DisposedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"TermObjectKey" text,
	"PerformedBy" uuid
);
CREATE TABLE "DocumentHashes" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"DocumentId" uuid NOT NULL,
	"Sequence" bigint NOT NULL,
	"PreviousHash" text,
	"Hash" text NOT NULL,
	"SealedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "DocumentHashes_Document_Sequence_key" UNIQUE("DocumentId","Sequence")
);
CREATE TABLE "Dossiers" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"GeneratedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ObjectKey" text,
	"Manifest" jsonb,
	"IntegrityVerified" boolean DEFAULT false NOT NULL
);
CREATE TABLE "ExcerptRefs" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"SourceType" text NOT NULL,
	"SourceId" uuid NOT NULL,
	"OffsetStart" integer,
	"OffsetEnd" integer,
	"DatedAt" timestamp with time zone NOT NULL,
	CONSTRAINT "ExcerptRefs_Offsets_ck" CHECK ((("OffsetEnd" IS NULL) OR ("OffsetStart" IS NULL) OR ("OffsetEnd" >= "OffsetStart"))),
	CONSTRAINT "ExcerptRefs_SourceType_ck" CHECK (("SourceType" = ANY (ARRAY['note'::text, 'transcript'::text, 'document'::text])))
);
CREATE TABLE "ExternalDocuments" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"ProviderId" uuid NOT NULL,
	"Filename" text NOT NULL,
	"MimeType" text NOT NULL,
	"ObjectKey" text NOT NULL CONSTRAINT "ExternalDocuments_ObjectKey_key" UNIQUE,
	"ByteSize" bigint NOT NULL,
	"ScanStatus" text DEFAULT 'pending' NOT NULL,
	"UploadedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ExternalDocuments_ScanStatus_ck" CHECK (("ScanStatus" = ANY (ARRAY['pending'::text, 'clean'::text, 'infected'::text, 'failed'::text])))
);
CREATE TABLE "ExtractedFacts" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ExternalDocumentId" uuid NOT NULL,
	"Kind" text NOT NULL,
	"Value" text NOT NULL,
	"CorrectedValue" text,
	"PageRef" integer,
	"ExcerptRefId" uuid,
	"ConfirmedBy" uuid,
	"ConfirmedAt" timestamp with time zone,
	CONSTRAINT "ExtractedFacts_Kind_ck" CHECK (("Kind" = ANY (ARRAY['issuer'::text, 'registration'::text, 'issue_date'::text, 'icd'::text, 'medication'::text, 'conclusion'::text])))
);
CREATE TABLE "FocusAreas" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"Code" text NOT NULL CONSTRAINT "FocusAreas_Code_key" UNIQUE,
	"Label" text NOT NULL,
	"ParentId" uuid,
	"VocabularyVersion" text DEFAULT '1' NOT NULL,
	"Active" boolean DEFAULT true NOT NULL
);
CREATE TABLE "FollowUpItems" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"NoteId" uuid,
	"Description" text NOT NULL,
	"ResolvedAt" timestamp with time zone,
	"ResolvedNoteId" uuid,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE TABLE "Instruments" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"Code" text NOT NULL CONSTRAINT "Instruments_Code_key" UNIQUE,
	"Name" text NOT NULL,
	"ItemCount" integer,
	"MinScore" integer DEFAULT 0 NOT NULL,
	"MaxScore" integer NOT NULL,
	"SatepsiStatus" text DEFAULT 'sem_classificacao' NOT NULL,
	"SatepsiCheckedOn" date,
	"LicenseMode" text DEFAULT 'proprietario' NOT NULL,
	"MayRenderItems" boolean DEFAULT false NOT NULL,
	"SourceRef" text,
	"StatusExplanation" text NOT NULL,
	"Enabled" boolean DEFAULT false NOT NULL,
	"Version" text DEFAULT '1' NOT NULL,
	CONSTRAINT "Instruments_Checked_ck" CHECK ((("SatepsiStatus" = 'sem_classificacao'::text) OR ("SatepsiCheckedOn" IS NOT NULL))),
	CONSTRAINT "Instruments_Enabled_ck" CHECK ((("Enabled" = false) OR ("SatepsiStatus" = ANY (ARRAY['nao_privativo'::text, 'favoravel'::text])))),
	CONSTRAINT "Instruments_Explanation_ck" CHECK ((length(TRIM(BOTH FROM "StatusExplanation")) > 20)),
	CONSTRAINT "Instruments_License_ck" CHECK (("LicenseMode" = ANY (ARRAY['livre'::text, 'dominio_publico'::text, 'autorizacao_necessaria'::text, 'proprietario'::text, 'vedado_digital'::text]))),
	CONSTRAINT "Instruments_Range_ck" CHECK (("MaxScore" > "MinScore")),
	CONSTRAINT "Instruments_Render_ck" CHECK ((("MayRenderItems" = false) OR (("LicenseMode" = ANY (ARRAY['livre'::text, 'dominio_publico'::text])) AND ("SatepsiStatus" = ANY (ARRAY['nao_privativo'::text, 'favoravel'::text]))))),
	CONSTRAINT "Instruments_Satepsi_ck" CHECK (("SatepsiStatus" = ANY (ARRAY['nao_privativo'::text, 'favoravel'::text, 'desfavoravel'::text, 'nao_avaliado'::text, 'sem_classificacao'::text])))
);
CREATE TABLE "InsurerAuthorizations" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"InsurerId" uuid NOT NULL,
	"Number" text NOT NULL,
	"SessionsAuthorized" integer NOT NULL,
	"SessionsPerformed" integer DEFAULT 0 NOT NULL,
	"ValidFrom" date NOT NULL,
	"ValidUntil" date NOT NULL,
	CONSTRAINT "InsurerAuthorizations_Dates_ck" CHECK (("ValidUntil" >= "ValidFrom")),
	CONSTRAINT "InsurerAuthorizations_Sessions_ck" CHECK ((("SessionsPerformed" >= 0) AND ("SessionsAuthorized" > 0)))
);
CREATE TABLE "Insurers" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"Name" text NOT NULL,
	"AnsRegistry" text,
	"PortalUrl" text,
	CONSTRAINT "Insurers_Provider_Name_key" UNIQUE("ProviderId","Name")
);
CREATE TABLE "IntakeQuestions" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"TemplateId" uuid NOT NULL,
	"Key" text NOT NULL,
	"Label" text NOT NULL,
	"AnswerType" text DEFAULT 'text' NOT NULL,
	"Position" integer DEFAULT 0 NOT NULL,
	"TargetField" text,
	"Required" boolean DEFAULT false NOT NULL,
	CONSTRAINT "IntakeQuestions_Template_Key_key" UNIQUE("TemplateId","Key"),
	CONSTRAINT "IntakeQuestions_AnswerType_ck" CHECK (("AnswerType" = ANY (ARRAY['text'::text, 'number'::text, 'date'::text, 'boolean'::text, 'choice'::text])))
);
CREATE TABLE "IntakeResponses" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"SessionId" uuid,
	"QuestionId" uuid NOT NULL,
	"Answer" text,
	"RecordedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE TABLE "IntakeTemplates" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"Name" text NOT NULL,
	"Version" text DEFAULT '1' NOT NULL,
	"Active" boolean DEFAULT true NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "IntakeTemplates_Provider_Name_Version_key" UNIQUE("ProviderId","Name","Version")
);
CREATE TABLE "MedicationEntries" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"Drug" text NOT NULL,
	"Dose" text,
	"Posology" text,
	"StartedOn" date NOT NULL,
	"EndedOn" date,
	"SourceFactId" uuid,
	CONSTRAINT "MedicationEntries_Dates_ck" CHECK ((("EndedOn" IS NULL) OR ("EndedOn" >= "StartedOn")))
);
CREATE TABLE "Notes" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"ProviderId" uuid NOT NULL,
	"SessionId" uuid,
	"Status" text DEFAULT 'Draft' NOT NULL,
	"InputMode" text NOT NULL,
	"Format" text DEFAULT 'DAP' NOT NULL,
	"RawTranscript" text,
	"SignedAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"FailureReason" text,
	"RecordingId" uuid,
	"StructureStatus" text,
	"StructureFailure" text,
	"SignedByCrp" text,
	"PdfObjectKey" text,
	"PdfSha256" text,
	"ExportedAt" timestamp with time zone,
	CONSTRAINT "Notes_Format_ck" CHECK (("Format" = ANY (ARRAY['DAP'::text, 'SOAP'::text, 'Biopsychosocial'::text, 'PtFunctional'::text]))),
	CONSTRAINT "Notes_InputMode_ck" CHECK (("InputMode" = ANY (ARRAY['VoiceBatch'::text, 'VoiceLive'::text, 'VoiceAppointment'::text, 'Text'::text]))),
	CONSTRAINT "Notes_Signed_ck" CHECK ((("Status" = ANY (ARRAY['Signed'::text, 'Exported'::text])) = ("SignedAt" IS NOT NULL))),
	CONSTRAINT "Notes_Status_ck" CHECK (("Status" = ANY (ARRAY['Draft'::text, 'Processing'::text, 'ReadyForReview'::text, 'Signed'::text, 'Exported'::text, 'Failed'::text]))),
	CONSTRAINT "Notes_FailureReason_ck" CHECK ((("Status" = 'Failed'::text) = ("FailureReason" IS NOT NULL)) AND (("FailureReason" IS NULL) OR ("FailureReason" = ANY (ARRAY['no_speech'::text, 'transcription_error'::text, 'server_restart'::text])))),
	CONSTRAINT "Notes_StructureStatus_ck" CHECK ((("StructureStatus" IS NULL) OR ("StructureStatus" = ANY (ARRAY['pending'::text, 'done'::text, 'failed'::text])))),
	CONSTRAINT "Notes_StructureFailure_ck" CHECK (((("StructureStatus" IS NOT DISTINCT FROM 'failed'::text) = ("StructureFailure" IS NOT NULL)) AND (("StructureFailure" IS NULL) OR ("StructureFailure" = ANY (ARRAY['model_error'::text, 'too_long'::text, 'server_restart'::text]))))),
	CONSTRAINT "Notes_SignedByCrp_ck" CHECK ((("SignedAt" IS NULL) = ("SignedByCrp" IS NULL))),
	CONSTRAINT "Notes_Exported_ck" CHECK (((("Status" = 'Exported') = ("PdfObjectKey" IS NOT NULL)) AND (("PdfObjectKey" IS NULL) = ("PdfSha256" IS NULL)) AND (("PdfObjectKey" IS NULL) = ("ExportedAt" IS NULL))))
);
CREATE TABLE "NoteSections" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"NoteId" uuid NOT NULL,
	"Key" text NOT NULL,
	"Content" text DEFAULT '' NOT NULL,
	"Position" integer DEFAULT 0 NOT NULL,
	"GeneratedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"EditedAt" timestamp with time zone,
	"Revision" integer DEFAULT 0 NOT NULL,
	CONSTRAINT "NoteSections_NoteId_Key_key" UNIQUE("NoteId","Key")
);
CREATE TABLE "Notifications" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"Kind" text NOT NULL,
	"SubjectType" text,
	"SubjectId" uuid,
	"Channel" text DEFAULT 'email' NOT NULL,
	"ScheduledFor" timestamp with time zone NOT NULL,
	"SentAt" timestamp with time zone,
	"DeliveryStatus" text DEFAULT 'pending' NOT NULL,
	"ReadAt" timestamp with time zone,
	CONSTRAINT "Notifications_Channel_ck" CHECK (("Channel" = ANY (ARRAY['email'::text, 'push'::text, 'in_app'::text]))),
	CONSTRAINT "Notifications_Status_ck" CHECK (("DeliveryStatus" = ANY (ARRAY['pending'::text, 'sent'::text, 'failed'::text, 'cancelled'::text])))
);
CREATE TABLE "PatientQuotes" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"NoteId" uuid NOT NULL,
	"TermId" uuid,
	"Text" text NOT NULL,
	"ExcerptRefId" uuid,
	"RecordedAt" timestamp with time zone NOT NULL
);
CREATE TABLE "PatientAccounts" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ExternalSubject" uuid NOT NULL CONSTRAINT "PatientAccounts_ExternalSubject_key" UNIQUE,
	"Email" text NOT NULL,
	"FirstName" text NOT NULL,
	"LastName" text NOT NULL,
	"Phone" text,
	"DateOfBirth" date,
	"LastLoginAt" timestamp with time zone,
	"TermsAcceptedAt" timestamp with time zone,
	"DisabledAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE TABLE "Patients" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"AccountId" uuid,
	"FirstName" text NOT NULL,
	"LastName" text NOT NULL,
	"DateOfBirth" date,
	"PreferredLanguage" text,
	"LastVisit" timestamp with time zone,
	"ContactEmail" text,
	"Phone" text,
	"ArchivedAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "Patients_Id_ProviderId_key" UNIQUE("Id","ProviderId"),
	CONSTRAINT "Patients_Account_Provider_key" UNIQUE("AccountId","ProviderId")
);
CREATE TABLE "PatientInvitations" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"Email" text NOT NULL,
	"TokenHash" bytea NOT NULL CONSTRAINT "PatientInvitations_TokenHash_key" UNIQUE,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ExpiresAt" timestamp with time zone NOT NULL,
	"AcceptedAt" timestamp with time zone,
	"AcceptedAccountId" uuid,
	"RevokedAt" timestamp with time zone,
	CONSTRAINT "PatientInvitations_Closed_ck" CHECK (("AcceptedAt" IS NULL) OR ("RevokedAt" IS NULL)),
	CONSTRAINT "PatientInvitations_Accepted_ck" CHECK (("AcceptedAccountId" IS NULL) OR ("AcceptedAt" IS NOT NULL))
);
CREATE TABLE "PatientDiagnoses" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"Description" text NOT NULL,
	"IcdCode" text,
	"RecordedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ResolvedOn" date
);
CREATE TABLE "PlanFeatures" (
	"PlanId" uuid,
	"FeatureKey" text,
	CONSTRAINT "PlanFeatures_pkey" PRIMARY KEY("PlanId","FeatureKey")
);
CREATE TABLE "PlanObjectives" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PlanId" uuid NOT NULL,
	"Description" text NOT NULL,
	"TermId" uuid,
	"LastRecordedSessionId" uuid,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE TABLE "Plans" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"Code" text NOT NULL,
	"Name" text NOT NULL,
	"Version" integer DEFAULT 1 NOT NULL,
	"Active" boolean DEFAULT true NOT NULL,
	"MonthlyPriceId" text,
	"AnnualPriceId" text,
	CONSTRAINT "Plans_Code_Version_key" UNIQUE("Code","Version")
);
CREATE TABLE "ProfileListingEvents" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProfileId" uuid NOT NULL,
	"FromState" text NOT NULL,
	"ToState" text NOT NULL,
	"At" timestamp with time zone DEFAULT now() NOT NULL,
	"ActorId" uuid,
	"TermsVersion" text,
	"Reason" text
);
CREATE TABLE "ProviderFocusAreas" (
	"ProfileId" uuid,
	"FocusAreaId" uuid,
	"DisplayOrder" integer DEFAULT 0 NOT NULL,
	CONSTRAINT "ProviderFocusAreas_pkey" PRIMARY KEY("ProfileId","FocusAreaId")
);
CREATE TABLE "ProviderInstrumentOptIns" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"InstrumentId" uuid NOT NULL,
	"AcknowledgedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ExplanationShown" text NOT NULL,
	"RevokedAt" timestamp with time zone,
	CONSTRAINT "ProviderInstrumentOptIns_key" UNIQUE("ProviderId","InstrumentId")
);
CREATE TABLE "ProviderProfiles" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL CONSTRAINT "ProviderProfiles_ProviderId_key" UNIQUE,
	"Slug" text NOT NULL CONSTRAINT "ProviderProfiles_Slug_key" UNIQUE,
	"DisplayName" text NOT NULL,
	"Headline" text,
	"Bio" text,
	"PhotoObjectKey" text,
	"Languages" text[] DEFAULT '{pt-BR}' NOT NULL,
	"Modalities" text[] DEFAULT '{online}' NOT NULL,
	"City" text,
	"State" text,
	"ListingState" text DEFAULT 'never_listed' NOT NULL,
	"ListingOptedInAt" timestamp with time zone,
	"ListingTermsVersion" text,
	"ListingWithdrawnAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ProviderProfiles_ListingState_ck" CHECK (("ListingState" = ANY (ARRAY['never_listed'::text, 'listed'::text, 'withdrawn'::text]))),
	CONSTRAINT "ProviderProfiles_OptIn_ck" CHECK ((("ListingState" <> 'listed'::text) OR (("ListingOptedInAt" IS NOT NULL) AND ("ListingTermsVersion" IS NOT NULL))))
);
CREATE TABLE "Providers" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"Email" text NOT NULL CONSTRAINT "Providers_Email_unique" UNIQUE,
	"ExternalSubject" uuid NOT NULL CONSTRAINT "Providers_ExternalSubject_unique" UNIQUE,
	"Name" text NOT NULL,
	"CrpNumber" text,
	"CrpRegion" text,
	"Specialty" text DEFAULT 'MentalHealth' NOT NULL,
	"PreferredNoteFormat" text DEFAULT 'DAP' NOT NULL,
	"BillingSystem" integer DEFAULT 2 NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"PracticeCity" text,
	"DocumentCheckMode" text DEFAULT 'blocking' NOT NULL,
	CONSTRAINT "Providers_DocumentCheckMode_ck" CHECK (("DocumentCheckMode" = ANY (ARRAY['blocking'::text, 'advisory'::text]))),
	CONSTRAINT "Providers_Crp_ck" CHECK ((("CrpNumber" IS NULL) = ("CrpRegion" IS NULL))),
	CONSTRAINT "Providers_NoteFormat_ck" CHECK (("PreferredNoteFormat" = ANY (ARRAY['DAP'::text, 'SOAP'::text, 'Biopsychosocial'::text, 'PtFunctional'::text])))
);
CREATE TABLE "RecordingConsents" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"TreatmentPlanId" uuid,
	"State" text DEFAULT 'NOT_REQUESTED' NOT NULL,
	"TemplateVersion" text,
	"IssuedAt" timestamp with time zone,
	"SignedAt" timestamp with time zone,
	"SignatureEvidence" text,
	"ValidUntil" timestamp with time zone,
	"RevokedAt" timestamp with time zone,
	"GuardianName" text,
	"AssentRecordedAt" timestamp with time zone,
	CONSTRAINT "RecordingConsents_Signed_ck" CHECK ((("State" <> 'SIGNED'::text) OR (("SignedAt" IS NOT NULL) AND ("SignatureEvidence" IS NOT NULL)))),
	CONSTRAINT "RecordingConsents_State_ck" CHECK (("State" = ANY (ARRAY['NOT_REQUESTED'::text, 'SENT'::text, 'SIGNED'::text, 'REVOKED'::text, 'EXPIRED'::text])))
);
CREATE TABLE "Reminders" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"AppointmentId" uuid NOT NULL,
	"Channel" text DEFAULT 'whatsapp' NOT NULL,
	"TemplateKey" text NOT NULL,
	"ScheduledFor" timestamp with time zone NOT NULL,
	"SentAt" timestamp with time zone,
	"DeliveryStatus" text DEFAULT 'pending' NOT NULL,
	"Attempts" integer DEFAULT 0 NOT NULL,
	"ConfirmationId" uuid,
	CONSTRAINT "Reminders_Channel_ck" CHECK (("Channel" = ANY (ARRAY['whatsapp'::text, 'email'::text, 'sms'::text]))),
	CONSTRAINT "Reminders_Status_ck" CHECK (("DeliveryStatus" = ANY (ARRAY['pending'::text, 'sent'::text, 'failed'::text, 'cancelled'::text])))
);
CREATE TABLE "RetentionRules" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"RecordKind" text NOT NULL,
	"GuardYears" integer DEFAULT 5 NOT NULL,
	"Basis" text DEFAULT 'Res. CFP 001/2009' NOT NULL,
	CONSTRAINT "RetentionRules_Provider_Kind_key" UNIQUE("ProviderId","RecordKind"),
	CONSTRAINT "RetentionRules_GuardYears_ck" CHECK (("GuardYears" >= 5))
);
CREATE TABLE "RoomSessions" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"AppointmentId" uuid NOT NULL CONSTRAINT "RoomSessions_AppointmentId_key" UNIQUE,
	"StartedAt" timestamp with time zone,
	"EndedAt" timestamp with time zone,
	"RejoinCount" integer DEFAULT 0 NOT NULL,
	"RelayUsed" boolean DEFAULT false NOT NULL
);
CREATE TABLE "ScaleApplications" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"SessionId" uuid,
	"InstrumentId" uuid NOT NULL,
	"Score" integer NOT NULL,
	"AppliedAt" timestamp with time zone NOT NULL,
	"AppliedBy" uuid,
	"RawAnswers" jsonb,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"RemovedAt" timestamp with time zone,
	"RemovedBy" uuid,
	CONSTRAINT "ScaleApplications_Removed_ck" CHECK ((("RemovedAt" IS NULL) = ("RemovedBy" IS NULL))),
	CONSTRAINT "ScaleApplications_Score_ck" CHECK (("Score" >= 0))
);
CREATE TABLE "ServiceOfferings" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"Kind" text NOT NULL,
	"DurationMinutes" integer DEFAULT 50 NOT NULL,
	"PriceCents" integer NOT NULL,
	"Currency" char(3) DEFAULT 'BRL' NOT NULL,
	"IsPublic" boolean DEFAULT true NOT NULL,
	"Active" boolean DEFAULT true NOT NULL,
	"EffectiveFrom" date DEFAULT CURRENT_DATE NOT NULL,
	"EffectiveTo" date,
	CONSTRAINT "ServiceOfferings_Duration_ck" CHECK (("DurationMinutes" > 0)),
	CONSTRAINT "ServiceOfferings_Kind_ck" CHECK (("Kind" = ANY (ARRAY['first_consultation'::text, 'session'::text, 'couple'::text, 'document'::text, 'group'::text]))),
	CONSTRAINT "ServiceOfferings_Price_ck" CHECK (("PriceCents" >= 0)),
	CONSTRAINT "ServiceOfferings_Range_ck" CHECK ((("EffectiveTo" IS NULL) OR ("EffectiveTo" >= "EffectiveFrom")))
);
CREATE TABLE "Sessions" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"ProviderId" uuid NOT NULL,
	"ScheduledStart" timestamp with time zone,
	"ScheduledMinutes" integer,
	"ActualStart" timestamp with time zone,
	"ActualEnd" timestamp with time zone,
	"Modality" text,
	"Attendance" text DEFAULT 'scheduled' NOT NULL,
	"CancelledAt" timestamp with time zone,
	"CancellationLeadHours" numeric(6, 2),
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "Sessions_Attendance_ck" CHECK (("Attendance" = ANY (ARRAY['scheduled'::text, 'attended'::text, 'cancelled'::text, 'no_show'::text]))),
	CONSTRAINT "Sessions_Modality_ck" CHECK ((("Modality" IS NULL) OR ("Modality" = ANY (ARRAY['presencial'::text, 'online'::text])))),
	CONSTRAINT "Sessions_Times_ck" CHECK ((("ActualEnd" IS NULL) OR ("ActualStart" IS NULL) OR ("ActualEnd" > "ActualStart")))
);
CREATE TABLE "ShareAccesses" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ShareLinkId" uuid NOT NULL,
	"At" timestamp with time zone DEFAULT now() NOT NULL,
	"Outcome" text NOT NULL,
	"CoarseGeo" text,
	"UserAgent" text,
	CONSTRAINT "ShareAccesses_Outcome_ck" CHECK (("Outcome" = ANY (ARRAY['opened'::text, 'bad_password'::text, 'expired'::text, 'revoked'::text, 'cap_reached'::text])))
);
CREATE TABLE "ShareLinks" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"DocumentId" uuid NOT NULL,
	"AuthorizationId" uuid NOT NULL,
	"TokenHash" text NOT NULL CONSTRAINT "ShareLinks_TokenHash_key" UNIQUE,
	"PasswordHash" text NOT NULL,
	"ExpiresAt" timestamp with time zone NOT NULL,
	"ViewCap" integer,
	"ViewsUsed" integer DEFAULT 0 NOT NULL,
	"DownloadEnabled" boolean DEFAULT false NOT NULL,
	"RevokedAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ShareLinks_Expiry_ck" CHECK ((("ExpiresAt" > "CreatedAt") AND ("ExpiresAt" <= ("CreatedAt" + '30 days'::interval)))),
	CONSTRAINT "ShareLinks_ViewCap_ck" CHECK ((("ViewCap" IS NULL) OR ("ViewCap" > 0)))
);
CREATE TABLE "SpecialistTitles" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"Title" text NOT NULL,
	"GrantedAt" date,
	"RegistryRef" text,
	"EvidenceObjectKey" text,
	"VerifiedAt" timestamp with time zone,
	CONSTRAINT "SpecialistTitles_Provider_Title_key" UNIQUE("ProviderId","Title")
);
CREATE TABLE "SpeechMarkers" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"SessionId" uuid NOT NULL,
	"Kind" text NOT NULL,
	"Value" numeric NOT NULL,
	"PatientBaseline" numeric,
	"MeasuredAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE TABLE "Staff" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ExternalSubject" uuid NOT NULL CONSTRAINT "Staff_ExternalSubject_unique" UNIQUE,
	"Email" text NOT NULL CONSTRAINT "Staff_Email_unique" UNIQUE,
	"Name" text NOT NULL,
	"Role" text,
	"DisabledAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "Staff_Role_ck" CHECK (("Role" = ANY (ARRAY['owner'::text, 'support'::text, 'ops'::text])))
);
CREATE TABLE "Subscriptions" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL CONSTRAINT "Subscriptions_ProviderId_key" UNIQUE,
	"PlanId" uuid NOT NULL,
	"Status" text DEFAULT 'trialing' NOT NULL,
	"PeriodStart" timestamp with time zone,
	"PeriodEnd" timestamp with time zone,
	"TrialEndsAt" timestamp with time zone,
	"PaddleCustomerId" text,
	"PaddleSubscriptionId" text,
	"CancelledAt" timestamp with time zone,
	"SuspendedAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "Subscriptions_Status_ck" CHECK (("Status" = ANY (ARRAY['trialing'::text, 'active'::text, 'past_due'::text, 'paused'::text, 'cancelled'::text, 'suspended'::text])))
);
CREATE TABLE "ThemeTags" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"NoteId" uuid NOT NULL,
	"TermId" uuid NOT NULL,
	"ExcerptRefId" uuid,
	"Confidence" numeric(4, 3),
	"CreatedBy" text DEFAULT 'ai' NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"RemovedAt" timestamp with time zone,
	"AddedById" uuid,
	"RemovedById" uuid,
	CONSTRAINT "ThemeTags_CreatedBy_ck" CHECK (("CreatedBy" = ANY (ARRAY['ai'::text, 'human'::text]))),
	CONSTRAINT "ThemeTags_Removed_ck" CHECK ((("RemovedAt" IS NULL) OR ("RemovedById" IS NOT NULL)))
);
CREATE TABLE "ThemeTermChanges" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"TermId" uuid NOT NULL,
	"Kind" text NOT NULL,
	"PreviousLabel" text,
	"NewLabel" text,
	"StaffId" uuid NOT NULL,
	"At" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ThemeTermChanges_Kind_ck" CHECK (("Kind" = ANY (ARRAY['add'::text, 'rename'::text, 'retire'::text, 'split'::text, 'merge'::text])))
);
CREATE TABLE "ThemeTermSuccessions" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"FromTermId" uuid NOT NULL,
	"ToTermId" uuid NOT NULL,
	"Kind" text NOT NULL,
	"At" timestamp with time zone DEFAULT now() NOT NULL,
	"StaffId" uuid NOT NULL,
	CONSTRAINT "ThemeTermSuccessions_From_To_key" UNIQUE("FromTermId","ToTermId"),
	CONSTRAINT "ThemeTermSuccessions_Kind_ck" CHECK (("Kind" = ANY (ARRAY['split'::text, 'merge'::text])))
);
CREATE TABLE "ThemeTerms" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"VocabularyId" uuid NOT NULL,
	"Code" text NOT NULL,
	"Label" text NOT NULL,
	"ParentTermId" uuid,
	"IntroducedOn" date DEFAULT CURRENT_DATE NOT NULL,
	"Status" text DEFAULT 'active' NOT NULL,
	"SupersededByTermId" uuid,
	CONSTRAINT "ThemeTerms_Vocab_Code_key" UNIQUE("VocabularyId","Code"),
	CONSTRAINT "ThemeTerms_Status_ck" CHECK (("Status" = ANY (ARRAY['active'::text, 'superseded'::text, 'retired'::text])))
);
CREATE TABLE "ThemeVocabularies" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"Version" text NOT NULL CONSTRAINT "ThemeVocabularies_Version_key" UNIQUE,
	"PublishedAt" timestamp with time zone,
	"Active" boolean DEFAULT false NOT NULL
);
CREATE TABLE "TissGuides" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"NoteId" uuid NOT NULL,
	"SessionId" uuid,
	"InsurerId" uuid NOT NULL,
	"AuthorizationId" uuid,
	"GuideNumber" text,
	"Payload" jsonb,
	"Status" text DEFAULT 'draft' NOT NULL,
	"SubmittedAt" timestamp with time zone,
	CONSTRAINT "TissGuides_Status_ck" CHECK (("Status" = ANY (ARRAY['draft'::text, 'ready'::text, 'submitted'::text, 'paid'::text, 'denied'::text])))
);
CREATE TABLE "Transcripts" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"SessionId" uuid NOT NULL CONSTRAINT "Transcripts_SessionId_key" UNIQUE,
	"NoteId" uuid,
	"Text" text NOT NULL,
	"RetentionUntil" date,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"DestroyedAt" timestamp with time zone
);
CREATE TABLE "TreatmentPlans" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ReviewedAt" timestamp with time zone,
	"ClosedAt" timestamp with time zone
);
CREATE TABLE "UsageCounters" (
	"ProviderId" uuid,
	"Period" text,
	"CapturedSessions" integer DEFAULT 0 NOT NULL,
	"Cap" integer,
	"WarnedAt" timestamp with time zone,
	CONSTRAINT "UsageCounters_pkey" PRIMARY KEY("ProviderId","Period"),
	CONSTRAINT "UsageCounters_Counts_ck" CHECK ((("CapturedSessions" >= 0) AND (("Cap" IS NULL) OR ("Cap" > 0))))
);
CREATE TABLE "WebhookEvents" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ExternalEventId" text NOT NULL CONSTRAINT "WebhookEvents_ExternalEventId_key" UNIQUE,
	"Type" text NOT NULL,
	"OccurredAt" timestamp with time zone,
	"ReceivedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ProcessedAt" timestamp with time zone,
	"Payload" jsonb NOT NULL
);
CREATE INDEX "AccessLogs_ProviderId_At_idx" ON "AccessLogs" ("ProviderId","At");
CREATE INDEX "AccountExports_ProviderId_idx" ON "AccountExports" ("ProviderId","RequestedAt");
CREATE INDEX "AppointmentConfirmations_AppointmentId_idx" ON "AppointmentConfirmations" ("AppointmentId");
CREATE INDEX "AppointmentConfirmations_Deadline_idx" ON "AppointmentConfirmations" ("DeadlineAt") WHERE "ConfirmedAt" IS NULL AND "ClosedAt" IS NULL AND "DeadlineAt" IS NOT NULL;
CREATE INDEX "AppointmentConfirmationTokens_ConfirmationId_idx" ON "AppointmentConfirmationTokens" ("ConfirmationId");
CREATE INDEX "Appointments_ExternalEventId_idx" ON "Appointments" ("ExternalEventId");
CREATE INDEX "Appointments_Patient_Starts_idx" ON "Appointments" ("PatientId","StartsAt");
CREATE INDEX "Appointments_Provider_Starts_idx" ON "Appointments" ("ProviderId","StartsAt");
CREATE INDEX "AppointmentSeries_ProviderId_idx" ON "AppointmentSeries" ("ProviderId");
CREATE INDEX "AudioDestructionLogs_SessionId_idx" ON "AudioDestructionLogs" ("SessionId");
CREATE INDEX "AuditEntries_At_idx" ON "AuditEntries" ("At");
CREATE INDEX "AuditEntries_DocumentId_At_idx" ON "AuditEntries" ("DocumentId","At");
CREATE INDEX "AuditEntries_NoteId_At_idx" ON "AuditEntries" ("NoteId","At");
CREATE INDEX "AvailabilityExceptions_Provider_Range_idx" ON "AvailabilityExceptions" ("ProviderId","StartsAt","EndsAt");
CREATE INDEX "AvailabilityRules_Provider_idx" ON "AvailabilityRules" ("ProviderId","Weekday");
CREATE INDEX "BillingCodes_NoteId_idx" ON "BillingCodes" ("NoteId");
CREATE INDEX "BillingCodes_ProcedureCode_idx" ON "BillingCodes" ("ProcedureCode");
CREATE INDEX "BookingHolds_ExpiresAt_idx" ON "BookingHolds" ("ExpiresAt");
CREATE INDEX "BookingHolds_Provider_Starts_idx" ON "BookingHolds" ("ProviderId","StartsAt");
CREATE INDEX "BreakGlassGrants_StaffId_idx" ON "BreakGlassGrants" ("StaffId");
CREATE INDEX "BreakGlassGrants_ProviderId_idx" ON "BreakGlassGrants" ("ProviderId");
CREATE INDEX "BreakGlassGrants_ExpiresAt_idx" ON "BreakGlassGrants" ("ExpiresAt");
CREATE INDEX "CalendarConnections_ChannelExpiresAt_idx" ON "CalendarConnections" ("ChannelExpiresAt");
CREATE INDEX "ClinicalDocuments_PatientId_idx" ON "ClinicalDocuments" ("PatientId");
CREATE INDEX "ClinicalDocuments_RetentionUntil_idx" ON "ClinicalDocuments" ("RetentionUntil");
CREATE INDEX "ConformityChecks_DocumentId_idx" ON "ConformityChecks" ("DocumentId");
CREATE INDEX "ConsentEvents_ConsentId_At_idx" ON "ConsentEvents" ("ConsentId","At");
CREATE INDEX "ContactMessages_CreatedAt_idx" ON "ContactMessages" ("CreatedAt");
CREATE INDEX "DenialFlags_GuideId_idx" ON "DenialFlags" ("GuideId");
CREATE INDEX "Denials_ReasonCode_idx" ON "Denials" ("ReasonCode");
CREATE INDEX "DisclosureAuthorizations_DocumentId_idx" ON "DisclosureAuthorizations" ("DocumentId");
CREATE INDEX "DisposalRecords_PatientId_idx" ON "DisposalRecords" ("PatientId");
CREATE INDEX "Dossiers_PatientId_idx" ON "Dossiers" ("PatientId","GeneratedAt");
CREATE INDEX "ExcerptRefs_Source_idx" ON "ExcerptRefs" ("SourceType","SourceId");
CREATE INDEX "ExternalDocuments_PatientId_idx" ON "ExternalDocuments" ("PatientId");
CREATE INDEX "ExtractedFacts_DocumentId_idx" ON "ExtractedFacts" ("ExternalDocumentId");
CREATE INDEX "FollowUpItems_PatientId_idx" ON "FollowUpItems" ("PatientId");
CREATE INDEX "InsurerAuthorizations_ValidUntil_idx" ON "InsurerAuthorizations" ("ValidUntil");
CREATE INDEX "IntakeResponses_PatientId_idx" ON "IntakeResponses" ("PatientId");
CREATE INDEX "MedicationEntries_Patient_Started_idx" ON "MedicationEntries" ("PatientId","StartedOn");
CREATE INDEX "Notes_PatientId_idx" ON "Notes" ("PatientId");
CREATE INDEX "Notes_Patient_Created_idx" ON "Notes" ("PatientId","CreatedAt" DESC);
CREATE INDEX "Notes_ProviderId_idx" ON "Notes" ("ProviderId");
CREATE INDEX "Notes_Status_idx" ON "Notes" ("Status");
CREATE UNIQUE INDEX "Notes_RecordingId_key" ON "Notes" ("RecordingId");
CREATE INDEX "NoteSections_NoteId_idx" ON "NoteSections" ("NoteId");
CREATE INDEX "Notifications_due_idx" ON "Notifications" ("ScheduledFor");
CREATE INDEX "Notifications_Provider_Scheduled_idx" ON "Notifications" ("ProviderId", "ScheduledFor" DESC);
CREATE INDEX "PatientQuotes_NoteId_idx" ON "PatientQuotes" ("NoteId");
CREATE INDEX "Patients_ProviderId_idx" ON "Patients" ("ProviderId");
CREATE UNIQUE INDEX "PatientAccounts_Email_key" ON "PatientAccounts" (lower("Email"));
CREATE UNIQUE INDEX "PatientInvitations_OnePending_key" ON "PatientInvitations" ("PatientId") WHERE "AcceptedAt" IS NULL AND "RevokedAt" IS NULL;
CREATE INDEX "PatientDiagnoses_PatientId_idx" ON "PatientDiagnoses" ("PatientId");
CREATE INDEX "PlanObjectives_PlanId_idx" ON "PlanObjectives" ("PlanId");
CREATE INDEX "ProfileListingEvents_ProfileId_At_idx" ON "ProfileListingEvents" ("ProfileId","At");
CREATE INDEX "ProviderFocusAreas_FocusAreaId_idx" ON "ProviderFocusAreas" ("FocusAreaId");
CREATE INDEX "ProviderProfiles_listed_idx" ON "ProviderProfiles" ("City","State");
CREATE UNIQUE INDEX "Providers_Id_key" ON "Providers" ("Id");
CREATE INDEX "RecordingConsents_PatientId_idx" ON "RecordingConsents" ("PatientId");
CREATE INDEX "Reminders_due_idx" ON "Reminders" ("ScheduledFor");
CREATE INDEX "ScaleApplications_Patient_Applied_idx" ON "ScaleApplications" ("PatientId","InstrumentId","AppliedAt");
CREATE INDEX "ServiceOfferings_Provider_idx" ON "ServiceOfferings" ("ProviderId");
CREATE INDEX "Sessions_PatientId_Start_idx" ON "Sessions" ("PatientId","ScheduledStart");
CREATE INDEX "Sessions_ProviderId_Start_idx" ON "Sessions" ("ProviderId","ScheduledStart");
CREATE INDEX "ShareAccesses_ShareLinkId_At_idx" ON "ShareAccesses" ("ShareLinkId","At");
CREATE INDEX "ShareLinks_DocumentId_idx" ON "ShareLinks" ("DocumentId");
CREATE INDEX "SpeechMarkers_SessionId_idx" ON "SpeechMarkers" ("SessionId");
CREATE INDEX "Subscriptions_PaddleSubscriptionId_idx" ON "Subscriptions" ("PaddleSubscriptionId");
CREATE INDEX "ThemeTags_NoteId_idx" ON "ThemeTags" ("NoteId");
CREATE INDEX "ThemeTags_TermId_idx" ON "ThemeTags" ("TermId");
CREATE UNIQUE INDEX "ThemeTags_Note_Term_active_key" ON "ThemeTags" ("NoteId","TermId") WHERE "RemovedAt" IS NULL;
CREATE INDEX "ThemeTermChanges_TermId_idx" ON "ThemeTermChanges" ("TermId");
CREATE INDEX "ThemeTerms_VocabularyId_idx" ON "ThemeTerms" ("VocabularyId");
CREATE UNIQUE INDEX "ThemeVocabularies_single_active_key" ON "ThemeVocabularies" ("Active") WHERE "Active";
CREATE INDEX "TissGuides_InsurerId_idx" ON "TissGuides" ("InsurerId");
CREATE INDEX "TissGuides_NoteId_idx" ON "TissGuides" ("NoteId");
CREATE INDEX "TreatmentPlans_PatientId_idx" ON "TreatmentPlans" ("PatientId");
CREATE INDEX "WebhookEvents_unprocessed_idx" ON "WebhookEvents" ("ReceivedAt");
ALTER TABLE "AccessLogs" ADD CONSTRAINT "AccessLogs_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "AccountExports" ADD CONSTRAINT "AccountExports_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "AppointmentConfirmations" ADD CONSTRAINT "AppointmentConfirmations_Appointment_fk" FOREIGN KEY ("AppointmentId") REFERENCES "Appointments"("Id") ON DELETE CASCADE;
ALTER TABLE "AppointmentConfirmationTokens" ADD CONSTRAINT "AppointmentConfirmationTokens_Confirmation_fk" FOREIGN KEY ("ConfirmationId") REFERENCES "AppointmentConfirmations"("Id") ON DELETE CASCADE;
ALTER TABLE "Appointments" ADD CONSTRAINT "Appointments_Connection_fk" FOREIGN KEY ("ConnectionId") REFERENCES "CalendarConnections"("Id") ON DELETE SET NULL;
ALTER TABLE "Appointments" ADD CONSTRAINT "Appointments_Hold_fk" FOREIGN KEY ("HoldId") REFERENCES "BookingHolds"("Id") ON DELETE SET NULL;
ALTER TABLE "Appointments" ADD CONSTRAINT "Appointments_Offering_fk" FOREIGN KEY ("OfferingId") REFERENCES "ServiceOfferings"("Id") ON DELETE SET NULL;
ALTER TABLE "Appointments" ADD CONSTRAINT "Appointments_Patient_fk" FOREIGN KEY ("PatientId","ProviderId") REFERENCES "Patients"("Id","ProviderId") ON DELETE RESTRICT;
ALTER TABLE "Appointments" ADD CONSTRAINT "Appointments_SeriesId_fkey" FOREIGN KEY ("SeriesId") REFERENCES "AppointmentSeries"("Id") ON DELETE SET NULL;
ALTER TABLE "Appointments" ADD CONSTRAINT "Appointments_SessionId_fkey" FOREIGN KEY ("SessionId") REFERENCES "Sessions"("Id") ON DELETE SET NULL;
ALTER TABLE "AppointmentSeries" ADD CONSTRAINT "AppointmentSeries_Patient_fk" FOREIGN KEY ("PatientId","ProviderId") REFERENCES "Patients"("Id","ProviderId") ON DELETE CASCADE;
ALTER TABLE "AudioDestructionLogs" ADD CONSTRAINT "AudioDestructionLogs_SessionId_fkey" FOREIGN KEY ("SessionId") REFERENCES "Sessions"("Id") ON DELETE CASCADE;
ALTER TABLE "AuditEntries" ADD CONSTRAINT "AuditEntries_DocumentId_fkey" FOREIGN KEY ("DocumentId") REFERENCES "ClinicalDocuments"("Id") ON DELETE RESTRICT;
ALTER TABLE "AuditEntries" ADD CONSTRAINT "AuditEntries_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE RESTRICT;
ALTER TABLE "AvailabilityExceptions" ADD CONSTRAINT "AvailabilityExceptions_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "AvailabilityRules" ADD CONSTRAINT "AvailabilityRules_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "BillingCodes" ADD CONSTRAINT "BillingCodes_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE CASCADE;
ALTER TABLE "BookingHolds" ADD CONSTRAINT "BookingHolds_ConvertedAppointmentId_fkey" FOREIGN KEY ("ConvertedAppointmentId") REFERENCES "Appointments"("Id") ON DELETE SET NULL;
ALTER TABLE "BookingHolds" ADD CONSTRAINT "BookingHolds_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "BookingHolds" ADD CONSTRAINT "BookingHolds_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "BookingPolicies" ADD CONSTRAINT "BookingPolicies_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "BreakGlassGrants" ADD CONSTRAINT "BreakGlassGrants_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "BreakGlassGrants" ADD CONSTRAINT "BreakGlassGrants_StaffId_fkey" FOREIGN KEY ("StaffId") REFERENCES "Staff"("Id") ON DELETE RESTRICT;
ALTER TABLE "BreakGlassGrants" ADD CONSTRAINT "BreakGlassGrants_AuthorizedBy_fkey" FOREIGN KEY ("AuthorizedBy") REFERENCES "Staff"("Id") ON DELETE RESTRICT;
ALTER TABLE "CalendarConnections" ADD CONSTRAINT "CalendarConnections_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "ClinicalDocuments" ADD CONSTRAINT "ClinicalDocuments_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE SET NULL;
ALTER TABLE "ClinicalDocuments" ADD CONSTRAINT "ClinicalDocuments_Patient_fk" FOREIGN KEY ("PatientId","ProviderId") REFERENCES "Patients"("Id","ProviderId") ON DELETE RESTRICT;
ALTER TABLE "ClinicalDocuments" ADD CONSTRAINT "ClinicalDocuments_RulesetId_fkey" FOREIGN KEY ("RulesetId") REFERENCES "ConformityRulesets"("Id") ON DELETE RESTRICT;
ALTER TABLE "ClinicalDocuments" ADD CONSTRAINT "ClinicalDocuments_SupersededBy_fkey" FOREIGN KEY ("SupersededBy") REFERENCES "ClinicalDocuments"("Id") ON DELETE SET NULL;
ALTER TABLE "ConformityChecks" ADD CONSTRAINT "ConformityChecks_DocumentId_fkey" FOREIGN KEY ("DocumentId") REFERENCES "ClinicalDocuments"("Id") ON DELETE CASCADE;
ALTER TABLE "ConformityChecks" ADD CONSTRAINT "ConformityChecks_RulesetId_fkey" FOREIGN KEY ("RulesetId") REFERENCES "ConformityRulesets"("Id") ON DELETE RESTRICT;
ALTER TABLE "ConsentEvents" ADD CONSTRAINT "ConsentEvents_ConsentId_fkey" FOREIGN KEY ("ConsentId") REFERENCES "RecordingConsents"("Id") ON DELETE CASCADE;
ALTER TABLE "DenialFlags" ADD CONSTRAINT "DenialFlags_GuideId_fkey" FOREIGN KEY ("GuideId") REFERENCES "TissGuides"("Id") ON DELETE CASCADE;
ALTER TABLE "Denials" ADD CONSTRAINT "Denials_GuideId_fkey" FOREIGN KEY ("GuideId") REFERENCES "TissGuides"("Id") ON DELETE CASCADE;
ALTER TABLE "DisclosureAuthorizations" ADD CONSTRAINT "DisclosureAuthorizations_DocumentId_fkey" FOREIGN KEY ("DocumentId") REFERENCES "ClinicalDocuments"("Id") ON DELETE CASCADE;
ALTER TABLE "DisclosureAuthorizations" ADD CONSTRAINT "DisclosureAuthorizations_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE RESTRICT;
ALTER TABLE "DisposalRecords" ADD CONSTRAINT "DisposalRecords_DocumentId_fkey" FOREIGN KEY ("DocumentId") REFERENCES "ClinicalDocuments"("Id") ON DELETE SET NULL;
ALTER TABLE "DisposalRecords" ADD CONSTRAINT "DisposalRecords_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE RESTRICT;
ALTER TABLE "DocumentHashes" ADD CONSTRAINT "DocumentHashes_DocumentId_fkey" FOREIGN KEY ("DocumentId") REFERENCES "ClinicalDocuments"("Id") ON DELETE RESTRICT;
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE RESTRICT;
ALTER TABLE "ExternalDocuments" ADD CONSTRAINT "ExternalDocuments_Patient_fk" FOREIGN KEY ("PatientId","ProviderId") REFERENCES "Patients"("Id","ProviderId") ON DELETE RESTRICT;
ALTER TABLE "ExtractedFacts" ADD CONSTRAINT "ExtractedFacts_ExcerptRefId_fkey" FOREIGN KEY ("ExcerptRefId") REFERENCES "ExcerptRefs"("Id") ON DELETE SET NULL;
ALTER TABLE "ExtractedFacts" ADD CONSTRAINT "ExtractedFacts_ExternalDocumentId_fkey" FOREIGN KEY ("ExternalDocumentId") REFERENCES "ExternalDocuments"("Id") ON DELETE CASCADE;
ALTER TABLE "FocusAreas" ADD CONSTRAINT "FocusAreas_ParentId_fkey" FOREIGN KEY ("ParentId") REFERENCES "FocusAreas"("Id") ON DELETE SET NULL;
ALTER TABLE "FollowUpItems" ADD CONSTRAINT "FollowUpItems_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE SET NULL;
ALTER TABLE "FollowUpItems" ADD CONSTRAINT "FollowUpItems_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "FollowUpItems" ADD CONSTRAINT "FollowUpItems_ResolvedNoteId_fkey" FOREIGN KEY ("ResolvedNoteId") REFERENCES "Notes"("Id") ON DELETE SET NULL;
ALTER TABLE "InsurerAuthorizations" ADD CONSTRAINT "InsurerAuthorizations_InsurerId_fkey" FOREIGN KEY ("InsurerId") REFERENCES "Insurers"("Id") ON DELETE RESTRICT;
ALTER TABLE "InsurerAuthorizations" ADD CONSTRAINT "InsurerAuthorizations_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "Insurers" ADD CONSTRAINT "Insurers_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "IntakeQuestions" ADD CONSTRAINT "IntakeQuestions_TemplateId_fkey" FOREIGN KEY ("TemplateId") REFERENCES "IntakeTemplates"("Id") ON DELETE CASCADE;
ALTER TABLE "IntakeResponses" ADD CONSTRAINT "IntakeResponses_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "IntakeResponses" ADD CONSTRAINT "IntakeResponses_QuestionId_fkey" FOREIGN KEY ("QuestionId") REFERENCES "IntakeQuestions"("Id") ON DELETE RESTRICT;
ALTER TABLE "IntakeResponses" ADD CONSTRAINT "IntakeResponses_SessionId_fkey" FOREIGN KEY ("SessionId") REFERENCES "Sessions"("Id") ON DELETE SET NULL;
ALTER TABLE "IntakeTemplates" ADD CONSTRAINT "IntakeTemplates_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "MedicationEntries" ADD CONSTRAINT "MedicationEntries_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "MedicationEntries" ADD CONSTRAINT "MedicationEntries_SourceFactId_fkey" FOREIGN KEY ("SourceFactId") REFERENCES "ExtractedFacts"("Id") ON DELETE SET NULL;
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_Patient_fk" FOREIGN KEY ("PatientId","ProviderId") REFERENCES "Patients"("Id","ProviderId") ON DELETE RESTRICT;
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_Session_fk" FOREIGN KEY ("SessionId") REFERENCES "Sessions"("Id") ON DELETE SET NULL;
ALTER TABLE "NoteSections" ADD CONSTRAINT "NoteSections_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE CASCADE;
ALTER TABLE "Notifications" ADD CONSTRAINT "Notifications_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "PatientQuotes" ADD CONSTRAINT "PatientQuotes_ExcerptRefId_fkey" FOREIGN KEY ("ExcerptRefId") REFERENCES "ExcerptRefs"("Id") ON DELETE SET NULL;
ALTER TABLE "PatientQuotes" ADD CONSTRAINT "PatientQuotes_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE CASCADE;
ALTER TABLE "PatientDiagnoses" ADD CONSTRAINT "PatientDiagnoses_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "PatientQuotes" ADD CONSTRAINT "PatientQuotes_TermId_fkey" FOREIGN KEY ("TermId") REFERENCES "ThemeTerms"("Id") ON DELETE SET NULL;
ALTER TABLE "Patients" ADD CONSTRAINT "Patients_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE RESTRICT;
ALTER TABLE "Patients" ADD CONSTRAINT "Patients_AccountId_fkey" FOREIGN KEY ("AccountId") REFERENCES "PatientAccounts"("Id") ON DELETE SET NULL;
ALTER TABLE "PatientInvitations" ADD CONSTRAINT "PatientInvitations_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "PatientInvitations" ADD CONSTRAINT "PatientInvitations_AcceptedAccountId_fkey" FOREIGN KEY ("AcceptedAccountId") REFERENCES "PatientAccounts"("Id") ON DELETE SET NULL;
ALTER TABLE "PlanFeatures" ADD CONSTRAINT "PlanFeatures_PlanId_fkey" FOREIGN KEY ("PlanId") REFERENCES "Plans"("Id") ON DELETE CASCADE;
ALTER TABLE "PlanObjectives" ADD CONSTRAINT "PlanObjectives_PlanId_fkey" FOREIGN KEY ("PlanId") REFERENCES "TreatmentPlans"("Id") ON DELETE CASCADE;
ALTER TABLE "PlanObjectives" ADD CONSTRAINT "PlanObjectives_Session_fk" FOREIGN KEY ("LastRecordedSessionId") REFERENCES "Sessions"("Id") ON DELETE SET NULL;
ALTER TABLE "PlanObjectives" ADD CONSTRAINT "PlanObjectives_Term_fk" FOREIGN KEY ("TermId") REFERENCES "ThemeTerms"("Id") ON DELETE SET NULL;
ALTER TABLE "ProfileListingEvents" ADD CONSTRAINT "ProfileListingEvents_ProfileId_fkey" FOREIGN KEY ("ProfileId") REFERENCES "ProviderProfiles"("Id") ON DELETE CASCADE;
ALTER TABLE "ProviderFocusAreas" ADD CONSTRAINT "ProviderFocusAreas_FocusAreaId_fkey" FOREIGN KEY ("FocusAreaId") REFERENCES "FocusAreas"("Id") ON DELETE RESTRICT;
ALTER TABLE "ProviderFocusAreas" ADD CONSTRAINT "ProviderFocusAreas_ProfileId_fkey" FOREIGN KEY ("ProfileId") REFERENCES "ProviderProfiles"("Id") ON DELETE CASCADE;
ALTER TABLE "ProviderInstrumentOptIns" ADD CONSTRAINT "ProviderInstrumentOptIns_InstrumentId_fkey" FOREIGN KEY ("InstrumentId") REFERENCES "Instruments"("Id") ON DELETE RESTRICT;
ALTER TABLE "ProviderInstrumentOptIns" ADD CONSTRAINT "ProviderInstrumentOptIns_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "ProviderProfiles" ADD CONSTRAINT "ProviderProfiles_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "RecordingConsents" ADD CONSTRAINT "RecordingConsents_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "RecordingConsents" ADD CONSTRAINT "RecordingConsents_TreatmentPlanId_fkey" FOREIGN KEY ("TreatmentPlanId") REFERENCES "TreatmentPlans"("Id") ON DELETE SET NULL;
ALTER TABLE "Reminders" ADD CONSTRAINT "Reminders_AppointmentId_fkey" FOREIGN KEY ("AppointmentId") REFERENCES "Appointments"("Id") ON DELETE CASCADE;
ALTER TABLE "Reminders" ADD CONSTRAINT "Reminders_Confirmation_fk" FOREIGN KEY ("ConfirmationId") REFERENCES "AppointmentConfirmations"("Id") ON DELETE SET NULL;
ALTER TABLE "RetentionRules" ADD CONSTRAINT "RetentionRules_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "RoomSessions" ADD CONSTRAINT "RoomSessions_AppointmentId_fkey" FOREIGN KEY ("AppointmentId") REFERENCES "Appointments"("Id") ON DELETE CASCADE;
ALTER TABLE "ScaleApplications" ADD CONSTRAINT "ScaleApplications_InstrumentId_fkey" FOREIGN KEY ("InstrumentId") REFERENCES "Instruments"("Id") ON DELETE RESTRICT;
ALTER TABLE "ScaleApplications" ADD CONSTRAINT "ScaleApplications_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "ScaleApplications" ADD CONSTRAINT "ScaleApplications_SessionId_fkey" FOREIGN KEY ("SessionId") REFERENCES "Sessions"("Id") ON DELETE SET NULL;
ALTER TABLE "ServiceOfferings" ADD CONSTRAINT "ServiceOfferings_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "Sessions" ADD CONSTRAINT "Sessions_Patient_fk" FOREIGN KEY ("PatientId","ProviderId") REFERENCES "Patients"("Id","ProviderId") ON DELETE RESTRICT;
ALTER TABLE "ShareAccesses" ADD CONSTRAINT "ShareAccesses_ShareLinkId_fkey" FOREIGN KEY ("ShareLinkId") REFERENCES "ShareLinks"("Id") ON DELETE CASCADE;
ALTER TABLE "ShareLinks" ADD CONSTRAINT "ShareLinks_AuthorizationId_fkey" FOREIGN KEY ("AuthorizationId") REFERENCES "DisclosureAuthorizations"("Id") ON DELETE RESTRICT;
ALTER TABLE "ShareLinks" ADD CONSTRAINT "ShareLinks_DocumentId_fkey" FOREIGN KEY ("DocumentId") REFERENCES "ClinicalDocuments"("Id") ON DELETE CASCADE;
ALTER TABLE "SpecialistTitles" ADD CONSTRAINT "SpecialistTitles_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "SpeechMarkers" ADD CONSTRAINT "SpeechMarkers_SessionId_fkey" FOREIGN KEY ("SessionId") REFERENCES "Sessions"("Id") ON DELETE CASCADE;
ALTER TABLE "Subscriptions" ADD CONSTRAINT "Subscriptions_PlanId_fkey" FOREIGN KEY ("PlanId") REFERENCES "Plans"("Id") ON DELETE RESTRICT;
ALTER TABLE "Subscriptions" ADD CONSTRAINT "Subscriptions_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE RESTRICT;
ALTER TABLE "ThemeTags" ADD CONSTRAINT "ThemeTags_ExcerptRefId_fkey" FOREIGN KEY ("ExcerptRefId") REFERENCES "ExcerptRefs"("Id") ON DELETE SET NULL;
ALTER TABLE "ThemeTags" ADD CONSTRAINT "ThemeTags_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE CASCADE;
ALTER TABLE "ThemeTags" ADD CONSTRAINT "ThemeTags_TermId_fkey" FOREIGN KEY ("TermId") REFERENCES "ThemeTerms"("Id") ON DELETE RESTRICT;
ALTER TABLE "ThemeTermChanges" ADD CONSTRAINT "ThemeTermChanges_StaffId_fkey" FOREIGN KEY ("StaffId") REFERENCES "Staff"("Id") ON DELETE RESTRICT;
ALTER TABLE "ThemeTermChanges" ADD CONSTRAINT "ThemeTermChanges_TermId_fkey" FOREIGN KEY ("TermId") REFERENCES "ThemeTerms"("Id") ON DELETE RESTRICT;
ALTER TABLE "ThemeTermSuccessions" ADD CONSTRAINT "ThemeTermSuccessions_FromTermId_fkey" FOREIGN KEY ("FromTermId") REFERENCES "ThemeTerms"("Id") ON DELETE RESTRICT;
ALTER TABLE "ThemeTermSuccessions" ADD CONSTRAINT "ThemeTermSuccessions_StaffId_fkey" FOREIGN KEY ("StaffId") REFERENCES "Staff"("Id") ON DELETE RESTRICT;
ALTER TABLE "ThemeTermSuccessions" ADD CONSTRAINT "ThemeTermSuccessions_ToTermId_fkey" FOREIGN KEY ("ToTermId") REFERENCES "ThemeTerms"("Id") ON DELETE RESTRICT;
ALTER TABLE "ThemeTerms" ADD CONSTRAINT "ThemeTerms_ParentTermId_fkey" FOREIGN KEY ("ParentTermId") REFERENCES "ThemeTerms"("Id") ON DELETE SET NULL;
ALTER TABLE "ThemeTerms" ADD CONSTRAINT "ThemeTerms_SupersededByTermId_fkey" FOREIGN KEY ("SupersededByTermId") REFERENCES "ThemeTerms"("Id") ON DELETE SET NULL;
ALTER TABLE "ThemeTerms" ADD CONSTRAINT "ThemeTerms_VocabularyId_fkey" FOREIGN KEY ("VocabularyId") REFERENCES "ThemeVocabularies"("Id") ON DELETE RESTRICT;
ALTER TABLE "TissGuides" ADD CONSTRAINT "TissGuides_AuthorizationId_fkey" FOREIGN KEY ("AuthorizationId") REFERENCES "InsurerAuthorizations"("Id") ON DELETE SET NULL;
ALTER TABLE "TissGuides" ADD CONSTRAINT "TissGuides_InsurerId_fkey" FOREIGN KEY ("InsurerId") REFERENCES "Insurers"("Id") ON DELETE RESTRICT;
ALTER TABLE "TissGuides" ADD CONSTRAINT "TissGuides_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE RESTRICT;
ALTER TABLE "TissGuides" ADD CONSTRAINT "TissGuides_SessionId_fkey" FOREIGN KEY ("SessionId") REFERENCES "Sessions"("Id") ON DELETE SET NULL;
ALTER TABLE "Transcripts" ADD CONSTRAINT "Transcripts_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE SET NULL;
ALTER TABLE "Transcripts" ADD CONSTRAINT "Transcripts_SessionId_fkey" FOREIGN KEY ("SessionId") REFERENCES "Sessions"("Id") ON DELETE CASCADE;
ALTER TABLE "TreatmentPlans" ADD CONSTRAINT "TreatmentPlans_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "UsageCounters" ADD CONSTRAINT "UsageCounters_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
CREATE VIEW "InstrumentSeries" AS (SELECT a."Id", a."PatientId", a."SessionId", a."InstrumentId", a."Score", a."AppliedAt", a."AppliedBy", a."RawAnswers", a."CreatedAt", i."Code" AS "InstrumentCode", i."SatepsiStatus" FROM "ScaleApplications" a JOIN "Instruments" i ON i."Id" = a."InstrumentId" WHERE i."SatepsiStatus" = ANY (ARRAY['nao_privativo'::text, 'favoravel'::text]));
-- Instrument catalog (seed). Re-running keeps rows already present.
insert into "Instruments"
  ("Code","Name","ItemCount","MinScore","MaxScore","SatepsiStatus","SatepsiCheckedOn",
   "LicenseMode","MayRenderItems","Enabled","StatusExplanation","SourceRef") values

  -- Consta na lista oficial de Instrumentos Não Privativos do SATEPSI, com
  -- análise em 07/05/2020. Isso NÃO é aprovação: significa que o CFP
  -- determinou que o PHQ-9 não é teste psicológico e portanto está fora da
  -- certificação. A qualidade psicométrica não é avaliada pelo CFP, e conferir
  -- respaldo científico é dever do profissional. Como fonte complementar
  -- (art. 4º), nunca pode ser base única de conclusão.
  -- Validação brasileira: Santos IS et al., Cad. Saúde Pública 2013;29(8):1533-43.
  ('PHQ-9', 'Patient Health Questionnaire-9', 9, 0, 27,
   'nao_privativo', date '2026-08-23', 'livre', true, true,
   'O CFP classificou o PHQ-9 como instrumento não privativo (SATEPSI, análise em 07/05/2020). Isso não é aprovação: significa que o CFP entende que ele não é teste psicológico e, por isso, está fora da certificação — o SATEPSI não avalia a qualidade psicométrica dos não privativos. Como fonte complementar (Res. CFP 31/2022, art. 4º), pode apoiar seu raciocínio, mas nunca ser a base única de uma conclusão. Conferir o respaldo científico é responsabilidade sua. A reprodução dos itens é liberada pelo detentor, sem necessidade de autorização.',
   'SATEPSI Instrumentos Não Privativos, análise 07/05/2020; rodapé do instrumento: "No permission required to reproduce, translate, display or distribute"'),

  -- Os dois inventários de "A Mente Vencendo o Humor" (Greenberger & Padesky),
  -- na lista de Não Privativos do SATEPSI. São o achado que faltava: foram
  -- desenhados PARA aplicação seriada em TCC, que é exatamente o uso do
  -- Anamnys — diferente do PHQ-9, que é rastreio adaptado a isso.
  --
  -- Só escore. A Guilford e o site oficial vedam expressamente reprodução em
  -- site, intranet ou plataforma, e vedam uso comercial. "MayRenderItems" fica
  -- false, e a restrição "Instruments_Render_ck" impede ligá-lo por engano,
  -- porque 'proprietario' não está na lista permitida.
  --
  -- E NÃO EXISTEM NORMAS NEM PONTOS DE CORTE. Padesky afirma isso: "We do not
  -- have established norms". Aqui isso é vantagem, não defeito — o esquema já
  -- se recusa a guardar faixa de gravidade. O valor é a curva do próprio
  -- paciente ao longo do tempo, nunca comparação com população.
  --
  -- Tradução brasileira da Artmed (2ª ed., 2017), editorial: não há estudo
  -- psicométrico brasileiro publicado. Os estudos de validade concorrente são
  -- da versão em inglês (Beal et al. 2004 para o de depressão; Cox para o de
  -- ansiedade).
  ('MOM-D', 'Inventário de Depressão — A Mente Vencendo o Humor', 19, 0, 57,
   'nao_privativo', date '2026-08-23', 'proprietario', false, true,
   'O CFP classificou os inventários de A Mente Vencendo o Humor como instrumentos não privativos (SATEPSI). Isso não é aprovação — significa que estão fora da certificação, como fonte complementar (Res. CFP 31/2022, art. 4º), nunca base única de conclusão. O Anamnys registra o escore que você obtiver, mas não exibe os itens: a Guilford e o site oficial vedam reprodução em plataformas e uso comercial. Aplique pelo livro (Artmed, 2ª ed.). Atenção clínica: os autores declaram que não existem normas nem pontos de corte estabelecidos, então leia a evolução do próprio paciente ao longo do tempo, e não o número como classificação.',
   'SATEPSI Instrumentos Não Privativos; Guilford Press FAQ e mindovermood.com/worksheets (vedação de uso em plataforma e uso comercial)'),

  ('MOM-A', 'Inventário de Ansiedade — A Mente Vencendo o Humor', 24, 0, 72,
   'nao_privativo', date '2026-08-23', 'proprietario', false, true,
   'O CFP classificou os inventários de A Mente Vencendo o Humor como instrumentos não privativos (SATEPSI). Isso não é aprovação — significa que estão fora da certificação, como fonte complementar (Res. CFP 31/2022, art. 4º), nunca base única de conclusão. O Anamnys registra o escore que você obtiver, mas não exibe os itens: a Guilford e o site oficial vedam reprodução em plataformas e uso comercial. Aplique pelo livro (Artmed, 2ª ed.). Atenção clínica: os autores declaram que não existem normas nem pontos de corte estabelecidos, então leia a evolução do próprio paciente ao longo do tempo, e não o número como classificação.',
   'SATEPSI Instrumentos Não Privativos; Guilford Press FAQ e mindovermood.com/worksheets (vedação de uso em plataforma e uso comercial)')

on conflict ("Code") do nothing;

insert into "Instruments"
  ("Code","Name","ItemCount","MinScore","MaxScore","SatepsiStatus","SatepsiCheckedOn",
   "LicenseMode","MayRenderItems","Enabled","StatusExplanation","SourceRef") values

  -- (a) falta ética — art. 12
  ('DASS-21', 'Escala de Depressão, Ansiedade e Estresse', 21, 0, 63,
   'nao_avaliado', date '2026-08-23', 'autorizacao_necessaria', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('HAD', 'Escala Hospitalar de Ansiedade e Depressão', 14, 0, 42,
   'nao_avaliado', date '2026-08-23', 'proprietario', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('ROSENBERG', 'Escala de Autoestima de Rosenberg', 10, 0, 30,
   'nao_avaliado', date '2026-08-23', 'autorizacao_necessaria', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('EPDS', 'Escala de Depressão Pós-Natal de Edimburgo', 10, 0, 30,
   'nao_avaliado', date '2026-08-23', 'autorizacao_necessaria', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('MBI', 'Maslach Burnout Inventory', 22, 0, 132,
   'nao_avaliado', date '2026-08-23', 'proprietario', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação. Além disso, é obra proprietária: exibir os itens exigiria licença do detentor.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  ('BSQ', 'Body Shape Questionnaire', 34, 34, 204,
   'nao_avaliado', date '2026-08-23', 'autorizacao_necessaria', false, false,
   'Este instrumento consta na lista de Testes Não Avaliados do SATEPSI. A Res. CFP 31/2022, art. 12, trata o uso de instrumento dessa lista como falta ética, e a apuração recai sobre você, não sobre o Anamnys — por isso o sistema não o oferece nem aceita registros dele. Para mudar isso, o caminho é o art. 13: peça ao seu CRP que submeta o instrumento à CCAP do CFP para avaliação.',
   'https://satepsi.cfp.org.br/testesNaoAvaliados.cfm'),

  -- (b) sem classificação. Eu havia habilitado estes dois presumindo que a CCAP
  -- os classificaria como não privativos, do mesmo jeito que fez com o PHQ-9.
  -- A semelhança é grande, e a presunção continua sendo presunção — que é
  -- exatamente o que não se pode fazer aqui. Desligados até o art. 13 correr.
  ('GAD-7', 'Generalized Anxiety Disorder-7', 7, 0, 21,
   'sem_classificacao', null, 'livre', false, false,
   'O CFP ainda não se pronunciou sobre o GAD-7: ele não consta em nenhuma lista do SATEPSI — nem como não privativo, nem como vedado. Isso é diferente de estar proibido. O art. 12 tipifica falta ética por lista, e o GAD-7 não está em nenhuma; o art. 5º lhe dá a prerrogativa de escolher instrumentos fundamentados na literatura científica, e ele tem validação brasileira publicada (Moreno et al., 2016). Por isso o Anamnys não o oferece no catálogo padrão, mas registra seus escores se você aderir explicitamente, ciente de que a classificação está pendente e pode mudar. O que o app nunca fará é exibir os itens: se a CCAP vier a considerá-lo teste psicológico, a aplicação informatizada dependeria de manual aprovado e estudo de equivalência. Para resolver a pendência, o caminho é o art. 13 — pedir ao seu CRP que o submeta à CCAP.',
   'https://satepsi.cfp.org.br/faq.cfm'),

  ('PCL-5', 'PTSD Checklist for DSM-5', 20, 0, 80,
   'sem_classificacao', null, 'dominio_publico', false, false,
   'O CFP ainda não se pronunciou sobre o PCL-5: ele não consta em nenhuma lista do SATEPSI, o que é diferente de estar proibido. O instrumento é de domínio público (National Center for PTSD) e tem adaptação transcultural brasileira publicada (Osório et al., 2017), mas domínio público não substitui classificação do CFP. Fora do catálogo padrão; seus escores podem ser registrados mediante adesão explícita sua, ciente da pendência. Os itens não são exibidos pelo app. Para resolver a pendência, peça ao seu CRP que o submeta à CCAP (Res. CFP 31/2022, art. 13).',
   'https://satepsi.cfp.org.br/faq.cfm'),

  -- (c) direito autoral. Estes dois são testes psicológicos com parecer
  -- FAVORÁVEL — o CFP permitiria. Ficam desligados por causa da editora.
  -- "MayRenderItems" nunca pode virar true aqui: "Instruments_Render_ck"
  -- recusa em 'proprietario'.
  ('BDI-2', 'Inventário de Depressão de Beck — 2ª edição', 21, 0, 63,
   'favoravel', date '2026-08-23', 'proprietario', false, false,
   'O CFP não impede o uso: o BDI-2 é teste psicológico com parecer favorável no SATEPSI. O impedimento é de direito autoral — a obra é da Hogrefe, com venda restrita a psicólogos mediante CRP e plataforma de aplicação própria. O Anamnys não pode exibir os itens sem contrato. Aplique pelo material oficial que você adquiriu; assim que a habilitação comercial existir, você poderá lançar aqui o escore obtido.',
   'Hogrefe Brasil — Escalas Beck'),

  ('BAI', 'Inventário de Ansiedade de Beck', 21, 0, 63,
   'favoravel', date '2026-08-23', 'proprietario', false, false,
   'O CFP não impede o uso: o BAI é teste psicológico com parecer favorável no SATEPSI. O impedimento é de direito autoral, igual ao do BDI-2 — obra da Hogrefe, venda restrita a psicólogos mediante CRP. Aplique pelo material oficial; o Anamnys não exibe os itens sem contrato com a editora.',
   'Hogrefe Brasil — Escalas Beck'),

  -- (b) + (c): sem classificação no SATEPSI E com uso digital vedado pela
  -- licença. Dois impedimentos independentes, ambos suficientes sozinhos.
  ('ORS', 'Outcome Rating Scale', 4, 0, 40,
   'sem_classificacao', null, 'vedado_digital', false, false,
   'Dois impedimentos independentes. O CFP ainda não se pronunciou sobre a ORS — ela não consta em nenhuma lista do SATEPSI, o que por si só a mantém fora do catálogo padrão. Mas o segundo impedimento é mais duro e não se resolve com adesão sua: a licença gratuita do autor cobre apenas papel e caneta, e veda expressamente o uso digital das escalas. Registrar escores aqui exigiria acordo direto com o detentor, além da classificação pela CCAP (art. 13).',
   'scottdmiller.com/downloadmeasures'),

  ('SRS', 'Session Rating Scale', 4, 0, 40,
   'sem_classificacao', null, 'vedado_digital', false, false,
   'Mesma situação da ORS: sem classificação no SATEPSI, e com uso digital das escalas expressamente vedado pela licença do autor. O segundo impedimento não se resolve com adesão sua — depende de acordo com o detentor.',
   'scottdmiller.com/downloadmeasures')

on conflict ("Code") do nothing;

-- Conformity rulesets, Res. CFP 006/2019 (seed)
insert into "ConformityRulesets"
  ("DocumentKind","Version","ArticleRef","Elements","Prohibitions","EffectiveFrom") values

  -- DECLARAÇÃO — registra prestação de serviço, e nada além disso.
  -- Note que a norma NÃO pede identificação do solicitante aqui: a declaração
  -- é sobre quem foi atendido.
  ('declaracao', 'cfp-006-2019.1', 'Art. 9º, §§1º e 2º; Art. 5º §8º; Art. 17',
   '["titulo_declaracao",
     "nome_pessoa_atendida",
     "finalidade",
     "local_dias_horarios_e_duracao_do_acompanhamento",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "assinatura",
     "laudas_numeradas_rubricadas",
     "prazo_validade"]',
   -- Art. 9º §1º. A vedação é o coração da declaração: é o que a separa do
   -- atestado. Documento que descreve estado psicológico não é declaração.
   '["sintomas_situacoes_ou_estados_psicologicos"]',
   '2019-06-01'),

  -- ATESTADO PSICOLÓGICO — certifica situação, estado ou funcionamento
  -- psicológico com base em avaliação psicológica.
  ('atestado', 'cfp-006-2019.1', 'Art. 10, §§5º e 6º; Art. 5º §8º; Art. 17',
   '["titulo_atestado_psicologico",
     "nome_pessoa_ou_instituicao_atendida",
     "nome_solicitante",
     "finalidade",
     "descricao_condicoes_psicologicas",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "laudas_numeradas_rubricadas",
     "assinatura",
     "prazo_validade"]',
   -- Art. 10 §5º: texto corrido, separado só por pontuação, sem parágrafos,
   -- para evitar adulteração; havendo parágrafo, preencher com traços. E o
   -- documento restringe-se à informação solicitada.
   '["quebra_de_paragrafo_sem_preenchimento",
     "informacao_alem_do_solicitado"]',
   '2019-06-01'),

  -- RELATÓRIO PSICOLÓGICO — cinco itens. Sem referências obrigatórias.
  ('relatorio', 'cfp-006-2019.1', 'Art. 11, §§1º a 6º; Art. 5º §8º; Art. 17',
   '["identificacao",
     "titulo_relatorio_psicologico",
     "nome_pessoa_ou_instituicao_atendida",
     "nome_solicitante",
     "finalidade",
     "nome_autor_e_inscricao_crp",
     "descricao_demanda",
     "procedimento",
     "referencial_teorico",
     "pessoas_ouvidas_numero_de_encontros_e_duracao",
     "analise",
     "conclusao",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "laudas_numeradas_rubricadas",
     "assinatura",
     "prazo_validade"]',
   -- Art. 11 §5º III e Art. 6º §5º / Art. 11 III.
   '["afirmacao_sem_fonte_ou_sustentacao",
     "descricao_literal_das_sessoes"]',
   '2019-06-01'),

  -- RELATÓRIO MULTIPROFISSIONAL — modalidade própria do Art. 12, não é uma
  -- variação do relatório psicológico: a autoria é coletiva, os procedimentos
  -- privativos da Psicologia vêm separados e cada profissional assina a sua
  -- análise. Faltava inteiramente no rascunho anterior.
  ('relatorio_multiprofissional', 'cfp-006-2019.1', 'Art. 12, §§1º a 9º; Art. 5º §8º; Art. 17',
   '["identificacao",
     "titulo_relatorio_multiprofissional",
     "nome_pessoa_ou_instituicao_atendida",
     "nome_solicitante",
     "finalidade",
     "nomes_autores_categorias_e_registros_profissionais",
     "descricao_demanda",
     "procedimento",
     "procedimentos_privativos_da_psicologia_em_secao_separada",
     "analise",
     "analise_identificada_por_profissional_e_categoria",
     "conclusao",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nomes_profissionais",
     "inscricoes_profissionais",
     "laudas_numeradas_rubricadas",
     "assinatura_do_psicologo",
     "prazo_validade"]',
   -- Art. 12 §7º remete ao Art. 11 §5º, então as vedações do relatório valem.
   '["afirmacao_sem_fonte_ou_sustentacao",
     "descricao_literal_das_sessoes"]',
   '2019-06-01'),

  -- LAUDO PSICOLÓGICO — seis itens, obrigatoriamente em forma de itens, e
  -- referências obrigatórias. Não existe item "metodologia": o referencial
  -- teórico-metodológico é conteúdo do Procedimento (Art. 13 §4º).
  ('laudo', 'cfp-006-2019.1', 'Art. 13, §§1º a 7º; Art. 5º §8º; Art. 17',
   '["secoes_em_forma_de_itens",
     "identificacao",
     "titulo_laudo_psicologico",
     "nome_pessoa_ou_instituicao_atendida",
     "nome_solicitante",
     "finalidade",
     "nome_autor_e_inscricao_crp",
     "descricao_demanda",
     "procedimento",
     "referencial_teorico_metodologico",
     "pessoas_ouvidas_numero_de_encontros_e_duracao",
     "analise",
     "conclusao",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "laudas_numeradas_rubricadas",
     "assinatura",
     "referencias",
     "prazo_validade"]',
   -- Art. 13 §5º I e III.
   '["descricao_literal_das_sessoes",
     "afirmacao_sem_fonte_ou_sustentacao"]',
   '2019-06-01'),

  -- PARECER PSICOLÓGICO — cinco itens, SEM procedimento, com referências
  -- obrigatórias e com exigência extra na identificação: a titulação que
  -- comprove conhecimento específico no assunto (Art. 14 §2º V).
  ('parecer', 'cfp-006-2019.1', 'Art. 14, §§1º a 6º; Art. 5º §8º; Art. 17',
   '["secoes_em_forma_de_itens",
     "identificacao",
     "titulo_parecer_psicologico",
     "nome_pessoa_ou_instituicao_objeto_do_questionamento",
     "nome_solicitante",
     "finalidade",
     "nome_autor_inscricao_crp_e_titulacao",
     "descricao_demanda",
     "analise",
     "conclusao",
     "local_emissao",
     "data_emissao",
     "carimbo",
     "nome_psicologo",
     "inscricao_crp",
     "laudas_numeradas_rubricadas",
     "assinatura",
     "referencias",
     "prazo_validade"]',
   -- Art. 14, inciso IV: o parecer NÃO é documento resultante de processo de
   -- avaliação psicológica ou de intervenção psicológica. Parecer que conclui
   -- a partir de avaliação é laudo com o título errado.
   '["resultado_de_avaliacao_ou_intervencao_psicologica"]',
   '2019-06-01')

on conflict ("DocumentKind","Version") do nothing;

-- ── theme vocabulary (F-02) ──────────────────────────────────────────────────
-- Mental health only. The per-specialty structure stays in the schema, but no
-- other specialty is seeded while the product is positioned for psychology.
--
-- O QUE A D-01 PERMITE E O QUE NÃO PERMITE, porque a diferença decide o que
-- pode ser semeado hoje:
--
--   ACRESCENTAR termo é seguro. Nenhuma série existente muda de significado —
--   'trabalho' continua querendo dizer trabalho depois que 'assedio_no_trabalho'
--   passa a existir. O único efeito é que a série do termo novo começa na data
--   em que ele entrou, e é exatamente para isso que existe "IntroducedOn".
--
--   RENOMEAR, DIVIDIR ou FUNDIR termo não é seguro, e continua bloqueado pela
--   D-01. É o caso de dividir 'ansiedade' em 'generalizada' e 'social': dois
--   anos de marcação anterior deixariam de ter sentido. Quando essa regra for
--   escrita, o caminho já está no esquema — "SupersededByTermId" liga o termo
--   antigo ao novo, e a série se remonta pela ligação.
--
-- Por isso os termos abaixo entram por acréscimo, e os recortes de trabalho e
-- imigração entram como FILHOS ("ParentTermId") em vez de substituir os termos
-- amplos. Uma consulta de rollup soma filho no pai, e a série de 'trabalho'
-- segue contínua de ponta a ponta.
-- Superado em 2026-10-21: a D-01 permite renomear, dividir e fundir sem mover marcações (documentation/23-theme-vocabulary.md).
insert into "ThemeVocabularies" ("Version","PublishedAt","Active")
  values ('mh-1', now(), true)
on conflict ("Version") do nothing;

-- Termos de primeiro nível.
insert into "ThemeTerms" ("VocabularyId","Code","Label")
select v."Id", t.code, t.label
  from "ThemeVocabularies" v
  cross join (values
    -- conjunto original
    ('sono',            'Sono'),
    ('humor',           'Humor'),
    ('ansiedade',       'Ansiedade'),
    ('trabalho',        'Trabalho'),
    ('familia',         'Família'),
    ('relacionamentos', 'Relacionamentos'),
    ('autocuidado',     'Autocuidado'),
    ('luto',            'Luto'),
    ('imagem_corporal', 'Imagem corporal'),
    ('alimentacao',     'Alimentação'),
    ('uso_substancias', 'Uso de substâncias'),
    ('estudos',         'Estudos'),
    ('financas',        'Finanças'),
    ('sexualidade',     'Sexualidade'),
    ('medicacao',       'Medicação'),

    -- Estados que apareciam diluídos em 'humor' e não se deixavam acompanhar.
    ('raiva_irritabilidade',    'Raiva e irritabilidade'),
    ('culpa_vergonha',          'Culpa e vergonha'),
    ('solidao',                 'Solidão'),
    ('motivacao_procrastinacao','Motivação e procrastinação'),

    -- RISCO — NÃO SEMEAR. 'ideacao_suicida' e 'autolesao' foram propostos e
    -- descartados. Registro do porquê, para que ninguém os acrescente de novo
    -- achando que é esquecimento:
    --
    --   1. Quem aplica a etiqueta é a IA. O F-02 restringe o prompt de
    --      estruturação ao vocabulário ativo, e "ThemeTags"."CreatedBy" tem
    --      default 'ai'. Máquina escolhendo 'ideacao_suicida' é máquina
    --      emitindo inferência de risco — a linha do Manual Orientativo do
    --      CFP (2025).
    --   2. A nota já diz, e diz melhor. Na prosa está o entendimento do
    --      profissional, com a nuance que fez daquilo um julgamento, escrito e
    --      assinado por ele. A etiqueta achata isso numa palavra e reatribui a
    --      autoria ao sistema.
    --   3. A série longitudinal não serve. Para 'sono' a tendência é o dado;
    --      para risco, decide-se na primeira menção, não pela curva. E a
    --      detecção de ausência (F-12) geraria "registrado em 4 das 9
    --      primeiras sessões; sem registro nas 11 seguintes" — afirmação de
    --      que o risco cessou, produzida por software.
    --   4. Parece rede de proteção e não é. O sistema vê só o que foi escrito
    --      em nota estruturada. Paciente em risco pode nunca dizer as palavras.
    --      Sinal silenciosamente incompleto com cara de vigilância é pior do
    --      que sinal nenhum.
    --   5. O esquema não consegue garantir "só humano aplica este termo".
    --      Seria convenção, não restrição, e para isto convenção não serve.
    --
    -- Se um dia o risco precisar de estrutura, entra como plano de segurança
    -- criado e assinado deliberadamente pelo profissional — nunca como termo
    -- extraível deste vocabulário.

    -- 'violencia_domestica' — NÃO SEMEAR. Descartado por motivo diferente do
    -- que derrubou os termos de risco acima. Não é conclusão clínica: é fato
    -- relatado, como 'luto'. Mas:
    --
    --   1. A detecção de ausência (F-12) geraria "registrada em 4 das 9
    --      primeiras sessões; sem registro nas 11 seguintes". Em violência
    --      doméstica o relato parar é característica conhecida do ciclo, e a
    --      frase renderiza o oposto disso. Pior aqui do que na ideação
    --      suicida: lá o silêncio é ambíguo, aqui o sistema inverte o sinal.
    --   2. Existe DEVER LEGAL DE NOTIFICAR. Lei 10.778/2003, art. 1º —
    --      notificação compulsória dos casos com indícios ou confirmação de
    --      violência contra a mulher atendida em serviços de saúde públicos E
    --      PRIVADOS; a Lei 13.931/2019 acrescentou comunicação à autoridade
    --      policial em 24 h. Para criança e adolescente, ECA art. 13,
    --      comunicação ao Conselho Tutelar. O alcance disso sobre o psicólogo
    --      autônomo é matéria em disputa, e o produto não decide por ele.
    --   3. Etiqueta estruturada e consultável não é a frase na prosa. É prova
    --      legível por máquina de que o caso foi identificado pelo sistema, em
    --      prontuário requisitável, sem registro de que algo foi feito. Isso
    --      não protege o profissional: expõe.
    --
    -- Violência no prontuário não é tema, é gatilho de um dever. O que precisa
    -- ser modelado é o dever e o ATO — quem notificou, quando, a quem —, não o
    -- assunto. Mesmo caminho do plano de segurança.
    --
    -- 'discriminacao' FICA. Testado contra os mesmos critérios e não compartilha
    -- nenhum: a IA não infere estado psicológico ao marcá-lo, a recorrência ao
    -- longo do acompanhamento tem uso clínico real, e não há dever legal
    -- pendurado. Mesma natureza de 'luto' e 'desemprego'.
    ('discriminacao',           'Discriminação'),

    ('dor_saude_fisica',        'Dor e saúde física'),
    ('sentido_espiritualidade', 'Sentido e espiritualidade'),
    ('parentalidade',           'Parentalidade'),
    ('identidade_genero',       'Identidade de gênero'),
    ('uso_de_telas',            'Uso de telas'),
    ('transicoes_de_vida',      'Transições de vida'),
    ('envelhecimento',          'Envelhecimento'),

    -- Raiz nova. Os recortes vêm abaixo, como filhos.
    ('imigracao',               'Imigração')
  ) as t(code,label)
 where v."Version" = 'mh-1'
on conflict ("VocabularyId","Code") do nothing;

-- Termos de segundo nível. O pai é resolvido pelo código, então esta parte pode
-- rodar quantas vezes for preciso e continua idempotente.
insert into "ThemeTerms" ("VocabularyId","Code","Label","ParentTermId")
select v."Id", t.code, t.label, p."Id"
  from "ThemeVocabularies" v
  join "ThemeTerms" p on p."VocabularyId" = v."Id"
  join (values
    -- sob 'trabalho'
    ('trabalho', 'assedio_no_trabalho',        'Assédio no trabalho'),
    ('trabalho', 'conflito_no_trabalho',       'Conflito no trabalho'),
    ('trabalho', 'sobrecarga_jornada',         'Sobrecarga e jornada'),
    ('trabalho', 'inseguranca_no_emprego',     'Insegurança no emprego'),
    ('trabalho', 'retorno_ao_trabalho',        'Retorno ao trabalho'),
    ('trabalho', 'limites_trabalho_vida',      'Limites entre trabalho e vida'),

    -- sob 'imigracao'
    ('imigracao','adaptacao_cultural',         'Adaptação cultural'),
    ('imigracao','saudade_desenraizamento',    'Saudade e desenraizamento'),
    ('imigracao','barreira_linguistica',       'Barreira linguística'),
    ('imigracao','familia_a_distancia',        'Família à distância'),
    ('imigracao','status_migratorio',          'Incerteza de status migratório'),
    ('imigracao','revalidacao_recomeco',       'Revalidação e recomeço profissional')
  ) as t(parent_code, code, label) on t.parent_code = p."Code"
 where v."Version" = 'mh-1'
on conflict ("VocabularyId","Code") do nothing;


