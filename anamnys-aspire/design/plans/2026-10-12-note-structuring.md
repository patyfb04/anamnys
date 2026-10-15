# Live Session — Note Structuring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After a dictation is transcribed, an in-process Qwen2.5 7B model (CPU, LLamaSharp) reorganises the transcript into the chosen format's sections (`NoteSections`), shown on the note page as an AI draft and regenerable in another format while nothing is edited.

**Architecture:** The existing in-memory queue and single worker gain a second job kind. A transcription job, once the note is `ReadyForReview`, continues with structuring in the same worker; a `StructureJob` (from the new restructure endpoint) runs structuring only. `NoteStructuring.ProcessAsync` calls an `INoteStructurer` (LLamaSharp, GBNF-constrained JSON) and replaces the note's sections in one transaction unless a section was edited meanwhile. `Notes.StructureStatus` (`pending`/`done`/`failed`) drives the UI; the note page polls while pending.

**Tech Stack:** .NET 10, LLamaSharp + LLamaSharp.Backend.Cpu, EF Core 10 + Npgsql, PostgreSQL 18, SignalR, xUnit v3 + FluentAssertions; React 19, TanStack Router/Query, Tailwind 4, i18next.

**Spec:** `design/specs/2026-10-12-note-structuring-design.md` (builds on `2026-10-10-live-session-transcription-design.md`).

## Global Constraints

- All inference in-process on CPU. Never call a cloud AI API. Model files are downloaded on first use and verified against SHA-256 values pinned in configuration; no hash → refuse to load (fail closed).
- Model: Qwen2.5 7B Instruct GGUF Q4_K_M, from the official `Qwen/Qwen2.5-7B-Instruct-GGUF` repository (split files `qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf` and `-00002-of-00002.gguf`), Apache 2.0.
- The AI only redistributes and cleans what was dictated: no added facts, hypotheses, diagnoses, interpretations or conclusions; names, numbers, doses and dates preserved; a section with nothing dictated is `""`. Portuguese only.
- Formats and section keys (in order): DAP = `data`, `assessment`, `plan`; SOAP = `subjective`, `objective`, `assessment`, `plan`; Biopsychosocial = `biological`, `psychological`, `social`, `plan`.
- `Notes.StructureStatus`: null | `pending` | `done` | `failed`. `Notes.StructureFailure`: null | `model_error` | `too_long` | `server_restart`, set exactly when `failed`.
- A structuring failure never changes `Notes.Status`; the transcript always stays.
- Sections are replaced only if no section of the note has `EditedAt` (checked inside the writing transaction).
- Logs never carry prompt, transcript or section text — only note id, stage, exception type.
- Every query provider-scoped (`ProviderId` from the providers cookie).
- Defaults: `ContextSize` 8192, `MaxOutputTokens` 2048, temperature 0.1, `Threads` = `Environment.ProcessorCount - 1` (min 1).
- UI strings in Portuguese in `packages/shared/src/lib/i18n/locales/pt.json`.
- Tests: `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"` from `anamnys-aspire/`; never `dotnet test`; `aspire stop` before building. Frontend: `npm run build` and `npm run lint` from `anamnys-aspire/`. No frontend test runner.
- The test fixture shares the dev Postgres volume: apply the migration to the dev DB (Task 1) before Postgres tests.
- **Do not commit** until the user has seen the changes (user rule). Preserve each file's line endings (CRLF files stay CRLF).

---

## File Structure

**Database:** `design/migrations/2026-10-12-note-structuring.sql` (new), `anamnys-db-script.sql`.

**Server (`anamnys-aspire.Server/`)**
- `Data/Entities/Note.cs` (+2 props), `Data/Entities/NoteSection.cs` (new), `Data/AnamnysDbContext.cs`.
- `Ai/ModelFiles.cs` (new) — shared verify/download used by both model providers; `LiveSession/WhisperModelProvider.cs` refactored onto it.
- `NoteStructuring/NoteFormats.cs`, `NoteGrammar.cs`, `NoteStructuringOptions.cs`, `LlmModelProvider.cs`, `INoteStructurer.cs`, `NotePrompt.cs`, `LlamaNoteStructurer.cs`, `NoteStructuring.cs`, `NoteStructuringSetup.cs` (new).
- `LiveSession/TranscriptionQueue.cs` (job hierarchy), `LiveSession/TranscriptionWorker.cs`, `LiveSession/LiveRecordings.cs` (finalize + orphan sweep).
- `Notes/NoteQueries.cs`, `Notes/NoteEndpoints.cs`, `Notes/NoteRestructure.cs` (new).
- `Program.cs`, `appsettings.json`, `appsettings.Development.json`, `anamnys-aspire.Server.csproj`, `anamnys-aspire.Server.http`.

**Tests (`anamnys-aspire.Tests/`)**: `Data/NoteStructuringMigrationTests.cs`, `NoteStructuring/NoteFormatsTests.cs`, `NoteStructuring/NoteGrammarTests.cs`, `NoteStructuring/FakeNoteStructurer.cs`, `NoteStructuring/NoteStructuringTests.cs`, `NoteStructuring/LlamaNoteStructurerTests.cs`, `NoteStructuring/LlmModelProviderTests.cs`; updates to `LiveSession/LiveRecordingsTests.cs`, `LiveSession/NoteTranscriptionTests.cs`, `LiveSession/TranscriptionWorkerTests.cs`, `Notes/NoteEndpointTests.cs`.

**Frontend:** `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts`, `apps/provider/src/routes/_app/notes/$noteId.tsx`, `apps/provider/src/components/notes/NoteSections.tsx` (new), `apps/provider/src/components/notes/RestructureControl.tsx` (new), `apps/provider/src/components/live-session/PrepareStep.tsx`, `pt.json`.

**Docs:** root `README.md`, `anamnys-aspire/CLAUDE.md`, `design/gaps/2026-10-03-patient-flows-gaps.md`, spec status.

---

### Task 1: Schema, migration and entities

**Files:**
- Create: `design/migrations/2026-10-12-note-structuring.sql`, `anamnys-aspire.Server/Data/Entities/NoteSection.cs`
- Modify: `anamnys-db-script.sql`, `anamnys-aspire.Server/Data/Entities/Note.cs`, `anamnys-aspire.Server/Data/AnamnysDbContext.cs`
- Test: `anamnys-aspire.Tests/Data/NoteStructuringMigrationTests.cs`

**Interfaces:**
- Produces: columns `Notes.StructureStatus`, `Notes.StructureFailure` with checks `Notes_StructureStatus_ck`, `Notes_StructureFailure_ck`; `Note.StructureStatus`, `Note.StructureFailure` (`string?`); `NoteSection` entity; `DbSet<NoteSection> NoteSections`.

- [ ] **Step 1: Read the pattern.** `anamnys-aspire.Tests/Data/LiveSessionMigrationTests.cs` and `design/migrations/2026-10-10-live-session.sql`.

