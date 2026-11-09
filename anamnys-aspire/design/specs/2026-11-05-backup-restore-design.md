# Backup and Point-in-Time Restore (F-46)

**Date:** 2026-11-05
**Status:** Approved for implementation
**Scope:** Backlog F-46. Verified backup of everything the product stores, point-in-time restore, and a rehearsed,
measured and recorded restore drill.

Done when (backlog): backup is verified, with RPO/RTO tested and documented. The word "cofre" may be used in any
outward material only when this is in production and a restore drill passed in the last 90 days.

Explicitly out of scope: an admin dashboard for backups; a live replica; the production deployment itself (chapter 17
still describes what a real deployment must supply); automatic rotation of the encryption key (documented as a manual
procedure).

## 1. Decisions (agreed with the user, 2026-11-05)

| Topic | Decision |
|---|---|
| Hosting | Not chosen yet. The backup uses tools that run on any Docker host (WAL-G for PostgreSQL, rclone for the object store) and targets any S3-compatible bucket off the server. The drill is run locally first. |
| What is protected | The single PostgreSQL server (both databases, `anamnysdb` and `keycloakdb`) and every object in SeaweedFS (note and document PDFs, external documents, authorization terms, dossiers, exports). |
| RPO | Database: 1 minute (WAL shipped at least every 60 s). Objects: 15 minutes (incremental copy every 10 minutes). |
| RTO | 2 hours to restore everything on a new server, measured in the drill. |
| Retention | Point-in-time restore over the last 30 days; 12 monthly full backups (kept, but `restore.sh` cannot restore one older than the 30-day window: a manual procedure, recorded as a gap; see 4.1). |
| Encryption | Everything is encrypted before it leaves the server: WAL-G with a libsodium key, rclone with a crypt remote. |
| Monitoring | A server job checks freshness every 15 minutes and e-mails the administrator (Resend), deduplicated, with no clinical data. |
| Drill | A scripted drill restores into a throwaway environment, verifies (F-45 chain, row counts, file hashes, Keycloak), measures RPO/RTO and writes a dated record. Monthly; first run now, locally. |

## 2. Components

### 2.1 PostgreSQL image with WAL-G

- `postgres/Dockerfile`: `FROM postgres:18` (the tag the AppHost uses today), WAL-G pinned (exact release, binary
  checked by SHA-256 in the Dockerfile), `ca-certificates`.
- With backup enabled the AppHost passes `archive_mode=on`, `archive_command='timeout 60 wal-g wal-push %p'`,
  `archive_timeout=60`, `wal_level=replica` and the WAL-G environment (`WALG_S3_PREFIX`, `AWS_ENDPOINT`,
  `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_S3_FORCE_PATH_STYLE=true`, `WALG_LIBSODIUM_KEY`,
  `WALG_LIBSODIUM_KEY_TRANSFORM=base64`, `WALG_COMPRESSION_METHOD=brotli`). The `timeout 60` bounds a push to an
  unreachable destination, which WAL-G otherwise retries without end and which hangs PostgreSQL's shutdown (a fresh
  volume's init never completes); a timed-out push fails and PostgreSQL retries the segment.
- With backup disabled (the default in Development and in tests) the image behaves exactly like `postgres:18`:
  no archiving, no WAL-G environment. The existing suite is unaffected.

### 2.2 Destination

- AppHost parameters, required when publishing: `backup-s3-endpoint`, `backup-s3-bucket`, `backup-s3-access-key`,
  `backup-s3-secret-key` (secret), `backup-encryption-key` (secret, 32 random bytes base64), `backup-alert-email`.
- In Development, `backup-enabled=true` (parameter, default `false`) turns everything on and adds a second SeaweedFS
  container `backup-store` (own volume, own S3 credentials) as the destination.
- Bucket layout: `pg/` (WAL-G), `files/current/` (rclone crypt), `files/versions/AAAA-MM-DDTHHMM/` (rclone
  `--backup-dir`), `meta/` (row-count snapshots, section 2.3).

### 2.3 `backup-agent` container

Image `backup-agent/Dockerfile` (WAL-G and rclone pinned and checksummed, `postgresql-client`, a POSIX `sh` scheduler
script). Runs only when backup is enabled.

