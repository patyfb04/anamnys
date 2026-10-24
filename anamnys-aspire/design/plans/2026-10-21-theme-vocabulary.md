# Theme Vocabulary (F-02) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A closed mental-health theme vocabulary: AI-proposed and provider-confirmed tags on notes, search by theme, and staff vocabulary maintenance under D-01.

**Architecture:** Schema already has `ThemeVocabularies`, `ThemeTerms`, `ThemeTags`; this adds the seed, audit/succession tables, a `Themes/` server area (vocabulary read, tagging service, suggester on the in-process LLM, admin service), a hook in `NoteStructuringJob`, a `TermId` filter in `NoteList`, a "Temas" block and filters in the provider app, and a vocabulary page in the admin app.

**Tech Stack:** .NET 10 minimal API, EF Core 10 + Npgsql, PostgreSQL 18, LLamaSharp (Qwen2.5 7B, GBNF), React 19 + TanStack Router/Query, Tailwind 4, i18next.

**Spec:** `design/specs/2026-10-21-theme-vocabulary-design.md` (binding; every section number below refers to it).

## Global Constraints

- Branch `feature/theme-vocabulary`. **Never commit, stage, stash or switch branches** — the user reviews first.
- Tests: `dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes (`SharedAppHostCollection`) need the user's Aspire stopped — the controller says per task whether they may run.
- If the user's app is running, build with `--artifacts-path "$TEMP/anamnys-artifacts"`.
- Edit/Write tools only (no `sed -i`); CRLF for new `.cs` files; keep existing endings.
- Provider scoping on every `/api/phi` query; admin mutations require role `owner`/`ops` (never a scope alone); provider realm on `/api/admin` = 401.
- No PHI in URLs, logs or audit values: audit values are term codes; logs never contain note text.
- All AI inference in-process (LLamaSharp); nothing leaves the server.
- Search counts only active tags on signed notes (`Signed`/`Exported`); child counts in parent; successions are not followed.
- Tags are never moved or deleted by vocabulary changes.
- Test names `<Method>_<Conditions>_<Outcome>`, `// Arrange // Act // Assert`.
- Frontend copy in `packages/shared/src/lib/i18n/locales/pt.json`; text actions `hover:underline`, hover backgrounds `hover:bg-pastelLavender`; `npm run build` and `npm run lint` pass.

---

### Task 1: Migration, seed and entities

**Files:** Create `design/migrations/2026-10-21-theme-vocabulary.sql`, `anamnys-aspire.Server/Data/Entities/{ThemeVocabulary,ThemeTerm,ThemeTag,ThemeTermSuccession,ThemeTermChange}.cs` (only those that do not exist), `anamnys-aspire.Tests/Data/ThemeVocabularyMigrationTests.cs`. Modify `anamnys-db-script.sql`, `Data/AnamnysDbContext.cs`, every `*MigrationTests` that compares against `anamnys-db-script.sql` (add the new migration after `2026-10-20-documents-team.sql`, as delivery 2 of F-05 did).

**Produces:** DbSets `ThemeVocabularies`, `ThemeTerms`, `ThemeTags`, `ThemeTermSuccessions`, `ThemeTermChanges`; `ThemeTag.AddedById`, `ThemeTag.RemovedById`.

- [ ] Migration per spec §3, seeding §2 verbatim from `git show 250b0442:backend/db/02_seed_reference.sql` (section "theme vocabulary (F-02)", keep its comments). Idempotent, BEGIN/COMMIT, constraints added only if absent.
- [ ] Same shape in `anamnys-db-script.sql` (tables, columns, index, constraints, FKs) — the seed rows go only in the migration unless the bootstrap script already seeds reference data (check how `ConformityRulesets`/scales are handled and follow that).
- [ ] Entities + mappings (table names as in SQL; `Status`, `Kind` as strings).
- [ ] Tests: shape parity old-path vs bootstrap; seed has 28 top-level + 12 children in `mh-1`, `Active = true`; running twice keeps counts; second active tag for same note+term violates the unique index (`23505`); removed tag without `RemovedById` violates the check (`23514`).

### Task 2: Vocabulary read and tagging service (provider API)

**Files:** Create `anamnys-aspire.Server/Themes/{ThemeVocabularyQuery.cs, NoteThemeService.cs, ThemeEndpoints.cs}`, tests `anamnys-aspire.Tests/Themes/{NoteThemeServiceTests.cs, ThemeEndpointTests.cs}`. Modify `Program.cs` (map on `phi`), `.http`.

**Produces:** `NoteThemeService.ListAsync/AddAsync/RemoveAsync(db, providerId, noteId, …, now, ct)` returning outcome enums (`Ok, NotFound, Archived, TermInactive, AlreadyTagged`); `ThemeVocabularyQuery.ActiveVocabularyAsync(db, ct)` (terms with successorIds); `ThemeTermOption(Guid Id, string Code, string Label, string? ParentLabel)` used by the suggester (Task 3).

- [ ] Routes and responses exactly as spec §5 (`suggest` route is added in Task 3).
- [ ] Row lock on the note (`NoteLocks.LockAsync`) for add/remove; audit via `NoteAudit.Add(db, noteId, providerId, "theme_added"|"theme_removed", "theme", prev, next, now)` in the same transaction; refusals write nothing.
- [ ] Note must belong to the provider (404 otherwise); signed notes allowed; archived patient → 409 `archived`.
- [ ] Tests per spec §10 "Tagging service" and endpoint 401/404/409 shapes.