- [ ] **Step 2: Failing test** — `Data/NoteStructuringMigrationTests.cs`, a copy of `LiveSessionMigrationTests` adapted:
  - the "old" database runs `2026-10-03-patient-accounts.sql`, `2026-10-04-patient-invitations.sql`, `2026-10-07-appointment-notifications.sql`, `2026-10-10-live-session.sql`, then `2026-10-12-note-structuring.sql` twice; the fresh one is `anamnys-db-script.sql` plus the new migration once;
  - `ShapeAsync` over tables `["Notes", "NoteSections"]`;
  - asserts shapes are equivalent and contain `"Notes.StructureStatus"`, `"Notes.StructureFailure"`;
  - a second test inserting a note with `"StructureStatus" = 'failed'` and no `StructureFailure` expects `PostgresException` with `ConstraintName` `Notes_StructureFailure_ck` (reuse the insert from `LiveSessionMigrationTests.FailedNote_WithoutReason_IsRejected`, setting `"Status" = 'ReadyForReview'`, `"StructureStatus" = 'failed'`).

- [ ] **Step 3: Run, expect FAIL** (migration file missing).
`dotnet run --project anamnys-aspire.Tests -- -class "*NoteStructuringMigrationTests*"`

- [ ] **Step 4: Migration** — `design/migrations/2026-10-12-note-structuring.sql`:

```sql
-- Note structuring state (design/specs/2026-10-12-note-structuring-design.md §3). Run after
-- 2026-10-10-live-session.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-12-note-structuring.sql

BEGIN;

ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "StructureStatus" text;
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "StructureFailure" text;

ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_StructureStatus_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_StructureStatus_ck" CHECK ((("StructureStatus" IS NULL) OR ("StructureStatus" = ANY (ARRAY['pending'::text, 'done'::text, 'failed'::text]))));

ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_StructureFailure_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_StructureFailure_ck" CHECK (((("StructureStatus" IS NOT DISTINCT FROM 'failed'::text) = ("StructureFailure" IS NOT NULL)) AND (("StructureFailure" IS NULL) OR ("StructureFailure" = ANY (ARRAY['model_error'::text, 'too_long'::text, 'server_restart'::text])))));

COMMIT;
```

- [ ] **Step 5: Mirror in `anamnys-db-script.sql`:** add `"StructureStatus" text,` and `"StructureFailure" text,` to `CREATE TABLE "Notes"` after `"RecordingId"`, and the two constraints with the exact expressions above.

- [ ] **Step 6: Apply to the dev DB** (the psql command in the header; start the app with `aspire start` if needed, `aspire stop` afterwards).

- [ ] **Step 7: Run, expect PASS** (2 tests).

- [ ] **Step 8: Entities.** `Note.cs` gains:

```csharp
    public string? StructureStatus { get; set; }
    public string? StructureFailure { get; set; }
```

`Data/Entities/NoteSection.cs`:

```csharp
namespace Anamnys.Server.Data.Entities;

// One section of a structured note (design/specs/2026-10-12-note-structuring-design.md §3).
// GeneratedAt marks the AI draft; EditedAt (delivery 3) marks the provider's edit.
public class NoteSection
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    public int Position { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
}
```

`AnamnysDbContext.cs`: `public DbSet<NoteSection> NoteSections => Set<NoteSection>();` and an entity block `ToTable("NoteSections")`, `HasKey(x => x.Id)`.

- [ ] **Step 9: Regression:** `-class "*Migration*" -class "*LiveRecordingsTests*" -class "*NoteEndpointTests*"` → PASS.

- [ ] **Step 10: Leave changes unstaged for review.**

---

### Task 2: Formats and grammar

**Files:**
- Create: `anamnys-aspire.Server/NoteStructuring/NoteFormats.cs`, `NoteStructuring/NoteGrammar.cs`
- Test: `anamnys-aspire.Tests/NoteStructuring/NoteFormatsTests.cs`, `NoteStructuring/NoteGrammarTests.cs`

**Interfaces:**
- Produces: `NoteFormats.KeysFor(string format) → IReadOnlyList<string>` (throws `ArgumentException` for unknown), `NoteFormats.IsStructurable(string format) → bool`, `NoteFormats.Describe(string key) → string` (Portuguese description used in the prompt); `NoteGrammar.For(IReadOnlyList<string> keys) → string` (GBNF, root rule `root`).

- [ ] **Step 1: Failing tests.**

`NoteFormatsTests.cs`:

```csharp
using Anamnys.Server.NoteStructuring;
using FluentAssertions;

namespace Anamnys.Tests.NoteStructuring;

public class NoteFormatsTests
{
    [Fact]
    public void KeysFor_EachFormat_ReturnsKeysInOrder()
    {
        NoteFormats.KeysFor("DAP").Should().Equal("data", "assessment", "plan");
        NoteFormats.KeysFor("SOAP").Should().Equal("subjective", "objective", "assessment", "plan");
        NoteFormats.KeysFor("Biopsychosocial").Should().Equal("biological", "psychological", "social", "plan");
    }

    [Theory]
    [InlineData("DAP", true)]
    [InlineData("SOAP", true)]
    [InlineData("Biopsychosocial", true)]
    [InlineData("PtFunctional", false)]
    [InlineData("dap", false)]
    public void IsStructurable_MatchesTheThreeFormats(string format, bool expected)
    {
        NoteFormats.IsStructurable(format).Should().Be(expected);
    }

    [Fact]
    public void KeysFor_UnknownFormat_Throws()
    {
        var act = () => NoteFormats.KeysFor("PtFunctional");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Describe_EveryKey_HasAPortugueseDescription()
    {
        foreach (var format in new[] { "DAP", "SOAP", "Biopsychosocial" })
        {
            foreach (var key in NoteFormats.KeysFor(format))
            {
                NoteFormats.Describe(key).Should().NotBeNullOrWhiteSpace();
            }
        }
    }
}
```

`NoteGrammarTests.cs`:

```csharp
using Anamnys.Server.NoteStructuring;
using FluentAssertions;

namespace Anamnys.Tests.NoteStructuring;

public class NoteGrammarTests
{
    [Fact]
    public void For_DapKeys_RequiresExactlyThoseKeysInOrder()
    {
        var gbnf = NoteGrammar.For(["data", "assessment", "plan"]);

        gbnf.Should().StartWith("root ::= \"{\" ws ");
        var root = gbnf.Split('\n')[0];
        root.IndexOf("\\\"data\\\"").Should().BeLessThan(root.IndexOf("\\\"assessment\\\""));
        root.IndexOf("\\\"assessment\\\"").Should().BeLessThan(root.IndexOf("\\\"plan\\\""));
        root.Should().EndWith("ws \"}\"");
        gbnf.Should().Contain("string ::=").And.Contain("ws ::=");
    }

    [Fact]
    public void For_RejectsKeysThatNeedEscaping()
    {
        var act = () => NoteGrammar.For(["bad\"key"]);

        act.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run, expect build FAIL** (`Anamnys.Server.NoteStructuring` missing).
`dotnet run --project anamnys-aspire.Tests -- -class "*NoteFormatsTests*" -class "*NoteGrammarTests*"`

- [ ] **Step 3: Implement.**

`NoteFormats.cs`:

```csharp
namespace Anamnys.Server.NoteStructuring;

// Section keys per note format, in display order (design/specs/2026-10-12-note-structuring-design.md §4).
public static class NoteFormats
{
    private static readonly Dictionary<string, string[]> Keys = new(StringComparer.Ordinal)
    {
        ["DAP"] = ["data", "assessment", "plan"],
        ["SOAP"] = ["subjective", "objective", "assessment", "plan"],
        ["Biopsychosocial"] = ["biological", "psychological", "social", "plan"],
    };

