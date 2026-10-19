# Psychometric Scales (F-01) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The provider records psychometric scores (score + date only) for a patient from the record or from a note, sees each instrument's dated series, and the catalog enforces the CFP/licence regimes with explanations.

**Architecture:** A migration seeds the 15-instrument catalog from the deleted backend and adds soft removal to `ScaleApplications`. Two services — `ScaleCatalog` (regimes, opt-in) and `ScaleRecording` (record, remove, series) — are the only write path; routes live in a new `Scales/ScaleEndpoints.cs`. The provider app gets a shared record modal, a help panel, an "Escalas" card on the record and an "Escalas desta sessão" card on the note page.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, PostgreSQL 18, xUnit v3 + FluentAssertions; React 19, TanStack Query, Tailwind 4, i18next.

**Spec:** `design/specs/2026-10-18-psychometric-scales-design.md`

## Global Constraints

- Store only score and date. Never write `RawAnswers`; never add or show a severity band, interpretive label, cut-off line or good/bad colour.
- Regimes (spec §3): `available` = `Enabled`; `needs_opt_in`/`opted_in` = `SatepsiStatus = 'sem_classificacao'` and `LicenseMode <> 'vedado_digital'` (opted in when a non-revoked `ProviderInstrumentOptIns` row exists); everything else `unavailable`. Recording allowed only for `available` and `opted_in`. No confirmation unlocks `unavailable`.
- Refusals always carry the instrument's `StatusExplanation` verbatim (`instrument_unavailable`).
- Opt-in stores `ExplanationShown` = the instrument's current `StatusExplanation`; one row per provider and instrument.
- Score must satisfy `MinScore <= Score <= MaxScore`; `AppliedAt <= now`.
- Soft removal: `RemovedAt` and `RemovedBy` (both null or both set); removed entries never appear in series or lists.
- Every query provider-scoped (patient `ProviderId`, session `ProviderId`).
- Catalog seed is the old file verbatim: `git show 250b0442e0330d350dedcbe4844fce3dbddd346d:backend/db/02_seed_reference.sql`, lines 48–93 and 111–186 (both `insert into "Instruments" … on conflict ("Code") do nothing;` blocks, comments included).
- Error bodies: `409 { code, message }` with codes `instrument_unavailable`, `opt_in_required`, `archived`, `opt_in_not_allowed`.
- UI strings pt-BR in `pt.json`. Help panel text exactly as spec §6.
- Tests: `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"` from `anamnys-aspire/`; never `dotnet test`; Aspire must be stopped (tests start their own AppHost on the shared volumes). Frontend: `npm run build`, `npm run lint`.
- C# style per CLAUDE.md; tests `<Method>_<Conditions>_<Outcome>` with Arrange/Act/Assert comments. Edit tool, not `sed -i` (it strips CRLF here); preserve line endings.
- **Do not commit** (user rule).

---

## File Structure

- DB: `design/migrations/2026-10-18-scales.sql` (new), `anamnys-db-script.sql`.
- Entities: `Data/Entities/Instrument.cs`, `ScaleApplication.cs`, `ProviderInstrumentOptIn.cs` (new); `Data/AnamnysDbContext.cs`.
- Server: `Scales/ScaleCatalog.cs`, `Scales/ScaleRecording.cs`, `Scales/ScaleEndpoints.cs` (new); `Program.cs` (map); `Notes/NoteQueries.cs` (`SessionId`); `anamnys-aspire.Server.http`.
- Tests: `Data/ScalesMigrationTests.cs`, `Scales/ScaleCatalogTests.cs`, `Scales/ScaleRecordingTests.cs` (new); `Scales/ScaleEndpointTests.cs` (new); older migration tests' old-DB chains.
- Frontend: `packages/shared/src/lib/types.ts`, `packages/shared/src/api/scales.ts` (new), `pt.json`; `apps/provider/src/components/scales/{ScaleRecordModal,ScaleHelpPanel,ScaleChart,ScalesCard,SessionScalesCard}.tsx` (new); `routes/_app/patients/$patientId/index.tsx`, `routes/_app/notes/$noteId.tsx`.
- Docs: `documentation/21-psychometric-scales.md` (new), gaps file, status file, spec status.

---

### Task 1: Migration, catalog seed and entities

**Files:** migration (new), `anamnys-db-script.sql`, three entities, `AnamnysDbContext.cs`; test `Data/ScalesMigrationTests.cs`; old-DB chains in every older migration test that compares against the bootstrap shape (append `2026-10-18-scales.sql`).

