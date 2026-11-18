-- F-26: transient audio, destruction recorded (design/specs/2026-11-16-transient-audio-tab-capture-design.md, section 2.2).
-- AudioDestructionLogs existed as schema only (no code ever wrote a row). "SessionId" now holds the recording id, so its
-- foreign key to Sessions goes; any old row is filled from its session (provider, patient, mode session, source in_person)
-- and a row whose session is gone is dropped. The row gains the provider, the patient (with the patient-and-provider
-- foreign key, cascade), the note (on delete set null), the recording mode (session | dictation) and the sources used
-- in order of first use (in_person | online_capture | online_speaker; {in_person} for a dictation); the outcome adds
-- abandoned; an index on the note; a discarded record takes no new row. Adds the audit action audio_destroyed.
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "AudioDestructionLogs" DROP CONSTRAINT IF EXISTS "AudioDestructionLogs_SessionId_fkey";
ALTER TABLE "AudioDestructionLogs" ADD COLUMN IF NOT EXISTS "ProviderId" uuid;
ALTER TABLE "AudioDestructionLogs" ADD COLUMN IF NOT EXISTS "PatientId" uuid;
ALTER TABLE "AudioDestructionLogs" ADD COLUMN IF NOT EXISTS "NoteId" uuid;
ALTER TABLE "AudioDestructionLogs" ADD COLUMN IF NOT EXISTS "RecordingMode" text;
ALTER TABLE "AudioDestructionLogs" ADD COLUMN IF NOT EXISTS "Sources" text[];
UPDATE "AudioDestructionLogs" l
   SET "ProviderId" = s."ProviderId", "PatientId" = s."PatientId",
       "RecordingMode" = coalesce(l."RecordingMode", 'session'), "Sources" = coalesce(l."Sources", ARRAY['in_person'])
  FROM "Sessions" s
 WHERE s."Id" = l."SessionId" AND l."ProviderId" IS NULL;
DELETE FROM "AudioDestructionLogs" WHERE "ProviderId" IS NULL OR "PatientId" IS NULL;
ALTER TABLE "AudioDestructionLogs" ALTER COLUMN "ProviderId" SET NOT NULL;
ALTER TABLE "AudioDestructionLogs" ALTER COLUMN "PatientId" SET NOT NULL;
ALTER TABLE "AudioDestructionLogs" ALTER COLUMN "RecordingMode" SET NOT NULL;
ALTER TABLE "AudioDestructionLogs" ALTER COLUMN "Sources" SET NOT NULL;

ALTER TABLE "AudioDestructionLogs" DROP CONSTRAINT IF EXISTS "AudioDestructionLogs_ProviderId_fkey";
ALTER TABLE "AudioDestructionLogs" ADD CONSTRAINT "AudioDestructionLogs_ProviderId_fkey"
    FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "AudioDestructionLogs" DROP CONSTRAINT IF EXISTS "AudioDestructionLogs_Patient_fk";
ALTER TABLE "AudioDestructionLogs" ADD CONSTRAINT "AudioDestructionLogs_Patient_fk"
    FOREIGN KEY ("PatientId", "ProviderId") REFERENCES "Patients"("Id", "ProviderId") ON DELETE CASCADE;
ALTER TABLE "AudioDestructionLogs" DROP CONSTRAINT IF EXISTS "AudioDestructionLogs_NoteId_fkey";
ALTER TABLE "AudioDestructionLogs" ADD CONSTRAINT "AudioDestructionLogs_NoteId_fkey"
    FOREIGN KEY ("NoteId") REFERENCES "Notes"("Id") ON DELETE SET NULL;
ALTER TABLE "AudioDestructionLogs" DROP CONSTRAINT IF EXISTS "AudioDestructionLogs_Outcome_ck";
ALTER TABLE "AudioDestructionLogs" ADD CONSTRAINT "AudioDestructionLogs_Outcome_ck" CHECK (("Outcome" = ANY (ARRAY['transcribed'::text, 'failed'::text, 'abandoned'::text])));
ALTER TABLE "AudioDestructionLogs" DROP CONSTRAINT IF EXISTS "AudioDestructionLogs_RecordingMode_ck";
ALTER TABLE "AudioDestructionLogs" ADD CONSTRAINT "AudioDestructionLogs_RecordingMode_ck" CHECK (("RecordingMode" = ANY (ARRAY['session'::text, 'dictation'::text])));
ALTER TABLE "AudioDestructionLogs" DROP CONSTRAINT IF EXISTS "AudioDestructionLogs_Sources_ck";
ALTER TABLE "AudioDestructionLogs" ADD CONSTRAINT "AudioDestructionLogs_Sources_ck" CHECK (((cardinality("Sources") >= 1) AND ("Sources" <@ ARRAY['in_person'::text, 'online_capture'::text, 'online_speaker'::text]) AND (("RecordingMode" = 'session'::text) OR ("Sources" = ARRAY['in_person'::text]))));
CREATE INDEX IF NOT EXISTS "AudioDestructionLogs_NoteId_idx" ON "AudioDestructionLogs" ("NoteId", "DestroyedAt" DESC) WHERE ("NoteId" IS NOT NULL);
CREATE INDEX IF NOT EXISTS "AudioDestructionLogs_Provider_Patient_idx" ON "AudioDestructionLogs" ("ProviderId", "PatientId");

-- A discarded record takes no new row (the function comes from 2026-10-28-retention-panel.sql).
DROP TRIGGER IF EXISTS "AudioDestructionLogs_patient_not_discarded" ON "AudioDestructionLogs";
CREATE TRIGGER "AudioDestructionLogs_patient_not_discarded" BEFORE INSERT ON "AudioDestructionLogs" FOR EACH ROW EXECUTE FUNCTION "Patients_not_discarded"();

-- This file owns the full, current action list: a re-run keeps it as it is. The guard looks for the last action.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%recording_source_switched%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text, 'disclosure_authorized'::text, 'share_link_created'::text, 'share_link_revoked'::text, 'disclosure_term_opened'::text, 'integrity_report_downloaded'::text, 'integrity_check_failed'::text, 'dossier_requested'::text, 'dossier_downloaded'::text, 'dossier_receipt_downloaded'::text, 'availability_changed'::text, 'block_created'::text, 'block_deleted'::text, 'appointment_outside_availability'::text, 'series_created'::text, 'series_split'::text, 'series_ended'::text, 'intake_started'::text, 'intake_signed'::text, 'intake_transfer_confirmed'::text, 'intake_transfer_ignored'::text, 'intake_template_adapted'::text, 'intake_discarded'::text, 'intake_answers_unlocked'::text, 'consent_sent'::text, 'consent_signed'::text, 'consent_revoked'::text, 'consent_expired'::text, 'consent_assent_recorded'::text, 'dictation_confirmed'::text, 'consent_document_downloaded'::text,
        'audio_destroyed'::text, 'recording_source_switched'::text]))));
  END IF;
END $$;

COMMIT;
