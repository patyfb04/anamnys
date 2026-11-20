-- F-27: third-party pseudonymisation (design/specs/2026-11-17-pseudonymization-design.md, section 5).
-- PatientPseudonymSecrets holds one random secret per patient, protected with ASP.NET Core Data Protection (purpose
-- Anamnys.Pseudonymization.v1). PseudonymTokens holds, per patient and kind (person | organization | school), the
-- HMAC-SHA256 fingerprint of a normalised name and its number; the name itself is never stored, and nothing maps a
-- number back to a name. PseudonymizationKeys holds the dedicated Data Protection key ring of those secrets (durable
-- with the database; the auth ring in Redis is not). Notes gain TranscriptPseudonymized (true once RawTranscript went through the step; false for
-- legacy rows until the backfill converts them) and PseudonymizationFailedAt; the failure reason adds
-- pseudonymization_failed. PseudonymizationBackfills keeps one backfill's progress per provider. Adds the audit actions
-- transcript_pseudonymized, pseudonymization_failed and pseudonymization_backfill_started. A discarded record takes no
-- new secret or token. Idempotent; all or nothing.
BEGIN;

CREATE TABLE IF NOT EXISTS "PatientPseudonymSecrets" (
	"PatientId" uuid PRIMARY KEY,
	"Protected" bytea NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "PatientPseudonymSecrets_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS "PseudonymTokens" (
	"PatientId" uuid NOT NULL,
	"Kind" text NOT NULL,
	"Fingerprint" bytea NOT NULL,
	"Number" integer NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "PseudonymTokens_pkey" PRIMARY KEY ("PatientId", "Kind", "Fingerprint"),
	CONSTRAINT "PseudonymTokens_Number_key" UNIQUE ("PatientId", "Kind", "Number"),
	CONSTRAINT "PseudonymTokens_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE,
	CONSTRAINT "PseudonymTokens_Kind_ck" CHECK (("Kind" = ANY (ARRAY['person'::text, 'organization'::text, 'school'::text]))),
	CONSTRAINT "PseudonymTokens_Number_ck" CHECK (("Number" >= 1)),
	CONSTRAINT "PseudonymTokens_Fingerprint_ck" CHECK ((octet_length("Fingerprint") = 32))
);

-- The Data Protection key ring of the pseudonymisation secrets only (the auth ring stays in Redis). One XML element per
-- key or revocation, as ASP.NET Core Data Protection writes it; outside Development each key is encrypted with the
-- DataProtection:CertificateThumbprint certificate. Lives with the data it protects, so a database backup carries both.
CREATE TABLE IF NOT EXISTS "PseudonymizationKeys" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"FriendlyName" text NOT NULL,
	"Xml" text NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "PseudonymizationKeys_FriendlyName_key" UNIQUE ("FriendlyName")
);

CREATE TABLE IF NOT EXISTS "PseudonymizationBackfills" (
	"ProviderId" uuid PRIMARY KEY,
	"StartedAt" timestamp with time zone NOT NULL,
	"FinishedAt" timestamp with time zone,
	"Total" integer DEFAULT 0 NOT NULL,
	"Done" integer DEFAULT 0 NOT NULL,
	"Failed" integer DEFAULT 0 NOT NULL,
	CONSTRAINT "PseudonymizationBackfills_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE,
	CONSTRAINT "PseudonymizationBackfills_Counts_ck" CHECK ((("Total" >= 0) AND ("Done" >= 0) AND ("Failed" >= 0)))
);

ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "TranscriptPseudonymized" boolean DEFAULT false NOT NULL;
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "PseudonymizationFailedAt" timestamp with time zone;
ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_FailureReason_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_FailureReason_ck" CHECK ((("Status" = 'Failed'::text) = ("FailureReason" IS NOT NULL)) AND (("FailureReason" IS NULL) OR ("FailureReason" = ANY (ARRAY['no_speech'::text, 'transcription_error'::text, 'server_restart'::text, 'pseudonymization_failed'::text]))));

-- A discarded record takes no new rows (the function comes from 2026-10-28-retention-panel.sql).
DROP TRIGGER IF EXISTS "PatientPseudonymSecrets_patient_not_discarded" ON "PatientPseudonymSecrets";
CREATE TRIGGER "PatientPseudonymSecrets_patient_not_discarded" BEFORE INSERT ON "PatientPseudonymSecrets" FOR EACH ROW EXECUTE FUNCTION "Patients_not_discarded"();
DROP TRIGGER IF EXISTS "PseudonymTokens_patient_not_discarded" ON "PseudonymTokens";
CREATE TRIGGER "PseudonymTokens_patient_not_discarded" BEFORE INSERT ON "PseudonymTokens" FOR EACH ROW EXECUTE FUNCTION "Patients_not_discarded"();

-- This file owns the full, current action list: a re-run keeps it as it is. The guard looks for the last action.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%pseudonymization_backfill_started%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text, 'disclosure_authorized'::text, 'share_link_created'::text, 'share_link_revoked'::text, 'disclosure_term_opened'::text, 'integrity_report_downloaded'::text, 'integrity_check_failed'::text, 'dossier_requested'::text, 'dossier_downloaded'::text, 'dossier_receipt_downloaded'::text, 'availability_changed'::text, 'block_created'::text, 'block_deleted'::text, 'appointment_outside_availability'::text, 'series_created'::text, 'series_split'::text, 'series_ended'::text, 'intake_started'::text, 'intake_signed'::text, 'intake_transfer_confirmed'::text, 'intake_transfer_ignored'::text, 'intake_template_adapted'::text, 'intake_discarded'::text, 'intake_answers_unlocked'::text, 'consent_sent'::text, 'consent_signed'::text, 'consent_revoked'::text, 'consent_expired'::text, 'consent_assent_recorded'::text, 'dictation_confirmed'::text, 'consent_document_downloaded'::text,
        'audio_destroyed'::text, 'recording_source_switched'::text,
        'transcript_pseudonymized'::text, 'pseudonymization_failed'::text, 'pseudonymization_backfill_started'::text]))));
  END IF;
END $$;

COMMIT;
