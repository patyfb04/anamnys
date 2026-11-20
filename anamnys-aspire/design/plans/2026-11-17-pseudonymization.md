# Third-Party Pseudonymisation (G3: F-27) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace third-party names, organizations, schools and addresses in transcripts and AI-mode text with stable, non-reversible per-patient tokens before anything is persisted, and convert legacy transcripts on demand.

**Architecture:** New `Pseudonymization/` server area: an `IThirdPartyDetector` (Qwen via LLamaSharp with a GBNF grammar, plus an IBGE first-name rule), a token service (HMAC per patient secret protected by Data Protection, `PseudonymTokens` numbering), a pipeline step called by the transcription worker and by AI-mode note creation, and a backfill worker. Provider UI: notice and highlighting on the note, failed-note retry, settings section.

**Tech Stack:** .NET 10, EF Core + Npgsql, LLamaSharp (existing), ASP.NET Core Data Protection, React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-11-17-pseudonymization-design.md` (binding).

## Global Constraints

- Branch `feature/pseudonymization`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests/anamnys-aspire.Tests.csproj -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time; output to a file in the scratchpad `C:/Users/patri/AppData/Local/Temp/claude/cddb4cc4d594bfe5/13806727-9a85-492d-af33-bc5a7050064b/scratchpad/` and grep it. Fixture classes only when no AppHost/Server/aspire/Tests/dcp process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. If a run hangs over 25 minutes at AppHost startup, kill only the Tests process you started. Never start/stop Aspire. Do not apply migrations to the dev DB. Scratch databases for new-schema tests.
- Change files only with Edit/Write (no shell commands that write, move or delete files; never perl/sed on files; keep UTF-8 accents).
- The original (un-pseudonymised) text is never persisted, logged, put in exceptions, audit values or URLs; surface strings of detected entities are never stored.
- The real Qwen model is never loaded in tests: use a fake detector with fixed answers.
- Provider scoping; 404 for foreign ids; 401 first; `no-store`.
- Signed note sections and the integrity chain (chapter 38) are never modified.
- Wording: "reduz a exposição de terceiros", never "anonimizado".
- Copy in `pt.json` (`pseudonymization.*`); `npm run build` / `npm run lint` pass in the provider app.

---

### Task 1: Server — detector, tokens, pipeline step

**Files:** `design/migrations/2026-11-17-pseudonymization.sql` (spec §5) + `anamnys-db-script.sql` + migration-test chains (after `2026-11-16-transient-audio.sql`); entities/mapping; create `anamnys-aspire.Server/Pseudonymization/IThirdPartyDetector.cs`, `LlamaThirdPartyDetector.cs` (prompt + GBNF grammar, shares `LlmGate`/model with `NoteStructuring`), `FirstNameRule.cs` + embedded resource of IBGE first names, `PseudonymTokens.cs` (normalize, HMAC, numbering), `PatientSecrets.cs` (Data Protection), `Pseudonymizer.cs` (detect → exclusions → safety net → overlaps → replace; returns text + counts); modify `LiveSession/NoteTranscription.cs` (step before writing `RawTranscript`; failure → `Failed`/`pseudonymization_failed`; in-memory retry), `Notes/NoteCreation.cs` (AI-mode text), note view contract (`transcriptPseudonymized`), a retry endpoint for a failed note while the text is in memory, `Retention/DiscardService.cs`, `Program.cs`, `.http`; tests `anamnys-aspire.Tests/Pseudonymization/*`, `Data/PseudonymizationMigrationTests.cs`.

**Interfaces (produced):**
- `IThirdPartyDetector.DetectAsync(string text, IReadOnlyCollection<string> exclusions, CancellationToken ct) : Task<IReadOnlyList<DetectedSpan>>`; `DetectedSpan(int Start, int Length, string Kind)` with kinds `person|organization|school|address`.
- `Pseudonymizer.PseudonymizeAsync(AnamnysDbContext db, Guid providerId, Guid patientId, string text, CancellationToken ct) : Task<PseudonymizationResult>`; `PseudonymizationResult(string Text, IReadOnlyDictionary<string,int> CountsByKind)`.

- [ ] Spec §2–§5 and the server bullets of §8 (except backfill); tests first.

### Task 2: Server — backfill

**Files:** `Pseudonymization/BackfillEndpoints.cs`, `BackfillWorker.cs` (hosted; resumes after restart; one note at a time; lowest priority on `LlmGate`), `PseudonymizationBackfills` entity; tests.

**Interfaces:** Consumes `Pseudonymizer.PseudonymizeAsync`. Produces `POST/GET /api/phi/providers/me/pseudonymization/backfill`.

- [ ] Spec §6 and §8 backfill bullets; tests first (sections and seals untouched; integrity chain still verifies).

### Task 3: Provider UI

**Files:** shared API/types; note page and review (notice, token highlighting, failed-note retry); `components/settings/PseudonymizationBackfillSection.tsx` in the Prontuário tab; `pt.json` (`pseudonymization.*`).

**Interfaces:** Consumes Task 1 note fields and retry endpoint, Task 2 backfill endpoints.

- [ ] Spec §7. Build and lint.

### Task 4: Documentation

**Files:** `documentation/46-pseudonymization.md`, `00-index.md`, `design/status/2026-10-15-implementation-status.md` (F-27 row; verification line), `design/gaps/2026-10-03-patient-flows-gaps.md` (spec §9).

- [ ] Spec §10 (Confluence by the controller).
