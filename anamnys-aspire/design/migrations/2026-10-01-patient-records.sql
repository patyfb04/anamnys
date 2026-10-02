-- Brings a database bootstrapped before 2026-09-27 up to date with anamnys-db-script.sql.
-- DatabaseInitializer only runs the full script against an empty database, so existing
-- dev volumes (which the test fixture shares) need this applied by hand. Idempotent.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb' < design/migrations/2026-10-01-patient-records.sql

-- Patient list (design/specs/2026-09-27-patient-list-design.md §3)
CREATE INDEX IF NOT EXISTS "Notes_Patient_Created_idx" ON "Notes" ("PatientId","CreatedAt" DESC);

-- Patient records (design/specs/2026-10-01-patient-records-design.md §3)
ALTER TABLE "Patients" ADD COLUMN IF NOT EXISTS "ArchivedAt" timestamp with time zone;

CREATE TABLE IF NOT EXISTS "PatientDiagnoses" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"Description" text NOT NULL,
	"IcdCode" text,
	"RecordedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ResolvedOn" date
);
CREATE INDEX IF NOT EXISTS "PatientDiagnoses_PatientId_idx" ON "PatientDiagnoses" ("PatientId");

DO $$
BEGIN
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'PatientDiagnoses_PatientId_fkey') THEN
		ALTER TABLE "PatientDiagnoses" ADD CONSTRAINT "PatientDiagnoses_PatientId_fkey"
			FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
	END IF;
END $$;
