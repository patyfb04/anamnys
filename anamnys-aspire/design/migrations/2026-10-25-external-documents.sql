-- F-07: external documents (design/specs/2026-10-25-external-documents-design.md, section 2).
-- Gives ExternalDocuments a kind, an optional document date and description, the scan bookkeeping
-- (when, how many attempts) and a soft-delete date; the object key becomes nullable (infected and deleted
-- documents keep their row but not their object); lets the audit trail record the four
-- external_document_* actions, which belong to no note or clinical document.
-- Idempotent; all or nothing.
BEGIN;

-- Rows that predate the kind (none are created by the application) are mapped to "outro" once, when
-- the column is first added.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                  WHERE table_schema = 'public' AND table_name = 'ExternalDocuments' AND column_name = 'Kind') THEN
    ALTER TABLE "ExternalDocuments" ADD COLUMN "Kind" text NOT NULL DEFAULT 'outro';
    ALTER TABLE "ExternalDocuments" ALTER COLUMN "Kind" DROP DEFAULT;
  END IF;
END $$;

ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "DocumentDate" date;
ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "Description" text;
ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "ScannedAt" timestamp with time zone;
ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "ScanAttempts" integer NOT NULL DEFAULT 0;
ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp with time zone;

-- The unique constraint stays: it only applies to non-null keys.
ALTER TABLE "ExternalDocuments" ALTER COLUMN "ObjectKey" DROP NOT NULL;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExternalDocuments_Kind_ck') THEN
    ALTER TABLE "ExternalDocuments"
      ADD CONSTRAINT "ExternalDocuments_Kind_ck" CHECK (("Kind" = ANY (ARRAY['laudo'::text, 'receita'::text, 'exame'::text, 'encaminhamento'::text, 'relatorio_externo'::text, 'outro'::text])));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExternalDocuments_Description_ck') THEN
    ALTER TABLE "ExternalDocuments"
      ADD CONSTRAINT "ExternalDocuments_Description_ck" CHECK (("Description" IS NULL) OR (char_length("Description") <= 200));
  END IF;
  -- Only an infected or a deleted document may have no object.
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExternalDocuments_ObjectKey_ck') THEN
    ALTER TABLE "ExternalDocuments"
      ADD CONSTRAINT "ExternalDocuments_ObjectKey_ck" CHECK (("ObjectKey" IS NOT NULL) OR ("ScanStatus" = 'infected') OR ("DeletedAt" IS NOT NULL));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExternalDocuments_Patient_fk') THEN
    ALTER TABLE "ExternalDocuments"
      ADD CONSTRAINT "ExternalDocuments_Patient_fk" FOREIGN KEY ("PatientId","ProviderId") REFERENCES "Patients"("Id","ProviderId") ON DELETE RESTRICT;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS "ExternalDocuments_Provider_Uploaded_idx" ON "ExternalDocuments" ("ProviderId","UploadedAt" DESC);

-- External document actions are audited with the provider as actor and the document id and kind in
-- NewValue; they belong to no note or clinical document.
-- Skipped once a later migration (2026-10-26-document-facts.sql) owns a longer action list.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%fact_confirmed%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text]))));
  END IF;
END $$;

COMMIT;
