# Excerpt References (F-03) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every theme points to the exact passage (or section) of its note; a passage endpoint and a hard-rule helper prepare the analyses.

**Architecture:** Extend `ExcerptRefs`; `Excerpts/` server area (locator, service, endpoint, Require helper); theme suggestion returns quotes; tagging and section saves create/relocate references; provider app highlights passages.

**Tech Stack:** .NET 10, EF Core + Npgsql, LLamaSharp (GBNF), React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-10-24-excerpt-references-design.md` (binding).

## Global Constraints

- Branch `feature/excerpt-refs`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1200 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe runs (`tasklist`); otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run.
- Edit/Write only; CRLF for new `.cs`; keep existing endings.
- Provider scoping on every query; no PHI (note text, quotes, names) in URLs or logs; audit values are codes/ids.
- In-process inference only; keep the suggestion prefix cache, exclusions-after-parse and recovery behaviour.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Data, locator, service, passage endpoint, Require helper

**Files:** Create `design/migrations/2026-10-24-excerpt-refs.sql`, `anamnys-aspire.Server/Excerpts/{ExcerptLocator.cs, ExcerptService.cs, ExcerptEndpoints.cs, ExcerptQueries.cs}`, entity `ExcerptRef` (if missing) + DbContext mapping; tests `anamnys-aspire.Tests/Excerpts/{ExcerptLocatorTests.cs, ExcerptServiceTests.cs, ExcerptEndpointTests.cs, ExcerptRequireArchitectureTests.cs}`, `anamnys-aspire.Tests/Data/ExcerptRefsMigrationTests.cs`. Modify `anamnys-db-script.sql`, `Program.cs`, `.http`, every `*MigrationTests` chain (after `2026-10-23-account-export.sql`).

- [ ] Spec §2 and §3 (locator, service create/relocate/get, endpoint, Require + architecture test), migration with backfill.

### Task 2: Themes use references

**Files:** Modify `Themes/{ThemeGrammar.cs, ThemePrompt.cs, LlamaThemeSuggester.cs, ThemeProposals.cs, NoteThemeService.cs, ThemeEndpoints.cs, IThemeSuggester.cs}`, `Notes/NoteEditing.cs` (relocate on save), `Notes/NoteList.cs` (excerptId/kind/snippet for theme searches), tests in `anamnys-aspire.Tests/Themes/*` and `Notes/NoteListTests.cs`.

- [ ] Spec §3 theme bullets: `{code, quote}` output, references on proposals/accepted suggestions/manual adds (selection or section), relocation on draft section save, "locate" endpoint with audit, list/search fields. Update the fake suggester. Real-model probe per spec §6 (temporary test, removed after; report outputs).

### Task 3: Provider app

**Files:** `apps/provider/src/components/themes/*`, `apps/provider/src/routes/_app/notes/$noteId.tsx`, note sections component(s), record notes card, `/note-editor` list, `packages/shared/src/api/{themes.ts,excerpts.ts}`, `types.ts`, `pt.json`.

- [ ] Spec §4 (highlight + scroll, `?excerpt=` on load, selection → "Marcar tema neste trecho", section picker for manual add, quotes in suggest dialog, snippet in theme results, "Localizar trechos").

### Task 4: Documentation

**Files:** Create `documentation/25-excerpt-references.md`; modify `documentation/00-index.md`, `documentation/23-theme-vocabulary.md` (suggestion output with quotes), `documentation/20-environment-configuration.md` (migration), `design/status/2026-10-15-implementation-status.md` (F-03 done; Next = F-07), `design/gaps/2026-10-03-patient-flows-gaps.md`.
