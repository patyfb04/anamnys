# Chapter 32: Theme Absence (F-12)

How a theme that was recorded repeatedly and then stopped is detected on top of the case analysis (chapter 31),
and how the "Análises" page and the settings show it. Last checked against the code on 2026-10-29 (branch
`feature/theme-absence`).
Design: `design/specs/2026-10-29-theme-absence-design.md`.

What a future change has to respect: absence is judged on the patient's whole signed history, never on the page
period (section 2); superseded and retired terms are never flagged (section 2); the wording is observation only,
neutral colour, no "requires attention" (section 7); and there are no notifications by decision (section 9).

---

## 1. Purpose

Done when (backlog): a theme not recorded for N sessions produces a descriptive notice with dates. The notice is
a card and a mark on the lane, both on the "Análises" page.

Migration `design/migrations/2026-10-29-theme-absence.sql`, mirrored in `anamnys-db-script.sql`. It is
idempotent. Apply it to every persistent environment (see section 4).

---

## 2. The rule

`Analysis/ThemeAbsence` is a pure function over the ordered signed sessions and the counted term occurrences
(the same occurrences as the grid: signed notes only, removed tags and untouched AI proposals never count).

- **Per term as recorded.** A term is absent when it was recorded in at least 3 distinct signed sessions
  (`MinimumRecordedSessions`) and in none of the patient's last N consecutive signed sessions.
- **N.** Default 4, configurable from 2 to 20 per provider (`Providers.AbsenceSessions`).
- **Exclusions.** Only terms with status `active` are judged; a superseded or retired term is never flagged
  (the grid already shows "substituído por X"). When there are no more than N sessions in total, nothing is
  returned.
- **Parents.** A parent is flagged only when neither it nor any descendant (any depth) was recorded in the last
  N sessions. A child is judged on its own records.
- **Whole history.** The horizon is always the patient's whole signed history up to the latest signed session,
  independent of the period selected on the page.
- **Six-session minimum.** Under 6 signed sessions in the whole history there are no absences. When the period
  makes the analysis unavailable (chapter 31, section 5), `absences` are still computed from the whole history.
- **Order.** By `lastOn` ascending (the longest silence first), then label, then id. This is a date order, not
  a ranking of importance.

---

## 3. Fields and wording

Each absence:

| Field | Meaning |
|---|---|
| `termId`, `code`, `label`, `parentId` | the term as recorded (`parentId` null for a top-level term) |
| `recordedIn` | distinct sessions with the term; all of them lie before the window |
| `spanSessions` | sessions from the first record up to and including the last record |
| `firstOn`, `lastOn` | practice-local dates (yyyy-MM-dd) of the first and the last record |
| `sessionsSince` | signed sessions after the last record (the real count, which can be larger than N) |
| `daysSince` | days from `lastOn` to today in the practice time zone (0 or more) |
| `lastOccurrence` | `noteId` and, when the tag has an excerpt reference, `excerptId`, `section`, `quote` of the last record; when the last session has several, one with an excerpt is preferred |

Wording on the page: "registrado em X das Y primeiras sessões; sem registro nas N seguintes (desde dd/mm/aaaa,
há D dias)", where X is `recordedIn`, Y is `spanSessions`, N is the real `sessionsSince` and D is `daysSince`
("hoje" when it is 0). Observation verbs only.

---

## 4. Endpoints and migration

`GET /api/phi/providers/me/patients/{patientId}/analysis` gains two members, always computed over the whole
history (the period only filters `sessions`, `themes` and `cooccurrence`, which are unchanged):

```json
{
  "available": true,
  "absences": [{
    "termId": "...", "code": "sono", "label": "Sono", "parentId": null,
    "recordedIn": 8, "spanSessions": 8, "firstOn": "2025-11-12", "lastOn": "2026-06-30",
    "sessionsSince": 4, "daysSince": 121,
    "lastOccurrence": { "noteId": "...", "excerptId": "...", "section": "data",
                        "quote": "acorda várias vezes durante a noite" }
  }],
  "absenceSessions": 4
}
```

When unavailable because of the period, `absences` still hold the whole-history result (empty only under 6 signed sessions in total); `absenceSessions` is always filled. The query keeps its fixed number of bounded
queries: the signed history is read once, and the tags of the whole history are loaded once.

Settings (provider-scoped, 401 first):

