# Theme Vocabulary (F-02)

**Date:** 2026-10-21
**Status:** Approved for implementation
**Scope:** Backlog F-02, one delivery. A closed, versioned mental-health vocabulary of themes; the
local model proposes themes from it when a note is structured and on demand; the provider confirms,
adds and removes themes on any note; notes are found by theme; staff evolve the vocabulary in the
admin app under decision D-01.

Done when (backlog): two sessions that describe sleep problems in different words resolve to the same
vocabulary term and both are retrieved by it.

Out of scope: excerpt references (F-03, `ThemeTags.ExcerptRefId` stays null), longitudinal panels
(F-10…F-16), provider-defined terms (the vocabulary is closed on purpose), other specialties.

## 1. Decisions (agreed with the user, 2026-10-20)

| Topic | Decision |
|---|---|
| D-01 | Terms keep a fixed identity (`Id`, `Code`); history is never rewritten. Rename changes only `Label`. Split and merge retire the old term(s) (status `superseded`, kept), create or point to the new term(s) and record the succession. Old tags stay on the old term. |
| Delivery | One delivery, admin area included. |
| Signed notes | Themes are metadata over the note, not part of the signed text: they can be added and removed on drafts and on signed notes. Removal sets `RemovedAt`; every change is audited. |
| AI tags | Proposed on the draft during structuring, shown with an "IA" badge on the review screen, removable. Search counts only active tags on **signed** notes. |
| Existing / hand-written notes | A "Sugerir temas" action on any note runs the model on demand and returns suggestions; nothing is written until the provider accepts each one. No bulk backfill. |
| Vocabulary owner | One vocabulary for all providers, maintained by staff (`owner`, `ops`); `support` reads only. |
| Confidence | Not recorded (`Confidence` stays null): the model gives no calibrated probability. |

## 2. Vocabulary seed

`mh-1`, copied from the deleted backend's `backend/db/02_seed_reference.sql` (commit `250b0442`), section
"theme vocabulary (F-02)": 28 top-level terms and 12 children (6 under `trabalho`, 6 under
`imigracao`), codes and labels verbatim, `Active = true`. The seed's comments on the excluded terms
(`ideacao_suicida`, `autolesao`, `violencia_domestica`) and on why `discriminacao` stays are copied into
the migration and summarised in the documentation chapter. Two levels at most.

## 3. Data

Migration `design/migrations/2026-10-21-theme-vocabulary.sql` (idempotent, one transaction), mirrored in
`anamnys-db-script.sql`:

- Seed `mh-1` (as §2, `ON CONFLICT DO NOTHING`, parents resolved by code).
- `ThemeTags` gains `"AddedById" uuid` (provider who added or accepted it; null for a proposal written by
  structuring) and `"RemovedById" uuid`; partial unique index `"ThemeTags_Note_Term_active_key"` on
  `("NoteId","TermId") WHERE "RemovedAt" IS NULL`; check `"RemovedAt" IS NULL OR "RemovedById" IS NOT NULL`.
- New `ThemeTermSuccessions` (`Id`, `FromTermId`, `ToTermId`, `Kind` `split`|`merge`, `At`, `StaffId`),
  FKs to `ThemeTerms` (restrict) and `Staff`; unique (`FromTermId`,`ToTermId`).
- New `ThemeTermChanges` (`Id`, `TermId`, `Kind` `add`|`rename`|`retire`|`split`|`merge`, `PreviousLabel`,
  `NewLabel`, `StaffId`, `At`): the vocabulary's audit trail.
- `ThemeTerms."SupersededByTermId"` is set only for a merge (one target); a split is read from
  `ThemeTermSuccessions`.

`CreatedBy` keeps its meaning: `ai` = the term came from the model (proposal or accepted suggestion),
`human` = picked by the provider.

## 4. Suggestion (local model only)

- `IThemeSuggester.SuggestAsync(string text, IReadOnlyList<ThemeTermOption> terms, CancellationToken ct)`
  → term codes (0–5, distinct). `LlamaThemeSuggester` uses the same in-process model as structuring
  (`LlmModelProvider`), a ChatML prompt and a GBNF grammar that only admits a JSON array of at most 5
  strings, each one of the given codes.
- Prompt rules (pt-BR): choose only themes the text explicitly talks about; never infer a state, risk or
  diagnosis; prefer the most specific child when the text names it; return `[]` when none fits. The
  list given to the model is the active terms as `code: label` (children as `pai > filho`).
- Input text: the note's sections (`Label: content` per non-empty section) or, if it has none, its
  `RawTranscript`. Control strings are stripped as in `NotePrompt`. Text that does not fit the context
  is cut to fit (themes from a prefix are still useful); documented.
- After `NoteStructuringJob` stores sections (outcome `Done`), it calls the suggester on the new
  sections and, in its own transaction, replaces the note's **active proposals** (`CreatedBy = 'ai'`
  and `AddedById IS NULL`) with the new ones — only while the note is not signed; tags added or accepted
  by the provider are never touched. A suggester failure is logged (no text) and never fails
  structuring. Audit `theme_proposed` (actor null) per tag. Untouched proposals replaced by a
  re-run are deleted (the provider never owned them) and audited `theme_proposal_withdrawn`; a term
  the provider removed on the note is never proposed again by structuring (on-demand suggest may still
  offer it). No proposals for archived patients.
