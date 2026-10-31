-- F-14: plan adherence and follow-ups (design/specs/2026-10-30-plan-adherence-design.md, section 2).
-- A point marked "para retomar" on a note (FollowUpItems) now belongs to the provider as well as to the patient (so
-- every query is provider-scoped like the other clinical rows), keeps the passage it was marked from (an ExcerptRef,
-- like a theme tag) and who resolved it. The audit trail records a plan review and the follow-up actions, which may
-- belong to no note. PlanObjectives.TermId and TreatmentPlans.ReviewedAt already exist.
-- New rows for a discarded record are refused by trigger (Patients_not_discarded, F-06).
-- Idempotent; all or nothing.
BEGIN;

ALTER TABLE "FollowUpItems" ADD COLUMN IF NOT EXISTS "ProviderId" uuid;
UPDATE "FollowUpItems" f SET "ProviderId" = p."ProviderId" FROM "Patients" p WHERE p."Id" = f."PatientId" AND f."ProviderId" IS NULL;
ALTER TABLE "FollowUpItems" ALTER COLUMN "ProviderId" SET NOT NULL;
ALTER TABLE "FollowUpItems" ADD COLUMN IF NOT EXISTS "ExcerptRefId" uuid;
ALTER TABLE "FollowUpItems" ADD COLUMN IF NOT EXISTS "ResolvedById" uuid;
CREATE INDEX IF NOT EXISTS "FollowUpItems_Patient_Resolved_idx" ON "FollowUpItems" ("PatientId", "ResolvedAt");
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FollowUpItems_Patient_fk') THEN
    ALTER TABLE "FollowUpItems"
      ADD CONSTRAINT "FollowUpItems_Patient_fk" FOREIGN KEY ("PatientId","ProviderId") REFERENCES "Patients"("Id","ProviderId") ON DELETE CASCADE;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FollowUpItems_ExcerptRefId_fkey') THEN
    ALTER TABLE "FollowUpItems"
      ADD CONSTRAINT "FollowUpItems_ExcerptRefId_fkey" FOREIGN KEY ("ExcerptRefId") REFERENCES "ExcerptRefs"("Id") ON DELETE SET NULL;
  END IF;
END $$;

-- A plan review belongs to no note; a follow-up action may lose its note. This file owns the full, current action
-- list: a re-run keeps it as it is.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conname = 'AuditEntries_subject_ck' AND pg_get_constraintdef(oid) LIKE '%follow_up_reopened%') THEN
    ALTER TABLE "AuditEntries" DROP CONSTRAINT IF EXISTS "AuditEntries_subject_ck";
    ALTER TABLE "AuditEntries"
      ADD CONSTRAINT "AuditEntries_subject_ck" CHECK ((("NoteId" IS NOT NULL) OR ("DocumentId" IS NOT NULL) OR ("Action" = ANY (ARRAY['account_export_requested'::text, 'account_export_downloaded'::text, 'external_document_uploaded'::text, 'external_document_opened'::text, 'external_document_deleted'::text, 'external_document_blocked'::text, 'fact_confirmed'::text, 'fact_corrected'::text, 'fact_discarded'::text, 'facts_extracted'::text, 'facts_deleted'::text, 'medication_from_fact'::text, 'patient_discarded'::text, 'plan_reviewed'::text, 'follow_up_created'::text, 'follow_up_resolved'::text, 'follow_up_reopened'::text]))));
  END IF;
END $$;

-- The F-06 trigger on FollowUpItems (2026-10-28-retention-panel.sql creates it for every patient table; repeated
-- here so this file stands alone). The function is the one that file defines.
DROP TRIGGER IF EXISTS "FollowUpItems_patient_not_discarded" ON "FollowUpItems";
CREATE TRIGGER "FollowUpItems_patient_not_discarded" BEFORE INSERT ON "FollowUpItems" FOR EACH ROW EXECUTE FUNCTION "Patients_not_discarded"();

COMMIT;
