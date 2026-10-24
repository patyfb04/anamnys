# Chapter 23: Theme Vocabulary (F-02)

How notes are tagged with themes from a closed vocabulary, how the local model proposes them,
how notes are found by theme, and how staff evolve the vocabulary. Last checked against the code
on 2026-10-21 (branch `feature/theme-vocabulary`). Design:
`design/specs/2026-10-21-theme-vocabulary-design.md`.

The code is short; the reasoning behind what the vocabulary leaves out, and why a theme is
metadata rather than part of the note, is what a future change has to respect.

---

## 1. Purpose and decision D-01

A theme is a tag on a note from a closed, versioned mental-health vocabulary. Two sessions that
describe sleep problems in different words resolve to the same term (`sono`) and both are found by
it. That is the done-when of F-02, and the base for the longitudinal panels (F-10 onwards).

**D-01 (decided 2026-10-20): terms keep a fixed identity and history is never rewritten.**

- `Id` and `Code` never change. A rename changes only `Label`.
- A split or merge retires the old term(s) (status `superseded`, kept, never deleted), creates or
  points to the new term(s) and records the succession.
- Old tags stay on the old term. Nothing moves, so a series built on a term never changes meaning.

Adding a term is safe: no existing series changes, and the new term's series starts on
`IntroducedOn`.

---

## 2. The vocabulary

One vocabulary for all providers, version `mh-1`, seeded by `design/migrations/2026-10-21-theme-vocabulary.sql`
(mirrored in `anamnys-db-script.sql`). It is copied from the reference seed of the deleted backend
(commit `250b0442`, `backend/db/02_seed_reference.sql`): **28 top-level terms and 12 children**
(6 under `trabalho`, 6 under `imigracao`), codes and labels verbatim. Two levels at most. The
seed is idempotent (`ON CONFLICT DO NOTHING`, parents resolved by code).

The children were added under the broad term instead of replacing it, so a rollup adds child
counts to the parent and the `trabalho` series stays continuous.

### 2.1 Terms left out on purpose

The migration carries the full reasoning in comments. It must not be reverted.

- **`ideacao_suicida` and `autolesao` (risk) are not seeded.**
  1. The tag is applied by the model, so the model would be emitting a risk inference, the line
     the CFP Manual Orientativo (2025) draws.
  2. The note already says it better: the prose is the professional's signed judgment, the tag
     flattens it into a word and moves the authorship to the system.
  3. The longitudinal series does not fit: for sleep the trend is the datum; for risk it is
     decided on the first mention. Absence detection (F-12) would produce "no record in the
     following 11 sessions", a statement by software that the risk ceased.
  4. It looks like a safety net and is not: the system only sees what was written in a structured
     note, and a patient at risk may never say the words.
  5. The schema cannot guarantee "only a human applies this term"; it would be a convention.

  If risk ever needs structure, it enters as a safety plan created and signed by the
  professional, never as a term extractable from this vocabulary.
- **`violencia_domestica` is not seeded**, for a different reason: it is a reported fact, not a
  clinical conclusion, but (1) absence detection would invert the signal (a report that stops is
  a known feature of the cycle); (2) there is a legal duty to notify (Lei 10.778/2003 art. 1º, as
  amended by Lei 13.931/2019; ECA art. 13 for children), and how far it reaches an independent
  psychologist is disputed, so the product does not decide it; (3) a structured, searchable tag
  is machine-readable proof that the system identified the case, in a record that can be
  requisitioned, with no record that anything was done. It exposes the professional instead of
  protecting. What has to be modelled is the duty and the act (who notified, when, to whom), not
  the subject.
- **`discriminacao` stays.** Tested against the same criteria it shares none: the model infers no
  psychological state by tagging it, recurrence has real clinical use, and no legal duty hangs
  on it. Same nature as `luto`.

---

## 3. Tagging

A tag is a row in `ThemeTags` (note, term). `CreatedBy` is `ai` when the term came from the model
(a proposal or an accepted suggestion) and `human` when the provider picked it. `AddedById` is the
provider who added or accepted it, and is null for a proposal written by structuring.
`ExcerptRefId` stays null (section 9) and `Confidence` is not recorded: the model gives no
calibrated probability.

