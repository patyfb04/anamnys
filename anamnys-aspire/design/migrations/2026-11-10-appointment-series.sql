-- F-29 delivery 3: recurring appointments as series (design/specs/2026-11-09-appointment-series-design.md, section 2).
-- AppointmentSeries existed as schema only; this adds the pattern (Interval, Weekday, StartTime, Timezone), the end by
-- count (OccurrenceCount), the generation mark (GeneratedUntil), the split link (PreviousSeriesId) and CreatedAt, with
-- their checks; the foreign key to Providers when missing (the one to Patients already exists as
-- "AppointmentSeries_Patient_fk"); Appointments.SeriesIndex and Appointments.Detached with the unique occurrence index
-- and the (SeriesId, StartsAt) index; and the audit actions series_created, series_split and series_ended.
-- Series rows written before this file (none are expected) take their weekday from StartsOn and 00:00 as their time.
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "AppointmentSeries" ADD COLUMN IF NOT EXISTS "Interval" integer DEFAULT 1 NOT NULL;
ALTER TABLE "AppointmentSeries" ADD COLUMN IF NOT EXISTS "Weekday" integer;
ALTER TABLE "AppointmentSeries" ADD COLUMN IF NOT EXISTS "StartTime" time without time zone;
ALTER TABLE "AppointmentSeries" ADD COLUMN IF NOT EXISTS "Timezone" text DEFAULT 'America/Sao_Paulo' NOT NULL;
ALTER TABLE "AppointmentSeries" ADD COLUMN IF NOT EXISTS "OccurrenceCount" integer;
ALTER TABLE "AppointmentSeries" ADD COLUMN IF NOT EXISTS "GeneratedUntil" date;
ALTER TABLE "AppointmentSeries" ADD COLUMN IF NOT EXISTS "PreviousSeriesId" uuid;
ALTER TABLE "AppointmentSeries" ADD COLUMN IF NOT EXISTS "CreatedAt" timestamp with time zone DEFAULT now() NOT NULL;
UPDATE "AppointmentSeries" SET "Weekday" = extract(dow FROM "StartsOn")::integer WHERE "Weekday" IS NULL;
UPDATE "AppointmentSeries" SET "StartTime" = time '00:00' WHERE "StartTime" IS NULL;
UPDATE "AppointmentSeries" SET "EndsOn" = "StartsOn" WHERE "EndsOn" < "StartsOn";
ALTER TABLE "AppointmentSeries" ALTER COLUMN "Weekday" SET NOT NULL;
ALTER TABLE "AppointmentSeries" ALTER COLUMN "StartTime" SET NOT NULL;

ALTER TABLE "AppointmentSeries" DROP CONSTRAINT IF EXISTS "AppointmentSeries_Interval_ck";
ALTER TABLE "AppointmentSeries" ADD CONSTRAINT "AppointmentSeries_Interval_ck" CHECK ((("Interval" >= 1) AND ("Interval" <= 2)));
ALTER TABLE "AppointmentSeries" DROP CONSTRAINT IF EXISTS "AppointmentSeries_Weekday_ck";
ALTER TABLE "AppointmentSeries" ADD CONSTRAINT "AppointmentSeries_Weekday_ck" CHECK ((("Weekday" >= 0) AND ("Weekday" <= 6)));
ALTER TABLE "AppointmentSeries" DROP CONSTRAINT IF EXISTS "AppointmentSeries_OccurrenceCount_ck";
ALTER TABLE "AppointmentSeries" ADD CONSTRAINT "AppointmentSeries_OccurrenceCount_ck"
    CHECK ((("OccurrenceCount" IS NULL) OR (("OccurrenceCount" >= 1) AND ("OccurrenceCount" <= 520))));
ALTER TABLE "AppointmentSeries" DROP CONSTRAINT IF EXISTS "AppointmentSeries_End_ck";
ALTER TABLE "AppointmentSeries" ADD CONSTRAINT "AppointmentSeries_End_ck" CHECK ((("EndsOn" IS NULL) OR ("OccurrenceCount" IS NULL)));
ALTER TABLE "AppointmentSeries" DROP CONSTRAINT IF EXISTS "AppointmentSeries_Range_ck";
ALTER TABLE "AppointmentSeries" ADD CONSTRAINT "AppointmentSeries_Range_ck" CHECK ((("EndsOn" IS NULL) OR ("EndsOn" >= "StartsOn")));

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AppointmentSeries_ProviderId_fkey') THEN
    ALTER TABLE "AppointmentSeries" ADD CONSTRAINT "AppointmentSeries_ProviderId_fkey"
      FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE CASCADE;
  END IF;
END $$;
ALTER TABLE "AppointmentSeries" DROP CONSTRAINT IF EXISTS "AppointmentSeries_PreviousSeriesId_fkey";
ALTER TABLE "AppointmentSeries" ADD CONSTRAINT "AppointmentSeries_PreviousSeriesId_fkey"
    FOREIGN KEY ("PreviousSeriesId") REFERENCES "AppointmentSeries"("Id") ON DELETE SET NULL;
CREATE INDEX IF NOT EXISTS "AppointmentSeries_PatientId_idx" ON "AppointmentSeries" ("PatientId");

ALTER TABLE "Appointments" ADD COLUMN IF NOT EXISTS "SeriesIndex" integer;
ALTER TABLE "Appointments" ADD COLUMN IF NOT EXISTS "Detached" boolean DEFAULT false NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "Appointments_Series_Index_key" ON "Appointments" ("SeriesId", "SeriesIndex") WHERE ("SeriesId" IS NOT NULL);
CREATE INDEX IF NOT EXISTS "Appointments_Series_StartsAt_idx" ON "Appointments" ("SeriesId", "StartsAt");

-- This file owns the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%series_ended%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text, 'disclosure_authorized'::text, 'share_link_created'::text, 'share_link_revoked'::text, 'disclosure_term_opened'::text, 'integrity_report_downloaded'::text, 'integrity_check_failed'::text, 'dossier_requested'::text, 'dossier_downloaded'::text, 'dossier_receipt_downloaded'::text, 'availability_changed'::text, 'block_created'::text, 'block_deleted'::text, 'appointment_outside_availability'::text, 'series_created'::text, 'series_split'::text, 'series_ended'::text]))));
  END IF;
END $$;

COMMIT;
