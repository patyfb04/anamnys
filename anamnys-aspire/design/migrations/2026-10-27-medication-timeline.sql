-- F-09: medication timeline (design/specs/2026-10-27-medication-timeline-design.md, section 2).
-- Groups the records of one treatment (a dose change shares its TreatmentId), dates their creation, and marks
-- the records that came from a document: the fact a record came from is cleared when the document is deleted
-- (ON DELETE SET NULL), and FromDocument is what tells "documento excluido" from a record typed by the provider.
-- Idempotent; all or nothing.
BEGIN;

-- Existing records are each their own treatment, once, when the column is first added. The default keeps
-- inserts that do not name a treatment valid (each such record is its own treatment).
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                  WHERE table_schema = 'public' AND table_name = 'MedicationEntries' AND column_name = 'TreatmentId') THEN
    ALTER TABLE "MedicationEntries" ADD COLUMN "TreatmentId" uuid;
    UPDATE "MedicationEntries" SET "TreatmentId" = "Id";
    ALTER TABLE "MedicationEntries" ALTER COLUMN "TreatmentId" SET NOT NULL;
    ALTER TABLE "MedicationEntries" ALTER COLUMN "TreatmentId" SET DEFAULT gen_random_uuid();
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                  WHERE table_schema = 'public' AND table_name = 'MedicationEntries' AND column_name = 'FromDocument') THEN
    ALTER TABLE "MedicationEntries" ADD COLUMN "FromDocument" boolean NOT NULL DEFAULT false;
    UPDATE "MedicationEntries" SET "FromDocument" = true WHERE "SourceFactId" IS NOT NULL;
  END IF;
END $$;

ALTER TABLE "MedicationEntries" ADD COLUMN IF NOT EXISTS "CreatedAt" timestamp with time zone NOT NULL DEFAULT now();

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'MedicationEntries_SourceFactId_fkey') THEN
    ALTER TABLE "MedicationEntries"
      ADD CONSTRAINT "MedicationEntries_SourceFactId_fkey" FOREIGN KEY ("SourceFactId") REFERENCES "ExtractedFacts"("Id") ON DELETE SET NULL;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS "MedicationEntries_Treatment_idx" ON "MedicationEntries" ("TreatmentId");
-- One record per fact: a fact turned into a record cannot be turned into another.
CREATE UNIQUE INDEX IF NOT EXISTS "MedicationEntries_SourceFactId_key" ON "MedicationEntries" ("SourceFactId") WHERE "SourceFactId" IS NOT NULL;

-- A medication recorded from a fact is audited with the provider as actor; it belongs to no note or clinical
-- document. This file owns the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%medication_from_fact%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text]))));
  END IF;
END $$;

COMMIT;
