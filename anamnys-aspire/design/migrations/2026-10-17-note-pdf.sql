-- Note PDF export (design/specs/2026-10-17-note-pdf-export-design.md §3). Run after
-- 2026-10-13-note-review.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-17-note-pdf.sql

BEGIN;

ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "PdfObjectKey" text;
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "PdfSha256" text;
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "ExportedAt" timestamp with time zone;
-- Seed rows marked Exported never had a file.
UPDATE "Notes" SET "Status" = 'Signed' WHERE "Status" = 'Exported' AND "PdfObjectKey" IS NULL;
ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_Exported_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_Exported_ck" CHECK (
    (("Status" = 'Exported') = ("PdfObjectKey" IS NOT NULL))
    AND (("PdfObjectKey" IS NULL) = ("PdfSha256" IS NULL))
    AND (("PdfObjectKey" IS NULL) = ("ExportedAt" IS NULL)));

COMMIT;