    // What belongs in each section, as told to the model. The assessment is always the
    // provider's own: the model files what was dictated and never writes one (CFP).
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["data"] = "o que o paciente relatou e o que foi observado na sessão",
        ["assessment"] = "a avaliação que o(a) profissional ditou; nunca crie uma",
        ["plan"] = "próximos passos, encaminhamentos e tarefas ditados",
        ["subjective"] = "o relato do paciente, como ditado",
        ["objective"] = "observações e medidas ditadas pelo(a) profissional",
        ["biological"] = "aspectos biológicos ditados (sono, alimentação, medicação, saúde física)",
        ["psychological"] = "aspectos psicológicos ditados (emoções, pensamentos, comportamento)",
        ["social"] = "aspectos sociais ditados (família, trabalho, relações)",
    };

    public static bool IsStructurable(string format) => Keys.ContainsKey(format);

    public static IReadOnlyList<string> KeysFor(string format) =>
        Keys.TryGetValue(format, out var keys) ? keys : throw new ArgumentException($"Unknown note format '{format}'.", nameof(format));

    public static string Describe(string key) => Descriptions[key];
}
```

`NoteGrammar.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace Anamnys.Server.NoteStructuring;

// GBNF that only admits a JSON object with exactly the given keys, in order, each a JSON string.
public static partial class NoteGrammar
{
    public static string For(IReadOnlyList<string> keys)
    {
        var root = new StringBuilder("root ::= \"{\" ws ");
        for (var i = 0; i < keys.Count; i++)
        {
            if (!SafeKey().IsMatch(keys[i]))
            {
                throw new ArgumentException($"Section key '{keys[i]}' is not a plain identifier.", nameof(keys));
            }
            if (i > 0)
            {
                root.Append("\",\" ws ");
            }
            root.Append($"\"\\\"{keys[i]}\\\"\" ws \":\" ws string ws ");
        }
        root.Append("\"}\"");
        return string.Join('\n',
            root.ToString(),
            "string ::= \"\\\"\" char* \"\\\"\"",
            "char ::= [^\"\\\\\\x00-\\x1f] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" hex hex hex hex)",
            "hex ::= [0-9a-fA-F]",
            "ws ::= [ \\t\\n]?");
    }

    [GeneratedRegex("^[a-z]+$")]
    private static partial Regex SafeKey();
}
```

Each key appends `string ws `, so the root line ends with `string ws "}"` and satisfies the test. `ws` is limited to one optional whitespace character so the model cannot spend its output budget on whitespace.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Leave unstaged.**

---

### Task 3: Job kinds, finalize and orphan sweep

**Files:**
- Modify: `anamnys-aspire.Server/LiveSession/TranscriptionQueue.cs`, `LiveSession/LiveRecordings.cs`, `LiveSession/NoteTranscription.cs` (signature only if needed), `LiveSession/TranscriptionWorker.cs` (compile only; behaviour in Task 6)
- Modify tests: `anamnys-aspire.Tests/LiveSession/LiveRecordingsTests.cs`, `NoteTranscriptionTests.cs`, `TranscriptionWorkerTests.cs`
- Modify: spec §5 "Queue" bullet to describe the job hierarchy below.

**Interfaces:**
- Produces:
  - `public abstract record NoteJob(Guid NoteId, Guid ProviderId);`
  - `public sealed record TranscriptionJob(Guid NoteId, Guid ProviderId, string Language, PcmAudio Audio) : NoteJob(NoteId, ProviderId);` (unchanged shape)
  - `public sealed record StructureJob(Guid NoteId, Guid ProviderId) : NoteJob(NoteId, ProviderId);`
  - `TranscriptionQueue`: `Enqueue(NoteJob)`, `ChannelReader<NoteJob> Reader`, `PositionOf`, `MarkStarted` unchanged otherwise.
  - Finalize writes `StructureStatus = "pending"`.
  - `LiveRecordings.FailOrphansAsync` also sets `StructureStatus = "failed"`, `StructureFailure = "server_restart"` where `StructureStatus == "pending"`, and returns the total rows touched.

Ruling (spec deviation, record in the spec): the spec's single `NoteJob(..., Kind, Audio?)` becomes an abstract record with two subtypes, so `TranscriptionJob.Audio` stays non-nullable and Delivery 1 code and tests keep compiling with minimal change.

- [ ] **Step 1: Failing tests** in `LiveRecordingsTests.cs`:
  - in `Finalize_WithoutAppointment_CreatesAttendedSessionAndProcessingNote` add `note.StructureStatus.Should().Be("pending");`
  - new test `FailOrphans_PendingStructure_BecomesFailedServerRestart`: create a note via finalize, then `ExecuteUpdate` it to `Status = "ReadyForReview"`, `RawTranscript = "x"` (still `StructureStatus = pending`); call `FailOrphansAsync`; assert `Status` stays `ReadyForReview`, `StructureStatus == "failed"`, `StructureFailure == "server_restart"`.

- [ ] **Step 2: Run, expect FAIL.** `-class "*LiveRecordingsTests*"`

- [ ] **Step 3: Implement.**
  - `TranscriptionQueue.cs`: add `NoteJob` and `StructureJob`; make `TranscriptionJob` derive from `NoteJob`; change the channel, `Reader` and `Enqueue` to `NoteJob`.
  - `LiveRecordings.FinalizeAsync`: set `StructureStatus = "pending"` on the new `Note`.
  - `LiveRecordings.FailOrphansAsync`:

```csharp
    public static async Task<int> FailOrphansAsync(AnamnysDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var transcriptions = await db.Notes
            .Where(n => n.Status == "Processing" && n.RecordingId != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, "Failed")
                .SetProperty(n => n.FailureReason, "server_restart")
                .SetProperty(n => n.StructureStatus, (string?)null)
                .SetProperty(n => n.UpdatedAt, now), ct);
        var structurings = await db.Notes
            .Where(n => n.StructureStatus == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.StructureStatus, "failed")
                .SetProperty(n => n.StructureFailure, "server_restart")
                .SetProperty(n => n.UpdatedAt, now), ct);
        return transcriptions + structurings;
    }
