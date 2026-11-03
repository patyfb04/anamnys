# Protected Sharing (F-17 + F-18) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Share a signed document with a named recipient through a token link and a separate password, after recording the patient's authorization.

**Architecture:** `Sharing/` server area (authorization, links, public open with watermarked PDF), provider share flow on the document page, public `/s/$token` route in the patient app.

**Tech Stack:** .NET 10, EF Core + Npgsql, QuestPDF, Argon2id, SeaweedFS S3, React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-11-01-protected-sharing-design.md` (binding).

## Global Constraints

- Branch `feature/protected-sharing`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time, and make sure it exits. Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB (controller does).
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping; no token, password, recipient or PHI in URLs beyond the token itself, logs or audit values.
- Identical 404 for unknown, expired, revoked and capped links. No e-mail sending of documents.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass in both apps.

---

### Task 1: Server

**Files:** `design/migrations/2026-11-01-protected-sharing.sql` + bootstrap + chains, entities/mapping, `anamnys-aspire.Server/Sharing/*`, watermark in the document PDF pipeline, `Retention/DiscardService` if needed, `Program.cs`, `.http`, csproj (Argon2), tests `anamnys-aspire.Tests/Sharing/*`, `Data/ProtectedSharingMigrationTests.cs`.

- [ ] Spec §2, §3, §6 server tests.

### Task 2: Apps

**Files:** provider document page and `components/sharing/*`, patient app route `/s/$token`, `packages/shared/src/api/*`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 3: Documentation

**Files:** `documentation/37-protected-sharing.md`, `documentation/00-index.md`, status, gaps.
