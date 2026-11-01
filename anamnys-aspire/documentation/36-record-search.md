# Chapter 36: Record Search (F-16)

How a provider asks a question in natural language about one patient and gets back passages of the signed notes,
each with its date and origin. Last checked against the code on 2026-10-31 (branch `feature/record-search`).
Design: `design/specs/2026-10-31-record-search-design.md`.

What a future change has to respect: the search only retrieves passages, it never summarises, interprets or answers
(section 2); nothing found is said plainly and the closest passage is never returned (section 4); the question
never goes in a URL, log or audit value (section 8); and only signed notes are searched (section 5).

---

## 1. Purpose

Done when (backlog): a natural-language question returns passages with provenance. The card "Perguntar ao
prontuário" on "Análises" takes a question such as "quando falamos de sono pela primeira vez?" for the selected
patient and lists passages of the signed notes: session date, section, the sentence with the matching words
highlighted, and a link to the note. There is no model and no vectors: the search is lexical in PostgreSQL
(Portuguese stemming, accent-insensitive) plus the theme vocabulary. This settles the pending decision of flow 11.

---

## 2. Retrieval only

- The result is a list of passages, never a synthesis. The card says "Esta busca devolve trechos das suas notas
  assinadas. Ela não resume nem interpreta."
- An evaluative question ("o sono melhorou?") returns the passages on the theme and makes no statement about it.
  Evaluative words ("melhorou", "piorou", "melhor", ...) are stop words, so they do not narrow the search.
- Nothing found gives an empty list and the card says "Nenhum trecho encontrado nas notas assinadas." The closest
  passage is never returned in its place.

---

## 3. Data and migration

Migration `design/migrations/2026-10-31-record-search.sql` (idempotent, mirrored in `anamnys-db-script.sql`).
**It must be applied to any persistent environment.** It needs the `unaccent` extension (`CREATE EXTENSION IF NOT
EXISTS unaccent`), so the database user that applies it must be allowed to create it. It:

- creates the text search configuration `anamnys_pt`, a copy of `portuguese` with `unaccent` before the stemmer;
- creates a GIN index on `to_tsvector('anamnys_pt', "Content")` of `NoteSections`.

No wrapper function is needed: `to_tsvector(regconfig, text)` is already immutable, and a test checks with EXPLAIN
that the search expression can use the index. The migration is appended to the chains of the other migration tests,
after `2026-10-30-patient-quotes.sql`.

Until it is applied, a search on an existing patient answers 500 (the text search configuration is missing).

---

## 4. Question handling and matching

`Analysis/RecordSearch.cs` is pure (no database) and does the normalisation:

- **Normalisation.** The question is lower-cased and accent-folded, and split into words of letters and digits.
  Operators and punctuation in the question are dropped by the tokeniser.
- **Stop words.** Articles, prepositions, question words ("quando", "falamos", "primeira", "vez", ...) and
  evaluative words are removed.
- **Order hints.** "primeira vez" means earliest first (`earliest`), "última vez" means latest first (`latest`);
  otherwise the order is `chronological`.
- **Vocabulary terms.** Terms are matched by whole label, by code (split on `_`), or by either side of a label of
  the form "A e B" ("vergonha" finds "Culpa e vergonha"). Whole words, no stemming. All `ThemeTerms` are considered,
  not only the active vocabulary, so tags on retired terms stay findable. Children are included at any depth, and
  the `term` returned is the tagged term, not the one named.

Matching decisions:

- **Text passages require every word** of the question (AND). The query is built with `to_tsquery` from sanitised
  tokens (letters and digits only, each quoted) passed as a bound parameter.
- **Theme passages only when the terms cover all the words of the question.** "sono na escola" uses text only,
  because a tag on Sono is not an answer to it. Several terms in one question are OR-ed ("ansiedade e trabalho"
  brings the tags of either), while text stays AND.
- **One passage per section.** A section with a theme passage is not repeated as a text passage, because the theme
  one has the reference. Several tags in one section each give a passage, de-duplicated by `excerptId`.
- **Stemmer, -ão and -ões.** The Postgres `portuguese_stem` does not join -ão and -ões, with or without
  `unaccent` (`discussão` becomes `discussa`, `discussões` becomes `discusso`; also `mão` and `mãos`). The text
  query therefore looks for the -ao, -oes and -aos forms together, so relação finds relações and emoção finds
  emoções. This is why the query uses `to_tsquery` and not `plainto_tsquery`. The -ães plural (pão, pães) is not
  covered.
- **Section references.** A reference of kind `section` can keep a quote that left the note, so a theme passage on
  one shows the first sentence of the section, not the quote. A signed note is never misquoted (same principle as
  chapter 35).

---

## 5. Which notes count

