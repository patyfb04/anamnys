# Plan Adherence (F-14) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Objectives compared with recorded themes, plan review, and "para retomar" follow-ups.

**Architecture:** migration on `FollowUpItems`, `Analysis/PlanAdherence*` query + endpoints, objective term link, follow-up endpoints; provider plan card, objective form field, note-view follow-ups.

**Tech Stack:** .NET 10, EF Core + Npgsql, React 19 + TanStack Query, i18next.

**Spec:** `design/specs/2026-10-30-plan-adherence-design.md` (binding).

## Global Constraints

- Branch `feature/plan-adherence`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB (controller does).
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping; discarded patients blocked; archived patients read-only; audit with ids only; no PHI in URLs/logs.
- Observation language; no alert colours; no notifications.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Server

**Files:** `design/migrations/2026-10-30-plan-adherence.sql` + bootstrap + chains, entities/mapping, objective write path, `Analysis/PlanAdherence*`, follow-up endpoints, `Retention/DiscardService` (delete follow-ups), `Program.cs`, `.http`, tests.

- [ ] Spec §2, §3, §7 server tests.

### Task 2: Provider app

**Files:** objective form, `components/analysis/*` plan card, note view follow-ups, `packages/shared/src/api/*`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 3: Seed and documentation

**Files:** `dev-seed/analysis-samples.sql`, `documentation/34-plan-adherence.md`, `documentation/00-index.md`, status, gaps.