**Interfaces — produces:** `ScaleApplications."RemovedAt" timestamptz`, `"RemovedBy" uuid`, `ScaleApplications_Removed_ck`; seeded `Instruments` (15 rows); entities:

```csharp
public class Instrument
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? ItemCount { get; set; }
    public int MinScore { get; set; }
    public int MaxScore { get; set; }
    public string SatepsiStatus { get; set; } = "";
    public DateOnly? SatepsiCheckedOn { get; set; }
    public string LicenseMode { get; set; } = "";
    public bool MayRenderItems { get; set; }
    public string? SourceRef { get; set; }
    public string StatusExplanation { get; set; } = "";
    public bool Enabled { get; set; }
    public string Version { get; set; } = "";
}

public class ScaleApplication
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid InstrumentId { get; set; }
    public int Score { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public Guid? AppliedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public Guid? RemovedBy { get; set; }
}

public class ProviderInstrumentOptIn
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid InstrumentId { get; set; }
    public DateTimeOffset AcknowledgedAt { get; set; }
    public string ExplanationShown { get; set; } = "";
    public DateTimeOffset? RevokedAt { get; set; }
}
```

(`RawAnswers` is deliberately not mapped.) `DbSet`s: `Instruments`, `ScaleApplications`, `ProviderInstrumentOptIns`, following the context's existing table-mapping style.

- [ ] **Step 1: Failing test** `ScalesMigrationTests` (copy the `NotePdfMigrationTests` pattern): old DB = full chain through `2026-10-17-note-pdf.sql`, then `2026-10-18-scales.sql` twice; fresh = bootstrap + new migration once; shapes equal and contain `ScaleApplications.RemovedAt`, `ScaleApplications.RemovedBy`. `Catalog_AfterMigrationTwice_Has15Instruments` (codes: PHQ-9, MOM-D, MOM-A, DASS-21, HAD, ROSENBERG, EPDS, MBI, BSQ, GAD-7, PCL-5, BDI-2, BAI, ORS, SRS; exactly 3 with `Enabled`). `RemovedWithoutRemover_IsRejected` → `ScaleApplications_Removed_ck`.
- [ ] **Step 2: Run → FAIL**.
- [ ] **Step 3: Migration** `design/migrations/2026-10-18-scales.sql` — header in the house style (spec link, "Run after 2026-10-17-note-pdf.sql. Idempotent, and all-or-nothing.", docker exec line), `BEGIN;`, `ALTER TABLE "ScaleApplications" ADD COLUMN IF NOT EXISTS "RemovedAt" timestamp with time zone;`, same for `"RemovedBy" uuid`, `DROP CONSTRAINT IF EXISTS` + `ADD CONSTRAINT "ScaleApplications_Removed_ck" CHECK ((("RemovedAt" IS NULL) = ("RemovedBy" IS NULL)))`, then the two seed blocks copied verbatim from the old file (Global Constraints), `COMMIT;`.
- [ ] **Step 4: Bootstrap script** — add the two columns and the constraint to `CREATE TABLE "ScaleApplications"`, and append the same two seed blocks at the end of the script (after the constraints/FKs, so `Instruments` exists).
- [ ] **Step 5: Entities + DbSets**.
- [ ] **Step 6:** Apply the migration to the dev DB (header command; `aspire start` only if needed, then `aspire stop`) and run → PASS (`-class "*MigrationTests*"`).
- [ ] **Step 7: Stop** — leave uncommitted.

---

### Task 2: Catalog and recording services

**Files:** create `anamnys-aspire.Server/Scales/ScaleCatalog.cs`, `Scales/ScaleRecording.cs`; tests `anamnys-aspire.Tests/Scales/ScaleCatalogTests.cs`, `Scales/ScaleRecordingTests.cs`.

**Interfaces — produces:**

