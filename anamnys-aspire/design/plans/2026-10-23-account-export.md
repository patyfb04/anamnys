# Full Account Export (F-60) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One action exports the provider's whole account as an offline-readable ZIP (HTML + PDFs + JSON/CSV), and the site/Terms state D-08.

**Architecture:** `AccountExports` row + in-process queue/worker builds the ZIP (`Exports/` server area) from provider-scoped queries, reusing `NotePdf`/`DocumentPdf`; ZIP in private storage, one-hour presigned download, 7-day expiry sweep, bell notification; Settings section in the provider app.

**Tech Stack:** .NET 10, EF Core + Npgsql, System.IO.Compression, QuestPDF, AWSSDK.S3 (SeaweedFS), React 19 + TanStack Query, i18next.

**Spec:** `design/specs/2026-10-23-account-export-design.md` (binding).

## Global Constraints

- Branch `feature/account-export`. **Never commit, stage, stash or switch branches.**
- Tests: `dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe process runs (check `tasklist`); otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run.
- Edit/Write only (no `sed -i`); CRLF for new `.cs`; keep existing endings.
- Every query filters by the provider (notes, documents, patients, scales, appointments, audit…).
- Never export `Notes.RawTranscript`, transcripts/audio, audit previous/new values, or other providers' data.
- No patient name in object keys, file names inside the ZIP, URLs or logs.
- HTML in the ZIP: no JavaScript, no external resources.
- Copy in `packages/shared/src/lib/i18n/locales/pt.json`; text actions `hover:underline`, hover backgrounds `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Data, storage and the export service

**Files:** Create `design/migrations/2026-10-23-account-export.sql`, `anamnys-aspire.Server/Data/Entities/AccountExport.cs` (if missing), `anamnys-aspire.Server/Exports/{AccountExportService.cs, ExportQueue.cs, ExportWorker.cs, ExportSweep.cs, ExportEndpoints.cs}`, tests `anamnys-aspire.Tests/Exports/{AccountExportServiceTests.cs, ExportEndpointTests.cs}`, `anamnys-aspire.Tests/Data/AccountExportMigrationTests.cs`. Modify `anamnys-db-script.sql`, `Data/AnamnysDbContext.cs`, `Storage/INoteFileStore.cs` + `S3NoteFileStore.cs` (+ any test fake store) with `GetAsync`/`DeleteAsync`, `Program.cs` (endpoints + hosted services), `.http`, migration tests chain (add the new migration after `2026-10-21-theme-vocabulary.sql`).

- [ ] Spec §2, §3, §5: request (409 when in progress), latest, download link (409 not_ready/expired, 404 other provider), audit actions, worker that calls an `IAccountExportBuilder` (Task 2 provides the real one; here a placeholder that writes a minimal ZIP so the pipeline is testable), upload, mark done + size + notification (`in_app`, kind `account_export_ready`, check `Notifications` constraints and how ProviderNotifications lists items so the bell shows it), failure handling, restart sweep (running → failed), 7-day expiry sweep (delete object, mark expired).
- [ ] Tests per spec §9 "Service/API" and "Migration".

### Task 2: ZIP builder

**Files:** Create `anamnys-aspire.Server/Exports/{AccountExportBuilder.cs, ExportData.cs, ExportHtml.cs, ExportCsv.cs, ExportReadme.cs}` (split as you see fit), tests `anamnys-aspire.Tests/Exports/AccountExportBuilderTests.cs`. Modify the worker registration to use the real builder.

- [ ] Spec §4 layout exactly; HTML/CSV/JSON rules; PDFs from storage when `PdfObjectKey` exists, else rendered from the signing snapshot with `NotePdf`/`DocumentPdf` (look at `Notes/NoteExport.cs` and `Documents/DocumentExport.cs` for how models are built); drafts and superseded marked; archived patients included; themes from active tags.
- [ ] Stream the ZIP (do not hold every PDF in memory at once where avoidable).
- [ ] Tests per spec §9 "ZIP builder" (marker never appears; no `<script`; layout; scoping).

### Task 3: Provider app section

**Files:** Create `apps/provider/src/components/settings/AccountExportSection.tsx`, `packages/shared/src/api/exports.ts`; modify the settings route, `types.ts`, `pt.json`, and the bell notification rendering for kind `account_export_ready` (link to the settings section).

- [ ] Spec §7; polling while queued/running; dates in pt-BR; warning text verbatim.

### Task 4: Site and Terms texts (D-08)

**Files:** `apps/web/src/content/site/faq.md`, `apps/web/src/content/legal/terms-of-service.md`, pricing/about strings in `pt.json` (grep for "o tempo todo", "exportação", "60 dias").

- [ ] Spec §6; same tone as the surrounding copy; no other legal changes.

### Task 5: Documentation

**Files:** Create `documentation/24-account-export.md`; modify `documentation/00-index.md`, `documentation/20-environment-configuration.md` (migration), `design/status/2026-10-15-implementation-status.md` (F-60 done; Next: the remaining launch gate F-50…F-59), `design/gaps/2026-10-03-patient-flows-gaps.md` (section `## Exportação`: F-57 owns the 60-day window and deletion; downloaded ZIP leaves our control; ZIP not encrypted).
