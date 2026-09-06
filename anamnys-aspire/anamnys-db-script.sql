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
	"RequiresConfirmation" boolean DEFAULT true NOT NULL,
	CONSTRAINT "BookingPolicies_Windows_ck" CHECK ((("MinLeadHours" >= 0) AND ("MaxHorizonDays" > 0) AND ("CancelLeadHours" >= 0) AND ("RescheduleLeadHours" >= 0) AND ("MaxOpenAppointments" > 0)))
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
	CONSTRAINT "ClinicalDocuments_Kind_ck" CHECK (("Kind" = ANY (ARRAY['declaracao'::text, 'atestado'::text, 'relatorio'::text, 'relatorio_multiprofissional'::text, 'laudo'::text, 'parecer'::text])))
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
	CONSTRAINT "Notes_Format_ck" CHECK (("Format" = ANY (ARRAY['DAP'::text, 'SOAP'::text, 'Biopsychosocial'::text, 'PtFunctional'::text]))),
	CONSTRAINT "Notes_InputMode_ck" CHECK (("InputMode" = ANY (ARRAY['VoiceBatch'::text, 'VoiceLive'::text, 'VoiceAppointment'::text, 'Text'::text]))),
	CONSTRAINT "Notes_Signed_ck" CHECK ((("Status" = ANY (ARRAY['Signed'::text, 'Exported'::text])) = ("SignedAt" IS NOT NULL))),
	CONSTRAINT "Notes_Status_ck" CHECK (("Status" = ANY (ARRAY['Draft'::text, 'Processing'::text, 'ReadyForReview'::text, 'Signed'::text, 'Exported'::text])))
);
CREATE TABLE "NoteSections" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"NoteId" uuid NOT NULL,
	"Key" text NOT NULL,
	"Content" text DEFAULT '' NOT NULL,
	"Position" integer DEFAULT 0 NOT NULL,
	"GeneratedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"EditedAt" timestamp with time zone,
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
CREATE TABLE "PatientAccounts" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL CONSTRAINT "PatientAccounts_PatientId_key" UNIQUE,
	"Email" text NOT NULL CONSTRAINT "PatientAccounts_Email_key" UNIQUE,
	"ExternalSubject" uuid CONSTRAINT "PatientAccounts_ExternalSubject_unique" UNIQUE,
	"Phone" text,
	"LastLoginAt" timestamp with time zone,
	"TermsAcceptedAt" timestamp with time zone,
	"DisabledAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE TABLE "PatientQuotes" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"NoteId" uuid NOT NULL,
	"TermId" uuid,
	"Text" text NOT NULL,
	"ExcerptRefId" uuid,
	"RecordedAt" timestamp with time zone NOT NULL
);
CREATE TABLE "Patients" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"FirstName" text NOT NULL,
	"LastName" text NOT NULL,
	"DateOfBirth" date,
	"PreferredLanguage" text,
	"LastVisit" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "Patients_Id_ProviderId_key" UNIQUE("Id","ProviderId")
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
	"Role" text NOT NULL,
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
	CONSTRAINT "ThemeTags_CreatedBy_ck" CHECK (("CreatedBy" = ANY (ARRAY['ai'::text, 'human'::text])))
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
CREATE INDEX "Notes_ProviderId_idx" ON "Notes" ("ProviderId");
CREATE INDEX "Notes_Status_idx" ON "Notes" ("Status");
CREATE INDEX "NoteSections_NoteId_idx" ON "NoteSections" ("NoteId");
CREATE INDEX "Notifications_due_idx" ON "Notifications" ("ScheduledFor");
CREATE INDEX "PatientQuotes_NoteId_idx" ON "PatientQuotes" ("NoteId");
CREATE INDEX "Patients_ProviderId_idx" ON "Patients" ("ProviderId");
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
CREATE INDEX "ThemeTerms_VocabularyId_idx" ON "ThemeTerms" ("VocabularyId");
CREATE INDEX "TissGuides_InsurerId_idx" ON "TissGuides" ("InsurerId");
CREATE INDEX "TissGuides_NoteId_idx" ON "TissGuides" ("NoteId");
CREATE INDEX "TreatmentPlans_PatientId_idx" ON "TreatmentPlans" ("PatientId");
CREATE INDEX "WebhookEvents_unprocessed_idx" ON "WebhookEvents" ("ReceivedAt");
ALTER TABLE "AccessLogs" ADD CONSTRAINT "AccessLogs_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "AccountExports" ADD CONSTRAINT "AccountExports_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
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
ALTER TABLE "PatientAccounts" ADD CONSTRAINT "PatientAccounts_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
ALTER TABLE "PatientQuotes" ADD CONSTRAINT "PatientQuotes_ExcerptRefId_fkey" FOREIGN KEY ("ExcerptRefId") REFERENCES "ExcerptRefs"("Id") ON DELETE SET NULL;
ALTER TABLE "PatientQuotes" ADD CONSTRAINT "PatientQuotes_NoteId_fkey" FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE CASCADE;
ALTER TABLE "PatientQuotes" ADD CONSTRAINT "PatientQuotes_TermId_fkey" FOREIGN KEY ("TermId") REFERENCES "ThemeTerms"("Id") ON DELETE SET NULL;
ALTER TABLE "Patients" ADD CONSTRAINT "Patients_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE RESTRICT;
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