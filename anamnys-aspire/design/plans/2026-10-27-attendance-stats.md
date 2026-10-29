# Attendance Statistics (F-04) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Per-patient attendance and interval statistics in a "Comparecimento" card, and a "Consultas" lane on the timeline.

**Architecture:** `Attendance/` server area (pure statistics, query, endpoint), timeline payload extended with appointments, provider card and timeline lane.

**Tech Stack:** .NET 10, EF Core + Npgsql, React 19 + TanStack Query, SVG, i18next.

**Spec:** `design/specs/2026-10-27-attendance-stats-design.md` (binding).

## Global Constraints

- Branch `feature/attendance-stats`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire.
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping; no PHI in URLs/logs.
- No value colours, no severity, no evaluative wording.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Server — statistics, endpoint, timeline lane

**Files:** `anamnys-aspire.Server/Attendance/*`, `Medications/MedicationTimeline.cs` (appointments list), `Program.cs`, `.http`, tests `anamnys-aspire.Tests/Attendance/*` and the timeline tests.

- [ ] Spec §2, §3, §6 server tests.

### Task 2: Provider app

**Files:** new `apps/provider/src/components/attendance/*`, `components/timeline/*`, patient record route, calendar/note invalidation, `packages/shared/src/api/*`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 3: Documentation

**Files:** `documentation/29-attendance-stats.md`, `documentation/00-index.md`, status, gaps.