### Task 3: Theme suggester and structuring hook

**Files:** Create `anamnys-aspire.Server/Themes/{IThemeSuggester.cs, LlamaThemeSuggester.cs, ThemePrompt.cs, ThemeGrammar.cs, ThemeProposals.cs}`, tests `anamnys-aspire.Tests/Themes/{ThemeGrammarTests.cs, ThemePromptTests.cs, FakeThemeSuggester.cs, ThemeProposalsTests.cs}`. Modify `NoteStructuring/NoteStructuringSetup.cs` (register the suggester singleton beside the structurer), `NoteStructuring/NoteStructuringJob.cs` (call `ThemeProposals.ProposeAsync` after a `Done` outcome), `LiveSession/TranscriptionWorker.cs` and any other caller of `ProcessAsync` (pass the suggester), `Themes/ThemeEndpoints.cs` (`POST …/themes/suggest`), test helpers that build the job.

**Consumes:** Task 2 types. **Produces:** `IThemeSuggester.SuggestAsync(string text, IReadOnlyList<ThemeTermOption> terms, CancellationToken ct) : Task<IReadOnlyList<string>>`; `ThemeProposals.ProposeAsync(db, suggester, noteId, providerId, clock, logger, ct)`; `NoteThemeService.SuggestAsync(...)`.

- [ ] Grammar: JSON array, 0–5 items, each item one of the given codes (codes validated `^[a-z][a-z0-9_]*$`); parse rejects anything else and dedupes.
- [ ] Prompt per spec §4 (ChatML like `NotePrompt`, control strings stripped, terms listed as `code: label` / `pai > filho`); fit-to-context by cutting the text, never failing for length.
- [ ] `ThemeProposals` per spec §4 (only unsigned notes; replace only untouched proposals; own transaction; audit `theme_proposed`; failures logged without text and swallowed).
- [ ] Suggest endpoint per §5 (503 `suggest_failed` on model error).
- [ ] Tests per spec §10 "Pure" and "Structuring job with fake suggester". The real model is not run in tests.

### Task 4: Search by theme

**Files:** Modify `anamnys-aspire.Server/Notes/NoteList.cs`, `anamnys-aspire.Tests/Notes/NoteListTests.cs`, `.http`.

- [ ] `NoteSearchRequest` gains `Guid? TermId` (last positional parameter with default null, so existing callers compile). Validation: unknown term id → error `termId` ("Tema inválido.") — check in the endpoint/search with a DB lookup.
- [ ] Filter per spec §5 (signed only, active tags, term or its children, provider-scoped). Combine with the existing filters.
- [ ] Tests per spec §10 "Search".

### Task 5: Admin vocabulary API

**Files:** Create `anamnys-aspire.Server/Themes/{ThemeAdminService.cs, ThemeAdminEndpoints.cs}`, tests `anamnys-aspire.Tests/Themes/{ThemeAdminServiceTests.cs, ThemeAdminEndpointTests.cs}`. Modify `Program.cs` (map on the `admin` group), `.http`.

- [ ] Everything in spec §6. Role check for mutations inside the endpoint group (`RequireClaim("roles", "owner", "ops")` on a sub-group), so `support` gets 403 and other realms stay 401 (follow how the `admin` group is built in `Program.cs`). Staff id from `principal.LocalIdOrNull()`.
- [ ] Tests per spec §10 "Admin" (look at existing owners-realm auth tests for how to get a staff session with a given role; if no helper exists for `support`/`ops`, add one in the test fixtures following the existing pattern).

### Task 6: Provider app

**Files:** Create `apps/provider/src/components/themes/{NoteThemes.tsx, ThemePicker.tsx, SuggestThemesDialog.tsx, ThemeFilter.tsx}`, `packages/shared/src/api/themes.ts`. Modify `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts` (search `termId`), `pt.json`, the note review/view route(s) under `apps/provider/src/routes/_app/notes/`, the record's notes card in `apps/provider/src/routes/_app/patients/$patientId/index.tsx`, `apps/provider/src/routes/_app/note-editor/index.tsx`.

- [ ] Spec §7 exactly (badge "IA", "(aposentado)", picker grouped by parent with active terms only, suggestion dialog with checkboxes, filters in POST body, read-only for archived patients, help line).
- [ ] Query invalidation so chips, filter results and the notes list refresh after add/remove.
- [ ] `npm run build`, `npm run lint`, pt.json valid.

### Task 7: Admin app

**Files:** Create `apps/admin/src/routes/vocabulary.tsx` (and components under `apps/admin/src/components/vocabulary/`), `packages/shared/src/api/adminThemes.ts`. Modify `apps/admin/src/routes/index.tsx` (link for staff), `pt.json`, route tree via `npx vite build` in `apps/admin`.

- [ ] Spec §8; staff-only rendering (reuse the role check pattern from `routes/index.tsx`); dialogs for add, rename, retire, split, merge with the server's 409/400 messages shown; mutating actions hidden for `support`.
- [ ] `npm run build`, `npm run lint`.

### Task 8: Documentation

**Files:** Create `documentation/23-theme-vocabulary.md`. Modify `documentation/00-index.md`, `design/status/2026-10-15-implementation-status.md` (F-02 done; "Next" = F-60; flows 01/09 untouched unless changed), `design/gaps/2026-10-03-patient-flows-gaps.md` (new section `## Temas` with the known limits: excerpt refs pending F-03, no panels yet, suggestion cuts long text, single vocabulary).

- [ ] Chapter per spec §9, written for developers like chapters 21–22.