```

  (A note that failed transcription never gets structured, so its `StructureStatus` is cleared.)
  - `NoteTranscription.ProcessAsync`: on `no_speech` and on transcription error, also clear `StructureStatus` (set null) in `FailAsync`, so a failed note is never "pending".
  - `TranscriptionWorker.ExecuteAsync`: `await foreach (var job in queue.Reader...)` now yields `NoteJob`; for now `if (job is TranscriptionJob t) await RunJobAsync(t, ...)`; other kinds are handled in Task 6.
  - Tests: `NoteTranscriptionTests.QueuedJobAsync` and `TranscriptionWorkerTests` read `NoteJob` from the reader — cast with `.Should().BeOfType<TranscriptionJob>().Subject` (or `(TranscriptionJob)queued!`).
  - In `NoteTranscriptionTests`, the no-speech and error tests assert `note.StructureStatus.Should().BeNull()`.

- [ ] **Step 4: Run, expect PASS:** `-class "*LiveRecordingsTests*" -class "*NoteTranscriptionTests*" -class "*TranscriptionWorkerTests*" -class "*TranscriptionHubTests*"`.

- [ ] **Step 5: Leave unstaged.**

---

### Task 4: Structurer contract and the structuring job

**Files:**
- Create: `anamnys-aspire.Server/NoteStructuring/INoteStructurer.cs`, `NoteStructuring/NoteStructuring.cs`
- Test: `anamnys-aspire.Tests/NoteStructuring/FakeNoteStructurer.cs`, `NoteStructuring/NoteStructuringTests.cs`

**Interfaces:**
- Consumes: `NoteFormats` (Task 2), `NoteSection`, `Note.StructureStatus/Failure` (Task 1).
- Produces:
  - `public sealed record StructuredSection(string Key, string Content);`
  - `public interface INoteStructurer { Task<IReadOnlyList<StructuredSection>> StructureAsync(string transcript, string format, CancellationToken ct); }`
  - `public sealed class TranscriptTooLongException : Exception;`
  - `public enum StructuringOutcome { Done, Failed, Discarded, Skipped }`
  - `NoteStructuring.ProcessAsync(AnamnysDbContext db, INoteStructurer structurer, Guid noteId, Guid providerId, TimeProvider clock, ILogger logger, CancellationToken ct) → Task<StructuringOutcome>`

Behaviour of `ProcessAsync`:
1. Load the note (`Id`, `ProviderId`) with `RawTranscript`, `Format`, `StructureStatus`. If missing, not `pending`, transcript empty, or format not structurable → set `StructureStatus = null` when it was `pending` and the format is not structurable; return `Skipped`.
2. Call the structurer. On `TranscriptTooLongException` → `failed`/`too_long`; any other exception (except cancellation of `ct`) → `failed`/`model_error`; log type only; return `Failed`.
3. Validate the result: keys equal `NoteFormats.KeysFor(format)` in order; otherwise `failed`/`model_error`.
4. In one transaction (execution strategy as in `LiveRecordings.FinalizeAsync`): if any `NoteSections` row of the note has `EditedAt != null` → set `StructureStatus = "done"`, commit, return `Discarded`. Else delete the note's sections, insert the new ones (`Position` = index, `GeneratedAt` = now, `Content` trimmed), set `StructureStatus = "done"`, `StructureFailure = null`, `UpdatedAt = now`; commit; return `Done`.

- [ ] **Step 1: Fake and failing tests.**

`FakeNoteStructurer.cs`:

```csharp
using Anamnys.Server.NoteStructuring;

namespace Anamnys.Tests.NoteStructuring;

internal sealed class FakeNoteStructurer(Func<string, string, IReadOnlyList<StructuredSection>> structure) : INoteStructurer
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<StructuredSection>> StructureAsync(string transcript, string format, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(structure(transcript, format));
    }
}
```

`NoteStructuringTests.cs` (real Postgres, `[Collection(SharedAppHostCollection.Name)]`), with a helper that creates a `ReadyForReview` note with `RawTranscript = "Paciente relata insônia. Plano: retorno em uma semana."`, `Format = "DAP"`, `StructureStatus = "pending"`, `InputMode = "VoiceLive"` via `PatientSearchSeed` (`AddPatientAsync` + `ExecuteSqlAsync` insert, or `LiveRecordings.FinalizeAsync` followed by an `ExecuteUpdate`). Tests:
  - `Process_Success_WritesSectionsInOrderAndDone`: fake returns `data`/`assessment`/`plan` (assessment `""`); assert 3 rows ordered by `Position` with those keys and contents, `GeneratedAt` = clock now, `EditedAt` null, `StructureStatus == "done"`, outcome `Done`.
  - `Process_Restructure_ReplacesPreviousSections`: run once with DAP, then set `Format = "SOAP"`, `StructureStatus = "pending"`, run with a SOAP fake → exactly the 4 SOAP rows remain.
  - `Process_SectionEdited_DiscardsResult`: insert a section with `EditedAt` set and content `"editado"`; run → outcome `Discarded`, the edited row unchanged, no other rows added, `StructureStatus == "done"`.
  - `Process_StructurerThrows_FailsModelErrorKeepsTranscript`: fake throws `InvalidOperationException` → `failed`/`model_error`, `Status` still `ReadyForReview`, `RawTranscript` unchanged, no sections.
  - `Process_TooLong_FailsTooLong`: fake throws `TranscriptTooLongException` → `failed`/`too_long`.
  - `Process_WrongKeys_FailsModelError`: fake returns `data`, `plan` only → `failed`/`model_error`, no sections.
  - `Process_NotPending_Skips`: `StructureStatus = "done"` → outcome `Skipped`, fake not called.
  - `Process_OtherProvider_Skips`: call with a different `providerId` → `Skipped`, nothing written.

- [ ] **Step 2: Run, expect build FAIL.** `-class "*NoteStructuringTests*"`

- [ ] **Step 3: Implement** `INoteStructurer.cs` (record, interface, exception) and `NoteStructuring.cs`:

```csharp
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.NoteStructuring;

public enum StructuringOutcome { Done, Failed, Discarded, Skipped }