Signed notes only (`Signed` or `Exported`). The date is the F-10 session date (actual start, else appointment,
else signing day, in the practice's time zone). Theme passages go through `RequireExcerpt`/`CountedTags`: a tag
with no reference, removed, or an untouched AI proposal is not a passage. The optional `from` and `to` are inclusive
on that date. Passages come 5 per page (`page`, default 1; the response carries `page` and `pageSize`); `total` counts all of them. Order: `chronological` by date and
section, `earliest` or `latest` by date as the hint says.

---

## 6. Endpoint

`POST /api/phi/providers/me/patients/{patientId}/record-search`, provider-scoped, camelCase JSON, dates
`yyyy-MM-dd`. It is a POST so that the question travels in the body.

Request:

```json
{ "question": "quando falamos de sono pela primeira vez?", "from": "2026-01-01", "to": "2026-12-31" }
```

`from` and `to` are optional.

Response 200 (`order` is `chronological`, `earliest` or `latest`):

```json
{ "passages": [ { "noteId": "c3…", "on": "2026-01-05", "section": "evolucao",
                  "snippet": "Teve discussões com o irmão.", "highlights": [{ "start": 5, "end": 15 }],
                  "excerptId": null, "matchedBy": "text", "term": null } ],
  "total": 1, "order": "earliest" }
```

A theme passage:

```json
{ "noteId": "c3…", "on": "2026-01-12", "section": "evolucao", "snippet": "dor na mandíbula",
  "highlights": [], "excerptId": "e1…", "matchedBy": "theme", "term": { "termId": "aa…", "label": "Ranger noturno" } }
```

Nothing found is a 200 with `passages: []` and `total: 0`.

| Case | Status | `errors` key |
|---|---|---|
| Question under 3 or over 300 characters after trimming | 400 | `question` |
| `from` after `to` | 400 | `from` |
| Another provider's or discarded patient | 404 | |

Archived patients can be searched. Validation messages do not echo the question.

---

## 7. Highlights contract

`snippet` is plain text. `highlights` is a list of `{ start, end }` as UTF-16 offsets into `snippet`, end exclusive
(the same unit as JavaScript string indexes). The client slices `snippet` by `highlights` into `<mark>` elements
rendered as text nodes; it never uses `dangerouslySetInnerHTML`. A snippet may contain a literal `<b>` that was in
the note. It starts or ends with `…` when a long sentence is cut. `highlights` is always empty on theme passages, so
the client shows `term.label` there. `excerptId` is null on text passages: link by `noteId` and `section`.

How the snippet is made: `ts_headline` with `HighlightAll` and control-character marks, then the sentence with the
most matches is cut in C#. `HighlightAll` keeps the note text verbatim, unlike fragment mode, which replaces tags
with spaces. A sentence over 300 characters is cut around the first match. Headlines are computed only for the
page of results, so a long history does not travel. The number of queries is fixed.

---

## 8. Privacy

The question is sensitive. It travels only in the request body: never in a URL or query string. There is no log
call and no audit entry that carries it, validation messages do not repeat it, and there is no
`EnableSensitiveDataLogging` or HTTP body logging. The searches themselves are not recorded.

---

## 9. Provider app

Card "Perguntar ao prontuário" on "Análises", below the anchors for the selected patient, collapsible, with its own
anchor: an input, examples, the fixed sentence of section 2, the list of passages (date, section, snippet with
highlights, "ver na nota" with the excerpt when there is one) and the empty state. It uses a POST mutation; nothing
goes in the URL. Texts are in `pt.json`; hover styling uses classes.

---

## 10. Tests

On the server, with real PostgreSQL:

- `RecordSearchTests` (pure): stop words, order hints, term detection, token sanitising, the -ao/-oes/-aos forms.
- `RecordSearchQueryTests` (scratch database from the bootstrap script): accent-insensitive and stemmed match,
  all words required, themes and children, signed notes only, the date rule and order, paging, scoping,
  discarded patients, nothing found, snippets with `<b>`, `<script>` and hyphenated words, privacy of validation
  messages, the index usable by EXPLAIN, and 200 notes (600 sections, 200 tags) under a 2 second bound. One HTTP
  test covers 400 and 404.
- `RecordSearchMigrationTests`, and the chains of the other `*MigrationTests`: chain against the bootstrap, re-run,
  earlier file re-run.

The provider app was checked with build and lint only.

---

## 11. Limits and known gaps

- **No synonyms outside the vocabulary.** Only the stemmer and the theme labels and codes widen a word.
- **No search across the practice.** One patient at a time.
- **No HTTP happy-path test.** The JSON shape relies on default camelCase serialisation; the query is tested
  directly on a scratch database, and the HTTP test covers 400 and 404 only.
- **The -ães plural is not covered** (pão, pães).
- **Not exercised in a browser;** build and lint only.

---

## 12. Where things are

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-10-31-record-search.sql` |
| Normalisation (pure) | `anamnys-aspire.Server/Analysis/RecordSearch.cs` |
| Query and contracts | `anamnys-aspire.Server/Analysis/RecordSearchQuery.cs` |
| Endpoint | `anamnys-aspire.Server/Analysis/RecordSearchEndpoints.cs` |
| Tests | `anamnys-aspire.Tests/Analysis/RecordSearch*Tests.cs`, `anamnys-aspire.Tests/Data/RecordSearchMigrationTests.cs` |

## Scope of a search

A search always covers the patient's whole signed history; it does not follow the period chosen on the page, so "primeira vez" is the true first time and "nenhum trecho encontrado" is absolute. The server keeps optional `from` and `to`; the app does not send them. A term named inside a longer term's match is dropped ("conflito no trabalho" names only that term, not the parent Trabalho).
