# Chapter 25: Excerpt References (F-03)

How a derived fact (today, a theme tag) points back to the text that produced it, how the passage
is shown, how it is located in the background, and the rule that analyses must use it. Last checked
against the code on 2026-10-25 (branch `feature/excerpt-refs`). Design:
`design/specs/2026-10-24-excerpt-references-design.md`.

The code is small. What a future change has to respect is the hard rule (section 6), that a
reference stores the quote and not only offsets (section 4), and that locating is slow and runs in
the background (section 5).

---

## 1. Purpose

Every derived fact must open the exact source passage with its session and date. F-03 builds the
reference model, the passage endpoint, the helper analyses must use, and applies it to themes (F-02,
chapter 23). Document facts (F-08) and patient quotes (F-15) will use the same model.

Done when (backlog, adapted until the longitudinal panel exists): clicking a theme on a note, or a
result of the theme search, opens the exact source passage with session and date.

Scales (F-01) do not use it: their origin is the scale application record itself (date, session).

---

## 2. Data model

Migration `design/migrations/2026-10-24-excerpt-refs.sql` (idempotent, one transaction, mirrored in
`anamnys-db-script.sql`). `ExcerptRefs` has:

- `ProviderId` (not null, foreign key to `Providers`, cascade), `SourceType`, `SourceId` (for notes:
  `note` and the note id), `SectionKey`, `Quote` (at most 200 characters), `OffsetStart`, `OffsetEnd`,
  `Kind` (`exact` or `section`), `DatedAt`.
- Checks: `exact` needs both offsets and a quote; `section` needs both offsets null; `SectionKey` is
  required when `SourceType = 'note'`; the quote is at most 200 characters.
- Index `ExcerptRefs_Provider_Source_idx` on (`ProviderId`, `SourceType`, `SourceId`).
- `DatedAt` is the note's session `ActualStart` (when the session happened), else `ScheduledStart`,
  else the note's creation time.

`ThemeTags.ExcerptRefId` points at the reference (foreign key, set null on delete).

**What the migration did to existing data:**

- References that predate this model carried no provider, section or quote and nothing created them.
  They were **deleted once**, when the columns were first added (a rerun does not repeat it). The tags
  that pointed at them were re-linked by the backfill below.
- Backfill: every active tag without a reference got a `section` reference to the first non-empty
  section of its note (114 tags on the dev database). A note with no text keeps its tag without a
  reference.

---

## 3. Two kinds of reference

| Kind | Meaning | UI |
|---|---|---|
| `exact` | Offsets into the section text, plus the quote | Lavender highlight of the range |
| `section` | The whole section | Border around the section |

A reference is `exact` when the provider selected a passage, or when the quote was found in the text
(`ExcerptLocator`). Otherwise it is `section`, which is always valid: the tag still opens the note at
a section.

`ExcerptLocator.Locate(sectionText, quote)` is pure: case-insensitive, whitespace runs count as one
space, offsets are in the original text, the first occurrence wins. `ExcerptService` also accepts a
quote the model copied with the section's `Title: ` prefix (it strips it when the plain quote is not
found) and refuses a range over 200 characters.

---

## 4. Lifecycle

`ExcerptService`:

- **Create** (`AddAsync`): for a provider's note section. With a selection (`start`, `end`) the bounds
  are checked against the current section text and the selected text becomes the quote (`exact`;
  invalid bounds, an empty or over-200 range is `InvalidSelection`). With a model quote, it is located:
  `exact` when found, else `section`. Without a section the first non-empty section is used; a quote
  without a section picks the first section it is found in.
- **Draft edit** (`RelocateSectionAsync`, called by `NoteEditing` in the same transaction as the section
  save): every reference of that section that has a quote is located again in the new text. Found:
  `exact` with the new offsets. Gone: `section`, but **the quote is kept**, so a later edit that brings
  the text back restores the exact range. This is why a reference stores the quote and not only offsets.
- **Signed notes:** the text is frozen, so nothing is relocated. A signed note can still gain an `exact`
  reference through a locate (only the reference changes, not the signed text).
- **Upgrade** (`TryUpgradeAsync`): used by locate; a `section` reference becomes `exact` when its quote is
  found. The reference's own section is tried first, then the others in order, unless the upgrade is
  restricted to the current section.
