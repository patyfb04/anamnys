# Excerpt References (F-03)

**Date:** 2026-10-24
**Status:** Approved for implementation
**Scope:** Backlog F-03. Every derived fact points back to the text that produced it. This delivery
builds the reference model, the passage endpoint, the hard rule helper for analysis queries, and
applies it to themes (F-02). Document facts (F-08) and patient quotes (F-15) will use the same model.

Done when (backlog, adapted until the longitudinal panel exists): clicking a theme on a note, or a
result of the theme search, opens the exact source passage with session and date.

## 1. Decisions (agreed with the user, 2026-10-24)

| Topic | Decision |
|---|---|
| Granularity | Exact excerpt inside a note section when it can be located (`exact`); otherwise the whole section (`section`). |
| AI themes | The model returns, per code, the sentence that justifies it; the server verifies it literally exists in a section (case-insensitive, whitespace-normalized) and stores its offsets. Not found → `section` reference. |
| Suggestion speed (option B, 2026-10-25) | Quotes cost about 3 s more per call, so the on-demand suggest dialog stays codes-only (≈ 4–5 s warm, as before) and shows themes only. Accepting a suggestion, or a manual add without selection, creates a `section` reference at once and queues a background locate for that tag; the highlight appears once it is found. Structuring proposals (nobody waits on them) ask for quotes directly. |
| Manual themes | The provider may select a passage in the note and tag it; without a selection the reference is a section the provider picks. |
| Draft edits | References store the quote; on every section save the quote is located again and offsets updated; if gone, the reference becomes `section`. Frozen at signing. |
| Existing themes | Migration creates `section` references for existing tags (first non-empty section); a per-note "Localizar trechos" action asks the model for quotes on demand. |
| Hard rule | Analysis queries (F-10+) only return items with a reference, enforced by a shared helper and an architecture test. |
| Scales | Their origin is the scale application record itself (date, session); no text reference. Documented, used by F-13. |

## 2. Data

Migration `design/migrations/2026-10-24-excerpt-refs.sql` (idempotent), mirrored in
`anamnys-db-script.sql`. `ExcerptRefs` gains: `"ProviderId" uuid NOT NULL` (FK Providers),
`"SectionKey" text`, `"Quote" text` (≤ 200 chars), `"Kind" text NOT NULL` (`exact` | `section`);
checks: `exact` ⇒ offsets and quote set; `section` ⇒ offsets null; `SectionKey` required when
`SourceType = 'note'`. `DatedAt` = the note's session start, else the note's creation time. Index on
(`ProviderId`, `SourceType`, `SourceId`). Backfill: for every active `ThemeTags` row without a reference,
insert a `section` reference to the first non-empty section of its note and link it.

## 3. Server

- `Excerpts/ExcerptLocator.Locate(sectionText, quote)` → offsets or null: case-insensitive, collapses
  whitespace runs, returns offsets in the original text. Pure.
- `Excerpts/ExcerptService`: create for a note section (exact or section), relocate all references of a
  section after a save (draft only), get passage (provider-scoped).
- `GET /api/phi/providers/me/excerpts/{id}` → `{ id, kind, noteId, patientId, sessionStartsAt,
  datedAt, sectionKey, sectionTitle, sectionText, start?, end? }`; 401 first; 404 for others.
- `Excerpts.Require(IQueryable<T>, selector)` (or an equivalent extension) used by analysis queries to
  exclude items whose `ExcerptRefId` is null; architecture test: every class in the analysis namespace
  (`Analyses/`, to be created by F-10) that queries `ThemeTags`, `PatientQuotes` or `ExtractedFacts`
  calls it (a reflection/source scan test is acceptable; write it so it passes now with no analysis
  classes and fails when one skips the helper).
- Theme suggestion (`ThemeGrammar`, `ThemePrompt`, `LlamaThemeSuggester`, `ThemeProposals`,
  `NoteThemeService.SuggestAsync`) has two modes on the same cached prefix (the mode's instruction goes
  after the note text): **codes only** (the previous array of codes; the suggest endpoint, which returns
  no quote) and **with quotes**, an array of `{"code": <listed code>, "quote": <string ≤ 200 chars>}`,
  still ≤ MaxItems (+ the excluded allowance), used by structuring proposals and by locate. Parse/recover
  accordingly; keep the prefix cache, exclusions and recovery semantics. The with-quotes grammar has
  fixed spacing, and the fixed JSON text is fed without sampling. Proposals create the reference (exact
  when located).
- `NoteThemeService.AddAsync` accepts optional `{ sectionKey, start, end }` (a selection) or
  `{ sectionKey }`; validates bounds against the current section text; stores the selected text as the
  quote. Without either → reference to the first non-empty section. An add that ends with a `section`
  reference (accepted suggestion, add without selection) queues a background locate for that tag
  (`ThemeLocateQueue` + `ThemeLocateWorker`, in-memory, one model run at a time through the suggester's
  gate). The tag's current section is tried first. A tag removed, or already exact, by then is skipped;
  signed notes are located too (only the reference changes). Failures are logged by note id and
  exception type. The POST response carries `excerptKind`.
- Section saves (`NoteEditing`): relocate references of that section in the same transaction (draft
  only; signed notes are locked already).
- "Localizar trechos": `POST providers/me/notes/{noteId}/themes/locate` queues the same background locate
  for all of the note's active tags without an exact reference and answers `202 { queued: true }` (404
  for another provider's note, 409 for an archived patient). The worker runs the suggester restricted to
  those codes, asking only for quotes, and upgrades `section` references to `exact` when located. It
  writes audit `theme_excerpt_located` (term code) per upgrade.
- Theme list (`GET …/notes/{id}/themes`) and note search by theme return `excerptId`, `excerptKind`;
  the search also returns a short snippet (≤ 160 chars around the quote) for theme-filtered results.
- No PHI in URLs (ids only) or logs.

## 4. Provider app

- Note page: clicking a theme chip highlights its passage (exact: lavender underline of the range;
  section: border around the section) and scrolls to it; `?excerpt=<id>` on the note URL does the same
  on load. Text selection in a section (draft or signed) shows "Marcar tema neste trecho" opening the
  theme picker; without selection, "+ Adicionar tema" asks which section.
- Suggest dialog: themes only (no quote; codes-only mode keeps it at about 5 s). After an accept or a
  manual add, the chip first points at its section; the theme list is polled briefly (as for
  proposals) and the exact highlight appears once the background locate finds the passage.
- Theme search results (record notes card, /note-editor): a snippet line; clicking opens the note with
  `?excerpt=`.
- "Localizar trechos" button in the Temas block when any tag has a `section` reference; it queues the
  locate (202) and the list is refreshed the same way.

## 5. Documentation

Chapter `documentation/25-excerpt-references.md`; index; status (F-03 done); gaps; chapter 23 updated
(suggestion output with quotes); Notion.

## 6. Testing

- Pure: locator (exact, case, whitespace, absent, multiple occurrences → first), grammar admits only
  `{code, quote}` objects with listed codes and bounded quote length, parse/recover.
- Service (real DB): proposal with locatable quote → exact; unlocatable → section; manual with
  selection → exact with quote; without → section; draft edit keeps or degrades references; signed note
  unchanged; passage endpoint scoping (other provider 404); migration backfill and idempotency;
  architecture test for `Require`.
- Real-model probe (manual, `ANAMNYS_LLM_MODEL`): quotes returned for the sample note and located.
