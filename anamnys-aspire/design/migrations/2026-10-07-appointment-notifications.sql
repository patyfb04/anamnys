-- Appointment confirmation, reminders outbox and auto-cancel policy
-- (design/specs/2026-10-07-appointment-notifications-design.md §3). Run after
-- 2026-10-04-patient-invitations.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-07-appointment-notifications.sql

BEGIN;

CREATE TABLE IF NOT EXISTS "AppointmentConfirmations" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"AppointmentId" uuid NOT NULL,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ExpiresAt" timestamp with time zone NOT NULL,
	"DeadlineAt" timestamp with time zone,
	"ConfirmedAt" timestamp with time zone,
	"ConfirmedBy" text,
	"ClosedAt" timestamp with time zone,
	CONSTRAINT "AppointmentConfirmations_ConfirmedBy_ck" CHECK ((("ConfirmedBy" IS NULL) = ("ConfirmedAt" IS NULL)) AND (("ConfirmedBy" IS NULL) OR ("ConfirmedBy" = ANY (ARRAY['patient'::text, 'provider'::text])))),
	CONSTRAINT "AppointmentConfirmations_Closed_ck" CHECK (("ConfirmedAt" IS NULL) OR ("ClosedAt" IS NULL))
);
CREATE INDEX IF NOT EXISTS "AppointmentConfirmations_AppointmentId_idx" ON "AppointmentConfirmations" ("AppointmentId");
CREATE INDEX IF NOT EXISTS "AppointmentConfirmations_Deadline_idx" ON "AppointmentConfirmations" ("DeadlineAt")
	WHERE "ConfirmedAt" IS NULL AND "ClosedAt" IS NULL AND "DeadlineAt" IS NOT NULL;

CREATE TABLE IF NOT EXISTS "AppointmentConfirmationTokens" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ConfirmationId" uuid NOT NULL,
	"TokenHash" bytea NOT NULL CONSTRAINT "AppointmentConfirmationTokens_TokenHash_key" UNIQUE,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE INDEX IF NOT EXISTS "AppointmentConfirmationTokens_ConfirmationId_idx" ON "AppointmentConfirmationTokens" ("ConfirmationId");

ALTER TABLE "BookingPolicies" ADD COLUMN IF NOT EXISTS "AutoCancelMode" text DEFAULT 'after_email' NOT NULL;
ALTER TABLE "BookingPolicies" ADD COLUMN IF NOT EXISTS "AutoCancelHours" integer DEFAULT 1 NOT NULL;
ALTER TABLE "Reminders" ADD COLUMN IF NOT EXISTS "Attempts" integer DEFAULT 0 NOT NULL;
ALTER TABLE "Reminders" ADD COLUMN IF NOT EXISTS "ConfirmationId" uuid;
CREATE INDEX IF NOT EXISTS "Notifications_Provider_Scheduled_idx" ON "Notifications" ("ProviderId", "ScheduledFor" DESC);

DO $$
BEGIN
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'BookingPolicies_AutoCancel_ck') THEN
		ALTER TABLE "BookingPolicies" ADD CONSTRAINT "BookingPolicies_AutoCancel_ck" CHECK (("AutoCancelMode" = ANY (ARRAY['off'::text, 'after_email'::text, 'before_session'::text])) AND ("AutoCancelHours" >= 1) AND ("AutoCancelHours" <= 168));
	END IF;
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AppointmentConfirmations_Appointment_fk') THEN
		ALTER TABLE "AppointmentConfirmations" ADD CONSTRAINT "AppointmentConfirmations_Appointment_fk" FOREIGN KEY ("AppointmentId") REFERENCES "Appointments"("Id") ON DELETE CASCADE;
	END IF;
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'AppointmentConfirmationTokens_Confirmation_fk') THEN
		ALTER TABLE "AppointmentConfirmationTokens" ADD CONSTRAINT "AppointmentConfirmationTokens_Confirmation_fk" FOREIGN KEY ("ConfirmationId") REFERENCES "AppointmentConfirmations"("Id") ON DELETE CASCADE;
	END IF;
	IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'Reminders_Confirmation_fk') THEN
		ALTER TABLE "Reminders" ADD CONSTRAINT "Reminders_Confirmation_fk" FOREIGN KEY ("ConfirmationId") REFERENCES "AppointmentConfirmations"("Id") ON DELETE SET NULL;
	END IF;
END $$;

COMMIT;