- **Restructure** (`RelocateNoteAsync`, called by `NoteStructuringJob` in the transaction that replaces the sections of a draft): every reference of the note with a quote is located again (its own section first, then any); not found, or the section key gone, becomes a `section` reference on the first non-empty section (the quote is kept). A note-wide locate is queued afterwards.
- **Passage** (`GetPassageAsync`): provider-scoped through the reference, the note and the patient; a range is returned as `exact` only when the text at the offsets still equals the quote.

---

## 5. Where references come from

| Origin | Result |
|---|---|
| Provider selects text in a section and uses "Marcar tema neste trecho" | `exact`, quote = the selection |
| "+ Adicionar tema" without selection | `section` for the section the provider picks; background locate queued, restricted to that section |
| Accepting a suggestion from "Sugerir temas" | `section` (first non-empty section); background locate queued |
| Proposals written by structuring | The model returns a quote per code; `exact` when located, else `section` |
| "Localizar trechos" | Background locate for all of the note's active tags without an exact reference |

### 5.1 Two suggestion modes

`IThemeSuggester.SuggestAsync(text, terms, excludedCodes, withQuotes, ct)` has two modes over the same
cached prefix (the instruction of the mode comes after the note text, so the cache is shared). Details
in chapter 23, section 4.

- **Codes only:** an array of codes, no quote. Used by the suggest endpoint, so the dialog stays at about
  4 to 4.5 s warm.
- **With quotes:** an array of `{"code", "quote"}`. Used by structuring proposals and by locate.
- `IThemeSuggester.QuoteAsync` asks only for the quote of codes already chosen (the note's tags).

**Why the split (option B, decided 2026-10-25):** asking for quotes in the suggest dialog made it about
8 s warm. Nobody waits on structuring or on a background locate, so those take the longer mode.

### 5.2 Background locate

`ThemeLocateQueue` (in-memory channel) and `ThemeLocateWorker` (hosted service, one job at a time).

- Jobs hold ids only: provider, note, optionally a tag and a "section only" flag (the provider chose the
  section, so only that section is searched; an explicit choice wins). Without a tag id, all active tags
  of the note without an exact reference.
- The worker calls `NoteThemeService.LocateAsync`, which runs the suggester restricted to the pending
  codes, asks for quotes only, and upgrades `section` references to `exact` when located. The work and
  the writes are separate: the model runs without a transaction; the tags are then re-read under the note
  lock, so a tag removed or already exact in the meantime is skipped. Every upgrade is audited as
  `theme_excerpt_located` (field `theme`, value the term code).
- **De-duplication:** a repeated enqueue for the same (provider, note, tag) is dropped while one is
  pending, and a pending note-wide job absorbs the per-tag jobs of that note.
- **The locate holds the suggester's shared gate** for the whole model run, so suggestions and other
  locates wait behind it (section 8).
- A failure is logged with the note id and the exception type, never text.
- **Queued jobs are lost on restart.** The tags keep their `section` reference; "Localizar trechos"
  runs the locate again.

### 5.3 Proposals with unfound quotes

If the model's quote is not found in any section, the proposal gets a `section` reference and stays so.
Nothing retries it by itself; the provider runs "Localizar trechos" to try again.

---

## 6. The hard rule

Analysis queries (F-10 onwards) may only return items that point back to their source text.
`ExcerptQueries.RequireExcerpt(source, x => x.ExcerptRefId)` adds the filter `ExcerptRefId != null`.

`ExcerptRequireArchitectureTests` scans the sources under `Analyses/` (to be created by F-10): every
class there that queries `ThemeTags`, `PatientQuotes` or `ExtractedFacts` must call `RequireExcerpt`.
It passes now (no analysis classes exist) and fails when one skips the helper. The scan is file-level:
it does not prove the helper is applied to every query inside the file.

---

## 7. Endpoints and UI

Provider routes (`/api/phi`, 401 before anything):

| Route | What |
|---|---|
| `GET providers/me/excerpts/{id}` | `{ id, kind, noteId, patientId, sessionStartsAt, datedAt, sectionKey, sectionTitle, sectionText, start?, end? }`; 404 for another provider's |
| `POST providers/me/notes/{noteId}/themes` | Body may carry `sectionKey` and `start`/`end` (a selection); answers `201 { id, excerptKind }` |
| `POST providers/me/notes/{noteId}/themes/locate` | Queues the note-wide locate; `202 { queued: true }`; 404 for another provider's note, 409 `archived` |
| `GET providers/me/notes/{noteId}/themes` | Tags now carry `excerptId`, `excerptKind` |
| notes search by theme | Results carry `excerptId`, `excerptKind` and a snippet (at most 160 characters around the quote, or the section start) |

Provider app (`apps/provider/src/components/`):

- `notes/NoteSections.tsx`: clicking a theme chip highlights its passage and scrolls to it;
  `?excerpt=<id>` on the note URL does the same on load. Selecting text in a section (draft or signed)
  shows "Marcar tema neste trecho", which opens the theme picker.
- `themes/NoteThemes.tsx`: the chips; "Localizar trechos" shows when any tag has a `section` reference.
  After an accept, a manual add or a locate request, the list is polled briefly until the references turn
  `exact`.
- `themes/SuggestThemesDialog.tsx`: themes only, no quote.
- Theme search results (patient record notes card, `/note-editor`): a snippet line; clicking opens the
  note with `?excerpt=`.

No PHI in URLs (ids only) and none in logs.

---

## 8. Measured cost and limits

Measured on the dev CPU (Qwen2.5-7B Q4_K_M), warm:

| Run | Time |
|---|---|
| Suggest, codes only | 4 to 4.5 s |
| Background locate, one tag | about 6 s |
| Background locate, 2 to 3 tags | 8.5 to 9.6 s |
| Structuring proposals, with quotes | 9.5 to 11.5 s |

The output token cap is per mode: 160 for codes (`CodesOutputTokens`), 720 for quotes
(`QuotesOutputTokens`). The input a note can have is what remains of the 4096-token context after the
prefix and the mode's cap, so the with-quotes mode takes room from the note text (cut to a prefix, as in
chapter 23).

