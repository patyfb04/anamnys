# Case Analysis (F-10 + F-11) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One aggregation call for a patient's theme history with origins, and an "Análises" page with the theme timeline, co-occurrence and the existing timeline.

**Architecture:** `Analysis/` server area (query + endpoint), provider route `/analyses` with patient picker, theme grid, co-occurrence list and the reused `TimelineCard`.

**Tech Stack:** .NET 10, EF Core + Npgsql, React 19 + TanStack Router/Query, i18next.

**Spec:** `design/specs/2026-10-28-case-analysis-design.md` (binding).

## Global Constraints

- Branch `feature/case-analysis`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire.
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping; discarded patients 404; no PHI in URLs/logs.
- Observation language only; one neutral colour; no severity ordering.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Server — aggregation query and endpoint

**Files:** `anamnys-aspire.Server/Analysis/*`, `Program.cs`, `.http`, tests `anamnys-aspire.Tests/Analysis/*`.

- [ ] Spec §2, §3, §6 server tests.

### Task 2: Provider app — Análises page

**Files:** route `apps/provider/src/routes/_app/analyses.tsx`, `apps/provider/src/components/analysis/*`, Sidenav link, `packages/shared/src/api/*`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 3: Documentation

**Files:** `documentation/31-case-analysis.md`, `documentation/00-index.md`, status, gaps.
