-- F-45: document integrity hash chain (design/specs/2026-11-03-integrity-chain-design.md, section 2).
-- One append-only chain of seals per provider and the daily external anchors. DocumentHashes (schema only, no code ever
-- used it) is dropped. Both tables are made append-only by triggers, so even the application cannot rewrite them: the
-- only change a seal accepts is DiscardedAt going from null to a value. The Notifications kind list has no database
-- constraint, so the new kind integrity_problem needs no change here. Idempotent; all or nothing.
BEGIN;

DROP TABLE IF EXISTS "DocumentHashes";

CREATE TABLE IF NOT EXISTS "IntegritySeals" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "ProviderId" uuid NOT NULL REFERENCES "Providers"("Id") ON DELETE RESTRICT,
    "Sequence" bigint NOT NULL,
    "SubjectType" text NOT NULL,
    "SubjectId" uuid NOT NULL,
    "ContentHash" text NOT NULL,
    "PreviousHash" text,
    "Hash" text NOT NULL,
    "SealedAt" timestamp with time zone NOT NULL,
    "Retroactive" boolean DEFAULT false NOT NULL,
    "DiscardedAt" timestamp with time zone,
    CONSTRAINT "IntegritySeals_Provider_Sequence_key" UNIQUE ("ProviderId", "Sequence"),
    CONSTRAINT "IntegritySeals_Subject_key" UNIQUE ("SubjectType", "SubjectId"),
    CONSTRAINT "IntegritySeals_SubjectType_ck" CHECK (("SubjectType" = ANY (ARRAY['note'::text, 'document'::text]))),
    CONSTRAINT "IntegritySeals_Previous_ck" CHECK ((("Sequence" = 1) = ("PreviousHash" IS NULL)))
);

CREATE TABLE IF NOT EXISTS "IntegrityAnchors" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "ProviderId" uuid NOT NULL REFERENCES "Providers"("Id") ON DELETE RESTRICT,
    "Sequence" bigint NOT NULL,
    "HeadHash" text NOT NULL,
    "TsaUrl" text NOT NULL,
    "Token" bytea NOT NULL,
    "AnchoredAt" timestamp with time zone NOT NULL,
    CONSTRAINT "IntegrityAnchors_Provider_Sequence_key" UNIQUE ("ProviderId", "Sequence")
);

CREATE OR REPLACE FUNCTION "IntegritySeals_append_only"() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN
  IF TG_OP = 'DELETE' THEN
    RAISE EXCEPTION 'integrity seals are append-only';
  END IF;
  IF OLD."DiscardedAt" IS NULL AND NEW."DiscardedAt" IS NOT NULL
     AND (NEW."Id", NEW."ProviderId", NEW."Sequence", NEW."SubjectType", NEW."SubjectId", NEW."ContentHash",
          NEW."PreviousHash", NEW."Hash", NEW."SealedAt", NEW."Retroactive")
         IS NOT DISTINCT FROM
         (OLD."Id", OLD."ProviderId", OLD."Sequence", OLD."SubjectType", OLD."SubjectId", OLD."ContentHash",
          OLD."PreviousHash", OLD."Hash", OLD."SealedAt", OLD."Retroactive") THEN
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'integrity seals are append-only';
END
$fn$;
DROP TRIGGER IF EXISTS "IntegritySeals_append_only" ON "IntegritySeals";
CREATE TRIGGER "IntegritySeals_append_only" BEFORE UPDATE OR DELETE ON "IntegritySeals"
  FOR EACH ROW EXECUTE FUNCTION "IntegritySeals_append_only"();

CREATE OR REPLACE FUNCTION "IntegrityAnchors_append_only"() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN
  RAISE EXCEPTION 'integrity anchors are append-only';
END
$fn$;
DROP TRIGGER IF EXISTS "IntegrityAnchors_append_only" ON "IntegrityAnchors";
CREATE TRIGGER "IntegrityAnchors_append_only" BEFORE UPDATE OR DELETE ON "IntegrityAnchors"
  FOR EACH ROW EXECUTE FUNCTION "IntegrityAnchors_append_only"();
CREATE OR REPLACE FUNCTION "Integrity_no_truncate"() RETURNS trigger LANGUAGE plpgsql AS $fn$
BEGIN
  RAISE EXCEPTION 'integrity tables are append-only';
END
$fn$;
DROP TRIGGER IF EXISTS "IntegritySeals_no_truncate" ON "IntegritySeals";
CREATE TRIGGER "IntegritySeals_no_truncate" BEFORE TRUNCATE ON "IntegritySeals"
  FOR EACH STATEMENT EXECUTE FUNCTION "Integrity_no_truncate"();
DROP TRIGGER IF EXISTS "IntegrityAnchors_no_truncate" ON "IntegrityAnchors";
CREATE TRIGGER "IntegrityAnchors_no_truncate" BEFORE TRUNCATE ON "IntegrityAnchors"
  FOR EACH STATEMENT EXECUTE FUNCTION "Integrity_no_truncate"();
-- This file owns the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%integrity_check_failed%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text, 'disclosure_authorized'::text, 'share_link_created'::text, 'share_link_revoked'::text, 'disclosure_term_opened'::text, 'integrity_report_downloaded'::text, 'integrity_check_failed'::text]))));
  END IF;
END $$;

COMMIT;
