-- F-05 delivery 2: co-authors of the relatório multiprofissional
-- (design/specs/2026-10-20-psychological-documents-delivery-2-design.md, section 4).
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "Team" jsonb DEFAULT '[]'::jsonb NOT NULL;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ClinicalDocuments_Team_array') THEN
    ALTER TABLE "ClinicalDocuments"
      ADD CONSTRAINT "ClinicalDocuments_Team_array" CHECK (jsonb_typeof("Team") = 'array');
  END IF;
END $$;

COMMIT;
