# Theme Absence Detection (F-12)

**Date:** 2026-10-29
**Status:** Approved for implementation
**Scope:** Backlog F-12. On top of the F-10 aggregation, detect themes that were recorded repeatedly and then
stopped, and describe them with observation verbs on the "Análises" page.

Done when (backlog): a theme not recorded for N sessions produces a descriptive notice with dates.

## 1. Decisions (agreed with the user, 2026-10-29)

| Topic | Decision |
|---|---|
| Rule | Per term as recorded. A term is "absent" when it was recorded in ≥ 3 signed sessions and in none of the patient's last N consecutive signed sessions. N defaults to 4, configurable 2–20 per provider. |
| Exclusions | Superseded or retired terms are never flagged (the grid already shows "substituído por X"). A parent is flagged only when neither it nor any child was recorded in the last N sessions; a child is flagged on its own rule. Below the 6-session minimum there are no absences. |
| Horizon | Always the patient's whole signed history up to the latest signed session, independent of the page period. |
| Where | Card "Temas sem registro recente" above the theme grid on "Análises"; a discreet "sem registro nas últimas N" mark on the term's lane. No notifications. |
| Language | "registrado em X das Y primeiras sessões; sem registro nas N seguintes (desde dd/mm/aaaa, há D dias)". Observation verbs only, neutral colour, ordered by the date of the last record. |

## 2. Data

Migration `design/migrations/2026-10-29-theme-absence.sql` (idempotent, mirrored in `anamnys-db-script.sql`):
`Providers."AbsenceSessions" integer NOT NULL DEFAULT 4 CHECK (between 2 and 20)`.

## 3. Server

- `Analysis/ThemeAbsence` (pure): from the ordered signed sessions and term occurrences (with parent links and
  status) returns absences `{ termId, code, label, parentId?, recordedIn, spanSessions, firstOn, lastOn,
  sessionsSince, daysSince, lastOccurrence: { noteId, excerptId?, section?, quote? } }`, where
  `spanSessions` = sessions from the first record up to and including the last record, and `recordedIn` =
  sessions in that span with the term.
- `GET …/patients/{id}/analysis` gains `absences` (always computed over the whole history, independent of the period's availability; empty only under 6 signed sessions in total)
  and `absenceSessions` (the N used).
- `GET/PUT providers/me/settings/analysis` → `{ absenceSessions }` (2–20).

## 4. Provider app

- Card "Temas sem registro recente" above "Temas por sessão": one line per absence, ordered by `lastOn`; click
  opens the existing occurrence modal on the last passage. Empty state "Nenhum tema sem registro nas últimas N
  sessões."
- Lane mark "sem registro nas últimas N" (neutral, text or small icon with tooltip).
- Settings: "Análises" section with N (2–20), same box style; saving invalidates `analysis`.
- Copy in pt.json; hover classes.

## 5. Documentation

Chapter `documentation/32-theme-absence.md`; index; status (F-12 done; flow 09 progress); gaps; Notion.

## 6. Testing

- Pure: 3-record threshold; N boundary (absent in N, not in N−1); parent with a recent child not flagged;
  child flagged alone; superseded/retired excluded; under 6 sessions none; counts and dates.
- Real DB: scoping, settings validation and use of N, last passage; the analysis seed (Sono stops after note 8
  → flagged with N = 4).
- Frontend: build + lint.
