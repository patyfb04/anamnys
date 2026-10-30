# Chapter 33: Scale Trajectory with Events (F-13)

How one instrument's scores are drawn over the "Análises" period with medication changes, signed notes and
missed or cancelled appointments on the same axis. Last checked against the code on 2026-10-29 (branch
`feature/scale-trajectory`).
Design: `design/specs/2026-10-29-scale-trajectory-design.md`.

What a future change has to respect: the chart is neutral, with no severity bands, no value colours, no trend
line and no wording that relates an event to a score change (section 4); nothing changes on the server
(section 2); and the model stays free of React (section 7).

---

## 1. Purpose

Done when (backlog): PHQ-9 over six months appears with medication changes and session notes marked on the same
axis. The card "Trajetória de escala" shows what was recorded and when, and leaves the reading to the
clinician.

---

## 2. Data sources

No server change, no migration. The card reuses two payloads the page already loads, with the same query keys
and query functions, so no extra request is made:

- `GET /api/phi/providers/me/patients/{patientId}/timeline?from=&to=` (F-04/F-09): `scales[].points` (score and
  `appliedOn`), `medications[].segments` and `appointments`.
- `GET /api/phi/providers/me/patients/{patientId}/analysis?from=&to=` (F-10): `sessions`, the signed notes with
  `on` and `noteId`. When the analysis is unavailable for the period, there are no notes to draw; the card still
  works with scales, medication and absences.

---

## 3. How the events are derived

Pure functions in `trajectoryModel.ts`. Every event is limited to the page period (start of the period up to
today) and to the types switched on.

| Event | Source |
|---|---|
| Medication start | per treatment, segments ordered by `startedOn`: the first segment start |
| Dose change | each later segment start (drug and dose) |
| Medication stop | the last segment `endedOn`, when it has one |
| Signed note | each entry of `analysis.sessions` |
| Missed or cancelled appointment | timeline `appointments` of kind `no_show`, `cancelled_provider` or `cancelled_patient` |

---

## 4. The chart

SVG drawn by hand (no chart library), in the component `TrajectoryChart.tsx`.

- **Axes.** Scores from the instrument's minimum to its maximum; dates on the horizontal axis over the page
  period. Points are joined by a line in one neutral colour.
- **Nothing interpretive.** No severity bands, no value colours, no trend line, and no text that relates an
  event to a change in score. Observation only.
- **Events.** A thin vertical line per event in the same neutral colour, with an icon in a row above the plot:
  triangle for a start, diamond for a dose change, square for a stop, page for a note, and the attendance mark
  for an absence. The dash style tells the group apart: solid for medication, dotted for notes, dashed for
  absences.
- **Width.** The SVG has a minimum width of 720 px inside a horizontally scrollable box, so it stays legible on
  a narrow screen.
- **Empty.** "Nenhuma escala aplicada no período." when the patient has no scale or the selected instrument has
  no point in the period.

---

## 5. The card

`TrajectoryCard.tsx`, placed on the page below "Temas sem registro recente" and above the "Linha do tempo"
card, in both the available and the unavailable state of the analysis.

- **Instrument picker.** A native select among the patient's instruments; the default is the one with most
  points in the period.
- **Toggles** (checkboxes): "Medicação" on; "Notas" off; "Faltas e cancelamentos" off. Notes come from the case
  analysis, so they only appear with at least 6 signed sessions (chapter 31, section 5); without them the toggle
  has nothing to show.
- **Legend** of the icons and dash styles.
- **Details.** Clicking a point shows its date and score. Clicking an event shows its details with a link: a
  note opens `/notes/$noteId`, a medication event opens the patient record `/patients/$patientId`. No patient
  data is placed in a URL beyond the ids.
- **Text list.** Under the chart, a list of the period's events in date order, with the same details.

---

## 6. Accessibility

Points and event marks are reachable by keyboard and open their details with Enter or Space. The text list under
the chart repeats every event as plain text, so the information does not depend on the drawing or on colour.
Icons and dash styles differ by shape, not by colour. Hover styling uses classes, not inline handlers.

---

## 7. Tests

The provider app has no frontend unit-test runner today, and adding one is out of scope. The model
(`pointsFor`, `instrumentOptions`, `defaultInstrument`, `buildEvents`, `axisDomain`, `dashFor`, `groupOf`,
`DEFAULT_TOGGLES`) is kept pure and free of React so it can be tested when a runner arrives. The server is
unchanged, so the server suite is unchanged. The card was checked with build and lint, and by hand on the seeded
patients.

---

## 8. Dev seed

`dev-seed/analysis-samples.sql` adds, for each seeded patient, 12 monthly PHQ-9 applications (18 down to 7) and
one fictitious treatment "Sertralina (exemplo)": 50mg, a dose change to 100mg and a stop. The block is skipped
for a patient that already has that treatment, and the scale part is skipped when PHQ-9 is not in the catalog.

---

## 9. Limits and known gaps

- **No export.** The chart cannot be saved or printed, so there is no PDF for operadoras yet.
- **Notes need at least 6 signed sessions,** because they come from the case analysis.
- **No frontend unit tests** for the model (section 7).
- **Not exercised in a browser by an automated test;** build and lint only.

---

## 10. Where things are

| Piece | Path |
|---|---|
| Model | `apps/provider/src/components/analysis/trajectoryModel.ts` |
| Chart | `apps/provider/src/components/analysis/TrajectoryChart.tsx` |
| Card | `apps/provider/src/components/analysis/TrajectoryCard.tsx` |
| Placement | `apps/provider/src/routes/_app/analyses.tsx` |
| Copy | `analysis.trajectory.*` in `packages/shared/src/lib/i18n/locales/pt.json` |
| Seed | `dev-seed/analysis-samples.sql` |

## Event list paging

The text list of the period's events under the chart shows 10 events per page with "Anteriores" / "Próximos" and a "1–10 de N eventos" range (announced to screen readers). It goes back to the first page whenever the toggles, instrument, period or patient change the list. The chart itself still draws every event of the period.