- Tests use a fake suggester registered like `FakeNoteStructurer`.

## 5. Provider API (`/api/phi`, provider-scoped, 401 before anything)

- `GET providers/me/theme-vocabulary` → `[{ id, code, label, parentId, status, successorIds }]`
  (all terms of the active vocabulary; `successorIds` from successions or `SupersededByTermId`).
- `GET providers/me/notes/{noteId}/themes` → `[{ id, termId, code, label, parentLabel, termStatus,
  origin: "ai"|"human", proposed: bool, addedAt }]` active tags only (`proposed` = AI tag not yet
  touched by the provider).
- `POST providers/me/notes/{noteId}/themes` `{ termId, fromSuggestion?: bool }` → 201 `{ id }`. Term
  must be `active` (409 `term_inactive`); an active tag for the same term → 409 `already_tagged`;
  archived patient → 409 `archived`. `CreatedBy` = `ai` when `fromSuggestion`, else `human`;
  `AddedById` = provider. Audit `theme_added`.
- `DELETE providers/me/notes/{noteId}/themes/{tagId}` → 204; sets `RemovedAt`/`RemovedById`; already
  removed → 404. Audit `theme_removed`. Archived patient → 409 `archived`.
- `POST providers/me/notes/{noteId}/themes/suggest` → `{ terms: [{ id, code, label }] }` excluding terms
  already actively tagged; writes nothing; model failure → 503 `{ code: "suggest_failed" }`; note without
  text → `{ terms: [] }`.
- Notes search (`NoteSearchRequest`) gains `TermId` (Guid?): when set, only signed notes
  (`Signed`/`Exported`) with an active tag on that term **or on a child of it**. Succession is not
  followed (D-01: separate series). Unknown term → 400.
- Audit rows go to `AuditEntries` through `NoteAudit`, in the same transaction; `FieldChanged = "theme"`,
  values are term codes (never note text).

## 6. Admin API (`/api/admin/theme-vocabulary`)

Reads: any staff role. Mutations: role `owner` or `ops` (403 otherwise; owners-realm only, other realms
401). `StaffId` = the caller's local id. All mutations in one transaction with a `ThemeTermChanges` row.

- `GET` → tree with, per term: id, code, label, parentId, status, introducedOn, successorIds,
  `activeTagCount` (count only; no patient or provider data).
- `POST terms` `{ code, label, parentId? }`: code `^[a-z][a-z0-9_]{1,59}$`, unique in the vocabulary;
  label 1–80; parent must be an active top-level term. `IntroducedOn` = today.
- `PUT terms/{id}/label` `{ label }`: active terms only.
- `POST terms/{id}/retire`: active only; refused (409 `has_active_children`) while it has active children.
  The same refusal applies to split and merge sources; a merge target must share the sources' parent
  (409 `level_mismatch`). `GET` returns a flat list with `parentId`; clients build the tree.
- `POST terms/{id}/split` `{ terms: [{ code, label }] }` (2–10): creates the new terms under the same
  parent, marks the old one `superseded`, one succession row per new term.
- `POST merge` `{ sourceIds: [..] (2–10, active, same level), target: { existingId } | { code, label } }`:
  marks sources `superseded`, sets `SupersededByTermId`, one succession row per source.
- Tags never move. 409 `{ code }` for state conflicts, 400 ValidationProblem for input.

## 7. Provider app

- Note review screen (draft) and signed note view: block "Temas" below the sections: chips (label, "IA"
  badge for `origin = ai`, "×" to remove), "+ Adicionar tema" (searchable picker grouped by parent, active
  terms only), "Sugerir temas" (dialog listing suggestions with checkboxes; accepting posts each with
  `fromSuggestion`). Retired/superseded term chips show "(aposentado)". Read-only for archived patients.
- Help line: "Temas são marcações sobre a nota; podem ser corrigidos depois de assinar e cada mudança fica
  registrada."
- Filter "Tema" (select grouped by parent) in the patient record's notes card and in `/note-editor`;
  sent as `termId` in the POST body.

## 8. Admin app

Route `/vocabulary` (link from the staff landing), visible to staff; tree with status, counts and the
actions of §6 (dialogs; split and merge show "N marcações ficam no termo antigo; nada é movido").
Mutating buttons hidden for `support` (server still enforces).

## 9. Documentation

New chapter `documentation/23-theme-vocabulary.md` (rules, D-01, excluded terms and why, suggestion
limits); index; status (F-02 done); gaps; Notion (F-02 ticked).

## 10. Testing

- Pure: grammar admits only listed codes and ≤5 items; prompt strips control strings and lists terms;
  parse rejects unknown/duplicate codes.
- Structuring job with fake suggester: proposals written on draft; provider-added tags kept on re-run;
  suggester failure leaves structuring `done`; nothing proposed on a signed note.
- Tagging service (real DB): add/remove/audit; signed note editable; inactive term 409; duplicate 409;
  archived 409; other provider 404; suggest excludes already-tagged terms.
- Search: term filter returns signed notes tagged with the term or a child; drafts excluded; removed tags
  excluded; other provider's notes never returned.
- Admin: rename keeps tags and id; split/merge leave tags, record successions and changes; retire with
  active children 409; `support` 403 on mutations; provider cookie 401.
- Migration: seed counts (28 + 12), idempotent, bootstrap parity.
