# Record Search (F-16) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A question on the Análises page returns passages of signed notes with provenance.

**Architecture:** Postgres text search on `NoteSections` plus the theme vocabulary, `Analysis/RecordSearch*`, a card on the Análises page.

**Tech Stack:** .NET 10, EF Core + Npgsql, PostgreSQL full-text search, React 19 + TanStack Query, i18next.

**Spec:** `design/specs/2026-10-31-record-search-design.md` (binding).

## Global Constraints

- Branch `feature/record-search`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time, and make sure it exits. Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB (controller does).
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping; discarded patients 404; the question never in URLs, logs or audit.
- Passages only, never synthesis; nothing found says so.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Server

**Files:** `design/migrations/2026-10-31-record-search.sql` + bootstrap + chains, `Analysis/RecordSearch*`, `Program.cs`, `.http`, tests.

- [ ] Spec §2, §3, §6 server tests.

### Task 2: Provider app

**Files:** `components/analysis/RecordSearchCard.tsx`, `AnalysisNav`, `routes/_app/analyses.tsx`, `packages/shared/src/api/*`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 3: Documentation

**Files:** `documentation/36-record-search.md`, `documentation/00-index.md`, status, gaps.