### 3.1 Proposals from structuring

After `NoteStructuringJob` stores the sections (outcome `Done`), it asks the suggester for themes
and replaces the note's **active proposals** (`CreatedBy = 'ai'` and `AddedById IS NULL`) in its
own transaction (`ThemeProposals`). Proposals arrive shortly after the sections: the note page
polls the tags every 5 s while structuring is pending and for about 2 minutes after it is done,
until a proposal appears.

- Only while the note is not signed. The status (and the patient's archived state) is re-checked
  under the note lock, because the note may be signed, or the patient archived, while the model runs.
- Tags the provider added or accepted are never touched.
- A term the provider removed on this note is **never proposed again by structuring**. On-demand
  suggestion may still offer it.
- No proposals for an archived patient.
- Terms retired while the model ran are not proposed.
- An untouched proposal that a re-run no longer wants is **deleted**, not marked removed: the
  provider never owned it, and `RemovedAt` requires `RemovedById`. The deletion is audited as
  `theme_proposal_withdrawn`.
- A suggester failure is logged (exception type and note id, never text) and never fails
  structuring.

### 3.2 Confirmation through review

On the review screen (draft) and on the signed note view a block "Temas" shows the active tags as
chips: the label, an "IA" badge when `origin = ai`, and "x" to remove. A proposal the provider has
not touched is `proposed = true`. There is no separate "confirm" call: the provider confirms a
proposal by leaving it, and the tag stays as written by structuring. "+ Adicionar tema" picks any
active term; "Sugerir temas" opens suggestions the provider accepts one by one (section 4).
Chips of a retired or superseded term show "(aposentado)". The block shows wherever the sign card
shows (ready for review, or signed), so hand-written (typed) notes are covered. It is read-only
for an archived patient.

### 3.3 Editing after signing

Themes are metadata over the note, not part of the signed text, so tags can be added and removed
on drafts and on signed notes alike. The signed text and its hash are unaffected. Every change is
audited, and the help line in the UI says so.

Rules (`NoteThemeService`):

- Add: the term must be active in the active vocabulary (`409 term_inactive`); an active tag on
  the same term answers `409 already_tagged` (also enforced by the partial unique index
  `ThemeTags_Note_Term_active_key` on `(NoteId, TermId) WHERE RemovedAt IS NULL`); an archived
  patient answers `409 archived`. `CreatedBy` is `ai` when the request has `fromSuggestion`.
- Remove: sets `RemovedAt` and `RemovedById` (check `ThemeTags_Removed_ck`); a tag already removed
  answers `404`; archived patient `409 archived`.
- Another provider's note is a `404`. Refusals leave no trace (the transaction commits only on Ok).

### 3.4 Audit actions

Rows go to `AuditEntries` through `NoteAudit`, in the same transaction, with
`FieldChanged = "theme"` and the term **code** as the value (never note text).

| Action | Actor | When |
|---|---|---|
| `theme_proposed` | none (system) | Structuring wrote a proposal |
| `theme_proposal_withdrawn` | none (system) | A re-run deleted an untouched proposal |
| `theme_added` | provider | Added or accepted a suggestion |
| `theme_removed` | provider | Removed a tag |

The vocabulary has its own trail in `ThemeTermChanges` (section 7).

---

## 4. Suggestion (local model only)

`IThemeSuggester.SuggestAsync(text, terms, excludedCodes, ct)` returns 0 to 5 distinct term codes,
each one of `terms` and none of `excludedCodes`. `terms` is always the **full active list**;
`excludedCodes` are the terms already actively tagged (on demand) or removed by the provider on
this note (proposals). `LlamaThemeSuggester` uses the same in-process Qwen model as structuring
(`LlmModelProvider`, one set of weights), a ChatML prompt (`ThemePrompt`) and a GBNF grammar
(`ThemeGrammar`) that only admits a JSON array of the vocabulary's codes. The output is still
parsed and unknown, duplicate or excluded codes are dropped (`ThemeGrammar.Pick`); the text never
leaves the server. Sampling can end generation before the array is closed (seen in dev: `[`
alone); `ThemeGrammar.ParseOrRecover` then keeps the complete known codes of that prefix (possibly
none) instead of failing. Generation stops at the first `]` (codes never contain one).

- **Prompt rules (pt-BR, kept short):** only themes the text explicitly talks about; never infer a
  state, risk or diagnosis; prefer the most specific child (`pai > filho`); at most 5; `[]` when
  none; answer with the JSON array of codes only.
- **Terms given to the model:** the full active list as `code: label`, children as
  `parent > child`, in a fixed order (input order does not matter).
- **Input text:** the note's sections as `Label: content` (non-empty only) or, with no sections,
  the `RawTranscript`. Control strings are stripped as in `NotePrompt`.

### 4.1 Prefix cache and small context

The prompt is laid out so that everything constant comes first: `ThemePrompt.Prefix(terms)` is the
system turn (rules + full term list) and the opening of the user turn; `ThemePrompt.Body(text)` is
the note text plus the assistant turn. `Build(text, terms) = Prefix(terms) + Body(text)`.

- `LlamaThemeSuggester` keeps **one `LLamaContext` of its own** for the process, created from the
  shared weights with `NoteStructuring:SuggestionContextSize` (default **4096**, versus 16384 for
  structuring). The context is built by `WarmAsync` (below) or, if that has not run yet, lazily by
  the first suggestion: it tokenizes the prefix and decodes it into that context (about 600 tokens
  with `mh-1`).
- Each call removes the KV entries after the prefix (`SafeLLamaContextHandle.MemorySequenceRemove`
  on sequence 0 from the prefix length to the end), decodes only the note tokens and the suffix
  with a `LLamaBatch`, and samples token by token with `DefaultSamplingPipeline` (grammar).
- **Cache key:** SHA-256 of the prefix text (`ThemePrompt.PrefixKey`), so any change of a code, a
  label, a parent or the rules rebuilds the context on the next call. A failed or cancelled build
  leaves no cache. Creation and use happen under the suggester's `SemaphoreSlim(1)`.
- **Exclusions are not in the prompt nor in the grammar's code list**, so neither depends on the
  note. Narrowing the grammar to the allowed codes was measured and rejected: the model commits to
  the opening quote before it can know a code is forbidden and is pushed onto loosely related
  terms (sleep, work and family excluded gave `dor_saude_fisica, raiva_irritabilidade,
  familia_a_distancia, sobrecarga_jornada` instead of `[]`). Instead the grammar admits all codes
  and up to 5 extra items (`ThemeGrammar.ItemsWithExcluded`) so excluded codes the model names do
  not crowd out the others, and they are dropped after parsing.
- **Threads:** `NoteStructuring:SuggestionThreads` (0 = `Threads`). Measured on an i7-12700H
  (6P + 8E cores, 20 logical): 6, 12 and 19 threads gave the same warm time within noise, so the
  default was not changed.
- **Limit:** text that does not fit what remains (`SuggestionContextSize` minus the prefix, the
  suffix, 160 output tokens and a margin; about 3300 tokens) is **cut to a prefix**; themes from
  the start of a long note are still useful, and a long note never fails for length. Themes
  mentioned only in the cut part are not found. If the vocabulary alone does not fit, the call
  throws.

**Measured** (Qwen2.5-7B Q4_K_M, CPU, 19 threads, the 40-term `mh-1` list, a one-paragraph note,
Aspire app running on the same machine), before → after:

| | Before (StatelessExecutor, 16384 ctx, ~700 prompt tokens per call) | After |
|---|---|---|
| First call (builds the prefix) | 40–43 s | 28–40 s |
| Later calls | 40–43 s | **≈ 4–5 s** (rare spikes to 12–14 s under machine load) |

The note "O paciente mencionou que não dorme há 3 dias, tem tido episódios de estresse no
trabalho, e que isso afeta a família dele" returns `sono, trabalho, familia`; with those three
excluded it returns `[]`.

**Warm-up.** Loading the weights and building the prefix take 30-60 s, which a provider would
otherwise wait out in the suggest dialog. `IThemeSuggester.WarmAsync(terms, ct)` does exactly that
work under the same gate and nothing else (no inference past the prefix); it is a no-op when the
cache is current. `ThemeWarmupService` (a `BackgroundService`, registered with the suggester) calls
it once after `ApplicationStarted`, so startup is never delayed, reading the active term options
with its own scope. After every successful vocabulary mutation (add, rename, retire, split, merge)
the admin endpoint calls `ThemeWarmupSignal.Request()`; the signal is a one-slot channel, so a burst
of edits coalesces into one re-warm and the HTTP response never waits for it. A warm-up failure is
logged by exception type only and not retried (the next suggestion builds the cache lazily).
`NoteStructuring:WarmSuggestionOnStart` (default `true`) turns it off; the test AppHost sets it to
`false`. The suggest dialog also shows "Preparando o modelo..." after 8 s of waiting.

**Memory:** from the start (warm-up) until the process stops, the server holds the model weights
and the cached context. The context holds ≈ 0.6 GB (measured private bytes delta 611–613 MiB: KV cache
for 4096 tokens ≈ 224 MiB at f16, compute buffer ≈ 311 MiB, plus the logits and batch buffers) for
the life of the process. It is freed when the server stops.

### 4.2 Endpoints and failures

- **On demand:** `POST providers/me/notes/{noteId}/themes/suggest` works on any note, writes
  nothing, and returns terms not already actively tagged. A note without text, or with every term
  tagged, returns an empty list without running the model. A model failure is logged without the
  text and answers `503 { code: "suggest_failed" }`. Nothing is written until the provider accepts
  each suggestion (a `POST` with `fromSuggestion`). There is no bulk backfill of existing notes.
- **Failure behaviour:** during structuring a failure is swallowed (section 3.1); on demand it
  is the 503 above.
- **Concurrency:** theme suggestion runs are serialized among themselves (`LlamaThemeSuggester`
  holds a `SemaphoreSlim(1)`; extra requests wait, and the dialog aborts its request when closed).
  They are **not** serialized with structuring or transcription: suggesting while a note is being
  structured runs two contexts on the same weights (the 16384 structuring context and the cached
  4096 suggestion context). Size the server accordingly (chapter 20, section 2.6).

Tests use a fake suggester registered like `FakeNoteStructurer`; no test loads the model. The pure
tests cover the prompt layout (constant prefix, key changes), the grammar's extra items and
`Pick`, and the suggester's no-model paths (`LlamaThemeSuggesterTests`).

---

## 5. Search

`NoteSearchRequest` has `TermId` (`Guid?`), sent in the POST body of the notes search (the filter
"Tema" on the patient record's notes card and on `/note-editor`).

- Only **signed** notes (`Signed`, `Exported`) with an **active** tag on that term **or on a child
  of it**. Drafts and removed tags never match.
- Succession is **not followed** (D-01): a superseded term and its successor are separate series.
  A term replaced by a split or merge finds only the notes tagged with it.
- An unknown term answers `400`. Results are always limited to the provider's own notes.

---

## 6. Admin: actions and roles

Route `/vocabulary` of the admin app (link from the staff landing); API under
`/api/admin/theme-vocabulary` (owners realm only; other realms answer `401`).

- **Read** (`GET`): any staff role, including `support`. It returns a **flat list** with
  `parentId`; the client builds the tree. Per term: id, code, label, parentId, status,
  `introducedOn`, `successorIds` and `activeTagCount` (a count only, no patient or provider data).
- **Mutations:** role `owner` or `ops` (`403` otherwise). The UI hides the buttons for `support`;
  the server enforces. `StaffId` is the caller's local id.

| Action | Route | Rules |
|---|---|---|
| Add | `POST terms` `{ code, label, parentId? }` | Code `^[a-z][a-z0-9_]{1,59}$`, unique in the vocabulary (`409 code_taken`); label 1 to 80; a parent must be an active top-level term. `IntroducedOn` is today. |
| Rename | `PUT terms/{id}/label` | Active terms only (`409 term_not_active`). Changes only the label. |
| Retire | `POST terms/{id}/retire` | Active only; refused while it has active children (`409 has_active_children`). Status `retired`; tags stay. |
| Split | `POST terms/{id}/split` `{ terms: [{ code, label }] }` | 2 to 10 new terms under the same parent; the old one becomes `superseded`; one succession row per new term. Refused with active children. |
| Merge | `POST merge` `{ sourceIds, target }` | 2 to 10 distinct active sources; target is `{ existingId }` or `{ code, label }`. Sources become `superseded` with `SupersededByTermId` set; one succession row per source. Refused if a source has active children. **The target shares the sources' parent** (`409 level_mismatch`, also when sources differ in level). |

Every mutation runs in one transaction that locks the active vocabulary row (concurrent edits
queue up) and writes a `ThemeTermChanges` row; it commits only when the outcome is Ok. Conflicts
answer `409 { code }`, bad input `400` (validation problem). **Tags never move**: the dialogs for
split and merge say "N marcações ficam no termo antigo; nada é movido".

---

## 7. Data model

Migration `design/migrations/2026-10-21-theme-vocabulary.sql` (idempotent, one transaction,
mirrored in `anamnys-db-script.sql`).

- `ThemeVocabularies` (`Version`, `Active`), `ThemeTerms` (`Code`, `Label`, `ParentTermId`,
  `Status` `active|superseded|retired`, `IntroducedOn`, `SupersededByTermId`).
- `ThemeTags` gains `AddedById` and `RemovedById` (no foreign keys, as the spec lists none), the
  partial unique index and the removal check described in section 3.3.
- `ThemeTermSuccessions` (`FromTermId`, `ToTermId`, `Kind` `split|merge`, `At`, `StaffId`), unique
  per pair, foreign keys with restrict. `SupersededByTermId` is set only for a merge (one target);
  a split is read from the successions. `successorIds` in the API comes from both.
- `ThemeTermChanges` (`TermId`, `Kind` `add|rename|retire|split|merge`, `PreviousLabel`,
  `NewLabel`, `StaffId`, `At`): the audit trail of the vocabulary.
- **The migration repairs legacy rows** before adding the index and the check: soft-removed tags
  without `RemovedById` get the note's provider as remover; when a note and term have several
  active tags, the earliest stays and the rest are marked removed. A second run finds nothing to
  fix. Production has no such rows; this exists for development databases.

Provider routes (`/api/phi`, `401` before anything): `GET providers/me/theme-vocabulary`,
`GET|POST providers/me/notes/{noteId}/themes`, `DELETE providers/me/notes/{noteId}/themes/{tagId}`,
`POST providers/me/notes/{noteId}/themes/suggest`.

Code: `anamnys-aspire.Server/Themes/` (`NoteThemeService`, `ThemeProposals`, `ThemeAdminService`,
the two endpoint classes, `LlamaThemeSuggester`, `ThemePrompt`, `ThemeGrammar`). Provider UI:
`apps/provider/src/components/themes/`. Admin page: `apps/admin/src/routes/vocabulary.tsx`.

---

## 8. Testing notes

- Tests that touch the database run only while no AppHost or Server process is running, and the
  dev database needs the migration applied.
- The admin endpoint tests create the Keycloak users `test.ops` and `test.owner` in the **dev
  owners realm** (`dev.owner` has only `support`). They persist there after the run.

---

## 9. What stays out

- **Excerpt references (F-03).** `ThemeTags."ExcerptRefId"` stays null; a tag points at the whole
  note, not at a passage.
- **Longitudinal panels (F-10 onwards).** Nothing reads the tags except the note search.
- **Provider-defined terms.** The vocabulary is closed on purpose and maintained by staff; there is
  no per-provider or per-specialty vocabulary, and only mental health is seeded.
- **Confidence and bulk backfill** (sections 3 and 4), and any risk or violence term (section 2.1).
