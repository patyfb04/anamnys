-- F-60: full account export (design/specs/2026-10-23-account-export-design.md, sections 2, 3 and 5).
-- Gives AccountExports a lifecycle (status, failure reason, size, expiry), allows one export in
-- progress per provider, and lets the audit trail record export actions that belong to no note or
-- document.
-- Idempotent; all or nothing.
BEGIN;

-- Rows that predate the lifecycle are mapped once, when the column is first added: a completed row is
-- done, anything else never finished and is failed (a queued backfill would collide with the
-- one-in-progress index below).
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                  WHERE table_schema = 'public' AND table_name = 'AccountExports' AND column_name = 'Status') THEN
    ALTER TABLE "AccountExports" ADD COLUMN "Status" text NOT NULL DEFAULT 'queued';
    UPDATE "AccountExports" SET "Status" = 'done' WHERE "CompletedAt" IS NOT NULL;
    UPDATE "AccountExports" SET "Status" = 'failed' WHERE "CompletedAt" IS NULL;
    UPDATE "AccountExports" SET "ObjectKey" = NULL WHERE "Status" <> 'done';
  END IF;
END $$;

ALTER TABLE "AccountExports" ADD COLUMN IF NOT EXISTS "FailureReason" text;
ALTER TABLE "AccountExports" ADD COLUMN IF NOT EXISTS "SizeBytes" bigint;
ALTER TABLE "AccountExports" ADD COLUMN IF NOT EXISTS "ExpiredAt" timestamp with time zone;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AccountExports_Status_ck') THEN
    ALTER TABLE "AccountExports"
      ADD CONSTRAINT "AccountExports_Status_ck" CHECK (("Status" = ANY (ARRAY['queued'::text, 'running'::text, 'done'::text, 'failed'::text, 'expired'::text])));
  END IF;
  -- An expired export keeps the completion time it had; only done and expired rows are completed.
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AccountExports_Completed_ck') THEN
    ALTER TABLE "AccountExports"
      ADD CONSTRAINT "AccountExports_Completed_ck" CHECK ((("CompletedAt" IS NOT NULL) = ("Status" = ANY (ARRAY['done'::text, 'expired'::text]))));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AccountExports_ObjectKey_ck') THEN
    ALTER TABLE "AccountExports"
      ADD CONSTRAINT "AccountExports_ObjectKey_ck" CHECK (("ObjectKey" IS NULL) OR ("Status" = 'done'));
  END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS "AccountExports_Provider_active_key"
    ON "AccountExports" ("ProviderId") WHERE "Status" = ANY (ARRAY['queued'::text, 'running'::text]);

-- Export requests and downloads are audited with the provider as actor and no note or document.
-- Later migrations widen this list (2026-10-25-external-documents.sql owns the full, current one): when the
-- constraint already allows the external document actions, a re-run of this file leaves it alone.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%external_document_blocked%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text]))));
  END IF;
END $$;

COMMIT;