- **Full backup** daily at 03:00 America/Sao_Paulo: `wal-g backup-push` against the PostgreSQL container. The first
  full backup of each calendar month is marked permanent (`wal-g backup-mark`). Retention after each full backup:
  `wal-g delete retain FULL 30 --confirm` (non-permanent fulls older than the 30 most recent days' are removed) and
  permanent backups older than 12 months are unmarked and deleted.
- **Row-count snapshot**: right after each full backup, a JSON with the row count of the main tables (Providers,
  Patients, Notes, NoteSections, ClinicalDocuments, ExternalDocuments, IntegritySeals, IntegrityAnchors,
  AuditEntries, Dossiers, ShareLinks) and the backup name, written to `meta/counts/<backup-name>.json` (counts only).
- **Objects** every 10 minutes (goal 15): `rclone sync` from the SeaweedFS bucket to the crypt remote `files/current/` with
  `--backup-dir files/versions/<timestamp>/`, so a deletion or overwrite at the origin moves the old copy into a dated
  version folder instead of losing it. Version folders older than 30 days are purged daily.
- **Run log**: every run writes a row to `BackupRuns` (section 3) through `psql` with a dedicated role that can only
  insert into that table: kind (`full`, `files`, `retention`), started, finished, outcome (`ok`, `failed`), bytes,
  and a short error code. No clinical data, no file names.

### 2.4 `BackupMonitor` (server)

A `BackgroundService` every 15 minutes, only when backup is enabled (`Backup:Enabled`):

- WAL: `pg_stat_archiver` — alert when `last_archived_time` is older than 10 minutes, or `last_failed_time` is newer
  than `last_archived_time`.
- Full backup: alert when the last `BackupRuns` row of kind `full` with outcome `ok` is older than 26 hours.
- Objects: alert when the last `files` `ok` row is older than 45 minutes.
- Any `failed` run since the last check: alert.
- E-mail to `Backup:AlertEmail` through the existing Resend sender, subject "Anamnys: problema no backup", body with
  the check name, the age and the time, nothing clinical. Deduplicated: one e-mail per check per problem episode
  (cleared when the check passes again), stored in a `BackupAlerts` table (check, opened, closed, notified).

## 3. Data

Migration `design/migrations/2026-11-05-backup-runs.sql` (idempotent, bootstrap, migration-test chains):

- `BackupRuns` (`Id`, `Kind` check `full|files|retention`, `StartedAt`, `FinishedAt`, `Outcome` check `ok|failed`,
  `Bytes` bigint, `ErrorCode` text, `BackupName` text) with an index on (`Kind`, `FinishedAt` desc).
- `BackupAlerts` (`Id`, `Check` text, `OpenedAt`, `ClosedAt`, `NotifiedAt`) with a partial unique index: one open
  alert per check.
- Role `anamnys_backup_agent` (created only if missing, `NOLOGIN` in the migration; the AppHost grants login with its
  own password parameter `backup-agent-db-password` when backup is enabled) with `INSERT` on `BackupRuns` only, plus
  `SELECT` on the tables of the row-count snapshot (counts only). WAL-G connects as the PostgreSQL superuser for
  `backup-push`, as WAL-G requires.

## 4. Restore

### 4.1 `scripts/restore.sh` (real restore, new server)

- Arguments: `--target-time "AAAA-MM-DD HH:MM:SS-03"` or `--latest`; the destination settings come from the same
  environment variables as the agent.
- Steps: `wal-g backup-fetch` of the latest full backup before the target; `recovery_target_time`, `recovery_target_action=promote`
  and `restore_command='wal-g wal-fetch %f %p'`; start PostgreSQL and wait for promotion; restore objects with
  `rclone copy` from `files/current/` and, for a target earlier than the last sync, overlay the version folders newer
  than the target in reverse order so each object is as it was at the target (documented limit: object state is exact
  to the 10-minute sync grid).
- The application server is not started by the script. Chapter 40 says to run `RestoreCheck` first and start the
  server only after it passes.
- The destination bucket has no default and must be new or empty: the script refuses a bucket that already holds any
  object (copy into a live store would silently revert its objects), so "restored objects >= objects in `current/`" is
  a valid copy check.
- Not supported: a target older than the 30-day point-in-time window. Both modes need the WAL after the full backup,
  which retention removes after 30 days, so the monthly permanent fulls (12 months) can only be restored by hand
  (`wal-g backup-fetch <name>` and `recovery_target = 'immediate'`). Recorded as a gap; a `--backup <name>` option is
  the fix when it is needed (YAGNI before production).

### 4.2 `anamnys-aspire.RestoreCheck` (console project)

References the server project. Input: a connection string, the S3 settings of the restored object store, the expected
row-count snapshot (optional), and a sample size (default 200). Checks:

1. Integrity: `IntegrityVerifier.VerifyChainAsync` for every provider; every chain must have no problem.
2. Row counts: for each table in the snapshot, restored count >= snapshot count (rows written after the full backup
   but before the target are expected; fewer means data is missing).
3. Files: a random sample of signed notes and documents with a stored PDF — the object exists and its SHA-256 equals
   `PdfSha256`; a random sample of external documents with status `clean` — the object exists and its size equals the
   stored size. In the drill (section 4.3) the external documents are also compared by SHA-256 against the live
   source bucket.
4. Keycloak: the `keycloakdb` database has the realm and at least the same number of users as the snapshot.

Output: JSON `{ "passed": bool, "checks": [ { "name", "passed", "detail" } ], "durationSeconds" }`, exit code 0 or 1.
No clinical text in the output.

### 4.3 `scripts/restore-drill.sh`

1. Create a throwaway Docker network and containers (PostgreSQL from the image in 2.1, a SeaweedFS), with no access to
   the live containers except reading the live object store for the drill-only hash comparison.
2. Note the target time (now minus 5 minutes by default) and the time of the last database change before it (the
   newest `AuditEntries.At` or seal `SealedAt` at or before the target, read from the live database).
3. Run the restore of 4.1 into the throwaway containers, then `RestoreCheck`.
4. Measure: RTO = start of the restore to `RestoreCheck` passing; RPO (database) = target minus the newest change
   recovered at or before the target; RPO (objects) = target minus the sync used.
5. Write `design/drills/AAAA-MM-DD-restore-drill.md`: date, origin (local or the environment name), target time,
   backup used, RPO and RTO measured against the goals (1 min, 15 min, 2 h), every `RestoreCheck` result, verdict
   (`aprovado` only when every check passed and RPO/RTO meet the goals). Then tear down the throwaway environment.

## 5. Rules

- The word "cofre" in any outward material requires F-46 in production and a drill with verdict `aprovado` in the
  last 90 days, recorded in `design/drills/`. Chapter 40 and the status file say so.
- The drill runs monthly. A failed drill opens a gap in the gaps file until a passing one.
- The encryption key is kept outside the server (password manager of the account owner); losing it makes every backup
  unreadable. Chapter 40 has the manual rotation procedure (new key, new full backup, old backups kept until they
  expire).

## 6. Testing

- `BackupMonitor`: no alert when fresh; alerts on WAL older than 10 min or failing, full older than 26 h, files older
  than 45 min, a failed run; one e-mail per episode, re-armed after recovery; no clinical data in the e-mail; nothing
  runs with backup disabled.
- `RestoreCheck` on a scratch database: a healthy database passes; a tampered seal, a missing row (count below the
  snapshot), a PDF with a different hash and a missing object each fail with their reason; the JSON shape is fixed.
- Migration: tables, checks, the partial index and the role; added to the migration-test chains.
- AppHost: with `backup-enabled` unset, PostgreSQL has no archive settings and there is no `backup-agent`; publish mode
  requires the backup parameters.
- Scripts: POSIX `sh`, `shellcheck` clean (run through Docker `koalaman/shellcheck` pinned).
- The first drill, locally, recorded in `design/drills/`. F-46 is not marked done without a passing drill.

## 7. Documentation

- Chapter `documentation/40-backup-restore.md`: architecture, goals, retention, keys, restore step by step
  (including "back to 14:32, before the mistake"), the drill, the "cofre" rule, what to do on each alert.
- Chapter 20: the new parameters. Chapter 17: a deployment must supply the backup destination and keys.
- Status, gaps, Notion (F-46).