```csharp
public static class ScaleRegime
{
    public const string Available = "available";
    public const string NeedsOptIn = "needs_opt_in";
    public const string OptedIn = "opted_in";
    public const string Unavailable = "unavailable";
}

public sealed record InstrumentItem(Guid Id, string Code, string Name, int MinScore, int MaxScore, string Regime, string Explanation);

public enum OptInOutcome { OptedIn, AlreadyOptedIn, NotFound, NotAllowed }

public static class ScaleCatalog
{
    public static string RegimeFor(Instrument instrument, bool optedIn);
    public static Task<IReadOnlyList<InstrumentItem>> ListAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct);
    public static Task<OptInOutcome> OptInAsync(AnamnysDbContext db, Guid providerId, Guid instrumentId, DateTimeOffset now, CancellationToken ct);
}

public sealed record RecordScaleRequest(Guid? InstrumentId, int? Score, DateTimeOffset? AppliedAt, Guid? SessionId);

public enum RecordOutcome { Recorded, Invalid, NotFound, Archived, Unavailable, OptInRequired }

public sealed record RecordResult(RecordOutcome Outcome, Guid? Id, Dictionary<string, string[]>? Errors, string? Explanation);

public sealed record ScaleEntry(Guid Id, int Score, DateTimeOffset AppliedAt, Guid? SessionId, DateTimeOffset? AppointmentStartsAt);

public sealed record ScaleSeries(Guid InstrumentId, string Code, string Name, int MinScore, int MaxScore, IReadOnlyList<ScaleEntry> Entries);

public static class ScaleRecording
{
    public static Task<RecordResult> RecordAsync(AnamnysDbContext db, Guid providerId, Guid patientId, RecordScaleRequest request, DateTimeOffset now, CancellationToken ct);
    public static Task<bool> RemoveAsync(AnamnysDbContext db, Guid providerId, Guid patientId, Guid applicationId, DateTimeOffset now, CancellationToken ct);
    public static Task<IReadOnlyList<ScaleSeries>?> SeriesAsync(AnamnysDbContext db, Guid providerId, Guid patientId, Guid? sessionId, CancellationToken ct);
}
```

Behaviour:

- `RegimeFor`: `Enabled` → `available`; `SatepsiStatus == "sem_classificacao" && LicenseMode != "vedado_digital"` → `optedIn ? opted_in : needs_opt_in`; otherwise `unavailable`.
- `ListAsync`: all instruments with the provider's non-revoked opt-ins; order by regime rank (available 0, opted_in 1, needs_opt_in 2, unavailable 3) then `Code`; `Explanation = StatusExplanation`.
- `OptInAsync`: instrument missing → `NotFound`; regime not `needs_opt_in`/`opted_in` → `NotAllowed`; existing non-revoked row → `AlreadyOptedIn`; else insert (`AcknowledgedAt = now`, `ExplanationShown = StatusExplanation`) → `OptedIn`. A unique-violation race returns `AlreadyOptedIn`.
- `RecordAsync` order: field validation (`instrumentId` required "Escolha o instrumento."; `score` required "Informe o escore."; `appliedAt` required "Informe a data."; `appliedAt > now` → "A data não pode estar no futuro.") → `Invalid`; patient of provider (else `NotFound`); archived → `Archived`; instrument (else `NotFound`); regime `unavailable` → `Unavailable` with `Explanation`; `needs_opt_in` → `OptInRequired` with `Explanation`; score outside `[MinScore, MaxScore]` → `Invalid` with `score` = $"O escore do {Code} vai de {MinScore} a {MaxScore}."; `sessionId` given and not a session with this `PatientId` and `ProviderId` → `NotFound`; insert (`AppliedBy = providerId`, `CreatedAt = now`) → `Recorded` with id.
- `RemoveAsync`: application of a patient of this provider, not removed → set `RemovedAt = now`, `RemovedBy = providerId` → true; else false.
- `SeriesAsync`: null when the patient is not the provider's; non-removed applications (optionally `SessionId == sessionId`), joined to instruments, grouped by instrument ordered by `Code`, entries by `AppliedAt` then `Id`; `AppointmentStartsAt` = the appointment whose `SessionId` equals the entry's session (null otherwise).

- [ ] **Step 1: Failing tests** (`[Collection(SharedAppHostCollection.Name)]`, `PatientSearchSeed`, fixed `Now`, look instruments up by `Code`):
  - Catalog: `RegimeFor` table test covering all 15 seeded instruments' expected regimes (PHQ-9/MOM-D/MOM-A available; GAD-7/PCL-5 needs_opt_in; the rest unavailable) via `ListAsync`; ordering; opted-in shows `opted_in`; `OptInAsync` → OptedIn then AlreadyOptedIn, stores `ExplanationShown`; on BDI-2/ORS/DASS-21 → NotAllowed; unknown id → NotFound.
  - Recording: available PHQ-9 at 0 and 27 recorded (`AppliedBy`, no `RawAnswers` — assert the column is null via SQL); 28 and -1 → Invalid `score`; future date → Invalid `appliedAt`; GAD-7 without opt-in → OptInRequired with explanation; after opt-in → Recorded; DASS-21 → Unavailable with its explanation, and still Unavailable after inserting an opt-in row directly with SQL; archived patient → Archived; another provider's patient → NotFound; session of another patient → NotFound; own session → Recorded with `SessionId`; Remove → true then false, series excludes it; Series filtered by `sessionId`; `AppointmentStartsAt` filled for a session linked to an appointment.
