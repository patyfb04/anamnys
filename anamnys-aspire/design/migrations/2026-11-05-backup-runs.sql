-- F-46: backup run log, alerts and the agent role (design/specs/2026-11-05-backup-restore-design.md, section 3).
-- BackupRuns is written by the backup agent (full backups, object syncs, retention) and read by the server's
-- BackupMonitor; BackupAlerts holds one open alert per check so each problem episode is e-mailed once. Neither table
-- holds clinical data, file names or patient names: kind, times, outcome, bytes, a short error code and the backup name.
-- The role anamnys_backup_agent is created only if missing, NOLOGIN: the AppHost gives it a password and LOGIN when
-- backup is enabled. It can only INSERT into BackupRuns, plus SELECT on the tables of the row-count snapshot (counts
-- only). pg_stat_archiver needs no grant: PostgreSQL lets every role read it, so the server's database user reads it
-- directly and no SECURITY DEFINER function is needed. Idempotent; all or nothing.
BEGIN;

CREATE TABLE IF NOT EXISTS "BackupRuns" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "Kind" text NOT NULL,
    "StartedAt" timestamp with time zone NOT NULL,
    "FinishedAt" timestamp with time zone NOT NULL,
    "Outcome" text NOT NULL,
    "Bytes" bigint,
    "ErrorCode" text,
    "BackupName" text,
    CONSTRAINT "BackupRuns_Kind_ck" CHECK ("Kind" IN ('full', 'files', 'retention')),
    CONSTRAINT "BackupRuns_Outcome_ck" CHECK ("Outcome" IN ('ok', 'failed'))
);
CREATE INDEX IF NOT EXISTS "BackupRuns_Kind_FinishedAt_idx" ON "BackupRuns" ("Kind", "FinishedAt" DESC);

CREATE TABLE IF NOT EXISTS "BackupAlerts" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "Check" text NOT NULL,
    "OpenedAt" timestamp with time zone NOT NULL,
    "ClosedAt" timestamp with time zone,
    "NotifiedAt" timestamp with time zone
);
CREATE UNIQUE INDEX IF NOT EXISTS "BackupAlerts_OpenCheck_key" ON "BackupAlerts" ("Check") WHERE "ClosedAt" IS NULL;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anamnys_backup_agent') THEN
    CREATE ROLE anamnys_backup_agent NOLOGIN;
  END IF;
EXCEPTION WHEN duplicate_object THEN NULL;
END
$$;

GRANT INSERT ON "BackupRuns" TO anamnys_backup_agent;
GRANT SELECT ON "Providers", "Patients", "Notes", "NoteSections", "ClinicalDocuments", "ExternalDocuments",
                "IntegritySeals", "IntegrityAnchors", "AuditEntries", "Dossiers", "ShareLinks"
    TO anamnys_backup_agent;

COMMIT;
