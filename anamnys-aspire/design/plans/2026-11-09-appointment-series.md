# Recurring Appointments (F-29 delivery 3) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Weekly and every-two-weeks appointment series with real occurrences generated 12 weeks ahead and extended daily, single-occurrence changes, "this and following" split, ending a series, and one summary notice instead of one e-mail per occurrence.

**Architecture:** New `Appointments/Series/` server area (generator, extension job, split/end, endpoints) on top of the existing appointment and notification code; provider calendar form, details and patient page changes.

**Tech Stack:** .NET 10, EF Core + Npgsql, React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-11-09-appointment-series-design.md` (binding).

## Global Constraints

- Branch `feature/agenda-series`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests/anamnys-aspire.Tests.csproj -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time; output to a file in the scratchpad `C:/Users/patri/AppData/Local/Temp/claude/cddb4cc4d594bfe5/13806727-9a85-492d-af33-bc5a7050064b/scratchpad/` and grep it. Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB. Scratch databases for new-schema tests.
- Edit/Write only; CRLF for new `.cs`; keep endings (Git Bash sed strips CR).
- Provider scoping; 404 for foreign ids; 401 first; `no-store`.
- No patient names in logs or audit values.
- Generation never cancels anything; only "end" and explicit single cancels cancel.
- Copy in `pt.json`; `npm run build` / `npm run lint` pass in the provider app.

---

### Task 1: Server

**Files:** `design/migrations/2026-11-10-appointment-series.sql` (spec §2) + bootstrap + migration-test chains; entity/mapping changes for `AppointmentSeries` and `Appointment`; `anamnys-aspire.Server/Appointments/Series/SeriesGenerator.cs`, `SeriesExtensionJob.cs`, `SeriesService.cs` (create, split preview/split, end), `SeriesEndpoints.cs`, `SeriesContracts.cs`; modify `Appointments.cs` (reschedule sets `Detached`; list/detail fields), `Notifications/*` (series summary and ended e-mails; confirmation scheduled at `StartsAt − 48 h` for series occurrences); `Program.cs`; `.http`; tests `anamnys-aspire.Tests/Series/*`, `Data/AppointmentSeriesMigrationTests.cs`.

**Interfaces (produced, JSON camelCase):** exactly spec §4.

- [ ] Spec §2–§4, §6 server bullets; tests first.

### Task 2: Provider UI

**Files:** `packages/shared/src/api/appointments.ts` (+ series api), types; `apps/provider/src/components/calendar/AppointmentFormModal.tsx` (repeat/end fields and summary), calendar cards (repeat icon), `AppointmentDetailsModal.tsx` (series line, reschedule/cancel scope choices, split preview), the patient page (active series with "Encerrar série"), `pt.json` (`series.*`).

- [ ] Spec §5. Build and lint.

### Task 3: Documentation

**Files:** `documentation/42-recurring-appointments.md`, `00-index.md`, status (F-29 row: delivery 3 done; verification line), gaps.

- [ ] Spec §7.
