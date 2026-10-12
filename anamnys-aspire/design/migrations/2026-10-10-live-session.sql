-- Live session transcription: failed notes and idempotent finalize
-- (design/specs/2026-10-10-live-session-transcription-design.md §3). Run after
-- 2026-10-07-appointment-notifications.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-10-live-session.sql

BEGIN;

ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "FailureReason" text;
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "RecordingId" uuid;
CREATE UNIQUE INDEX IF NOT EXISTS "Notes_RecordingId_key" ON "Notes" ("RecordingId");

ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_Status_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_Status_ck" CHECK (("Status" = ANY (ARRAY['Draft'::text, 'Processing'::text, 'ReadyForReview'::text, 'Signed'::text, 'Exported'::text, 'Failed'::text])));

ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_FailureReason_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_FailureReason_ck" CHECK ((("Status" = 'Failed'::text) = ("FailureReason" IS NOT NULL)) AND (("FailureReason" IS NULL) OR ("FailureReason" = ANY (ARRAY['no_speech'::text, 'transcription_error'::text, 'server_restart'::text]))));

COMMIT;