- [ ] **Step 2: Run → FAIL**.
- [ ] **Step 3: Implement** both services as specified (EF queries, provider-scoped; no transactions needed beyond single `SaveChangesAsync`).
- [ ] **Step 4: Run → PASS** (`-class "*ScaleCatalogTests*"`, `-class "*ScaleRecordingTests*"`).
- [ ] **Step 5: Stop** — leave uncommitted.

---

### Task 3: Routes and note session id

**Files:** create `anamnys-aspire.Server/Scales/ScaleEndpoints.cs`; modify `Program.cs` (`phi.MapScaleEndpoints();` next to `MapNoteEndpoints`), `Notes/NoteQueries.cs` (`NoteDetail` trailing `Guid? SessionId`), `anamnys-aspire.Server.http`; test `anamnys-aspire.Tests/Scales/ScaleEndpointTests.cs`.

**Interfaces — produces** (provider id via `PatientRecordEndpoints.ProviderIdAsync`, 401 when absent):

| Route | Result |
|---|---|
| `GET providers/me/instruments` | `200 InstrumentItem[]` |
| `POST providers/me/instruments/{id:guid}/opt-in` | OptedIn/AlreadyOptedIn → `204`; NotFound → `404`; NotAllowed → `409 { code: "opt_in_not_allowed", message: "Este instrumento não aceita adesão." }` |
| `GET providers/me/patients/{id:guid}/scales?sessionId=` | `200 ScaleSeries[]`; null → `404` |
| `POST providers/me/patients/{id:guid}/scales` (`RecordScaleRequest`) | Recorded → `201 { id }`; Invalid → `ValidationProblem(errors)`; NotFound → `404`; Archived → `409 { code: "archived", message: "Desarquive o paciente para registrar escalas." }`; Unavailable → `409 { code: "instrument_unavailable", message: explanation }`; OptInRequired → `409 { code: "opt_in_required", message: explanation }` |
| `DELETE providers/me/patients/{id:guid}/scales/{applicationId:guid}` | true → `204`; false → `404` |

- [ ] **Step 1: Failing tests** — `ScaleEndpointTests`: 401 without session for `GET /api/phi/providers/me/instruments` and `POST …/scales`; with `BrowserSession` login as the dev provider, `GET /api/phi/providers/me/instruments` returns 200 with 15 items and PHQ-9 `regime == "available"`. Plus a `NoteQueries.GetAsync` test that `SessionId` is returned for a note linked to a session.
- [ ] **Step 2: Run → FAIL**.
- [ ] **Step 3: Implement** the routes and `NoteDetail.SessionId`; `.http` requests in the file's existing style (instruments list, opt-in, record, series, remove).
- [ ] **Step 4: Run → PASS** (`-class "*ScaleEndpointTests*"`, `-class "*NoteEndpointTests*"`, `-class "*ScaleRecordingTests*"`).
- [ ] **Step 5: Stop** — leave uncommitted.

---

### Task 4: Provider app

**Files:** `packages/shared/src/lib/types.ts`, `packages/shared/src/api/scales.ts` (new), `pt.json`; `apps/provider/src/components/scales/*` (new); `routes/_app/patients/$patientId/index.tsx`, `routes/_app/notes/$noteId.tsx`.

**Interfaces:**

```ts
export type ScaleRegime = "available" | "needs_opt_in" | "opted_in" | "unavailable";
export interface InstrumentItem { id: string; code: string; name: string; minScore: number; maxScore: number; regime: ScaleRegime; explanation: string; }
export interface ScaleEntry { id: string; score: number; appliedAt: string; sessionId: string | null; appointmentStartsAt: string | null; }
export interface ScaleSeries { instrumentId: string; code: string; name: string; minScore: number; maxScore: number; entries: ScaleEntry[]; }
export interface RecordScaleRequest { instrumentId: string; score: number; appliedAt: string; sessionId: string | null; }
// NoteDetail gains: sessionId: string | null;

export const scalesApi = {
  instruments(): Promise<InstrumentItem[]>,                       // GET /phi/providers/me/instruments
  optIn(instrumentId: string): Promise<void>,                     // POST /phi/providers/me/instruments/{id}/opt-in
  series(patientId: string, sessionId?: string): Promise<ScaleSeries[]>, // GET .../patients/{id}/scales[?sessionId=]
  record(patientId: string, request: RecordScaleRequest): Promise<string>, // POST → id
  remove(patientId: string, applicationId: string): Promise<void>, // DELETE
};
```

