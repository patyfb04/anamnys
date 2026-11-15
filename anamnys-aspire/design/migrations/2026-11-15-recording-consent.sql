-- F-23 + F-24: recording consent (design/specs/2026-11-15-recording-consent-design.md, section 2).
-- RecordingConsents and ConsentEvents existed as schema only (no code ever wrote a row). This gives the consent its
-- provider (filled from the patient for any existing row, then NOT NULL, with the patient-and-provider foreign key), the
-- rendered term and its hash, the signer, the minor's assent note, the signing and revocation token hashes (unique), the
-- failed date-of-birth count (0..5), the revocation and expiry details, the signed PDF's object key and the timestamps;
-- allows at most one consent per patient in SENT or SIGNED; gives ConsentEvents an actor kind; and adds the audit
-- actions consent_sent, consent_signed, consent_revoked, consent_expired, consent_assent_recorded,
-- dictation_confirmed and consent_document_downloaded. TreatmentPlanId stays (null: the consent is per patient while in care). Idempotent; all or nothing.
BEGIN;

ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "ProviderId" uuid;
UPDATE "RecordingConsents" c SET "ProviderId" = p."ProviderId"
  FROM "Patients" p
 WHERE p."Id" = c."PatientId" AND c."ProviderId" IS NULL;
ALTER TABLE "RecordingConsents" ALTER COLUMN "ProviderId" SET NOT NULL;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "ProviderNote" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "TermText" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "TermSha256" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "SignerRole" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "SignerName" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "GuardianRelationship" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "AssentNote" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "SignTokenHash" bytea;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "SignTokenExpiresAt" timestamp with time zone;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "FailedBirthDateCount" integer DEFAULT 0 NOT NULL;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "RevokeTokenHash" bytea;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "RevokedBy" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "RevokeReason" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "ExpiredReason" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "DocumentObjectKey" text;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL;
ALTER TABLE "RecordingConsents" ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL;

ALTER TABLE "RecordingConsents" DROP CONSTRAINT IF EXISTS "RecordingConsents_ProviderId_fkey";
ALTER TABLE "RecordingConsents" ADD CONSTRAINT "RecordingConsents_ProviderId_fkey"
    FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "RecordingConsents" DROP CONSTRAINT IF EXISTS "RecordingConsents_Patient_fk";
ALTER TABLE "RecordingConsents" ADD CONSTRAINT "RecordingConsents_Patient_fk"
    FOREIGN KEY ("PatientId", "ProviderId") REFERENCES "Patients"("Id", "ProviderId") ON DELETE CASCADE;
ALTER TABLE "RecordingConsents" DROP CONSTRAINT IF EXISTS "RecordingConsents_SignerRole_ck";
ALTER TABLE "RecordingConsents" ADD CONSTRAINT "RecordingConsents_SignerRole_ck" CHECK ((("SignerRole" IS NULL) OR ("SignerRole" = ANY (ARRAY['patient'::text, 'guardian'::text]))));
ALTER TABLE "RecordingConsents" DROP CONSTRAINT IF EXISTS "RecordingConsents_RevokedBy_ck";
ALTER TABLE "RecordingConsents" ADD CONSTRAINT "RecordingConsents_RevokedBy_ck" CHECK ((("RevokedBy" IS NULL) OR ("RevokedBy" = ANY (ARRAY['patient'::text, 'provider'::text]))));
ALTER TABLE "RecordingConsents" DROP CONSTRAINT IF EXISTS "RecordingConsents_ExpiredReason_ck";
ALTER TABLE "RecordingConsents" ADD CONSTRAINT "RecordingConsents_ExpiredReason_ck" CHECK ((("ExpiredReason" IS NULL) OR ("ExpiredReason" = ANY (ARRAY['link'::text, 'discharge'::text, 'adulthood'::text, 'new_version'::text]))));
ALTER TABLE "RecordingConsents" DROP CONSTRAINT IF EXISTS "RecordingConsents_FailedBirthDateCount_ck";
ALTER TABLE "RecordingConsents" ADD CONSTRAINT "RecordingConsents_FailedBirthDateCount_ck" CHECK ((("FailedBirthDateCount" >= 0) AND ("FailedBirthDateCount" <= 5)));
ALTER TABLE "RecordingConsents" DROP CONSTRAINT IF EXISTS "RecordingConsents_Lengths_ck";
ALTER TABLE "RecordingConsents" ADD CONSTRAINT "RecordingConsents_Lengths_ck" CHECK (((char_length("ProviderNote") <= 1000) AND (char_length("SignerName") <= 200) AND (char_length("GuardianRelationship") <= 100) AND (char_length("AssentNote") <= 500) AND (char_length("RevokeReason") <= 500)));
CREATE UNIQUE INDEX IF NOT EXISTS "RecordingConsents_Patient_active_key" ON "RecordingConsents" ("PatientId") WHERE ("State" = ANY (ARRAY['SENT'::text, 'SIGNED'::text]));
CREATE UNIQUE INDEX IF NOT EXISTS "RecordingConsents_SignTokenHash_key" ON "RecordingConsents" ("SignTokenHash") WHERE ("SignTokenHash" IS NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS "RecordingConsents_RevokeTokenHash_key" ON "RecordingConsents" ("RevokeTokenHash") WHERE ("RevokeTokenHash" IS NOT NULL);
CREATE INDEX IF NOT EXISTS "RecordingConsents_Provider_Patient_Created_idx" ON "RecordingConsents" ("ProviderId", "PatientId", "CreatedAt" DESC);

-- Who moved the consent: the provider, the signer through a link, or the system (expiry). Reason holds codes only.
ALTER TABLE "ConsentEvents" ADD COLUMN IF NOT EXISTS "ActorKind" text DEFAULT 'system' NOT NULL;
ALTER TABLE "ConsentEvents" DROP CONSTRAINT IF EXISTS "ConsentEvents_ActorKind_ck";
ALTER TABLE "ConsentEvents" ADD CONSTRAINT "ConsentEvents_ActorKind_ck" CHECK (("ActorKind" = ANY (ARRAY['provider'::text, 'signer'::text, 'system'::text])));
ALTER TABLE "ConsentEvents" DROP CONSTRAINT IF EXISTS "ConsentEvents_Reason_ck";
ALTER TABLE "ConsentEvents" ADD CONSTRAINT "ConsentEvents_Reason_ck" CHECK ((("Reason" IS NULL) OR ("Reason" ~ '^[a-z_]{1,40}$'::text)));

-- This file owns the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%consent_document_downloaded%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text, 'disclosure_authorized'::text, 'share_link_created'::text, 'share_link_revoked'::text, 'disclosure_term_opened'::text, 'integrity_report_downloaded'::text, 'integrity_check_failed'::text, 'dossier_requested'::text, 'dossier_downloaded'::text, 'dossier_receipt_downloaded'::text, 'availability_changed'::text, 'block_created'::text, 'block_deleted'::text, 'appointment_outside_availability'::text, 'series_created'::text, 'series_split'::text, 'series_ended'::text, 'intake_started'::text, 'intake_signed'::text, 'intake_transfer_confirmed'::text, 'intake_transfer_ignored'::text, 'intake_template_adapted'::text, 'intake_discarded'::text, 'intake_answers_unlocked'::text, 'consent_sent'::text, 'consent_signed'::text, 'consent_revoked'::text, 'consent_expired'::text, 'consent_assent_recorded'::text, 'dictation_confirmed'::text, 'consent_document_downloaded'::text]))));
  END IF;
END $$;

COMMIT;