// Turns a note's transcript into sections (design/specs/2026-10-12-note-structuring-design.md §5).
// Never changes Notes.Status: a structuring failure leaves the transcript as the note.
public static class NoteStructuring
{
    public static async Task<StructuringOutcome> ProcessAsync(
        AnamnysDbContext db, INoteStructurer structurer, Guid noteId, Guid providerId,
        TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var note = await db.Notes.AsNoTracking()
            .Where(n => n.Id == noteId && n.ProviderId == providerId)
            .Select(n => new { n.RawTranscript, n.Format, n.StructureStatus })
            .SingleOrDefaultAsync(ct);
        if (note is null || note.StructureStatus != "pending" || string.IsNullOrWhiteSpace(note.RawTranscript))
        {
            return StructuringOutcome.Skipped;
        }
        if (!NoteFormats.IsStructurable(note.Format))
        {
            await SetStatusAsync(db, noteId, null, null, clock.GetUtcNow(), ct);
            return StructuringOutcome.Skipped;
        }

        IReadOnlyList<StructuredSection> sections;
        try
        {
            sections = await structurer.StructureAsync(note.RawTranscript, note.Format, ct);
        }
        catch (TranscriptTooLongException)
        {
            await SetStatusAsync(db, noteId, "failed", "too_long", clock.GetUtcNow(), ct);
            return StructuringOutcome.Failed;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("Structuring failed for note {NoteId}: {ExceptionType}", noteId, e.GetType().Name);
            await SetStatusAsync(db, noteId, "failed", "model_error", clock.GetUtcNow(), ct);
            return StructuringOutcome.Failed;
        }

        if (!sections.Select(s => s.Key).SequenceEqual(NoteFormats.KeysFor(note.Format)))
        {
            logger.LogError("Structuring for note {NoteId} returned unexpected sections.", noteId);
            await SetStatusAsync(db, noteId, "failed", "model_error", clock.GetUtcNow(), ct);
            return StructuringOutcome.Failed;
        }

        var now = clock.GetUtcNow();
        var outcome = StructuringOutcome.Done;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.NoteSections.AnyAsync(s => s.NoteId == noteId && s.EditedAt != null, ct))
            {
                outcome = StructuringOutcome.Discarded;
            }
            else
            {
                await db.NoteSections.Where(s => s.NoteId == noteId).ExecuteDeleteAsync(ct);
                db.NoteSections.AddRange(sections.Select((s, i) => new NoteSection
                {
                    Id = Guid.NewGuid(),
                    NoteId = noteId,
                    Key = s.Key,
                    Content = s.Content.Trim(),
                    Position = i,
                    GeneratedAt = now,
                }));
                await db.SaveChangesAsync(ct);
            }
            await SetStatusAsync(db, noteId, "done", null, now, ct);
            await tx.CommitAsync(ct);
        });
        return outcome;
    }

    private static Task<int> SetStatusAsync(AnamnysDbContext db, Guid noteId, string? status, string? failure, DateTimeOffset now, CancellationToken ct) =>
        db.Notes
            .Where(n => n.Id == noteId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.StructureStatus, status)
                .SetProperty(n => n.StructureFailure, failure)
                .SetProperty(n => n.UpdatedAt, now), ct);
}
```

Reset `outcome` to `Done` at the top of the strategy lambda so a retried attempt does not keep a stale value.

- [ ] **Step 4: Run, expect PASS** (8 tests).

- [ ] **Step 5: Leave unstaged.**

---

### Task 5: LLM model provider, prompt and LLamaSharp structurer

**Files:**
- Create: `anamnys-aspire.Server/Ai/ModelFiles.cs`, `NoteStructuring/NoteStructuringOptions.cs`, `NoteStructuring/LlmModelProvider.cs`, `NoteStructuring/NotePrompt.cs`, `NoteStructuring/LlamaNoteStructurer.cs`
- Modify: `anamnys-aspire.Server/LiveSession/WhisperModelProvider.cs` (use `ModelFiles`), `anamnys-aspire.Server.csproj`, `appsettings.json`, `appsettings.Development.json`
- Test: `anamnys-aspire.Tests/NoteStructuring/LlmModelProviderTests.cs`, `NoteStructuring/LlamaNoteStructurerTests.cs`

**Interfaces:**
- Produces:
  - `Ai.ModelFiles.VerifyAsync(string path, string sha256, CancellationToken)`, `Ai.ModelFiles.EnsureAsync(HttpClient client, Uri url, string path, string sha256, ILogger logger, CancellationToken)` (download to `path + ".download"`, verify, atomic move; delete the temp file on any failure; no-op when `path` exists and verifies).
  - `NoteStructuringOptions { string ModelDirectory; Uri ModelBaseUrl; List<ModelFile> Files; uint ContextSize = 8192; int Threads = 0; int MaxOutputTokens = 2048; float Temperature = 0.1f; string ResolvedDirectory; int ResolvedThreads }` with `ModelFile { string Name; string Sha256 }`. `Threads = 0` means `Math.Max(1, Environment.ProcessorCount - 1)`.
  - `LlmModelProvider(IOptions<NoteStructuringOptions>, IHttpClientFactory, ILogger<LlmModelProvider>)`: `Task<(LLamaWeights Weights, ModelParams Params)> GetAsync(CancellationToken)`. Fails closed when `Files` is empty or any `Sha256` is not 64 chars. Loads from the first file's path (llama.cpp picks up the other split parts in the same directory).
  - `NotePrompt.Build(string transcript, string format) → string` (ChatML for Qwen).
  - `LlamaNoteStructurer(LlmModelProvider models, IOptions<NoteStructuringOptions> options) : INoteStructurer`; internal static `Parse(string json, IReadOnlyList<string> keys) → IReadOnlyList<StructuredSection>`.

- [ ] **Step 1: Packages.** `dotnet add anamnys-aspire.Server package LLamaSharp` and `dotnet add anamnys-aspire.Server package LLamaSharp.Backend.Cpu` (same version for both). Read the installed version's API (README in the NuGet cache / XML docs) for: `ModelParams`, `LLamaWeights.LoadFromFile`, `StatelessExecutor`, `InferenceParams` (`MaxTokens`, `AntiPrompts`, `SamplingPipeline`), `DefaultSamplingPipeline` (`Temperature`, `Grammar`), the `Grammar` type constructor, and how to tokenize with weights (`weights.Tokenize(...)` or a context). Adapt the code below to the real signatures; record adaptations in the report.

- [ ] **Step 2: Model files and configuration.** Fetch the two LFS pointers (read-only, small):
`https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF/raw/main/qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf` and `...-00002-of-00002.gguf`; read each `oid sha256:` value. Download both files (≈4.7 GB total) into `%LOCALAPPDATA%\anamnys\llm\`, compute SHA-256 locally, and confirm they match the pointers. `appsettings.json`:

```json
  "NoteStructuring": {
    "ModelDirectory": "",
    "ModelBaseUrl": "https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF/resolve/main/",
    "Files": [
      { "Name": "qwen2.5-7b-instruct-q4_k_m-00001-of-00002.gguf", "Sha256": "<verified>" },
      { "Name": "qwen2.5-7b-instruct-q4_k_m-00002-of-00002.gguf", "Sha256": "<verified>" }
    ],
    "ContextSize": 8192,
    "Threads": 0,
    "MaxOutputTokens": 2048,
    "Temperature": 0.1
  }
```

`appsettings.Development.json` needs nothing extra (same model). If the repository's actual file names differ, use the real names and record that. If the split load fails in LLamaSharp, stop and report BLOCKED with the error — do not switch to a third-party repository on your own.

- [ ] **Step 3: Failing tests.**

`LlmModelProviderTests.cs` — same structure as `LiveSession/WhisperModelProviderTests.cs` (temp directory, stub handler/factory), covering: empty `Files` → `InvalidOperationException` and no HTTP call; a file with a wrong hash → throws, no final file and no `.download` left; both files served with matching pinned hashes are written to the directory (stop before loading: assert via `ModelFiles.EnsureAsync` calls, or catch the load exception from junk bytes, as the Whisper test does).

`LlamaNoteStructurerTests.cs`:

```csharp
using Anamnys.Server.NoteStructuring;
using FluentAssertions;

namespace Anamnys.Tests.NoteStructuring;

public class LlamaNoteStructurerTests
{
    private static readonly string[] Dap = ["data", "assessment", "plan"];

