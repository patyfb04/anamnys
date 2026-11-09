# Backup and Point-in-Time Restore (F-46) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Encrypted off-server backup of PostgreSQL (point-in-time, WAL-G) and SeaweedFS (rclone, versioned), freshness monitoring with e-mail alerts, scripted restore and a measured, recorded restore drill.

**Architecture:** A WAL-G-enabled PostgreSQL image and a `backup-agent` container, switched on by `backup-enabled` (off in Development and tests by default); a `BackupMonitor` in the server; a `RestoreCheck` console and `restore.sh` / `restore-drill.sh` scripts.

**Tech Stack:** PostgreSQL 18, WAL-G, rclone (crypt remote), POSIX sh, Docker, .NET 10 Aspire, EF Core + Npgsql, Resend e-mail.

**Spec:** `design/specs/2026-11-05-backup-restore-design.md` (binding).

## Global Constraints

- Branch `feature/backup-restore`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests/anamnys-aspire.Tests.csproj -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time, and make sure it exits. Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB (controller does). Scratch databases for new-schema tests.
- Edit/Write only; CRLF for new `.cs`; LF for `.sh` and Dockerfiles (they run in Linux containers); keep endings of existing files.
- Backup disabled by default: with `backup-enabled` unset, Development and the test AppHost behave exactly as today.
- Pin every downloaded binary to an exact version and check its SHA-256 in the Dockerfile.
- No clinical data, file names or patient names in `BackupRuns`, alerts, e-mails, logs, `RestoreCheck` output or drill records. Never print secrets.
- The word "cofre" only inside the rule that restricts it.

---

### Task 1: Data and monitor

**Files:** `design/migrations/2026-11-05-backup-runs.sql` (spec §3) wired into the bootstrap and every migration-test chain after `2026-11-04-patient-dossier.sql`; entities and mapping; `anamnys-aspire.Server/Backup/BackupOptions.cs` (`Enabled`, `AlertEmail`), `Backup/BackupMonitor.cs` (spec §2.4), `Program.cs`; tests `anamnys-aspire.Tests/Backup/BackupMonitorTests.cs`, `Data/BackupRunsMigrationTests.cs`.

**Interfaces (produced):** `BackupMonitor.CheckAsync(AnamnysDbContext db, IEmailSender email, BackupOptions options, DateTimeOffset now, CancellationToken ct) -> Task<IReadOnlyList<BackupCheckResult>>` with `record BackupCheckResult(string Check, bool Passed, TimeSpan? Age)`; check names `wal`, `full`, `files`, `failed_run`. (Use the actual e-mail abstraction in `Email/`.)

- [ ] Spec §6 monitor and migration bullets, tests first.

### Task 2: Images, agent, AppHost

**Files:** `postgres/Dockerfile`, `backup-agent/Dockerfile`, `backup-agent/agent.sh` (scheduler, full backup + permanent monthly + retention, row-count snapshot to `meta/counts/`, rclone sync with `--backup-dir`, version purge, `BackupRuns` rows), `backup-agent/rclone.conf.template`, AppHost (`backup-enabled` parameter, archive settings and WAL-G env on PostgreSQL only when enabled, `backup-agent` and Development `backup-store` only when enabled, publish-mode required parameters `backup-s3-endpoint`, `backup-s3-bucket`, `backup-s3-access-key`, `backup-s3-secret-key`, `backup-encryption-key`, `backup-alert-email`, `backup-agent-db-password`; server gets `Backup__Enabled` and `Backup__AlertEmail`).

- [ ] Spec §2.1–2.3. `shellcheck` via pinned `koalaman/shellcheck` image on every `.sh`. Build both images with `docker build` (allowed; no Aspire start). Confirm the AppHost builds and that with `backup-enabled` unset the resource graph is unchanged (an AppHost model test if the test project has one, otherwise a code-level assertion test).

### Task 3: RestoreCheck and scripts

**Files:** `anamnys-aspire.RestoreCheck/` (console project referencing the server, added to the solution; spec §4.2), `scripts/restore.sh` (spec §4.1), `scripts/restore-drill.sh` (spec §4.3, writes `design/drills/AAAA-MM-DD-restore-drill.md`); tests `anamnys-aspire.Tests/Backup/RestoreCheckTests.cs`.

- [ ] Spec §6 RestoreCheck bullets, tests first; `shellcheck` on the scripts.

### Task 4: First drill (controller)

The controller starts Aspire with `backup-enabled=true`, waits for a full backup and a few minutes of WAL and object syncs, runs `scripts/restore-drill.sh`, and keeps the record in `design/drills/`. A failing drill goes back to the task that owns the failure.

### Task 5: Documentation

**Files:** `documentation/40-backup-restore.md` (spec §7), `documentation/00-index.md`, chapter 20 (parameters), chapter 17 (deployment must supply the destination and keys), status (F-46 row, verification line), gaps.

- [ ] Spec §7, with the measured RPO/RTO from the drill record.
