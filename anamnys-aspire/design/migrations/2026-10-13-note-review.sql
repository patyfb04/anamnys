-- Note review and signing (design/specs/2026-10-13-note-review-and-signing-design.md §3). Run after
-- 2026-10-12-note-structuring.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-13-note-review.sql

BEGIN;

ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "SignedByCrp" text;
-- Notes signed before this change (seed data) have no recorded CRP.
UPDATE "Notes" SET "SignedByCrp" = '—' WHERE "SignedAt" IS NOT NULL AND "SignedByCrp" IS NULL;
ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_SignedByCrp_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_SignedByCrp_ck" CHECK ((("SignedAt" IS NULL) = ("SignedByCrp" IS NULL)));

ALTER TABLE "NoteSections" ADD COLUMN IF NOT EXISTS "Revision" integer DEFAULT 0 NOT NULL;

COMMIT;
