# Availability, Blocks and Month View (F-29 delivery 2) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Weekly availability with versioned validity, national holidays with provider preferences, blocks with an affected-appointments choice, a calendar overlay, an outside-availability warning on booking, and the month view.

**Architecture:** New `Availability/` server area (rules, holidays, overlay, check, blocks, endpoints) hooked into `Appointments.CreateAsync`/`UpdateAsync`; provider settings section and calendar changes.

**Tech Stack:** .NET 10, EF Core + Npgsql, NodaTime or TimeZoneInfo (match what `calendarTime`/appointments use), React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-11-09-availability-design.md` (binding).

## Global Constraints

- Branch `feature/agenda-f29`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests/anamnys-aspire.Tests.csproj -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time; output to a file in the scratchpad `C:/Users/patri/AppData/Local/Temp/claude/cddb4cc4d594bfe5/13806727-9a85-492d-af33-bc5a7050064b/scratchpad/` and grep it. Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB. Scratch databases for new-schema tests.
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping; 404 for foreign ids; 401 first; `no-store`.
- No patient names or block reasons in logs or audit values.
- Nothing is cancelled unless the request says `cancelAffected: true`.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `DetailCard` for settings sections; `npm run build` / `npm run lint` pass in the provider app.

---

### Task 1: Server

**Files:** `design/migrations/2026-11-09-availability.sql` (spec §2) + bootstrap + migration-test chains (after `2026-11-05-backup-runs.sql`); entities/mapping for `AvailabilityRule`, `AvailabilityException`, `ProviderHolidaySettings`; `anamnys-aspire.Server/Availability/NationalHolidays.cs`, `AvailabilityRules.cs` (versioning + validation), `AvailabilityOverlay.cs`, `AvailabilityCheck.cs`, `Blocks.cs`, `AvailabilityEndpoints.cs`, `AvailabilityContracts.cs`; modify `Appointments/Appointments.cs` and `AppointmentContracts.cs`/`AppointmentEndpoints.cs` for `acknowledgeOutsideAvailability` and the 409; `Program.cs`; `.http`; tests `anamnys-aspire.Tests/Availability/*` and `Data/AvailabilityMigrationTests.cs`.

**Interfaces (produced, JSON camelCase):** exactly the shapes in spec §4 (availability, holiday-settings, blocks, preview, calendar-overlay, `outside_availability` reasons).

- [ ] Spec §2–§4 and §6 server bullets, tests first.

### Task 2: Provider UI

**Files:** `packages/shared/src/api/availability.ts` (+ appointments api change), types; `apps/provider/src/components/settings/AvailabilitySection.tsx` (+ holiday list), `apps/provider/src/components/calendar/*` (overlay shading and bands, `MonthView.tsx`, view selector, `BlockPeriodModal.tsx`, upcoming blocks panel, outside-availability confirmation in `AppointmentFormModal`), `routes/_app/calendar.tsx`, settings route, `pt.json`.

- [ ] Spec §5. Build and lint.

### Task 3: Documentation

**Files:** `documentation/41-availability.md`, `00-index.md`, status (F-29 row: delivery 2 done, and a verification line), gaps.

- [ ] Spec §7.
