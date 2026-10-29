# Chapter 31: Case Analysis (F-10 + F-11)

How the longitudinal aggregation of a patient's themes is computed (one query, one endpoint) and how the
"Análises" page shows it: themes per session, the pairs that appear together, and the timeline card reused.
Last checked against the code on 2026-10-28 (branch `feature/case-analysis`).
Design: `design/specs/2026-10-28-case-analysis-design.md`.

What a future change has to respect: only signed notes count and a tag removed or still an untouched AI
proposal never counts (section 2), a retired or split term keeps its old sessions and nothing migrates to
the successor (section 3, D-01), the language is observation only, with no evaluative wording, one neutral
colour and no ordering by "requires attention" (section 8), and the URL carries the patient id only
(section 6).

---

## 1. Purpose

Done when (backlog): one API call returns a patient's whole theme history with an origin reference on every
element (F-10), and the panel renders the sessions of tagged themes with each cell leading to the passage
that originated it (F-11). F-12 to F-14 (absence detection, scale trajectory with events, plan adherence)
are meant to build on the same layer.

No migration. Sources: `Notes`, `Sessions`, `Appointments`, `ThemeTags`, `ThemeTerms`,
`ThemeTermSuccessions` and `ExcerptRefs`.

---

## 2. What counts

`Analysis/CaseAnalysisQuery` reads, for one provider and one patient:

- **Signed notes only.** A note counts when its status is `Signed` or `Exported` (same as the note list).
  Drafts and notes ready for review stay out until signed.
- **The date of a note** is the practice-local day (`PatientSearchQuery.PracticeToday`, America/Sao_Paulo)
  of, in this order: the actual start of the linked session; else the start of the earliest appointment
  linked to that session; else the signing day (creation day when there is no signing time).
- **A tag counts** when it is not removed (`RemovedAt` is null) and is not an untouched AI proposal
  (created by `ai` with no `AddedById`). An AI proposal the provider accepted has an `AddedById` and counts.
- **Period.** The optional `from` and `to` filter the sessions by practice-local day. The minimum of
  section 5 counts the signed notes in the period.
- **Scope.** Provider scoping: another provider's patient and a discarded patient (F-06) answer 404.

---

## 3. Hierarchy and succession

- **One lane per term as recorded.** Only terms with at least one counted occurrence in the period become
  a lane, ordered by code (the page reorders them).
- **Parents are always included.** Every listed child brings its parent, and the parent's ancestors, as a
  lane even with no occurrence of its own (`occurrences: []`), so `parentId` always points at a listed
  lane. The page shows children indented under the parent.
- **Succession (D-01).** A retired or split term stays on the page with the label it had then
  (`active: false`) and `supersededBy` lists the successor terms (`ThemeTermSuccessions`, with their
  labels). Old sessions stay on the old lane; nothing migrates to the successor.
