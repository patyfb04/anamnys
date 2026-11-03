-- F-17 + F-18: protected sharing (design/specs/2026-11-01-protected-sharing-design.md, section 2).
-- DisclosureAuthorizations gains the owning provider, how the authorization was obtained, the optional attached term and
-- the provider's confirmation; one authorization per document, recipient and purpose. No code ever wrote this table, so
-- the new columns are NOT NULL with no backfill: a row that somehow exists stops the migration instead of inventing a
-- consent for it. ShareLinks gains the wrong-password count and the blocked time. ShareAccesses keeps CoarseGeo unused
-- and gains the 'blocked' outcome. The F-06 trigger on DisclosureAuthorizations already refuses a discarded record.
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "DisclosureAuthorizations" ADD COLUMN IF NOT EXISTS "ProviderId" uuid;
ALTER TABLE "DisclosureAuthorizations" ADD COLUMN IF NOT EXISTS "ObtainedVia" text;
ALTER TABLE "DisclosureAuthorizations" ADD COLUMN IF NOT EXISTS "TermObjectKey" text;
ALTER TABLE "DisclosureAuthorizations" ADD COLUMN IF NOT EXISTS "ConfirmedAt" timestamp with time zone;
ALTER TABLE "DisclosureAuthorizations" ALTER COLUMN "ProviderId" SET NOT NULL;
ALTER TABLE "DisclosureAuthorizations" ALTER COLUMN "ObtainedVia" SET NOT NULL;
ALTER TABLE "DisclosureAuthorizations" ALTER COLUMN "ConfirmedAt" SET NOT NULL;
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'DisclosureAuthorizations_ObtainedVia_ck') THEN
    ALTER TABLE "DisclosureAuthorizations"
      ADD CONSTRAINT "DisclosureAuthorizations_ObtainedVia_ck" CHECK (("ObtainedVia" = ANY (ARRAY['verbal_session'::text, 'signed_term'::text, 'in_writing'::text])));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'DisclosureAuthorizations_ProviderId_fkey') THEN
    ALTER TABLE "DisclosureAuthorizations"
      ADD CONSTRAINT "DisclosureAuthorizations_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE RESTRICT;
  END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS "DisclosureAuthorizations_Recipient_key"
  ON "DisclosureAuthorizations" ("DocumentId", lower("RecipientName"), lower("RecipientRole"), lower("Purpose"));

ALTER TABLE "ShareLinks" ADD COLUMN IF NOT EXISTS "WrongPasswordCount" integer DEFAULT 0 NOT NULL;
ALTER TABLE "ShareLinks" ADD COLUMN IF NOT EXISTS "BlockedAt" timestamp with time zone;
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ShareLinks_ViewsUsed_ck') THEN
    ALTER TABLE "ShareLinks"
      ADD CONSTRAINT "ShareLinks_ViewsUsed_ck" CHECK ((("ViewsUsed" >= 0) AND (("ViewCap" IS NULL) OR ("ViewsUsed" <= "ViewCap"))));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ShareLinks_WrongPasswordCount_ck') THEN
    ALTER TABLE "ShareLinks"
      ADD CONSTRAINT "ShareLinks_WrongPasswordCount_ck" CHECK ((("WrongPasswordCount" >= 0) AND ("WrongPasswordCount" <= 5)));
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'ShareAccesses_Outcome_ck' AND pg_get_constraintdef(oid) LIKE '%blocked%') THEN
    ALTER TABLE "ShareAccesses" DROP CONSTRAINT IF EXISTS "ShareAccesses_Outcome_ck";
    ALTER TABLE "ShareAccesses"
      ADD CONSTRAINT "ShareAccesses_Outcome_ck" CHECK (("Outcome" = ANY (ARRAY['opened'::text, 'bad_password'::text, 'expired'::text, 'revoked'::text, 'cap_reached'::text, 'blocked'::text])));
  END IF;
END $$;

-- This file owns the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%disclosure_term_opened%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text, 'disclosure_authorized'::text, 'share_link_created'::text, 'share_link_revoked'::text, 'disclosure_term_opened'::text]))));
  END IF;
END $$;

COMMIT;
