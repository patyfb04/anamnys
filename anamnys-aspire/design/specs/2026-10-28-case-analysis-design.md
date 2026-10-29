# Case Analysis: Aggregation Layer and Theme Timeline (F-10 + F-11)

**Date:** 2026-10-28
**Status:** Approved for implementation
**Scope:** Backlog F-10 (longitudinal aggregation query) and F-11 (theme timeline panel), delivered together.
The "Análises" menu entry gets its page: patient picker, themes per session, co-occurrence, and the
existing timeline (appointments, medication, scales) reused.

Done when (backlog): one API call returns a patient's whole theme and scale history with an origin reference
on every element (F-10); the panel renders 20 sessions of tagged themes and each cell leads to the passage
that originated it (F-11).

## 1. Decisions (agreed with the user, 2026-10-28)

| Topic | Decision |
|---|---|
| Delivery | F-10 and F-11 together; F-12…F-14 later on the same layer. |
| Session column | Signed notes only. Date = the linked session (actual start) or appointment (start) day, else the signing day (practice-local). Drafts stay out until signed. The 6-session minimum counts signed notes in the period. |
| Hierarchy and succession | One lane per term as recorded. Children indented under the parent; a parent lane can collapse and then shows "parent or any child". A retired/split term keeps its label of the time with the mark "substituído por X"; old sessions stay on its lane, nothing migrates to the successor (D-01). |
| Page | Patient picker (id only in the URL), period 6 m / 1 y / all; "Temas por sessão"; "Aparecem juntos com mais frequência" list with session counts, ordered by count; the existing "Linha do tempo" card below. Co-occurrence also in the cell detail ("na mesma sessão: X, Y"). |
| Language | Observation verbs only ("registrado em", "aparece junto"); one neutral colour; ordering by frequency, recency or alphabetical, never by "requires attention". |

## 2. Data

No migration. Sources: `Notes` (signed), `Sessions`/`Appointments` for the date, `ThemeTags` (confirmed,
not removed; AI proposals not accepted do not count), `ThemeTerms` (code, label, parent, active),
`ThemeTermSuccessions`, `ExcerptRefs` (sentence/section origin of a tag).

## 3. Server (F-10)

- `Analysis/CaseAnalysisQuery`: one call, provider scoping (404 for another provider's or a discarded
  patient), period by practice-local day.
- `GET providers/me/patients/{id}/analysis?from=&to=` →
  `{ available, signedSessions, minimumSessions: 6, sessions: [{ noteId, on, sessionId?, appointmentId? }],
  themes: [{ termId, code, label, parentId?, active, supersededBy: [{ termId, label }], occurrences: [{ noteId,
  on, excerptId?, section?, quote? }] }], cooccurrence: [{ a: termId, b: termId, sessions }] }`.
  When `available` is false only `available`, `signedSessions` and `minimumSessions` are filled.
- Ordered: sessions by date; themes by code (UI reorders); co-occurrence by count desc then labels.
- Bounded queries (no N+1); a patient with 200 signed notes answers in well under a second on dev hardware.

## 4. Provider app (F-11)

- Route `/analyses` (existing "Análises" menu entry), `?patientId=` search param (id only).
- Patient picker with search (existing patient search API); empty state before a patient is chosen.
- Card "Temas por sessão": SVG or CSS grid, sessions as columns (date labels), theme lanes, children
  indented under collapsible parents, "substituído por X" mark, sort selector (frequência / recência /
  alfabética), horizontal scroll when many sessions. One neutral colour; a cell click opens the existing note
  passage modal (F-03 excerpt) and lists "na mesma sessão: …".
- Card "Aparecem juntos com mais frequência": top pairs with session counts.
- The existing `TimelineCard` below, same period.
- Unavailable: "Série indisponível: N sessões assinadas no período (mínimo 6)".
- Copy in pt.json; hover classes; no PHI in URLs.

## 5. Documentation

Chapter `documentation/31-case-analysis.md`; index; status (F-10 and F-11 done; flow 09 progress); gaps;
Notion.

## 6. Testing

- Query (real DB): signed-only sessions and date rule; removed tags and pending AI proposals excluded;
  hierarchy (parentId) and succession marks; co-occurrence counts; the 6-session cut; scoping (other
  provider, discarded patient); period; a 200-note patient within the time bound.
- Frontend: build + lint; manual check with the theme seeds.
