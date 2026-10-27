# Medication Timeline (F-09) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Medication facts become medication records through a prefilled form, and a "Linha do tempo" card shows medication lanes and scale scores on one axis.

**Architecture:** `Medications/` server area (prefill, fact→record service, timeline query), migration on `MedicationEntries`, facts-panel medication form and an SVG timeline card in the provider app.

**Tech Stack:** .NET 10, EF Core + Npgsql, React 19 + TanStack Query, SVG, i18next.

**Spec:** `design/specs/2026-10-27-medication-timeline-design.md` (binding).

## Global Constraints

- Branch `feature/medication-timeline`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire.
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping; audit; no PHI in URLs/logs/audit values.
- No value colours, no severity bands, no statement of relation between medication and scores.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Server — migration, prefill, fact→medication, timeline

**Files:** `design/migrations/2026-10-27-medication-timeline.sql` + bootstrap + chains (after `2026-10-26-document-facts.sql`), entity/mapping, `anamnys-aspire.Server/Medications/*`, existing manual medication flows (`Patients/PatientRecords.cs`) set TreatmentId, `Program.cs`, `.http`, tests `anamnys-aspire.Tests/Medications/*`, `Data/MedicationTimelineMigrationTests.cs`.

- [ ] Spec §2, §3, §6 server tests.

### Task 2: Provider app

**Files:** facts panel components in `apps/provider/src/components/externalDocuments/*`, new `apps/provider/src/components/timeline/*`, patient record route, `packages/shared/src/api/*`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 3: Documentation

**Files:** `documentation/28-medication-timeline.md`, `documentation/00-index.md`, status (also renumber the "Next" list), gaps.
