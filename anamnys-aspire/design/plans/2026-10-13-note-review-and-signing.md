# Live Session — Note Review and Signing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The provider edits note sections one at a time, writes sections by hand when the AI produced none, and signs (locks) the note; every note mutation appends an `AuditEntries` row in the same transaction, and every mutation serialises on a row lock of the note.

**Architecture:** A `Notes/NoteEditing` service holds the three mutations (edit section, create blank sections, sign). Each runs in one transaction: `NoteLocks.LockAsync` (`SELECT … FOR UPDATE` on the note), condition checks, the write, and `NoteAudit.Add`. The existing restructure request and structuring write take the same lock; structuring also audits `ai_generated`. Section concurrency uses a `Revision` counter. The note page gains per-section editing, "Escrever manualmente" and an "Assinatura" card.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, PostgreSQL 18, xUnit v3 + FluentAssertions; React 19, TanStack Router/Query, Tailwind 4, i18next.

**Spec:** `design/specs/2026-10-13-note-review-and-signing-design.md`

## Global Constraints

- Every note mutation appends an `AuditEntries` row in the same transaction (CLAUDE.md hard requirement). Actions: `ai_generated` (per section, `ActorId` null), `section_edited`, `sections_created`, `signed` (`ActorId` = provider id).
- Every note mutation first locks the note row (`SELECT 1 FROM "Notes" WHERE "Id" = @id FOR UPDATE`) inside its transaction, then checks conditions.
- A signed note is never changed by any route. Editing is refused while `StructureStatus = 'pending'`.
- Signing requires `Status = 'ReadyForReview'`, structuring not pending, ≥ 1 section, provider CRP present. `SignedByCrp` = `"{CrpNumber}/{CrpRegion}"`.
- Section content limit: 20,000 characters (trimmed).
- Every query provider-scoped. Logs never carry section content.
- UI strings in Portuguese in `pt.json`.
- Tests: `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"` from `anamnys-aspire/`; never `dotnet test`; `aspire stop` before building (if the user's app is running and you must not stop it, build with `--artifacts-path "$TEMP/anamnys-artifacts"`). Frontend: `npm run build` and `npm run lint` from `anamnys-aspire/`.
- Apply the migration to the dev DB before Postgres tests.
- **Do not commit** (user rule); preserve each file's line endings.

---

## File Structure

- Database: `design/migrations/2026-10-13-note-review.sql`, `anamnys-db-script.sql`.
- Server: `Data/Entities/Note.cs` (+`SignedByCrp`), `Data/Entities/NoteSection.cs` (+`Revision`), `Data/Entities/AuditEntry.cs` (new), `Data/AnamnysDbContext.cs`; `Notes/NoteLocks.cs`, `Notes/NoteAudit.cs`, `Notes/NoteEditing.cs` (new); `Notes/NoteEndpoints.cs`, `Notes/NoteQueries.cs`, `Notes/NoteRestructure.cs`; `NoteStructuring/NoteStructuringJob.cs`; `anamnys-aspire.Server.http`.
- Tests: `Data/NoteReviewMigrationTests.cs`, `Notes/NoteEditingTests.cs`, `Notes/NoteSigningTests.cs`; updates to `NoteStructuring/NoteStructuringTests.cs`, `Notes/NoteEndpointTests.cs`, `Data/LiveSessionMigrationTests.cs`, `Data/NoteStructuringMigrationTests.cs` (old-DB chains).
- Frontend: `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts`, `pt.json`; `apps/provider/src/components/notes/NoteSections.tsx`, `apps/provider/src/components/notes/SignCard.tsx` (new), `apps/provider/src/routes/_app/notes/$noteId.tsx`.
- Docs: gaps file.

---

### Task 1: Schema, migration and entities

**Files:** migration (new), `anamnys-db-script.sql`, `Note.cs`, `NoteSection.cs`, `AuditEntry.cs` (new), `AnamnysDbContext.cs`; test `Data/NoteReviewMigrationTests.cs`; old-DB chains in `LiveSessionMigrationTests.cs` and `NoteStructuringMigrationTests.cs`.

**Interfaces — produces:** `Notes."SignedByCrp" text` + `Notes_SignedByCrp_ck`; `NoteSections."Revision" integer DEFAULT 0 NOT NULL`; `Note.SignedByCrp` (`string?`); `NoteSection.Revision` (`int`); `AuditEntry { Guid Id; Guid? NoteId; Guid? DocumentId; Guid? ActorId; string Action; string? FieldChanged; string? PreviousValue; string? NewValue; DateTimeOffset At }`; `DbSet<AuditEntry> AuditEntries`.

- [ ] **Step 1: Failing test** `Data/NoteReviewMigrationTests.cs`, copy of `NoteStructuringMigrationTests`: old DB runs all earlier migrations through `2026-10-12-note-structuring.sql`, then `2026-10-13-note-review.sql` twice; fresh = bootstrap + new migration once; tables `["Notes", "NoteSections", "AuditEntries"]`; assert shapes equal and contain `"Notes.SignedByCrp"`, `"NoteSections.Revision"`. Second test: inserting a note with `Status = 'Signed'`, `SignedAt = now()`, no `SignedByCrp` fails with constraint `Notes_SignedByCrp_ck`.
- [ ] **Step 2: Run → FAIL** (`-class "*NoteReviewMigrationTests*"`).
- [ ] **Step 3: Migration**

```sql
-- Note review and signing (design/specs/2026-10-13-note-review-and-signing-design.md §3). Run after
-- 2026-10-12-note-structuring.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-13-note-review.sql

BEGIN;

ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "SignedByCrp" text;
-- Notes signed before this change (seed data) have no recorded CRP.
UPDATE "Notes" SET "SignedByCrp" = '—' WHERE "SignedAt" IS NOT NULL AND "SignedByCrp" IS NULL;
ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_SignedByCrp_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_SignedByCrp_ck" CHECK ((("SignedAt" IS NULL) = ("SignedByCrp" IS NULL)));

ALTER TABLE "NoteSections" ADD COLUMN IF NOT EXISTS "Revision" integer DEFAULT 0 NOT NULL;

COMMIT;
```

- [ ] **Step 4: Bootstrap mirror** — add `"SignedByCrp" text,` after `"StructureFailure"` and the check to `CREATE TABLE "Notes"`; add `"Revision" integer DEFAULT 0 NOT NULL,` to `CREATE TABLE "NoteSections"` after `"EditedAt"`.
- [ ] **Step 5: Old-DB chains** — in `LiveSessionMigrationTests` and `NoteStructuringMigrationTests`, append `RunMigrationAsync(db, "2026-10-13-note-review.sql")` to the old DB so their shape comparison (which includes `Notes`) stays valid. Check whether `PatientSearchSeed.AddNoteAsync` inserts signed notes — it sets `SignedAt` for `Signed`/`Exported`; update its SQL to also set `"SignedByCrp" = CASE WHEN … THEN '000000/00' END`.
- [ ] **Step 6: Apply to dev DB**; run → PASS; regression `-class "*Migration*" -class "*Patient*" -class "*NoteEndpointTests*"` → PASS.
- [ ] **Step 7: Entities** — add props; `AuditEntry.cs` mapped `ToTable("AuditEntries")`, `HasKey(x => x.Id)`.
- [ ] **Step 8: Leave unstaged.**

---

### Task 2: Lock, audit helper, edit and blank sections

**Files:** `Notes/NoteLocks.cs`, `Notes/NoteAudit.cs`, `Notes/NoteEditing.cs` (new); test `Notes/NoteEditingTests.cs`.

**Interfaces — produces:**
- `NoteLocks.LockAsync(AnamnysDbContext db, Guid noteId, CancellationToken ct) → Task` (must run inside a transaction).
- `NoteAudit.Add(AnamnysDbContext db, Guid noteId, Guid? actorId, string action, string? field, string? previous, string? next, DateTimeOffset at)`.
- `enum EditOutcome { Saved, NotFound, Locked, Stale, Invalid }`; `record EditResult(EditOutcome Outcome, int Revision)`.
- `NoteEditing.EditSectionAsync(db, providerId, noteId, key, content, expectedRevision, now, ct) → Task<EditResult>`.
- `enum BlankOutcome { Created, NotFound, Locked, AlreadyHasSections }`; `NoteEditing.CreateBlankSectionsAsync(db, providerId, noteId, now, ct) → Task<BlankOutcome>`.
- `NoteEditing.MaxContentLength = 20_000`.

Rules (both run under `db.Database.CreateExecutionStrategy().ExecuteAsync` with a transaction, `ChangeTracker.Clear()` first, then `NoteLocks.LockAsync`):
- Edit: note must exist for `providerId` (else `NotFound`); `Status == "ReadyForReview" && SignedAt == null && StructureStatus != "pending"` (else `Locked`); content length > 20,000 after trim → `Invalid` (check before the transaction); section `(NoteId, Key)` must exist (else `NotFound`) with `Revision == expectedRevision` (else `Stale`, return current revision); if trimmed content equals current → `Saved` with current revision, no audit, no write; else set `Content`, `EditedAt = now`, `Revision + 1`, `Notes.UpdatedAt = now`, `NoteAudit.Add(…, "section_edited", key, previous, next)`; save + commit.
- Blank: `NotFound` / `Locked` (same as edit, plus `!NoteFormats.IsStructurable(format)` → `Locked`) / `AlreadyHasSections`; insert `NoteFormats.KeysFor(format)` in order with `Content = ""`, `GeneratedAt = now`, `Revision = 0`; set `StructureStatus = "done"`, `StructureFailure = null` (a failed structuring is now superseded by manual writing); audit `sections_created` with `NewValue = string.Join(",", keys)`.

`NoteLocks.LockAsync`:

```csharp
public static Task LockAsync(AnamnysDbContext db, Guid noteId, CancellationToken ct) =>
    db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Notes\" WHERE \"Id\" = {noteId} FOR UPDATE", ct);
```

- [ ] **Step 1: Failing tests** `Notes/NoteEditingTests.cs` (Postgres, shared fixture). Helper creates a `ReadyForReview` DAP note with three sections via SQL (`Revision` 0, contents "a", "", "c"). Tests:
  - `Edit_Valid_SavesContentRevisionEditedAtAndAudit` (revision 1, `EditedAt` = now, one `section_edited` row with field, previous "a", new value, `ActorId` = provider).
  - `Edit_SameContent_NoAuditNoRevisionChange`.
  - `Edit_StaleRevision_Stale` (expected 5 → `Stale`, nothing written).
  - `Edit_PendingStructure_Locked`; `Edit_SignedNote_Locked` (set `Status='Signed'`, `SignedAt`, `SignedByCrp`).
  - `Edit_OtherProvider_NotFound`; `Edit_UnknownKey_NotFound`; `Edit_TooLong_Invalid`.
  - `Blank_NoSections_CreatesFormatKeysInOrderAndAudit` (note with `StructureStatus='failed'` and no sections → keys data/assessment/plan, `StructureStatus='done'`, one `sections_created` row).
  - `Blank_HasSections_AlreadyHasSections`; `Blank_Pending_Locked`.
- [ ] **Step 2: Run → FAIL**; **Step 3: Implement**; **Step 4: Run → PASS** (`-class "*NoteEditingTests*"`).
- [ ] **Step 5: Leave unstaged.**

---

### Task 3: Signing

**Files:** `Notes/NoteEditing.cs`; test `Notes/NoteSigningTests.cs`.

**Interfaces — produces:** `enum SignOutcome { Signed, NotFound, Locked, NoSections, MissingCrp }`; `NoteEditing.SignAsync(db, providerId, noteId, now, ct) → Task<SignOutcome>`.

Rules (transaction + lock as in Task 2): `NotFound`; `Locked` unless `ReadyForReview`, unsigned, not pending; `NoSections` when the note has no sections; read `Providers` row (`CrpNumber`, `CrpRegion`) → `MissingCrp` if null; set `Status = "Signed"`, `SignedAt = now`, `SignedByCrp = $"{number}/{region}"`, `UpdatedAt = now`; audit `signed` with `NewValue` = CRP.

- [ ] **Step 1: Failing tests:** `Sign_Valid_SignsAndAudits` (seed provider has a CRP — set it via SQL), `Sign_MissingCrp`, `Sign_NoSections`, `Sign_Pending_Locked`, `Sign_Twice_SecondLocked`, `Sign_OtherProvider_NotFound`, and `AfterSign_EditBlankRestructure_AllRefused` (edit → `Locked`, blank → `Locked`, `NoteRestructure.RequestAsync` → `Conflict`).
- [ ] **Step 2–4:** RED → implement → GREEN (`-class "*NoteSigningTests*"`).
- [ ] **Step 5: Leave unstaged.**

---

### Task 4: Lock and audit in restructure and structuring

**Files:** `Notes/NoteRestructure.cs`, `NoteStructuring/NoteStructuringJob.cs`; tests `NoteStructuring/NoteStructuringTests.cs`, `Notes/NoteEndpointTests.cs`.

- `NoteRestructure.RequestAsync`: wrap the existing conditional update in a transaction (execution strategy) that first calls `NoteLocks.LockAsync`; behaviour otherwise unchanged; enqueue after commit.
- `NoteStructuringJob.ProcessAsync`: inside the existing write transaction, call `NoteLocks.LockAsync` before the edited-section check; for each inserted section `NoteAudit.Add(db, noteId, null, "ai_generated", key, null, content, now)`. No audit on `Discarded` or failures.
- [ ] **Step 1: Failing test** in `NoteStructuringTests`: `Process_Success_AuditsEachSectionAsAiGenerated` (3 rows, `ActorId` null, `NewValue` = section content).
- [ ] **Step 2–4:** RED → implement → GREEN (`-class "*NoteStructuringTests*" -class "*NoteEndpointTests*" -class "*TranscriptionWorkerTests*"`).
- [ ] **Step 5: Leave unstaged.**

---

### Task 5: Endpoints and note detail

**Files:** `Notes/NoteEndpoints.cs`, `Notes/NoteQueries.cs`, `anamnys-aspire.Server.http`; test `Notes/NoteEndpointTests.cs`.

**Interfaces — produces:**
- `NoteSectionDto` gains `int Revision`; `NoteDetail` gains `DateTimeOffset? SignedAt`, `string? SignedByCrp`, `bool ProviderHasCrp`.
- `PUT {noteId}/sections/{key}` body `EditSectionRequest(string Content, int ExpectedRevision)` → 200 `{ revision }` / 400 / 404 / 409 `{ message, reason: "locked"|"stale" }`.
- `POST {noteId}/sections/blank` → 201 / 404 / 409 `{ message }`.
- `POST {noteId}/sign` → 200 / 404 / 409 `{ message, reason: "locked"|"no_sections" }` / 422 `{ message, reason: "missing_crp" }`.
- Portuguese messages: locked "A nota está sendo organizada ou já foi assinada."; stale "Esta seção foi alterada em outra janela."; no_sections "A nota não tem seções para assinar."; missing_crp "Cadastre seu CRP no perfil para assinar.".
- [ ] **Step 1: Failing tests:** `GetAsync` returns `revision`, `signedAt`, `signedByCrp`, `providerHasCrp`; HTTP 401 without session for the three new routes (through the provider dev server, like the existing 401 test).
- [ ] **Step 2–4:** RED → implement → GREEN (`-class "*NoteEndpointTests*"`).
- [ ] **Step 5: `.http`** entries in the file's style (cookie comment). Leave unstaged.

---

### Task 6: Note page — edit, write manually, sign

**Files:** `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts`, `pt.json`, `apps/provider/src/components/notes/NoteSections.tsx`, `apps/provider/src/components/notes/SignCard.tsx` (new), `apps/provider/src/routes/_app/notes/$noteId.tsx`.

**Interfaces — consumes:** Task 5 contract.
- Types: `NoteSectionView.revision: number`; `NoteDetail.signedAt: string | null`, `signedByCrp: string | null`, `providerHasCrp: boolean`.
- API: `notesApi.editSection(noteId, key, content, expectedRevision): Promise<{ revision: number }>`, `notesApi.createBlankSections(noteId)`, `notesApi.sign(noteId)`. Errors: let axios reject; components read `error.response?.data?.reason`.
- `pt.json` `noteView`: `edit` "Editar", `save` "Salvar", `cancel` "Cancelar", `editedByYou` "Editado por você", `inReview` "Em revisão", `signed` "Assinada", `writeManually` "Escrever manualmente", `writeManuallyHint` "A IA não organizou esta nota. Você pode escrever as seções à mão.", `editErrors` { `locked`, `stale`, `generic` "Não foi possível salvar." }, `signTitle` "Assinatura", `signButton` "Assinar e travar nota", `signConfirm` "Depois de assinada, a nota não pode ser alterada. Assinar agora?", `signNeedsCrp` "Cadastre seu CRP no perfil para assinar.", `goToProfile` "Ir para o perfil", `signedAt` "Assinada em {{date}} · CRP {{crp}}", `signErrors` { `locked`, `no_sections`, `missing_crp`, `generic` }.
- `NoteSections` props: `sections`, `editable: boolean`, `noteId`, `onSaved()`. Each block: header row with title + "Editado por você" chip when `editedAt` + "Editar" button when editable. Edit mode: textarea (auto-grow, `maxLength` 20000) + Salvar/Cancelar; on save call `editSection` with the section's `revision`; on 409 show the reason message; on success `onSaved()` (page refetches).
- `SignCard` props: `note`, `onSigned()`. States per spec §5. Link to `/account/profile`.
- Page: `editable = status === "ReadyForReview" && !signedAt && structureStatus !== "pending"`; `RestructureControl` only when editable and the existing conditions hold; "Escrever manualmente" button in the "Evolução" card when editable and no sections (calls `createBlankSections`, then refetch); header badge: signed → "Assinada" (mint), any section edited or no AI sections → "Em revisão", else "Rascunho gerado por IA"; side column order: `SignCard`, then `RestructureControl`.
- [ ] **Step 1:** implement; **Step 2:** `npm run build` and `npm run lint` → pass. Leave unstaged.

---

### Task 7: Verification and docs

- [ ] Full suite (`aspire stop`, or `--artifacts-path` if the user's app must keep running): report totals.
- [ ] Gaps file, "Notas clínicas": delivery 3 done (edit/sign/audit); still missing — audit history view, correction of a signed note through a new linked version, PDF export (needs private object storage + 1 h presigned URLs); `SignedByCrp` is format-only (no CRP registry check). Remove/adjust the delivery-2 gap about the missing lock (now closed).
- [ ] Spec status → Implemented.
- [ ] Manual checklist in the report: edit and save a section; open the note in two tabs and save in both (second → stale message); "Escrever manualmente" on a note whose structuring failed; sign with no CRP (message + profile link), add CRP, sign; after signing no Editar/Reorganizar; DB shows the audit rows.
