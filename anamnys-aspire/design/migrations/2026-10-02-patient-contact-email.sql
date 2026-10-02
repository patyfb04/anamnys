-- Brings a database up to date with anamnys-db-script.sql for the contact-email change
-- (design/specs/2026-10-01-patient-records-design.md §8). Run after
-- 2026-10-01-patient-records.sql. Idempotent.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb' < design/migrations/2026-10-02-patient-contact-email.sql

ALTER TABLE "Patients" ADD COLUMN IF NOT EXISTS "ContactEmail" text;

-- A row without a portal login keeps its email as a contact address only.
UPDATE "Patients"
SET "ContactEmail" = COALESCE("ContactEmail", "Email"), "Email" = NULL
WHERE "ExternalSubject" IS NULL AND "Email" IS NOT NULL;

-- A provider's record that is also a bound login gets the login email as its contact.
UPDATE "Patients"
SET "ContactEmail" = "Email"
WHERE "ProviderId" IS NOT NULL AND "ContactEmail" IS NULL AND "Email" IS NOT NULL;

DO $$
BEGIN
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'Patients_Login_ck') THEN
		ALTER TABLE "Patients" ADD CONSTRAINT "Patients_Login_ck"
			CHECK ((("Email" IS NULL) = ("ExternalSubject" IS NULL)));
	END IF;
END $$;