    [Fact]
    public void Parse_ValidJson_ReturnsSectionsInKeyOrder()
    {
        var sections = LlamaNoteStructurer.Parse("""{"data":"Relata insônia.","assessment":"","plan":"Retorno em uma semana."}""", Dap);

        sections.Select(s => s.Key).Should().Equal(Dap);
        sections[0].Content.Should().Be("Relata insônia.");
        sections[1].Content.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MissingKey_Throws()
    {
        var act = () => LlamaNoteStructurer.Parse("""{"data":"x","plan":"y"}""", Dap);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_TruncatedJson_Throws()
    {
        var act = () => LlamaNoteStructurer.Parse("""{"data":"x","assessment":""", Dap);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Prompt_ContainsRulesSectionsAndTranscript()
    {
        var prompt = NotePrompt.Build("Paciente relata insônia.", "DAP");

        prompt.Should().StartWith("<|im_start|>system");
        prompt.Should().Contain("Não acrescente fatos");
        prompt.Should().Contain("\"assessment\"");
        prompt.Should().Contain("Paciente relata insônia.");
        prompt.Should().EndWith("<|im_start|>assistant\n");
    }

    // Runs only with ANAMNYS_LLM_MODEL pointing at the first GGUF part.
    [Fact]
    public async Task Structure_RealModel_ReturnsFormatKeysWithoutInventedNumbers()
    {
        var model = Environment.GetEnvironmentVariable("ANAMNYS_LLM_MODEL");
        Assert.SkipWhen(model is null, "ANAMNYS_LLM_MODEL is not set.");
        const string transcript =
            "é hoje a paciente chegou relatando que tá dormindo mal faz duas semanas acorda umas três vezes por noite " +
            "ela disse que o trabalho tá pesado avaliação humor rebaixado relacionado ao estresse no trabalho " +
            "plano manter sessões semanais e ela vai fazer o diário do sono";
        var structurer = LlamaNoteStructurer.ForModelFile(model!, new NoteStructuringOptions());

        var sections = await structurer.StructureAsync(transcript, "DAP", TestContext.Current.CancellationToken);

        sections.Select(s => s.Key).Should().Equal(Dap);
        var digits = string.Concat(sections.SelectMany(s => s.Content).Where(char.IsDigit));
        digits.Should().BeEmpty("the transcript has no digits, so any digit was invented");
    }
}
```

`ForModelFile(path, options)` is a test-facing static factory that loads weights from a local path without download (internal is fine; `InternalsVisibleTo` exists).

- [ ] **Step 4: Run, expect build FAIL.** `-class "*LlmModelProviderTests*" -class "*LlamaNoteStructurerTests*"`

- [ ] **Step 5: Implement.**

`Ai/ModelFiles.cs`: move `VerifyAsync` and the download logic from `WhisperModelProvider` into `EnsureAsync` (temp file, verify, `File.Move(..., overwrite: true)`, delete temp in `catch`/`finally` on failure; log only the URL). Change `WhisperModelProvider` to call it; `WhisperModelProvider.VerifyAsync` stays as a forwarder so its existing tests keep passing. Run `-class "*WhisperModelProviderTests*" -class "*WhisperTranscriberTests*"` → PASS.

`NotePrompt.cs`:

```csharp
using System.Text;

namespace Anamnys.Server.NoteStructuring;

// ChatML prompt for Qwen2.5 Instruct. The rules restate the CFP limit: organise, never interpret.
public static class NotePrompt
{
    public static string Build(string transcript, string format)
    {
        var keys = NoteFormats.KeysFor(format);
        var system = new StringBuilder()
            .AppendLine($"Você organiza o ditado de um(a) psicólogo(a) nas seções de uma evolução clínica no formato {format}.")
            .AppendLine("Regras:")
            .AppendLine("1. Use apenas o que foi ditado. Não acrescente fatos, hipóteses, diagnósticos, interpretações ou conclusões.")
            .AppendLine("2. Coloque cada trecho na seção a que pertence:");
        foreach (var key in keys)
        {
            system.AppendLine($"   - \"{key}\": {NoteFormats.Describe(key)}.");
        }
        system
            .AppendLine("3. Remova vícios de fala (é, hum, né, tipo), repetições e hesitações; corrija a pontuação; escreva em português, em tom de prontuário, em terceira pessoa.")
            .AppendLine("4. Preserve nomes, números, doses e datas exatamente como foram ditados.")
            .AppendLine("5. Se nada foi ditado para uma seção, deixe-a vazia (\"\").")
            .Append("6. Responda somente com o objeto JSON com essas chaves, nessa ordem.");
        return $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\nDitado:\n{transcript}<|im_end|>\n<|im_start|>assistant\n";
    }
}
```

`LlamaNoteStructurer.cs` (adapt API names to the installed LLamaSharp):

```csharp
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.NoteStructuring;

public sealed class LlamaNoteStructurer(Func<CancellationToken, Task<(LLamaWeights Weights, ModelParams Params)>> load, NoteStructuringOptions options) : INoteStructurer
{
    public LlamaNoteStructurer(LlmModelProvider models, IOptions<NoteStructuringOptions> options)
        : this(models.GetAsync, options.Value) { }

    internal static LlamaNoteStructurer ForModelFile(string path, NoteStructuringOptions options)
    {
        var parameters = new ModelParams(path) { ContextSize = options.ContextSize, Threads = options.ResolvedThreads };
        var weights = LLamaWeights.LoadFromFile(parameters);
        return new LlamaNoteStructurer(_ => Task.FromResult((weights, parameters)), options);
    }

    public async Task<IReadOnlyList<StructuredSection>> StructureAsync(string transcript, string format, CancellationToken ct)
    {
        var keys = NoteFormats.KeysFor(format);
        var (weights, parameters) = await load(ct);
        var prompt = NotePrompt.Build(transcript, format);
        var promptTokens = weights.Tokenize(prompt, add_bos: true, special: true, Encoding.UTF8).Length;
        if (promptTokens + options.MaxOutputTokens > options.ContextSize)
        {
            throw new TranscriptTooLongException();
        }

        var executor = new StatelessExecutor(weights, parameters);
        var inference = new InferenceParams
        {
            MaxTokens = options.MaxOutputTokens,
            AntiPrompts = ["<|im_end|>"],
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = options.Temperature,
                Grammar = new Grammar(NoteGrammar.For(keys), "root"),
            },
        };
        var output = new StringBuilder();
        await foreach (var piece in executor.InferAsync(prompt, inference, ct))
        {
            output.Append(piece);
        }
        return Parse(output.ToString().Replace("<|im_end|>", "").Trim(), keys);
    }

    internal static IReadOnlyList<StructuredSection> Parse(string json, IReadOnlyList<string> keys)
    {
        Dictionary<string, string>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException e)
        {
            throw new FormatException("Model output is not the expected JSON object.", e);
        }
        if (values is null || values.Count != keys.Count || keys.Any(k => !values.ContainsKey(k)))
        {
            throw new FormatException("Model output does not have the format's sections.");
        }
        return keys.Select(k => new StructuredSection(k, values[k])).ToList();
    }
}
```

`LlmModelProvider.GetAsync(ct)` returns `(LLamaWeights, ModelParams)`: single-flighted with a `SemaphoreSlim` like `WhisperModelProvider`, `ModelFiles.EnsureAsync` for every configured file (named client `nameof(LlmModelProvider)`), then `LLamaWeights.LoadFromFile(new ModelParams(firstFilePath) { ContextSize, Threads })`. Dispose disposes the weights. Log "Downloading/Loading LLM model" with file names only.

- [ ] **Step 6: Run tests** (`-class "*LlmModelProviderTests*" -class "*LlamaNoteStructurerTests*" -class "*WhisperModelProviderTests*"`) → PASS, real-model test skipped. Then run the real-model test once with `ANAMNYS_LLM_MODEL` set to the first part's path; report its duration and output keys (not the content).

- [ ] **Step 7: Leave unstaged.**

---

### Task 6: Worker integration and wiring

**Files:**
- Create: `anamnys-aspire.Server/NoteStructuring/NoteStructuringSetup.cs`
- Modify: `anamnys-aspire.Server/LiveSession/TranscriptionWorker.cs`, `Program.cs`
- Test: `anamnys-aspire.Tests/LiveSession/TranscriptionWorkerTests.cs`

**Interfaces:**
- Consumes: `NoteJob`/`StructureJob` (Task 3), `NoteStructuring.ProcessAsync`, `INoteStructurer` (Task 4), `LlmModelProvider`, `LlamaNoteStructurer` (Task 5).
- Produces:
  - `TranscriptionWorker.RunJobAsync(TranscriptionJob, ...)` — after `NoteReady` is sent, runs structuring (stage `structuring`).
  - `internal static Task TranscriptionWorker.RunStructureAsync(Guid noteId, Guid providerId, IServiceScopeFactory scopes, IHubContext<ProgressHub> progress, TimeProvider clock, ILogger logger, CancellationToken ct)` — sends `NoteProgress(noteId, "structuring", 0)`, runs `NoteStructuring.ProcessAsync` in a fresh scope, then `NoteStructured(noteId)` on `Done`/`Discarded` or `NoteStructureFailed(noteId, reason)` on `Failed` (reason read back from the note). Exceptions are caught, logged by type, and best-effort set `StructureStatus = failed`/`model_error` where still `pending` (scoped by `ProviderId`).
  - `ExecuteAsync` dispatches: `TranscriptionJob` → `RunJobAsync`; `StructureJob` → `RunStructureAsync`.
  - `NoteStructuringSetup.AddNoteStructuring(this WebApplicationBuilder)`: binds options, named HttpClient `nameof(LlmModelProvider)` with 60-minute timeout and `.RemoveAllResilienceHandlers()` (same `#pragma` as `LiveSessionSetup`), `AddSingleton<LlmModelProvider>()`, `AddSingleton<INoteStructurer>(sp => new LlamaNoteStructurer(sp.GetRequiredService<LlmModelProvider>(), sp.GetRequiredService<IOptions<NoteStructuringOptions>>()))`.
  - `Program.cs`: `builder.AddNoteStructuring();` right after `builder.AddLiveSession();`.

- [ ] **Step 1: Failing tests** in `TranscriptionWorkerTests.cs` (the existing fake proxy/hub context records method names; give it an option to not fail):
  - `RunJob_Success_SendsReadyThenStructures`: finalize a recording (note `pending`), run `RunJobAsync` with `FakeTranscriber(_ => "Paciente relata insônia.")` and a `FakeNoteStructurer` returning DAP sections registered as `INoteStructurer`; assert proxy methods in order contain `NoteReady` before `NoteStructured`, note `ReadyForReview`, `StructureStatus == "done"`, 3 sections.
  - `RunJob_NoSpeech_DoesNotStructure`: transcriber returns `""` → `NoteFailed` sent, structurer `Calls == 0`, `StructureStatus` null.
  - `RunStructure_StructurerFails_SendsStructureFailed`: a `ReadyForReview` note with `pending`; structurer throws → `NoteStructureFailed` with `model_error`, note `failed`.

- [ ] **Step 2: Run, expect FAIL.** `-class "*TranscriptionWorkerTests*"`

- [ ] **Step 3: Implement** as described. In `RunJobAsync`, after the `outcome switch`, `if (outcome == TranscriptionOutcome.Ready) await RunStructureAsync(job.NoteId, job.ProviderId, ...)`. Keep the audio `finally` exactly where it is (audio is already destroyed before structuring starts — structuring reads only the stored transcript).

- [ ] **Step 4: Run** `-class "*TranscriptionWorkerTests*" -class "*TranscriptionHubTests*" -class "*NoteStructuringTests*"` → PASS.

- [ ] **Step 5: Leave unstaged.**

---

### Task 7: Note endpoints — sections and restructure

**Files:**
- Create: `anamnys-aspire.Server/Notes/NoteRestructure.cs`
- Modify: `anamnys-aspire.Server/Notes/NoteQueries.cs`, `Notes/NoteEndpoints.cs`, `anamnys-aspire.Server.http`
- Test: `anamnys-aspire.Tests/Notes/NoteEndpointTests.cs`

**Interfaces:**
- Produces:
  - `NoteSectionDto(string Key, string Content, int Position, DateTimeOffset GeneratedAt, DateTimeOffset? EditedAt)`.
  - `NoteDetail` gains `string? StructureStatus`, `string? StructureFailure`, `IReadOnlyList<NoteSectionDto> Sections` (ordered by `Position`).
  - `enum RestructureOutcome { Accepted, NotFound, Conflict, Invalid }`; `NoteRestructure.RequestAsync(AnamnysDbContext db, TranscriptionQueue queue, Guid providerId, Guid noteId, string format, DateTimeOffset now, CancellationToken ct) → Task<RestructureOutcome>`:
    - `Invalid` when `!NoteFormats.IsStructurable(format)`;
    - `NotFound` when the note is not the provider's;
    - one conditional `ExecuteUpdate` setting `Format = format`, `StructureStatus = "pending"`, `StructureFailure = null`, `UpdatedAt = now` WHERE `Id`, `ProviderId`, `Status == "ReadyForReview"`, `SignedAt == null`, `RawTranscript != null && RawTranscript != ""`, `(StructureStatus == null || StructureStatus != "pending")`, and no `NoteSections` row with `EditedAt != null`; 0 rows → `Conflict`; else enqueue `StructureJob` and return `Accepted`.
  - `POST /api/phi/providers/me/notes/{noteId}/restructure` with body `RestructureRequest(string Format)` → 202 / 400 / 404 / 409 (`{ message: "A nota não pode ser reorganizada agora." }`) / 401.

- [ ] **Step 1: Failing tests** in `NoteEndpointTests.cs`:
  - `GetAsync_WithSections_ReturnsThemOrdered` (insert sections via SQL with positions 2,0,1 → returned 0,1,2).
  - `RequestAsync_ReadyNote_AcceptsSetsPendingAndEnqueues`.
  - `RequestAsync_EditedSection_Conflict`.
  - `RequestAsync_PendingStructure_Conflict`.
  - `RequestAsync_SignedNote_Conflict` (set `Status = 'Signed'`, `SignedAt = now()`).
  - `RequestAsync_OtherProvider_NotFound`.
  - `RequestAsync_UnknownFormat_Invalid`.
  - HTTP: `Restructure_WithoutSession_Returns401` (POST through the provider dev server like the existing 401 test).

- [ ] **Step 2: Run, expect FAIL.** `-class "*NoteEndpointTests*"`

- [ ] **Step 3: Implement.** In `NoteQueries.GetAsync`, load the detail then the sections in a second query (`AsNoTracking`, `OrderBy(Position)`), or a projection with a subquery — keep it readable. Map the endpoint:

```csharp
        notes.MapPost("{noteId:guid}/restructure", async (Guid noteId, RestructureRequest request, HttpContext http,
            AnamnysDbContext db, TranscriptionQueue queue, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await NoteRestructure.RequestAsync(db, queue, providerId, noteId, request.Format, DateTimeOffset.UtcNow, ct) switch
            {
                RestructureOutcome.Accepted => Results.Accepted(),
                RestructureOutcome.NotFound => Results.NotFound(),
                RestructureOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["Formato inválido."] }),
                _ => Results.Conflict(new { message = "A nota não pode ser reorganizada agora." }),
            };
        });
```

`.http`: add a "Restructure note" entry in the file's existing style (comment that it needs the provider cookie from a browser session; body `{ "format": "SOAP" }`).

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Leave unstaged.**

---

### Task 8: Frontend — sections, regenerate, format help

**Files:**
- Modify: `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts`, `packages/shared/src/lib/i18n/locales/pt.json`, `apps/provider/src/routes/_app/notes/$noteId.tsx`, `apps/provider/src/components/live-session/PrepareStep.tsx`
- Create: `apps/provider/src/components/notes/NoteSections.tsx`, `apps/provider/src/components/notes/RestructureControl.tsx`

**Interfaces:**
- Consumes: `GET` detail with `structureStatus`, `structureFailure`, `sections[]`; `POST .../restructure` (Task 7).
- Produces: `NoteSectionView { key; content; position; generatedAt; editedAt }`, `StructureStatus = "pending" | "done" | "failed"`, `StructureFailure = "model_error" | "too_long" | "server_restart"`; `NoteDetail` gains `structureStatus`, `structureFailure`, `sections`; `notesApi.restructure(noteId, format): Promise<void>`.

- [ ] **Step 1: Types and API.** Add the types above to `types.ts` (next to `NoteDetail`) and:

```ts
  restructure: async (noteId: string, format: LiveNoteFormat): Promise<void> => {
    await api.post(`/phi/providers/me/notes/${noteId}/restructure`, { format });
  },
```

- [ ] **Step 2: Strings** — in `pt.json` `noteView`, replace `editorSoon` with `"editorSoon": "A edição e a assinatura da nota chegam na próxima entrega."` and add:

```json
    "aiDraft": "Rascunho gerado por IA",
    "emptySection": "Nada ditado para esta seção.",
    "structuring": "Organizando em seções…",
    "structureFailed": {
      "model_error": "Não foi possível organizar a transcrição em seções.",
      "too_long": "A transcrição é longa demais para ser organizada automaticamente; a nota ficou só com a transcrição.",
      "server_restart": "O servidor reiniciou enquanto a transcrição era organizada."
    },
    "retry": "Tentar de novo",
    "reorganize": "Reorganizar",
    "reorganizeConfirm": "As seções atuais serão substituídas. Continuar?",
    "reorganizeFailed": "Não foi possível reorganizar esta nota agora.",
    "format": "Formato",
    "sections": {
      "data": "Dados",
      "assessment": "Avaliação",
      "plan": "Plano",
      "subjective": "Subjetivo",
      "objective": "Objetivo",
      "biological": "Biológico",
      "psychological": "Psicológico",
      "social": "Social"
    }
```

and in `liveSession`: `"formatHelp": "DAP: Dados, Avaliação e Plano. SOAP: Subjetivo, Objetivo, Avaliação e Plano. Biopsicossocial: aspectos biológicos, psicológicos e sociais, e Plano."`.

- [ ] **Step 3: `NoteSections.tsx`** — renders `sections` in `position` order: an `aiDraft` badge on top (when every section is unedited), then per section an `h3` with `t(\`noteView.sections.${key}\`)` and either the content (`whitespace-pre-wrap`) or the `emptySection` text in muted style. Pure presentational; props `{ sections: NoteSectionView[] }`.

- [ ] **Step 4: `RestructureControl.tsx`** — props `{ noteId: string; format: string; onRequested: () => void }`; a `select` of the three formats (labels from `liveSession.formats.*`) defaulting to the current format, and a "Reorganizar" button; on click `window.confirm(t("noteView.reorganizeConfirm"))`, then `notesApi.restructure`; on success `onRequested()` (the page refetches); on error show `reorganizeFailed`. Disabled while the request runs.

- [ ] **Step 5: Note page** (`$noteId.tsx`):
  - `refetchInterval`: 5000 while `status === "Processing"` or `structureStatus === "pending"`.
  - Keep header, `Processing` and `Failed` messages as they are.
  - When `sections.length > 0`: `<NoteSections>`; the transcript goes into `<details>` with `<summary>` = `noteView.transcript` (closed). When there are no sections: transcript shown open as today.
  - `structureStatus === "pending"`: line `noteView.structuring`.
  - `structureStatus === "failed"`: `noteView.structureFailed.<reason>` and a "Tentar de novo" button calling `notesApi.restructure(noteId, note.format)` then `query.refetch()`; hide the button for `too_long` (retrying cannot help).
  - `RestructureControl` shown when `status === "ReadyForReview"`, `structureStatus !== "pending"`, `rawTranscript`, and no section has `editedAt`; on request → `query.refetch()`.
  - Keep the `editorSoon` line at the bottom.
  - Copy class conventions from the existing page and `AppointmentFormModal.tsx`.

- [ ] **Step 6: Prepare step** — below the format select, a `text-body-sm text-onSurfaceVariant` line with `liveSession.formatHelp`.

- [ ] **Step 7: Verify:** `npm run build` and `npm run lint` from `anamnys-aspire/` → both pass.

- [ ] **Step 8: Leave unstaged.**

---

### Task 9: Verification and documentation

**Files:**
- Modify: root `README.md`, `anamnys-aspire/CLAUDE.md`, `anamnys-aspire/design/gaps/2026-10-03-patient-flows-gaps.md`, spec status (`Approved` → `Implemented`)

- [ ] **Step 1: Full suite.** `aspire stop`, then `dotnet run --project anamnys-aspire.Tests`. Expected: all pass (real-model tests skipped without their env vars). Report totals.

- [ ] **Step 2: README** — add, after "Tech stack, at a glance", a short section:

```markdown
## AI models

Both models run inside the server process, on CPU. No audio, transcript or note ever leaves the
server, and no external AI API is called. Each model is downloaded once from Hugging Face on first
use and checked against a SHA-256 pinned in `appsettings*.json`.

| Model | Used for | Licence |
|---|---|---|
| Whisper (`ggml-base` in development, `ggml-medium` in production), via Whisper.net | Transcribing what the provider dictates in "Sessão ao Vivo" | MIT |
| Qwen2.5 7B Instruct, GGUF Q4_K_M, via LLamaSharp | Organising the transcript into the sections of the chosen note format (DAP, SOAP, Biopsychosocial) — it reorganises and cleans what was dictated; it does not interpret or add content | Apache 2.0 |
```

Keep the README's existing tone; if its intro still describes the product generically, leave it.

- [ ] **Step 3: CLAUDE.md** "Backend — present": add `- LLamaSharp (CPU backend) running Qwen2.5 7B Instruct in-process for note structuring; model files downloaded on first use to NoteStructuring:ModelDirectory, verified by the SHA-256 list in NoteStructuring:Files`.

- [ ] **Step 4: Gaps file** (English, short bullets, existing headings): under "Notas clínicas" — delivery 2 done (sections + regenerate); still missing: structuring of typed text (flow 03), transcripts longer than the model context (`too_long`), editing/signing/audit (delivery 3); production memory: Whisper medium + Qwen 7B (~6 GB) + recording buffers.

- [ ] **Step 5: Manual checklist** in the report for the user: dictate ~1 min with content for every DAP section → note page shows transcript, then "Organizando em seções…", then sections (time it); dictate with no assessment → assessment section shows "Nada ditado…"; reorganize to SOAP → confirmation, sections replaced; kill the server while "Organizando…" and restart → "O servidor reiniciou…" + "Tentar de novo"; check server logs contain no transcript or section text.

- [ ] **Step 6: Leave unstaged; report.**