- **Origin.** Each occurrence carries the note, the day and, when the tag has an excerpt reference, its
  `excerptId`, `section` and the stored `quote` (restricted to this provider's note excerpts). The quote is
  clinical text in the response only and is never logged.

---

## 4. Co-occurrence

For each signed note, the distinct pairs of counted terms in it form the pairs; the count of a pair is the
number of notes (sessions) in which both appear. In a pair, `a` sorts before `b` by label, then id. The
list is ordered by count (descending), then by the two labels.

---

## 5. Six-session minimum

The series is available only with at least 6 signed notes in the period. Below that, `available` is false
and only `available`, `signedSessions` and `minimumSessions` are filled (the other members are null). The
page then shows "Série indisponível: N sessões assinadas no período (mínimo 6)".

---

## 6. Endpoint

`GET /api/phi/providers/me/patients/{patientId}/analysis?from=yyyy-MM-dd&to=yyyy-MM-dd`, 401 first,
provider-scoped, dates only, no PHI in the URL. Both dates are optional. 400 validation problem when `from`
is after `to`; 404 for an unknown, foreign or discarded patient.

```json
{
  "available": true,
  "signedSessions": 6,
  "minimumSessions": 6,
  "sessions": [{ "noteId": "...", "on": "2026-04-09", "sessionId": "...", "appointmentId": "..." }],
  "themes": [{
    "termId": "...", "code": "irritabilidade", "label": "Irritabilidade", "parentId": "...", "active": true,
    "supersededBy": [{ "termId": "...", "label": "Ansiedade" }],
    "occurrences": [{ "noteId": "...", "on": "2026-04-09", "excerptId": "...",
                      "section": "subjective", "quote": "Dormiu mal hoje" }]
  }],
  "cooccurrence": [{ "a": "termId", "b": "termId", "sessions": 3 }]
}
```

Sessions are ordered by date; themes by code; co-occurrence by count. The optional members (`sessionId`,
`appointmentId`, `parentId`, `excerptId`, `section`, `quote`) are null when absent. The query uses a fixed
number of bounded queries (patient, notes, sessions, appointments, tags with terms and refs, successions,
plus one per hierarchy level), no N+1.

---

## 7. Provider app: Análises page

Route `/analyses` (the "Análises" menu entry), search parameter `patientId` (the id only; the name stays in
state). Code in `apps/provider/src/routes/_app/analyses.tsx` and `apps/provider/src/components/analysis/`.

- **Picker.** Without a patient the page shows the picker (`PatientPicker`, existing patient search by POST
  body) and an empty state; "Trocar paciente" goes back to it. Once chosen, a period selector offers
  6 meses, 1 ano and Tudo.
- **Grid ("Temas por sessão").** `ThemeGridCard`: sessions as columns with dd/MM/yy labels and horizontal
  scroll, one lane per theme with a sticky label column, one neutral mark per cell. Parents are collapsed by
  default; a collapsed parent marks a session when the parent or any child occurs. Children are indented.
  Retired terms show "substituído por X" and "(inativo)". The sort selector offers frequência, recência and
  alfabética (a group's metric for top-level lanes, the lane's own for children).
- **Modal.** Clicking a cell opens `OccurrenceModal`: the theme, the section, the quote from the stored
  excerpt, "na mesma sessão: X, Y" (the clicked term's ancestors and descendants are left out) and a link to `/notes/{noteId}?excerpt={excerptId}`, which opens the note
  with the passage highlighted (F-03).
- **Co-occurrence card.** "Aparecem juntos com mais frequência": the top 10 pairs with session counts, in
  the server order.
- **Timeline.** The existing `TimelineCard` (chapters 28 and 29: appointments, medication, scales) below,
  reused with the page period: `TimelineCard` takes a `period` prop and hides its own selector here.
- Client `packages/shared/src/api/analysis.ts`, types in `types.ts`, copy under `analysis.*` in `pt.json`.

---

## 8. Language

Observation verbs only ("registrado em", "aparece junto"). One neutral colour. Ordering by frequency,
recency or alphabet, never by severity or "requires attention". No value colours.

---

## 9. Tests

| Class | Covers |
|---|---|
| `Analysis/CaseAnalysisQueryTests` (real DB, 9) | unavailable below 6 (count only, drafts out); signed and exported listed in date order; date rule (session over appointment over signing day, with the UTC-3 boundary); removed tags and pending AI proposals excluded, accepted ones kept; origin (excerpt id, section, quote), `parentId` with the untagged parent listed, succession, inactive lane; co-occurrence counts and ordering; period filter and minimum; another provider and a discarded patient give null; 200 notes and 800 tags answer in under 2 s |

Task 1 reported 9/9 passing against the real database. The endpoint has no HTTP-level test (the 400/401/404
mapping mirrors the attendance endpoint). The provider app was checked with build and lint only.

---

## 10. Limits and known gaps

- There is no note passage modal: the cell shows the quote stored in the excerpt reference and links to the
  note, where the passage is highlighted in place.
- No practice-wide view: the page is per patient.
- No export of the analysis.
- Not exercised in a browser: the page is type-checked and built only.

---

## 11. Where things are

| Piece | Path |
|---|---|
| Server | `anamnys-aspire.Server/Analysis/` |
| Tests | `anamnys-aspire.Tests/Analysis/` |
| Page | `apps/provider/src/routes/_app/analyses.tsx`, `apps/provider/src/components/analysis/` |
| API client | `packages/shared/src/api/analysis.ts` |

## Dev seed

`dev-seed/analysis-samples.sql` adds 12 signed DAP notes (one every 30 days from about 11 months ago) to the first two active patients of every non-test provider, each tag with an exact passage: Sono in notes 1-8, Ansiedade 5-12, two Trabalho children (Sobrecarga e jornada, Conflito no trabalho), Família, Luto and Saudade e desenraizamento. It passes the 6-session minimum and gives lanes, a parent with children, co-occurrence and a theme that stops (useful for F-12). Never run outside a local database.
