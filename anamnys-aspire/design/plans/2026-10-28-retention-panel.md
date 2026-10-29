# Retention and Compliance Panel (F-06) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retention clock per patient, a "Conformidade" page (unsigned notes, retention, document compliance), daily bell notifications, and an irreversible discard with a disposal term.

**Architecture:** `Retention/` server area (clock, compliance query, settings, daily job, discard service with term PDF), migration on `Patients`/`Providers` plus `DisposalTerms`, export extension, provider "Conformidade" page and settings section.

**Tech Stack:** .NET 10, EF Core + Npgsql, QuestPDF, SeaweedFS S3, React 19 + TanStack Query, i18next.

**Spec:** `design/specs/2026-10-28-retention-panel-design.md` (binding).

## Global Constraints

- Branch `feature/attendance-stats` (F-04 is uncommitted in the same tree; do not touch F-04 files beyond shared registration points). **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire.
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping everywhere; no PHI in URLs, logs, audit values or object keys.
- Discard is irreversible: one transaction for rows, object deletes after commit with retries, never partial clinical content left behind.
- Lists ordered by date; no severity colours or risk ordering.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Server — migration, clock, panel, settings, notifications

**Files:** `design/migrations/2026-10-28-retention-panel.sql` + bootstrap + chains (after `2026-10-27-medication-timeline.sql`), `anamnys-db-script.sql`, entities/mapping, `anamnys-aspire.Server/Retention/*` (clock, compliance query, settings, daily job), hooks in note signing / document signing / session creation, `Program.cs`, `.http`, tests `anamnys-aspire.Tests/Retention/*`, `Data/RetentionPanelMigrationTests.cs`.

- [ ] Spec §2, §3 (clock, compliance, settings, daily job and notifications), §6 matching tests.

### Task 2: Server — discard and disposal term

**Files:** `anamnys-aspire.Server/Retention/Discard*`, term PDF (QuestPDF, existing PDF style), disposal-term PDF endpoint, export (F-60) inclusion, tests.

- [ ] Spec §3 discard, term endpoint, export; §6 discard and export tests.

### Task 3: Provider app

**Files:** Conformidade route/page, components under `apps/provider/src/components/compliance/*`, settings "Guarda" section, notifications link, `packages/shared/src/api/*`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 4: Documentation

**Files:** `documentation/30-retention-panel.md`, `documentation/00-index.md`, status, gaps.
