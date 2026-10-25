# External Documents (F-07) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upload, antivirus-scan, store and open external documents on the patient's record.

**Architecture:** `ExternalDocuments/` server area (sniffer, scanner interface + ClamAV client, service, scan queue/worker, endpoints), ClamAV container in the AppHost, migration extending `ExternalDocuments`, provider UI card + Documentos tab.

**Tech Stack:** .NET 10, EF Core + Npgsql, AWSSDK.S3 (SeaweedFS), clamd TCP protocol, Aspire 13.5, React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-10-25-external-documents-design.md` (binding).

## Global Constraints

- Branch `feature/external-documents`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs (`tasklist`); otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire (the controller does migrations and measurements).
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping everywhere; no PHI (patient names, original file names, document content) in object keys, URLs, logs or audit values.
- Automated tests use the fake scanner; the fixture must not start ClamAV.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Server — data, sniffer, scanner, service, queue, endpoints

**Files:** Create `design/migrations/2026-10-25-external-documents.sql`, `anamnys-aspire.Server/ExternalDocuments/*`, entity updates + DbContext mapping, tests `anamnys-aspire.Tests/ExternalDocuments/*`, `anamnys-aspire.Tests/Data/ExternalDocumentsMigrationTests.cs`. Modify `anamnys-db-script.sql`, `Program.cs`, `.http`, every `*MigrationTests` chain (after `2026-10-24-excerpt-refs.sql`), test fixture configuration (scanner = fake).

- [ ] Spec §2, §3 (except the AppHost container — Task 2), §6 automated tests.

### Task 2: AppHost — ClamAV container and configuration

**Files:** `anamnys-aspire.AppHost/AppHost.cs`, server configuration binding (`ExternalDocuments` options), `documentation/20-environment-configuration.md` (container section; numbers filled by the controller after measurement).

- [ ] ClamAV resource (`clamav/clamav` pinned tag), data volume, health check, internal endpoint wired to the server (`ExternalDocuments__ClamAvHost/Port`, `ExternalDocuments__Scanner=clamav`); fixture keeps `fake` and does not add the resource (follow how `Notifications__WorkerEnabled` / `NoteStructuring__WarmSuggestionOnStart` are passed through). Publish mode: same container (document).

### Task 3: Provider app

**Files:** `apps/provider/src/components/externalDocuments/*`, patient record route, `apps/provider/src/routes/_app/documents/index.tsx` (tabs), `packages/shared/src/api/externalDocuments.ts`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 4: Documentation

**Files:** Create `documentation/26-external-documents.md`; modify `documentation/00-index.md`, `design/status/2026-10-15-implementation-status.md` (F-07 done; Next F-08), `design/gaps/2026-10-03-patient-flows-gaps.md`.
