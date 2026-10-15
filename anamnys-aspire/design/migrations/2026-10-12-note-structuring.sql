-- Note structuring state (design/specs/2026-10-12-note-structuring-design.md §3). Run after
-- 2026-10-10-live-session.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-12-note-structuring.sql

BEGIN;

ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "StructureStatus" text;
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "StructureFailure" text;

ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_StructureStatus_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_StructureStatus_ck" CHECK ((("StructureStatus" IS NULL) OR ("StructureStatus" = ANY (ARRAY['pending'::text, 'done'::text, 'failed'::text]))));

ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_StructureFailure_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_StructureFailure_ck" CHECK (((("StructureStatus" IS NOT DISTINCT FROM 'failed'::text) = ("StructureFailure" IS NOT NULL)) AND (("StructureFailure" IS NULL) OR ("StructureFailure" = ANY (ARRAY['model_error'::text, 'too_long'::text, 'server_restart'::text])))));

COMMIT;
