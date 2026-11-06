-- F-44: patient dossier (design/specs/2026-11-04-patient-dossier-design.md, section 2).
-- The Dossiers table (schema only until now, no code ever wrote it) becomes the request and delivery record of one
-- dossier: who asked for it and why, its state, the ZIP's object, size, SHA-256, manifest and RFC 3161 token. The row
-- outlives the ZIP (deleted after 7 days), so what was delivered can be proven later. A legacy row, if one exists, is
-- kept: it gets its patient's provider, the next number, reason 'other' and the state its file allows. The audit actions
-- dossier_requested, dossier_downloaded and dossier_receipt_downloaded are added; the Notifications kind list has no
-- database constraint, so the new kind dossier_ready needs no change here. Idempotent; all or nothing.
BEGIN;

ALTER TABLE "Dossiers"
    ADD COLUMN IF NOT EXISTS "ProviderId" uuid,
    ADD COLUMN IF NOT EXISTS "Number" integer,
    ADD COLUMN IF NOT EXISTS "Status" text DEFAULT 'queued' NOT NULL,
    ADD COLUMN IF NOT EXISTS "Reason" text,
    ADD COLUMN IF NOT EXISTS "ReasonNote" text,
    ADD COLUMN IF NOT EXISTS "PeriodFrom" date,
    ADD COLUMN IF NOT EXISTS "PeriodTo" date,
    ADD COLUMN IF NOT EXISTS "RequestedAt" timestamp with time zone,
    ADD COLUMN IF NOT EXISTS "ExpiresAt" timestamp with time zone,
    ADD COLUMN IF NOT EXISTS "ExpiredAt" timestamp with time zone,
    ADD COLUMN IF NOT EXISTS "SizeBytes" bigint,
    ADD COLUMN IF NOT EXISTS "ZipSha256" text,
    ADD COLUMN IF NOT EXISTS "AnchorToken" bytea,
    ADD COLUMN IF NOT EXISTS "AnchoredAt" timestamp with time zone,
    ADD COLUMN IF NOT EXISTS "FailureReason" text;

-- GeneratedAt is now the completion time: set by the worker, never by default.
ALTER TABLE "Dossiers" ALTER COLUMN "GeneratedAt" DROP DEFAULT;
ALTER TABLE "Dossiers" ALTER COLUMN "GeneratedAt" DROP NOT NULL;

WITH legacy AS (
    SELECT d."Id", p."ProviderId",
           row_number() OVER (PARTITION BY p."ProviderId" ORDER BY d."GeneratedAt", d."Id") AS n
      FROM "Dossiers" d
      JOIN "Patients" p ON p."Id" = d."PatientId"
     WHERE d."ProviderId" IS NULL
)
UPDATE "Dossiers" d
   SET "ProviderId" = l."ProviderId",
       "Number" = l.n,
       "Status" = CASE WHEN d."ObjectKey" IS NOT NULL THEN 'done' ELSE 'expired' END,
       "Reason" = 'other',
       "ReasonNote" = 'Registro anterior ao dossiê do paciente',
       "RequestedAt" = coalesce(d."GeneratedAt", now()),
       "GeneratedAt" = coalesce(d."GeneratedAt", now()),
       "ExpiresAt" = coalesce(d."GeneratedAt", now()) + interval '7 days',
       "ExpiredAt" = CASE WHEN d."ObjectKey" IS NULL THEN coalesce(d."GeneratedAt", now()) + interval '7 days' END
  FROM legacy l
 WHERE d."Id" = l."Id";

ALTER TABLE "Dossiers" ALTER COLUMN "ProviderId" SET NOT NULL;
ALTER TABLE "Dossiers" ALTER COLUMN "Number" SET NOT NULL;
ALTER TABLE "Dossiers" ALTER COLUMN "Reason" SET NOT NULL;
ALTER TABLE "Dossiers" ALTER COLUMN "RequestedAt" SET NOT NULL;

ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_ProviderId_fkey";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_ProviderId_fkey" FOREIGN KEY ("ProviderId") REFERENCES "Providers"("Id") ON DELETE RESTRICT;
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'Dossiers_PatientId_fkey') THEN
    ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE RESTRICT;
  END IF;
END $$;

ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_Provider_Number_key";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_Provider_Number_key" UNIQUE ("ProviderId", "Number");
ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_Number_ck";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_Number_ck" CHECK (("Number" >= 1));
ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_Status_ck";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_Status_ck"
    CHECK (("Status" = ANY (ARRAY['queued'::text, 'running'::text, 'done'::text, 'failed'::text, 'expired'::text])));
ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_Reason_ck";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_Reason_ck"
    CHECK (("Reason" = ANY (ARRAY['crp'::text, 'court'::text, 'patient_request'::text, 'other'::text])));
ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_ReasonNote_ck";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_ReasonNote_ck"
    CHECK (((("Reason" = 'other'::text) = ("ReasonNote" IS NOT NULL)) AND (("ReasonNote" IS NULL) OR ((char_length("ReasonNote") >= 1) AND (char_length("ReasonNote") <= 200)))));
ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_Period_ck";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_Period_ck"
    CHECK ((("PeriodFrom" IS NULL) OR ("PeriodTo" IS NULL) OR ("PeriodFrom" <= "PeriodTo")));
ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_Generated_ck";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_Generated_ck"
    CHECK ((("GeneratedAt" IS NOT NULL) = ("Status" = ANY (ARRAY['done'::text, 'expired'::text]))));
ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_ObjectKey_ck";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_ObjectKey_ck" CHECK ((("ObjectKey" IS NULL) OR ("Status" = 'done'::text)));
ALTER TABLE "Dossiers" DROP CONSTRAINT IF EXISTS "Dossiers_FailureReason_ck";
ALTER TABLE "Dossiers" ADD CONSTRAINT "Dossiers_FailureReason_ck"
    CHECK ((("FailureReason" IS NULL) OR ("FailureReason" = ANY (ARRAY['dossier_error'::text, 'interrupted'::text]))));

-- Newest first per patient; one queued or running dossier per patient (the request's 409 is race-safe on it).
DROP INDEX IF EXISTS "Dossiers_PatientId_idx";
CREATE INDEX "Dossiers_PatientId_idx" ON "Dossiers" ("PatientId", "RequestedAt");
CREATE UNIQUE INDEX IF NOT EXISTS "Dossiers_Patient_active_key" ON "Dossiers" ("PatientId")
    WHERE "Status" = ANY (ARRAY['queued'::text, 'running'::text]);

-- This file owns the full, current action list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%dossier_receipt_downloaded%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text, 'quote_recorded'::text, 'quote_removed'::text, 'disclosure_authorized'::text, 'share_link_created'::text, 'share_link_revoked'::text, 'disclosure_term_opened'::text, 'integrity_report_downloaded'::text, 'integrity_check_failed'::text, 'dossier_requested'::text, 'dossier_downloaded'::text, 'dossier_receipt_downloaded'::text]))));
  END IF;
END $$;

COMMIT;
