# Document Integrity Chain (F-45) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Seal every signed note and signed issued document in a per-provider append-only hash chain, anchor the chain head daily with RFC 3161 timestamps, and let the provider verify and download a proof.

**Architecture:** New `Integrity/` server area (canonical content, sealing inside the signing transaction, verifier, TSA anchoring, nightly job, backfill, endpoints, report PDF); a badge on the signed note and document pages and an "Integridade dos registros" block on Conformidade.

**Tech Stack:** .NET 10, EF Core + Npgsql, PostgreSQL triggers, System.Security.Cryptography.Pkcs (`Rfc3161TimestampRequest`), QuestPDF, React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-11-03-integrity-chain-design.md` (binding).

## Global Constraints

- Branch `feature/integrity-chain`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests/anamnys-aspire.Tests.csproj -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time, and make sure it exits. Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire. Do not apply migrations to the dev DB (controller does).
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping on every query; unknown or foreign ids answer 404.
- No clinical text in seals, anchors, logs, audit values or notices. Only the head hash leaves the system (to the TSA).
- Signing never depends on the TSA; a sealing failure fails the signing (same transaction).
- Hash format exactly as spec §2 and canonical content exactly as spec §3 (key order, NFC, `yyyy-MM-ddTHH:mm:ss.ffffffZ`, nulls written, `"v": 1` first).
- Copy in `pt.json` under `integrity.*`; `DetailCard` with `collapsibleId` and top anchor on Conformidade; `npm run build` / `npm run lint` pass in the provider app.

---

### Task 1: Data, canonical content and sealing

**Files:**
- Create: `design/migrations/2026-11-03-integrity-chain.sql` (spec §2: tables, triggers, drop `DocumentHashes`, audit actions, notification kind), wired into the test DB bootstrap and migration chain the same way `2026-11-01-protected-sharing.sql` is.
- Create: `anamnys-aspire.Server/Data/Entities/IntegritySeal.cs`, `IntegrityAnchor.cs`; mapping in `AnamnysDbContext`.
- Create: `anamnys-aspire.Server/Integrity/IntegrityCanonical.cs` (spec §3: `NoteBytes`, `DocumentBytes`, `ContentHash`), `Integrity/IntegritySeals.cs` (`SealAsync`, `SealHash`).
- Modify: `Notes/NoteEditing.cs` (`SignAsync`), `Documents/ClinicalDocumentService.cs` (`SignAsync`), `Retention/DiscardService.cs` (mark `DiscardedAt` instead of deleting `DocumentHashes`).
- Test: `anamnys-aspire.Tests/Integrity/IntegrityCanonicalTests.cs`, `IntegritySealTests.cs`, `Data/IntegrityChainMigrationTests.cs`.

**Interfaces (produced):**
- `IntegrityCanonical.ForNote(Note, IReadOnlyList<NoteSection>) -> byte[]`, `IntegrityCanonical.ForDocument(ClinicalDocument) -> byte[]`, `IntegrityCanonical.Hash(byte[]) -> string` (lowercase hex SHA-256).
- `IntegritySeals.SealAsync(AnamnysDbContext db, Guid providerId, string subjectType, Guid subjectId, string contentHash, DateTimeOffset now, CancellationToken ct) -> Task<IntegritySeal>`.
- `IntegritySeals.SealHash(string? previousHash, long sequence, string subjectType, Guid subjectId, string contentHash, DateTimeOffset sealedAt) -> string`.
- `IntegritySeals.NoteContentHashAsync(db, providerId, noteId, ct)` and `DocumentContentHashAsync(db, providerId, documentId, ct) -> Task<string?>` (load rows, canonicalize, hash; null when not found or not signed).

- [ ] Canonical tests (spec §10 canonical bullet) first, then `IntegrityCanonical`.
- [ ] Migration + trigger tests (UPDATE/DELETE fail, `DiscardedAt` once passes, twice fails, anchors immutable).
- [ ] Sealing tests (note and document seal in the signing transaction, correction gets its own seal, two concurrent signings get consecutive sequences, sealing failure rolls the signing back, discard sets `DiscardedAt` and keeps the seal), then `SealAsync` with `pg_advisory_xact_lock` and the three callers.
- [ ] Run `*Integrity*`, `*NoteEditing*`, `*ClinicalDocument*`, `*Discard*`.

### Task 2: Verification, timestamping, job, backfill, endpoints, report

**Files:**
- Create: `Integrity/IntegrityVerifier.cs` (spec §5), `Integrity/IntegrityAnchoring.cs` + `Integrity/ITimestampAuthority.cs` + `Integrity/HttpTimestampAuthority.cs` (spec §6), `Integrity/IntegrityOptions.cs` (`TsaUrl`, `TsaRootCertificate`), `Integrity/IntegrityJob.cs` (spec §7, `RetentionJob` pattern), `Integrity/IntegrityBackfill.cs` (spec §4 retroactive), `Integrity/IntegrityEndpoints.cs` + `Integrity/IntegrityContracts.cs` (spec §8), `Integrity/IntegrityReportPdf.cs` (QuestPDF).
- Modify: `Program.cs` (options, services, job, backfill at startup, endpoints), AppHost (`Integrity:TsaUrl`, `Integrity:TsaRootCertificate`; required outside Development), `.http` file.
- Test: `anamnys-aspire.Tests/Integrity/IntegrityVerifierTests.cs`, `IntegrityAnchoringTests.cs` (with `FakeTimestampAuthority` in the test project), `IntegrityJobTests.cs`, `IntegrityBackfillTests.cs`, `IntegrityEndpointTests.cs`.

**Interfaces:**
- Consumes Task 1's `IntegrityCanonical`, `IntegritySeals`.
- Produces `IntegrityVerifier.VerifySubjectAsync(db, providerId, string subjectType, Guid subjectId, ct) -> Task<SubjectIntegrity?>` with `record SubjectIntegrity(string Status, DateTimeOffset? SealedAt, bool Retroactive, bool Anchored)`; `VerifyChainAsync(db, providerId, ITimestampVerifier, ct) -> Task<ChainIntegrity>` with `record ChainIntegrity(long TotalSeals, long Discarded, ChainProblem? FirstProblem, AnchorInfo? LastAnchor, DateTimeOffset VerifiedAt)`.
- JSON (camelCase): `{ status, sealedAt, retroactive, anchored }` and `{ totalSeals, discarded, firstProblem: { sequence, subjectType, kind } | null, lastAnchor: { sequence, anchoredAt, tsaUrl } | null, verifiedAt }`.

- [ ] Verifier tests (spec §10 verification bullet), then the verifier.
- [ ] Anchoring tests (token stored and verifies, tampered token or other head hash fails, TSA down leaves signing unaffected and the job retries), then anchoring with `Rfc3161TimestampRequest`.
- [ ] Job tests (one notice per problem with `DedupeKey`, audit `integrity_check_failed`), backfill tests (order, retroactive flag, idempotent), then both.
- [ ] Endpoint tests (shapes, 401, 404 for foreign ids, `no-store`, report PDF renders and is audited), then endpoints and report.
- [ ] Run `*Integrity*` and `*Endpoint*` shape tests that list routes, if any.

### Task 3: Provider UI

**Files:**
- Create: `packages/shared/src/api/integrity.ts`; types in `packages/shared/src/lib/types.ts`.
- Create: `apps/provider/src/components/integrity/IntegrityBadge.tsx`, `apps/provider/src/components/compliance/IntegrityCard.tsx`.
- Modify: signed note page and document page (badge next to the signature, not on drafts), `apps/provider/src/routes/_app/compliance.tsx` (new card + top anchor via `cardAnchors.ts`), `pt.json` (`integrity.*`).

- [ ] Spec §9: badge states (intact, retroactive, awaiting timestamp, problem in red with link to Conformidade); card with chain state, total, last timestamp, "Verificar agora" (refetch), "Baixar comprovante (PDF)".
- [ ] `npm run build` and `npm run lint` in `apps/provider`.

### Task 4: Documentation

**Files:** `documentation/38-integrity-chain.md`, `documentation/00-index.md`, `design/status/2026-10-15-implementation-status.md` (F-44/F-45 row), `design/gaps/2026-10-03-patient-flows-gaps.md` (external documents and authorizations not sealed; tokens not embedded in the report), chapter 20 (deployment: TSA parameters, migration, backfill at startup).

- [ ] Spec §11.
