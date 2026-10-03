-- Splits the patient-portal login out of "Patients" into "PatientAccounts"
-- (design/specs/2026-10-02-patient-accounts-design.md §3). Run after
-- 2026-10-01-patient-records.sql and 2026-10-02-patient-contact-email.sql. Idempotent, and
-- all-or-nothing: any failure rolls the whole script back.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-03-patient-accounts.sql

BEGIN;

CREATE TABLE IF NOT EXISTS "PatientAccounts" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ExternalSubject" uuid NOT NULL CONSTRAINT "PatientAccounts_ExternalSubject_key" UNIQUE,
	"Email" text NOT NULL,
	"FirstName" text NOT NULL,
	"LastName" text NOT NULL,
	"Phone" text,
	"DateOfBirth" date,
	"LastLoginAt" timestamp with time zone,
	"TermsAcceptedAt" timestamp with time zone,
	"DisabledAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "PatientAccounts_Email_key" ON "PatientAccounts" (lower("Email"));

ALTER TABLE "Patients" ADD COLUMN IF NOT EXISTS "AccountId" uuid;

DO $$
BEGIN
	-- Only a database still on the old shape has logins on "Patients" to move.
	IF EXISTS (SELECT 1 FROM information_schema.columns
	           WHERE table_schema = 'public' AND table_name = 'Patients' AND column_name = 'ExternalSubject') THEN

		-- A provider-less row is a self-registration and becomes an account only. Deleting
		-- its "Patients" row is safe only if nothing clinical points at it.
		IF EXISTS (
			SELECT 1 FROM "Patients" p
			WHERE p."ProviderId" IS NULL AND (
				   EXISTS (SELECT 1 FROM "Notes" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "Appointments" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "AppointmentSeries" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "Sessions" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "ClinicalDocuments" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "ExternalDocuments" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "Dossiers" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "DisclosureAuthorizations" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "DisposalRecords" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "ScaleApplications" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "IntakeResponses" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "RecordingConsents" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "MedicationEntries" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "PatientDiagnoses" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "TreatmentPlans" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "FollowUpItems" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "BookingHolds" x WHERE x."PatientId" = p."Id")
				OR EXISTS (SELECT 1 FROM "InsurerAuthorizations" x WHERE x."PatientId" = p."Id"))
		) THEN
			RAISE EXCEPTION 'A self-registered patient row (no ProviderId) has clinical records; resolve it before migrating.';
		END IF;

		-- The account reuses the row id, so live patient sessions (LocalId = that id) stay valid.
		INSERT INTO "PatientAccounts" ("Id", "ExternalSubject", "Email", "FirstName", "LastName", "Phone",
		                               "DateOfBirth", "LastLoginAt", "TermsAcceptedAt", "DisabledAt", "CreatedAt", "UpdatedAt")
		SELECT "Id", "ExternalSubject", "Email", "FirstName", "LastName", "Phone",
		       "DateOfBirth", "LastLoginAt", "TermsAcceptedAt", "DisabledAt", "CreatedAt", "UpdatedAt"
		FROM "Patients"
		WHERE "ExternalSubject" IS NOT NULL
		ON CONFLICT DO NOTHING;

		UPDATE "Patients" SET "AccountId" = "Id"
		WHERE "ExternalSubject" IS NOT NULL AND "ProviderId" IS NOT NULL;

		DELETE FROM "Patients" WHERE "ProviderId" IS NULL;

		ALTER TABLE "Patients" DROP CONSTRAINT IF EXISTS "Patients_Login_ck";
		ALTER TABLE "Patients"
			DROP COLUMN "Email",
			DROP COLUMN "ExternalSubject",
			DROP COLUMN "LastLoginAt",
			DROP COLUMN "TermsAcceptedAt",
			DROP COLUMN "DisabledAt";
	END IF;
END $$;

ALTER TABLE "Patients" ALTER COLUMN "ProviderId" SET NOT NULL;

DO $$
BEGIN
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'Patients_AccountId_fkey') THEN
		ALTER TABLE "Patients" ADD CONSTRAINT "Patients_AccountId_fkey"
			FOREIGN KEY ("AccountId") REFERENCES "PatientAccounts"("Id") ON DELETE SET NULL;
	END IF;
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'Patients_Account_Provider_key') THEN
		ALTER TABLE "Patients" ADD CONSTRAINT "Patients_Account_Provider_key" UNIQUE ("AccountId", "ProviderId");
	END IF;
END $$;

COMMIT;