- **The locate holds the shared gate** (about 6 s or more per job). A suggestion requested meanwhile
  waits for it. A note-wide locate of several tags is one job.
- **Queued jobs are lost on restart**; the tags stay `section` until "Localizar trechos" is used.
- **A provider-chosen section restricts the locate** to that section. If the model's passage is in
  another section, it is not found there and the reference stays `section`: fewer exact upgrades by
  design.
- **Unfound quotes stay `section`** (proposals and locates) until the provider asks again.
- **Locating depends on the model quoting literally.** It is verified against the text; a paraphrase is
  not accepted.
- **Selection can split a surrogate pair** (an emoji at the edge of the range); offsets are UTF-16.
- Exact references are validated by bounds only (not that the quote still matches at the offsets).
- **Not covered by automated frontend tests:** the highlight, the selection popover and the polling are
  verified in the browser only.

---

## 9. What stays out

- **Document facts and patient quotes** (F-08, F-15) will add their own `SourceType`; the model and the
  helper are ready, nothing else is built.
- **Longitudinal analyses** (F-10 onwards): the helper exists, no query uses it yet.
- **Transcript and live-session text** are not sources: references point at note sections only.
- **Bulk backfill with the model:** existing tags got `section` references from the migration;
  upgrading them is up to the provider, note by note.

---

## 10. Where things are

| Piece | File |
|---|---|
| Locator, service, endpoint, helper | `Excerpts/ExcerptLocator.cs`, `ExcerptService.cs`, `ExcerptEndpoints.cs`, `ExcerptQueries.cs` |
| Suggestion modes, prompt, grammar | `Themes/IThemeSuggester.cs`, `LlamaThemeSuggester.cs`, `ThemePrompt.cs`, `ThemeGrammar.cs` |
| Tags, proposals, locate | `Themes/NoteThemeService.cs`, `ThemeProposals.cs`, `ThemeEndpoints.cs` |
| Background locate | `Themes/ThemeLocateQueue.cs`, `ThemeLocateWorker.cs` |
| Section save hook | `Notes/NoteEditing.cs` |
| Search snippet | `Notes/NoteList.cs` |
| Migration | `design/migrations/2026-10-24-excerpt-refs.sql` |
| UI | `apps/provider/src/components/notes/NoteSections.tsx`, `components/themes/` |
| Tests | `anamnys-aspire.Tests/Excerpts/`, `Themes/`, `Data/ExcerptRefsMigrationTests.cs` |
