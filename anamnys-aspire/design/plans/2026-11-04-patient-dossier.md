# Patient Dossier (F-44) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One action builds a patient's dossier (ZIP with `dossie.pdf`, original files, manifest with hashes, integrity proof), timestamped, downloadable for 7 days, with a permanent delivery receipt.

**Architecture:** New `Dossiers/` server area: a pure builder (reads the patient's official record, writes the ZIP and returns the manifest and integrity result), then the request flow on the account-export pattern (row, queue, worker, sweep, endpoints, receipt PDF); a "Dossiês" card on the provider's patient page.

**Tech Stack:** .NET 10, EF Core + Npgsql, QuestPDF, System.IO.Compression, the F-45 `ITimestampAuthority` and `IntegrityVerifier`, SeaweedFS S3 via `INoteFileStore`, React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-11-04-patient-dossier-design.md` (binding).

## Global Constraints

- Branch `feature/patient-dossier`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests/anamnys-aspire.Tests.csproj -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time, and make sure it exits. Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB (controller does). Tests that need the new schema use scratch databases.
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping on every query; 404 for another provider's patient or dossier; 401 first; `no-store`.
- No patient name in URLs, file names, object keys, logs, audit values or the manifest. The reason note never goes to the audit.
- The word "cofre" appears nowhere in code, copy or docs; the product name is "Dossiê do paciente".
- Excluded from the ZIP: drafts, `RawTranscript`, audit values, other patients' data.
- Copy in `pt.json` under `dossier.*`; `DetailCard` with `collapsibleId`; 5 rows per page; `npm run build` / `npm run lint` pass in the provider app.

---

### Task 1: Dossier builder

**Files:**
- Create: `anamnys-aspire.Server/Dossiers/DossierData.cs` (loads the official record for one patient and period: signed notes with sections, signed documents with versions, clean/blocked/pending external documents, authorizations with term files, share links with access logs, compliance status per document, retention, integrity per subject via `IntegrityVerifier.VerifySubjectAsync`, covering anchors), `Dossiers/DossierBuilder.cs` (writes the ZIP of spec §4 to a stream, returns `DossierBuildResult(Manifest, IntegrityVerified)`), `Dossiers/DossierPdf.cs` (spec §5 `dossie.pdf`), `Dossiers/DossierReadme.cs`, `Dossiers/DossierFileNames.cs` (sanitizing, `-N` collision suffixes).
- Reuse: `NotePdf` / `DocumentPdf` rendering when a stored PDF is missing (as `AccountExportBuilder` does); the F-45 report for `integridade/comprovante.pdf`, restricted to this patient's seals (add a per-subject-set overload if needed).
- Test: `anamnys-aspire.Tests/Dossiers/DossierBuilderTests.cs`.

**Interfaces (produced):**
- `DossierRequest(Guid DossierId, int Number, Guid ProviderId, Guid PatientId, string Reason, string? ReasonNote, DateOnly? PeriodFrom, DateOnly? PeriodTo, DateTimeOffset GeneratedAt)`.
- `DossierBuilder.BuildAsync(AnamnysDbContext db, INoteFileStore store, ITimestampAuthority tsa, DossierRequest request, Stream output, CancellationToken ct) -> Task<DossierBuildResult>`; `DossierBuildResult(DossierManifest Manifest, bool IntegrityVerified)`; `DossierManifest` serializes to the spec §4 `manifesto.json`.
- The cover always says the ZIP hash and its timestamp are in the delivery receipt (the builder cannot know them). The builder takes `tsa` only to verify the covering anchors' tokens for the integrity section.

- [ ] Tests first (spec §7 content, period, manifest, integrity bullets), then the data loader, the PDF and the builder.
- [ ] Run `*Dossier*`.

### Task 2: Request flow, worker, sweep, endpoints, receipt

**Files:**
- Create: `design/migrations/2026-11-04-patient-dossier.sql` (spec §2), wired into the bootstrap and every migration-test chain after `2026-11-03-integrity-chain.sql`; `Data/Entities/Dossier.cs` + mapping; `Dossiers/DossierService.cs` (request, list, download, receipt, complete, fail), `Dossiers/DossierQueue.cs`, `Dossiers/DossierWorker.cs` (build to a temp file, SHA-256, timestamp, upload, complete; restart handling; daily sweep), `Dossiers/DossierEndpoints.cs` + `DossierContracts.cs`, `Dossiers/DossierReceiptPdf.cs`.
- Modify: `Program.cs`, `.http` file, notifications listing if it filters kinds.
- Test: `anamnys-aspire.Tests/Dossiers/DossierFlowTests.cs`, `DossierEndpointTests.cs`, `Data/PatientDossierMigrationTests.cs`.

**Interfaces:**
- Consumes Task 1's `DossierBuilder.BuildAsync`, `DossierRequest`, `DossierBuildResult`.
- JSON (camelCase): request `{ reason, reasonNote?, periodFrom?, periodTo? }`; list `{ items: [{ id, number, status, reason, periodFrom, periodTo, requestedAt, generatedAt, expiresAt, sizeBytes, anchored, integrityVerified }], total, page, pageSize }`; download `{ url, expiresAt }`; errors `invalid_reason`, `reason_note_required`, `invalid_period`, `dossier_in_progress`, `not_ready`, `expired`.

- [ ] Spec §2, §3, §5 receipt, §7 flow, timestamp and validation bullets; tests first.
- [ ] Run `*Dossier*` and `*MigrationTests*`.

### Task 3: Provider UI

**Files:** `packages/shared/src/api/dossiers.ts`, types in `packages/shared/src/lib/types.ts`, `apps/provider/src/components/dossiers/*` (card, dialog), the patient page route, `NotificationBell` (`dossier_ready`), `pt.json` (`dossier.*`).

- [ ] Spec §6. `npm run build` and `npm run lint` in `apps/provider` (and `apps/patient` if shared types it uses change).

### Task 4: Documentation

**Files:** `documentation/39-patient-dossier.md`, `documentation/00-index.md`, `design/status/2026-10-15-implementation-status.md` (F-44 row and a dated verification line), `design/gaps/2026-10-03-patient-flows-gaps.md` (structured CSV, e-mail delivery, patient-portal request out of scope), chapter 20 if deployment needs anything.

- [ ] Spec §8.
