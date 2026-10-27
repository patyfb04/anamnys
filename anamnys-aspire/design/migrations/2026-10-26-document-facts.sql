-- F-08: document reading and declared facts (design/specs/2026-10-26-document-facts-design.md, section 2).
-- Gives ExternalDocuments the read bookkeeping (status, attempts, when, page count, truncation), adds
-- the per-page text (ExternalDocumentPages), gives ExtractedFacts an owner, a review status, the quoted
-- sentence and its dates, lets ExcerptRefs point at a page of a document, and lets the audit trail
-- record the fact review actions, which belong to no note or clinical document.
-- Idempotent; all or nothing.
BEGIN;

-- Documents already scanned clean when the column is first added are queued for reading once.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                  WHERE table_schema = 'public' AND table_name = 'ExternalDocuments' AND column_name = 'ReadStatus') THEN
    ALTER TABLE "ExternalDocuments" ADD COLUMN "ReadStatus" text;
    UPDATE "ExternalDocuments" SET "ReadStatus" = 'pending' WHERE "ScanStatus" = 'clean' AND "DeletedAt" IS NULL;
  END IF;
END $$;

ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "ReadAttempts" integer NOT NULL DEFAULT 0;
ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "ReadAt" timestamp with time zone;
ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "PageCount" integer;
ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "PagesTruncated" boolean NOT NULL DEFAULT false;
-- Fact extractions started since the last read: the startup sweep stops re-queuing a document whose
-- extraction keeps bringing the process down.
ALTER TABLE "ExternalDocuments" ADD COLUMN IF NOT EXISTS "ExtractAttempts" integer NOT NULL DEFAULT 0;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExternalDocuments_ReadStatus_ck') THEN
    ALTER TABLE "ExternalDocuments"
      ADD CONSTRAINT "ExternalDocuments_ReadStatus_ck" CHECK (("ReadStatus" IS NULL) OR ("ReadStatus" = ANY (ARRAY['pending'::text, 'reading'::text, 'done'::text, 'failed'::text])));
  END IF;
  -- Only a document scanned clean is read.
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExternalDocuments_ReadClean_ck') THEN
    ALTER TABLE "ExternalDocuments"
      ADD CONSTRAINT "ExternalDocuments_ReadClean_ck" CHECK (("ReadStatus" IS NULL) OR ("ScanStatus" = 'clean'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExternalDocuments_PageCount_ck') THEN
    ALTER TABLE "ExternalDocuments"
      ADD CONSTRAINT "ExternalDocuments_PageCount_ck" CHECK (("PageCount" IS NULL) OR ("PageCount" >= 0));
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS "ExternalDocumentPages" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ExternalDocumentId" uuid NOT NULL,
	"ProviderId" uuid NOT NULL,
	"PageNumber" integer NOT NULL,
	"Text" text NOT NULL,
	"FromOcr" boolean DEFAULT false NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ExternalDocumentPages_Document_Page_key" UNIQUE ("ExternalDocumentId","PageNumber"),
	CONSTRAINT "ExternalDocumentPages_PageNumber_ck" CHECK (("PageNumber" >= 1))
);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExternalDocumentPages_ExternalDocumentId_fkey') THEN
    ALTER TABLE "ExternalDocumentPages"
      ADD CONSTRAINT "ExternalDocumentPages_ExternalDocumentId_fkey" FOREIGN KEY ("ExternalDocumentId") REFERENCES "ExternalDocuments"("Id") ON DELETE CASCADE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExternalDocumentPages_ProviderId_fkey') THEN
    ALTER TABLE "ExternalDocumentPages"
      ADD CONSTRAINT "ExternalDocumentPages_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
  END IF;
END $$;

-- Facts that predate this model carry no provider or status and nothing creates them: they are removed
-- (the foreign key from MedicationEntries is ON DELETE SET NULL), once, when the owner is first added.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                  WHERE table_schema = 'public' AND table_name = 'ExtractedFacts' AND column_name = 'ProviderId') THEN
    DELETE FROM "ExtractedFacts";
    ALTER TABLE "ExtractedFacts" ADD COLUMN "ProviderId" uuid NOT NULL;
  END IF;
END $$;

ALTER TABLE "ExtractedFacts" ADD COLUMN IF NOT EXISTS "Status" text NOT NULL DEFAULT 'pending';
ALTER TABLE "ExtractedFacts" ADD COLUMN IF NOT EXISTS "Quote" text;
ALTER TABLE "ExtractedFacts" ADD COLUMN IF NOT EXISTS "CreatedAt" timestamp with time zone NOT NULL DEFAULT now();
ALTER TABLE "ExtractedFacts" ADD COLUMN IF NOT EXISTS "DiscardedAt" timestamp with time zone;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExtractedFacts_ProviderId_fkey') THEN
    ALTER TABLE "ExtractedFacts"
      ADD CONSTRAINT "ExtractedFacts_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExtractedFacts_Status_ck') THEN
    ALTER TABLE "ExtractedFacts"
      ADD CONSTRAINT "ExtractedFacts_Status_ck" CHECK (("Status" = ANY (ARRAY['pending'::text, 'confirmed'::text, 'corrected'::text, 'discarded'::text])));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExtractedFacts_Corrected_ck') THEN
    ALTER TABLE "ExtractedFacts"
      ADD CONSTRAINT "ExtractedFacts_Corrected_ck" CHECK (("Status" <> 'corrected') OR ("CorrectedValue" IS NOT NULL));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExtractedFacts_Reviewed_ck') THEN
    ALTER TABLE "ExtractedFacts"
      ADD CONSTRAINT "ExtractedFacts_Reviewed_ck" CHECK (("Status" <> ALL (ARRAY['confirmed'::text, 'corrected'::text])) OR (("ConfirmedBy" IS NOT NULL) AND ("ConfirmedAt" IS NOT NULL)));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ExtractedFacts_Discarded_ck') THEN
    ALTER TABLE "ExtractedFacts"
      ADD CONSTRAINT "ExtractedFacts_Discarded_ck" CHECK (("Status" <> 'discarded') OR ("DiscardedAt" IS NOT NULL));
  END IF;
END $$;

-- A document reference points into one page's text.
ALTER TABLE "ExcerptRefs" ADD COLUMN IF NOT EXISTS "PageNumber" integer;

-- Document references without a page predate this model and nothing creates them. The check is
-- recreated on every run (an early draft let a null page through: NULL >= 1 is not false).
DELETE FROM "ExcerptRefs" WHERE "SourceType" = 'document' AND ("PageNumber" IS NULL OR "PageNumber" < 1);
ALTER TABLE "ExcerptRefs" DROP CONSTRAINT IF EXISTS "ExcerptRefs_DocumentPage_ck";
ALTER TABLE "ExcerptRefs"
  ADD CONSTRAINT "ExcerptRefs_DocumentPage_ck" CHECK ((("SourceType" <> 'document') AND ("PageNumber" IS NULL)) OR (("SourceType" = 'document') AND ("PageNumber" IS NOT NULL) AND ("PageNumber" >= 1)));

-- Fact review actions are audited with the provider as actor and the fact kind and id in NewValue
-- (facts_deleted: the document id and how many facts went with it);
-- they belong to no note or clinical document.
-- Skipped once a later migration (2026-10-27-medication-timeline.sql) owns a longer action list.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%medication_from_fact%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
  ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text]))));
  END IF;
END $$;

COMMIT;
