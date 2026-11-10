-- F-29 delivery 2: availability, national holidays and blocks (design/specs/2026-11-09-availability-design.md, section 2).
-- AvailabilityRules (weekly hours, one row per interval per weekday per version) and AvailabilityExceptions (blocks:
-- block, vacation, local holiday) existed as schema only; this adds ProviderHolidaySettings (works on national holidays,
-- holiday keys switched off), AvailabilityVersions (one header per version; rules reference it through VersionId),
-- the foreign keys to Providers when missing, the version index, the 200-character limit
-- on a block's reason and the audit actions availability_changed, block_created, block_deleted and
-- appointment_outside_availability. The existing "AvailabilityExceptions_Provider_Range_idx" (ProviderId, StartsAt,
-- EndsAt) already serves (ProviderId, StartsAt), so no second index is made for it. Idempotent; all or nothing.
BEGIN;

CREATE TABLE IF NOT EXISTS "ProviderHolidaySettings" (
    "ProviderId" uuid PRIMARY KEY,
    "WorksOnHolidays" boolean DEFAULT false NOT NULL,
    "DisabledHolidays" text[] DEFAULT '{}' NOT NULL
);
ALTER TABLE "ProviderHolidaySettings" DROP CONSTRAINT IF EXISTS "ProviderHolidaySettings_ProviderId_fkey";
ALTER TABLE "ProviderHolidaySettings" ADD CONSTRAINT "ProviderHolidaySettings_ProviderId_fkey"
    FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AvailabilityRules_ProviderId_fkey') THEN
    ALTER TABLE "AvailabilityRules" ADD CONSTRAINT "AvailabilityRules_ProviderId_fkey"
      FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AvailabilityExceptions_ProviderId_fkey') THEN
    ALTER TABLE "AvailabilityExceptions" ADD CONSTRAINT "AvailabilityExceptions_ProviderId_fkey"
      FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS "AvailabilityRules_Provider_EffectiveFrom_idx" ON "AvailabilityRules" ("ProviderId", "EffectiveFrom");

-- One header per version of the weekly hours: a version where every day is "Não atendo" is a header with no rules,
-- so it can be shown and cancelled. Rules belong to a version and keep its EffectiveFrom/EffectiveTo.
CREATE TABLE IF NOT EXISTS "AvailabilityVersions" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "ProviderId" uuid NOT NULL,
    "EffectiveFrom" date NOT NULL,
    "EffectiveTo" date,
    "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
ALTER TABLE "AvailabilityVersions" DROP CONSTRAINT IF EXISTS "AvailabilityVersions_ProviderId_fkey";
ALTER TABLE "AvailabilityVersions" ADD CONSTRAINT "AvailabilityVersions_ProviderId_fkey"
    FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
ALTER TABLE "AvailabilityVersions" DROP CONSTRAINT IF EXISTS "AvailabilityVersions_Range_ck";
ALTER TABLE "AvailabilityVersions" ADD CONSTRAINT "AvailabilityVersions_Range_ck"
    CHECK ((("EffectiveTo" IS NULL) OR ("EffectiveTo" >= "EffectiveFrom")));
ALTER TABLE "AvailabilityVersions" DROP CONSTRAINT IF EXISTS "AvailabilityVersions_Provider_EffectiveFrom_key";
ALTER TABLE "AvailabilityVersions" ADD CONSTRAINT "AvailabilityVersions_Provider_EffectiveFrom_key" UNIQUE ("ProviderId", "EffectiveFrom");

-- Rules written before this file (none are expected) get a header per provider and start date.
ALTER TABLE "AvailabilityRules" ADD COLUMN IF NOT EXISTS "VersionId" uuid;
INSERT INTO "AvailabilityVersions" ("ProviderId", "EffectiveFrom", "EffectiveTo")
SELECT DISTINCT ON ("ProviderId", "EffectiveFrom") "ProviderId", "EffectiveFrom", "EffectiveTo"
  FROM "AvailabilityRules"
 WHERE "VersionId" IS NULL
 ORDER BY "ProviderId", "EffectiveFrom", "EffectiveTo" NULLS FIRST
ON CONFLICT ("ProviderId", "EffectiveFrom") DO NOTHING;
UPDATE "AvailabilityRules" r
   SET "VersionId" = v."Id", "EffectiveTo" = v."EffectiveTo"
  FROM "AvailabilityVersions" v
 WHERE r."VersionId" IS NULL AND v."ProviderId" = r."ProviderId" AND v."EffectiveFrom" = r."EffectiveFrom";
ALTER TABLE "AvailabilityRules" ALTER COLUMN "VersionId" SET NOT NULL;
ALTER TABLE "AvailabilityRules" DROP CONSTRAINT IF EXISTS "AvailabilityRules_VersionId_fkey";
ALTER TABLE "AvailabilityRules" ADD CONSTRAINT "AvailabilityRules_VersionId_fkey"
    FOREIGN KEY ("VersionId") REFERENCES "AvailabilityVersions"("Id") ON DELETE CASCADE;
CREATE INDEX IF NOT EXISTS "AvailabilityRules_VersionId_idx" ON "AvailabilityRules" ("VersionId");

-- A reason longer than the limit (none is written before this delivery) is cut, so the check can be added.
UPDATE "AvailabilityExceptions" SET "Reason" = left("Reason", 200) WHERE char_length("Reason") > 200;
ALTER TABLE "AvailabilityExceptions" DROP CONSTRAINT IF EXISTS "AvailabilityExceptions_Reason_ck";
ALTER TABLE "AvailabilityExceptions" ADD CONSTRAINT "AvailabilityExceptions_Reason_ck"
    CHECK ((("Reason" IS NULL) OR (char_length("Reason") <= 200)));

-- This file owns the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%appointment_outside_availability%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text, 'disclosure_authorized'::text, 'share_link_created'::text, 'share_link_revoked'::text, 'disclosure_term_opened'::text, 'integrity_report_downloaded'::text, 'integrity_check_failed'::text, 'dossier_requested'::text, 'dossier_downloaded'::text, 'dossier_receipt_downloaded'::text, 'availability_changed'::text, 'block_created'::text, 'block_deleted'::text, 'appointment_outside_availability'::text]))));
  END IF;
END $$;

COMMIT;
