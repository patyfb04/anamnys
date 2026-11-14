# Structured Intake (F-31) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A default intake template the provider can adapt, an intake filled in during the first session that becomes a signed `Anamnese` note, and one-by-one confirmation of answers into the record.

**Architecture:** New `Intake/` server area (templates, intakes, note generation, transfers) hooked into note signing; provider patient page card, intake page, transfer panel, Note Editor shortcut and a settings section.

**Tech Stack:** .NET 10, EF Core + Npgsql, React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-11-12-structured-intake-design.md` (binding).

## Global Constraints

- Branch `feature/structured-intake`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests/anamnys-aspire.Tests.csproj -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time; output to a file in the scratchpad `C:/Users/patri/AppData/Local/Temp/claude/cddb4cc4d594bfe5/13806727-9a85-492d-af33-bc5a7050064b/scratchpad/` and grep it. Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB. Scratch databases for new-schema tests.
- Edit/Write only; CRLF for new `.cs`; keep endings (Git Bash sed strips CR).
- Provider scoping; 404 for foreign ids; 401 first; `no-store`.
- No patient names or answers in logs or audit values (ids only).
- Nothing is written to the record (patient fields, medication, diagnosis, plan) without an explicit confirm.
- Copy in `pt.json`; `DetailCard`; `npm run build` / `npm run lint` pass in the provider app.

---

### Task 1: Server

**Files:** `design/migrations/2026-11-12-structured-intake.sql` (spec §2, incl. the default template seed) + bootstrap + migration-test chains; entities/mapping; `anamnys-aspire.Server/Intake/IntakeTemplates.cs`, `IntakeService.cs`, `IntakeNote.cs`, `IntakeTransfers.cs`, `IntakeEndpoints.cs`, `IntakeContracts.cs`; modify note signing (`Notes/NoteEditing.cs`, `NoteStructuring/NoteFormats.cs`) to accept an intake note's template section keys and to create transfers; `Retention/DiscardService.cs`; `Program.cs`; `.http`; tests `anamnys-aspire.Tests/Intake/*`, `Data/StructuredIntakeMigrationTests.cs`.

**Interfaces (produced):** exactly spec §4.

- [ ] Spec §2–§4 and §6 server bullets; tests first.

### Task 2: Provider UI

**Files:** `packages/shared/src/api/intake.ts`, types; patient page card; route `apps/provider/src/routes/_app/patients/$patientId/intake.$intakeId.tsx` (or the project's route-file convention); transfer panel; Note Editor shortcut; settings section in the Prontuário tab; `pt.json` (`intake.*`).

- [ ] Spec §5. Build and lint.

### Task 3: Documentation

**Files:** `documentation/43-structured-intake.md`, `00-index.md`, status (F-31 row; verification line), gaps.

- [ ] Spec §7.