Components:
- `ScaleRecordModal({ patientId, sessionId?, onClose, onRecorded })` — uses `Modal`; loads instruments (`["instruments"]`); picker grouped "Disponíveis" (available + opted_in), "Requer sua adesão" (needs_opt_in), "Indisponíveis" (unavailable, dimmed; selecting shows only the explanation in an info box and no score field); needs_opt_in shows the explanation and the button "Li e quero registrar este instrumento" (calls `optIn`, invalidates `["instruments"]`); score `<input type="number">` with the hint `{min}–{max}`; date `<input type="date">` default today (practice zone), sent as that day at 12:00 local practice time converted to ISO; 400 field errors shown under fields; 409 `message` shown in the modal; on success invalidate `["scales", patientId]` and close.
- `ScaleHelpPanel({ onClose })` — `Modal` titled "Como funcionam as escalas" with the five spec §6 bullets.
- `ScaleChart({ series })` — inline SVG line chart (no new dependency): x = entries in date order, y from `minScore` (bottom) to `maxScore` (top), neutral `text-onSurfaceVariant`/`stroke-primary` colour, points with `<title>` "dd/MM/yyyy: score", axis labels only `minScore` and `maxScore`; no bands, thresholds or semantic colours; renders nothing for zero entries, a single point for one entry. Read the `dataviz` skill before writing it.
- `ScalesCard({ patientId, readOnly })` — `DetailCard` "Escalas" (icon `Activity`), action buttons "?" (help) and, unless `readOnly`, "Registrar escala"; per series: name + code, `ScaleChart`, list (date, score, linked appointment date when present, "Remover" with `ConfirmDialog` "Remover este registro? Ele sai da série, mas fica guardado." unless `readOnly`); empty state "Nenhuma escala registrada."
- `SessionScalesCard({ patientId, sessionId })` — `DetailCard` "Escalas desta sessão": entries of `series(patientId, sessionId)` as "{code}: {score}" lines and "Registrar escala" opening the modal with `sessionId`.
- Record page: add `<ScalesCard patientId={patientId} readOnly={archived} />` to the clinical-lists grid. Note page: render `<SessionScalesCard …/>` in the right column when `note.sessionId` is set.

- [ ] **Step 1:** Types, `scales.ts`, i18n keys (`scales.*`: titles, groups, button labels, help text, confirm text, empty state, errors).
- [ ] **Step 2:** Components and page wiring.
- [ ] **Step 3:** `npm run build` and `npm run lint` → pass.
- [ ] **Step 4: Stop** — leave uncommitted.

---

### Task 5: Documentation and full verification

**Files:** `documentation/21-psychometric-scales.md` (new), gaps file, status file, spec status.

- [ ] **Step 1:** Write `21-psychometric-scales.md` (English, house style of the other chapters): what is stored and never stored; the three regimes with the rule and the instruments in each; the legal grounds (Res. CFP 31/2022 arts. 4, 6 sole paragraph, 11, 12, 13; Res. CFP 09/2024 art. 2 sole paragraph; copyright as a separate barrier) summarised from the old seed's comments; opt-in semantics (once per instrument, stores the text read, not revocable yet); soft removal; enforcement in the application (no trigger) and why; how to add or reclassify an instrument (a new migration with explanation, source and check date; never flip `Enabled` without a SATEPSI listing); the in-app help text.
- [ ] **Step 2:** Gaps file `## Notas clínicas`: no database trigger for scale regimes; opt-in cannot be revoked; multidimensional instruments (F-67); scales recorded from the record are not linked to a session; manual browser check pending.
- [ ] **Step 3:** Status file: F-01 → done; "Next" list moves F-01 off.
- [ ] **Step 4:** Full suite (`aspire stop`; `dotnet run --project anamnys-aspire.Tests`), `npm run build`, `npm run lint`; record totals.
- [ ] **Step 5:** Spec status → `Implemented`.
- [ ] **Step 6: Stop** — report; leave uncommitted.