- `GET /api/phi/providers/me/settings/analysis` returns 200 `{ "absenceSessions": 4 }`.
- `PUT` on the same path with `{ "absenceSessions": 7 }` returns 204. A missing or out-of-range value (outside
  2 to 20) returns a 400 validation problem
  `{ "errors": { "absenceSessions": ["Use entre 2 e 20 sessões."] } }`.

Migration `2026-10-29-theme-absence.sql`: `Providers."AbsenceSessions" integer NOT NULL DEFAULT 4` with the
constraint `Providers_AbsenceSessions_ck` (between 2 and 20). It must be applied to any persistent environment;
without it the analysis endpoint fails (it reads the provider's N).

---

## 5. Provider app

Code in `apps/provider/src/components/analysis/` and `apps/provider/src/components/settings/`.

- **Card "Temas sem registro recente"** (`AbsenceCard`), above "Temas por sessão". One line per absence in the
  server order, dates as dd/mm/aaaa, using the real `sessionsSince` and "hoje" when `daysSince` is 0. Clicking
  a line opens the existing `OccurrenceModal` on the last passage. Empty state: "Nenhum tema sem registro nas
  últimas N sessões."
- **Lane mark.** In the grid, a neutral line "sem registro nas últimas N" under the lane label, with a tooltip.
- **Settings.** A section "Análises" in the settings page with N (2 to 20), in the same box style as the
  retention section. Saving invalidates the `analysis` and `analysis-settings` queries; server field errors
  are shown.
- Client `packages/shared/src/api/analysis.ts` (`getSettings`, `saveSettings`), types in `types.ts`, copy under
  `analysis.absence.*` and `settings.analysis.*` in `pt.json`.

---

## 6. Dev seed

The analysis seed (`dev-seed/analysis-samples.sql`) has Sono in notes 1 to 8 and nothing after, out of 12
notes. With N = 4 it is flagged (recorded in 8 of the first 8 sessions, no record in the 4 that follow).
Ansiedade and the others continue to the end and are not flagged.

---

## 7. Language

Observation verbs only ("registrado em", "sem registro"). One neutral colour. Ordered by the date of the last
record, never by severity or "requires attention".

---

## 8. Tests

| Class | Covers |
|---|---|
| `Analysis/ThemeAbsenceTests` (pure, 8) | the 3-record threshold; the N boundary (absent over N, not over N-1); a parent with a recent child is not flagged; a child is flagged alone; superseded and retired terms excluded; under 6 sessions none; counts and dates |
| `Analysis/ThemeAbsenceQueryTests` (real DB, 7 facts + a 5-case theory) | provider scoping, use of N, the last passage, whole history independent of the period, the settings validation, the seed shape (Sono flagged with N = 4) |
| `Data/ThemeAbsenceMigrationTests` (3) | the column, default and constraint; idempotence; the migration chain |

The migration file was appended to the chain of the other migration tests. The existing `CaseAnalysisQueryTests`
now also need the migration (the query reads the provider's N). The provider app was checked with build and
lint only.

---

## 9. Limits and known gaps

- **No notifications**, by decision: absence only shows on the page.
- `daysSince` counts to today, not to the latest signed session, so it keeps growing while the patient has no
  new session.
- The modal opened from the card has no "na mesma sessão" list (the entry point does not carry the other terms
  of that session), and the modal reads "Registrado em" with the date of the last record. When the term has no
  lane in the period, the modal uses a synthesized theme.
- Absence is per term as recorded: a theme renamed or split is not followed to its successor.
- Not exercised in a browser: the page is type-checked and built only.

---

## 10. Where things are

| Piece | Path |
|---|---|
| Rule | `anamnys-aspire.Server/Analysis/ThemeAbsence.cs` |
| Settings | `anamnys-aspire.Server/Analysis/AnalysisSettings.cs` and `CaseAnalysisEndpoints.cs` |
| Query | `anamnys-aspire.Server/Analysis/CaseAnalysisQuery.cs` |
| Migration | `design/migrations/2026-10-29-theme-absence.sql` |
| Tests | `anamnys-aspire.Tests/Analysis/`, `anamnys-aspire.Tests/Data/ThemeAbsenceMigrationTests.cs` |
| Provider app | `components/analysis/AbsenceCard.tsx`, `ThemeGridCard.tsx`, `components/settings/AnalysisSettingsSection.tsx` |

## Entry point from the patient record

The patient record header has a "Ver análises" button (also for archived patients, since it only reads) that opens `/analyses?patientId=<id>` with the patient selected.
