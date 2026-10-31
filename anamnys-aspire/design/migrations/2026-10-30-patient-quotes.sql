-- F-15: patient quotes (design/specs/2026-10-30-patient-quotes-design.md, section 2).
-- A quote is only what the provider marks on a note: PatientQuotes now keeps who recorded it and, since a removal keeps
-- the row (the audit trail points at it), who removed it and when. The removal columns go together. The index on NoteId
-- already exists. The table has no PatientId: it reaches the patient through the note, so the F-06 trigger does not
-- apply; a note of a discarded record does not exist any more. DiscardService already deletes quotes.
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "PatientQuotes" ADD COLUMN IF NOT EXISTS "AddedById" uuid;
ALTER TABLE "PatientQuotes" ADD COLUMN IF NOT EXISTS "RemovedAt" timestamp with time zone;
ALTER TABLE "PatientQuotes" ADD COLUMN IF NOT EXISTS "RemovedById" uuid;
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'PatientQuotes_Removed_ck') THEN
    ALTER TABLE "PatientQuotes"
      ADD CONSTRAINT "PatientQuotes_Removed_ck" CHECK ((("RemovedAt" IS NULL) = ("RemovedById" IS NULL)));
  END IF;
END $$;

-- This file owns the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%quote_removed%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text]))));
  END IF;
END $$;

COMMIT;
