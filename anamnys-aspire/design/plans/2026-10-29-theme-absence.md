# Theme Absence (F-12) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect themes that stopped being recorded and show them on the Análises page.

**Architecture:** pure `Analysis/ThemeAbsence` on top of the F-10 query, migration for the provider preference, settings endpoint, card + lane mark + settings section in the provider app.

**Tech Stack:** .NET 10, EF Core + Npgsql, React 19 + TanStack Query, i18next.

**Spec:** `design/specs/2026-10-29-theme-absence-design.md` (binding).

## Global Constraints

- Branch `feature/theme-absence`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB (controller does).
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping; no PHI in URLs/logs; observation language; neutral colour; no notifications.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Server — migration, absence rule, settings

**Files:** `design/migrations/2026-10-29-theme-absence.sql` + bootstrap + migration test chains (after `2026-10-28-retention-panel.sql`), `Data/Entities/Provider.cs`, `Analysis/*`, settings endpoint, `Program.cs`, `.http`, tests `anamnys-aspire.Tests/Analysis/*`, `Data/ThemeAbsenceMigrationTests.cs`.

- [ ] Spec §2, §3, §6 server tests.

### Task 2: Provider app

**Files:** `apps/provider/src/components/analysis/*`, `routes/_app/analyses.tsx`, settings page section, `packages/shared/src/api/*`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 3: Documentation

**Files:** `documentation/32-theme-absence.md`, `documentation/00-index.md`, status, gaps.
