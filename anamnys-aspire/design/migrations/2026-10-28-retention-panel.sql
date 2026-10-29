-- F-06: retention and compliance panel (design/specs/2026-10-28-retention-panel-design.md, section 2).
-- Gives each patient record its retention clock (date of the last record, the deadline that follows from the
-- guard years, the date it was discarded), the provider the age at which an unsigned note is listed, the
-- disposal term kept after a discard, a dedupe key so the daily job never repeats a bell notification, and
-- lets the audit trail record a discard, which belongs to no note or clinical document.
-- New rows for a discarded record are refused by trigger (Patients_not_discarded).
-- The clock itself is filled by the server (Retention/RetentionClock), at start and daily.
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "Patients" ADD COLUMN IF NOT EXISTS "LastRecordAt" date;
ALTER TABLE "Patients" ADD COLUMN IF NOT EXISTS "RetentionUntil" date;
ALTER TABLE "Patients" ADD COLUMN IF NOT EXISTS "DiscardedAt" timestamp with time zone;
CREATE INDEX IF NOT EXISTS "Patients_Provider_RetentionUntil_idx" ON "Patients" ("ProviderId", "RetentionUntil");

ALTER TABLE "Providers" ADD COLUMN IF NOT EXISTS "UnsignedNoteDays" integer NOT NULL DEFAULT 7;
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'Providers_UnsignedNoteDays_ck') THEN
    ALTER TABLE "Providers" ADD CONSTRAINT "Providers_UnsignedNoteDays_ck" CHECK (("UnsignedNoteDays" >= 1));
  END IF;
END $$;

-- One notification per item: the key names the item (retention:{patientId}:{retentionUntil}, unsigned:{noteId}).
ALTER TABLE "Notifications" ADD COLUMN IF NOT EXISTS "DedupeKey" text;
CREATE UNIQUE INDEX IF NOT EXISTS "Notifications_Provider_DedupeKey_key" ON "Notifications" ("ProviderId", "DedupeKey") WHERE "DedupeKey" IS NOT NULL;

-- The term outlives the clinical content: the patient row is kept (RESTRICT), with ids and counts only.
CREATE TABLE IF NOT EXISTS "DisposalTerms" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"PatientId" uuid NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ObjectKey" text NOT NULL,
	"PeriodFrom" date NOT NULL,
	"PeriodTo" date NOT NULL,
	"GuardYears" integer NOT NULL,
	"Basis" text NOT NULL,
	"Counts" jsonb DEFAULT '{}'::jsonb NOT NULL,
	"RetentionUntil" date NOT NULL,
	CONSTRAINT "DisposalTerms_PatientId_key" UNIQUE("PatientId"),
	CONSTRAINT "DisposalTerms_Period_ck" CHECK (("PeriodFrom" <= "PeriodTo")),
	CONSTRAINT "DisposalTerms_GuardYears_ck" CHECK (("GuardYears" >= 5)),
	CONSTRAINT "DisposalTerms_Counts_ck" CHECK ((jsonb_typeof("Counts") = 'object'::text))
);
-- The deadline that ended, as the clock had it at the discard (what the term PDF states). A table created by an
-- earlier run of this file gets the column, back-filled from the last record plus the guard years.
ALTER TABLE "DisposalTerms" ADD COLUMN IF NOT EXISTS "RetentionUntil" date;
UPDATE "DisposalTerms" SET "RetentionUntil" = ("PeriodTo" + make_interval(years => "GuardYears"))::date WHERE "RetentionUntil" IS NULL;
ALTER TABLE "DisposalTerms" ALTER COLUMN "RetentionUntil" SET NOT NULL;
CREATE INDEX IF NOT EXISTS "DisposalTerms_Provider_Created_idx" ON "DisposalTerms" ("ProviderId", "CreatedAt" DESC);
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'DisposalTerms_Patient_fk') THEN
    ALTER TABLE "DisposalTerms"
      ADD CONSTRAINT "DisposalTerms_Patient_fk" FOREIGN KEY ("PatientId","ProviderId") REFERENCES "Patients"("Id","ProviderId") ON DELETE RESTRICT;
  END IF;
END $$;

-- A discard is audited with the provider as actor; it belongs to no note or clinical document. This file owns
-- the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%patient_discarded%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text]))));
  END IF;
END $$;

-- A discarded record takes no new rows (design/specs/2026-10-28-retention-panel-design.md section 3). FOR KEY SHARE
-- waits for an open discard (it holds the patient row FOR UPDATE) and then reads the committed row, so an insert that
-- races the discard fails once the discard commits. SQLSTATE ANDIS is mapped by the server (Retention/DiscardedPatient).
CREATE OR REPLACE FUNCTION "Patients_not_discarded"() RETURNS trigger LANGUAGE plpgsql AS $fn$
DECLARE
  discarded timestamp with time zone;
BEGIN
  IF NEW."PatientId" IS NULL THEN
    RETURN NEW;
  END IF;
  SELECT p."DiscardedAt" INTO discarded FROM "Patients" p WHERE p."Id" = NEW."PatientId" FOR KEY SHARE;
  IF discarded IS NOT NULL THEN
    RAISE EXCEPTION 'patient record % was discarded', NEW."PatientId" USING ERRCODE = 'ANDIS';
  END IF;
  RETURN NEW;
END
$fn$;
DO $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['Appointments', 'AppointmentSeries', 'BookingHolds', 'ClinicalDocuments', 'DisclosureAuthorizations',
                           'Dossiers', 'ExternalDocuments', 'FollowUpItems', 'InsurerAuthorizations', 'IntakeResponses',
                           'MedicationEntries', 'Notes', 'PatientDiagnoses', 'PatientInvitations', 'RecordingConsents',
                           'ScaleApplications', 'Sessions', 'TreatmentPlans'] LOOP
    EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I', t || '_patient_not_discarded', t);
    EXECUTE format('CREATE TRIGGER %I BEFORE INSERT ON %I FOR EACH ROW EXECUTE FUNCTION "Patients_not_discarded"()',
                   t || '_patient_not_discarded', t);
  END LOOP;
END $$;

COMMIT;
