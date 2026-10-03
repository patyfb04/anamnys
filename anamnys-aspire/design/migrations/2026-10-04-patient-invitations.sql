-- Adds patient-portal invitations (design/specs/2026-10-03-portal-invitation-design.md §3).
-- Run after 2026-10-03-patient-accounts.sql. Idempotent, all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-04-patient-invitations.sql

BEGIN;

CREATE TABLE IF NOT EXISTS "PatientInvitations" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"Email" text NOT NULL,
	"TokenHash" bytea NOT NULL CONSTRAINT "PatientInvitations_TokenHash_key" UNIQUE,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ExpiresAt" timestamp with time zone NOT NULL,
	"AcceptedAt" timestamp with time zone,
	"AcceptedAccountId" uuid,
	"RevokedAt" timestamp with time zone,
	CONSTRAINT "PatientInvitations_Closed_ck" CHECK (("AcceptedAt" IS NULL) OR ("RevokedAt" IS NULL)),
	CONSTRAINT "PatientInvitations_Accepted_ck" CHECK (("AcceptedAccountId" IS NULL) OR ("AcceptedAt" IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS "PatientInvitations_OnePending_key" ON "PatientInvitations" ("PatientId")
	WHERE "AcceptedAt" IS NULL AND "RevokedAt" IS NULL;

DO $$
BEGIN
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'PatientInvitations_PatientId_fkey') THEN
		ALTER TABLE "PatientInvitations" ADD CONSTRAINT "PatientInvitations_PatientId_fkey"
			FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
	END IF;
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'PatientInvitations_AcceptedAccountId_fkey') THEN
		ALTER TABLE "PatientInvitations" ADD CONSTRAINT "PatientInvitations_AcceptedAccountId_fkey"
			FOREIGN KEY ("AcceptedAccountId") REFERENCES "PatientAccounts"("Id") ON DELETE SET NULL;
	END IF;
END $$;

COMMIT;
