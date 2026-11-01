# Record Search in Natural Language (F-16)

**Date:** 2026-10-31
**Status:** Approved for implementation
**Scope:** Backlog F-16. A question typed on the "Análises" page returns passages of the patient's signed notes
with date and origin. Retrieval only.

Done when (backlog): a natural-language question returns passages with provenance.

## 1. Decisions (agreed with the user, 2026-10-31)

| Topic | Decision |
|---|---|
| Approach | Lexical search in Postgres (Portuguese stemming, accent-insensitive) plus the theme vocabulary. No model, no vectors. This settles the pending decision of flow 11. |
| Where | Card "Perguntar ao prontuário" on "Análises", below the anchors, for the selected patient. No practice-wide search. |
| Output | Passages only, never a synthesis. Nothing found: say so; never return the closest passage. An evaluative question returns the passages on the theme and no statement about it. |
| Privacy | The question travels in the request body, never in a URL, log or audit value. |

## 2. Data

Migration `design/migrations/2026-10-31-record-search.sql` (idempotent, mirrored in `anamnys-db-script.sql`, appended
to every migration test chain after `2026-10-30-patient-quotes.sql`):

- `CREATE EXTENSION IF NOT EXISTS unaccent`.
- Text search configuration `anamnys_pt` (copy of `portuguese` with `unaccent` before the stemmer).
- An immutable wrapper function if needed for the index expression, and a GIN index on
  `to_tsvector('anamnys_pt', "Content")` of `NoteSections`.

## 3. Server

- `Analysis/RecordSearch` (pure): normalises the question: removes stop words and question words ("quando",
  "falamos", "primeira", "vez", …), detects the order hint ("primeira vez" → earliest first, "última vez" → latest
  first), matches vocabulary terms by label or code (accent-insensitive), including children.
- `POST providers/me/patients/{id}/record-search { question, from?, to? }` (question 3–300 chars) → `{ passages:
  [{ noteId, on, section, snippet, excerptId?, matchedBy: "text" | "theme", term?: { termId, label } }], total, order:
  "chronological" | "earliest" | "latest" }`, 5 passages per page (`page`, default 1). Signed notes only, F-10 date rule; tags through
  `RequireExcerpt`/`CountedTags`; text passages use the section sentence around the match (`ts_headline`, no HTML
  injected into the client: return plain text plus match offsets or marked terms). 404 for another provider's or a
  discarded patient; archived patients can search.
- Question text never logged or audited.

## 4. Provider app

- Card "Perguntar ao prontuário" (collapsible, anchor added): input, examples, the sentence "Esta busca devolve
  trechos das suas notas assinadas. Ela não resume nem interpreta.", list of passages (date, section, snippet with
  terms highlighted, "ver na nota" with the excerpt when there is one), empty state "Nenhum trecho encontrado nas
  notas assinadas."
- Uses a POST mutation; nothing in the URL. Copy in pt.json; hover classes.

## 5. Documentation

Chapter `documentation/36-record-search.md`; index; status (F-16 done; flow 11 built); gaps; Notion.

## 6. Testing

Pure: stop words, order hints, term detection. Real DB: accent-insensitive and stemmed match, theme and children,
signed only, date rule and order, cap of 20, scoping, discarded, nothing found, 200 notes within a bound, migration
re-run and chains. Frontend: build + lint.
