# Scale Trajectory with Events (F-13)

**Date:** 2026-10-29
**Status:** Approved for implementation
**Scope:** Backlog F-13. A "Trajetória de escala" card on the "Análises" page: one instrument's scores over the
page period with recorded events drawn on the same axis. Frontend only; it reuses the timeline (F-04/F-09)
and analysis (F-10) payloads.

Done when (backlog): PHQ-9 over six months appears with medication changes and session notes marked on the same
axis.

## 1. Decisions (agreed with the user, 2026-10-29)

| Topic | Decision |
|---|---|
| Where | Card "Trajetória de escala" on "Análises", below "Temas sem registro recente", same period as the page. |
| Chart | SVG, no chart library: score axis from the instrument's minimum to maximum, dates on the horizontal axis, points joined by a line in one neutral colour. No severity bands, no value colours, no trend line, no statement relating events and scores. |
| Instrument | Picker among the patient's instruments; default the one with most points in the period. |
| Events | Thin vertical lines with an icon and dash style per type, same neutral colour: medication (start, dose change, stop — drug and dose), signed notes, missed/cancelled appointments. Toggles per type: Medicação on, Notas off, Faltas e cancelamentos off. |
| Details | Click a point: date and score. Click an event: details with a link (note → `/notes/$noteId`; medication → patient record). A text list of the period's events under the chart (accessibility). |
| Empty | "Nenhuma escala aplicada no período." |

## 2. Data (no server change)

- `GET …/patients/{id}/timeline?from=&to=`: `scales[].points` (score, appliedOn), `medications[].segments`
  (a treatment's first segment start = start; each later segment start = dose change; the last segment's
  `endedOn` = stop), `appointments` (kinds `no_show`, `cancelled_provider`, `cancelled_patient`).
- `GET …/patients/{id}/analysis?from=&to=`: `sessions` (signed notes with `on`, `noteId`).

## 3. Provider app

- Pure model `components/analysis/trajectoryModel.ts`: builds points for an instrument and the event list from
  the two payloads, applying type toggles and the period. Kept free of React for future tests (the provider app
  has no unit-test runner today; adding one is out of scope).
- `TrajectoryCard.tsx` + chart component; copy in pt.json; hover classes; keyboard reachable points/events.

## 4. Dev seed

`dev-seed/analysis-samples.sql` also adds monthly PHQ-9 applications and a medication with a dose change and a
stop for the seeded patients.

## 5. Documentation

Chapter `documentation/33-scale-trajectory.md`; index; status (F-13 done; flow 09); gaps; Notion.

## 6. Testing

Build + lint; full suite (unchanged server); manual check on the seeded patients.
