# Chapter 28: Medication Timeline (F-09)

How a confirmed medication fact from a received document becomes a medication record through a short
prefilled form, and how the "Linha do tempo" card shows medication periods and applied-scale scores on
one shared time axis. Last checked against the code on 2026-10-27 (branch `feature/medication-timeline`).
Design: `design/specs/2026-10-27-medication-timeline-design.md`.

What a future change has to respect: the prefill is a suggestion and never a record by itself
(section 3), the fact and the medication change are written in one transaction (section 5), the timeline
states no relation between medications and scores (section 7), and no drug text, dose or document text is
ever logged or audited (section 9).

---

## 1. Purpose

Done when (backlog): prescriptions and PHQ-9 scores appear on the same time axis. A medication fact
read from a document (chapter 27) is turned into a record on the patient, with its origin kept, and the
patient record shows medication lanes above scale series. It is the first longitudinal view; the timeline
payload is shaped so F-10 can add layers to it.

---

## 2. Data

Migration `design/migrations/2026-10-27-medication-timeline.sql` (idempotent, one transaction), mirrored
in `anamnys-db-script.sql`. `MedicationEntries` gains:

| Column | Meaning |
|---|---|
| `TreatmentId` uuid NOT NULL | groups the records of one treatment. A dose change shares the id of the record it closes; every other new record has its own (backfilled with `Id`) |
| `CreatedAt` timestamptz NOT NULL, default now() | when the record was written |
| `FromDocument` boolean NOT NULL, default false | the record was made from a document fact (backfilled true where `SourceFactId` is set) |

- `SourceFactId` already referenced `ExtractedFacts(Id)` with `ON DELETE SET NULL`. Deleting a document
  deletes its facts (chapter 27, section 8); the medication records stay, because they are the provider's
  own record, with `SourceFactId` cleared. `FromDocument` is what then tells "documento excluído" from a
  typed record.
- Index `MedicationEntries_Treatment_idx` on `TreatmentId`.
- Unique partial index `MedicationEntries_SourceFactId_key` on `SourceFactId`: one record per fact. A
  second `new` or `dose_change` from the same fact answers 409 `already_recorded`.
- `AuditEntries_subject_ck` gains `medication_from_fact`. The replacement is guarded with
  `pg_get_constraintdef ... LIKE '%medication_from_fact%'`, and the earlier `2026-10-26-document-facts.sql`
  replacement is guarded the same way, so re-running either does not narrow the list.
- Manual medications (existing card) get `TreatmentId` = own id (`PatientRecords.NewMedication`); the
  edit and end flows are unchanged.

Migration tests: `Data/MedicationTimelineMigrationTests`, and the new file is added to the chain of the
other migration test classes.

---

## 3. Prefill (`Medications/MedicationPrefill`)

Pure class, simple text rules over the fact value and quote. Everything it cannot find stays blank.

| Field | Rule |
|---|---|
| drug | the text before the dose |
| dose | `\d+([.,]\d+)? (mg\|mcg\|g\|ml\|UI)` (comma accepted) |
| posology | the text after the dose |
| start | date quoted with "início", "desde" or "em uso desde"; else the `issue_date` fact of the document (confirmed or corrected first, then pending); else the document date entered at upload. The upload timestamp is not used. A fallback start later than a found end is left blank |
| end | date after "suspens...", "interromp..." or "descontinu..." |

Dates are `dd/mm/aaaa` or "10 de março de 2026". `composed` is the prefill drug, dose and posology as one
text (null when there is no drug); it is used by the confirm-or-correct rule (section 5).

---

## 4. Endpoints

All under `/api/phi/providers/me` (401 first, provider-scoped, no PHI in URLs).

**`GET external-documents/{id}/facts/{factId}/medication-draft`**

```json
{ "factId": "...", "value": "...", "quote": "...", "page": 1, "status": "pending", "recorded": false,
  "prefill": { "drug": "...", "dose": "...", "posology": "...", "startedOn": "2026-03-10", "endedOn": null },
  "composed": "...",
  "activeMatches": [ { "id": "...", "treatmentId": "...", "drug": "...", "dose": "...",
                       "posology": "...", "startedOn": "2026-01-05" } ] }
```

`value` is the correction when the fact was corrected. `activeMatches` are the active records (no end
date) of the same drug on the patient, compared ignoring case and accents. 404; 409 `not_medication` or
`not_pending`.

**`POST external-documents/{id}/facts/{factId}/medication`**

```json
{ "action": "new | dose_change | stop", "targetMedicationId": "...", "drug": "...", "dose": "...",
  "posology": "...", "startedOn": "2026-03-10", "endedOn": null }
```

201 `{ id }`. 400 validation problem with keys `action`, `targetMedicationId`, `drug`, `dose`,
`posology`, `startedOn`, `endedOn`. 404. 409 `{ message, code }` with `archived`, `not_pending`,
`not_medication`, `already_recorded` or `target_not_active`.

**`GET patients/{patientId}/timeline?from=yyyy-MM-dd&to=yyyy-MM-dd`**: see section 7. Both dates are
optional; 400 when `from` is after `to`; 404 for an unknown or foreign patient.

**`GET external-documents/{id}/facts`** (chapter 27) items now carry `recorded: boolean`: a medication
record has this fact as `SourceFactId`.

---

## 5. Recording a medication

`MedicationFacts.RecordAsync` runs in one transaction that locks the document row (as the document
delete does), validates, writes or closes the records, settles the fact and audits.

**Confirm or correct.** The form text (drug, dose, posology) counts as unchanged, and the fact is
confirmed, when it equals the fact value or the `composed` text, comparing lowercase letters and digits
only (spacing, commas and periods are ignored; accents still count). Otherwise the fact is corrected with
the composed form text. Audit `fact_confirmed` or `fact_corrected` is written only when the status
changed.

**Actions**

| Action | Effect |
|---|---|
| `new` | a new record with its own `TreatmentId`, `SourceFactId` set, `FromDocument` true. Needs `startedOn`; `endedOn` optional and not before the start |
| `dose_change` | the target must be active and belong to the document's patient. The old record gets `EndedOn = startedOn - 1 day` (the same day when `startedOn` equals the target start); a new record shares the target's `TreatmentId`. `startedOn` before the target start answers 400 on `startedOn` |
| `stop` | needs `targetMedicationId` and `endedOn` (falls back to `startedOn`). The target is closed; no record is created and the fact is confirmed if pending. The returned id is the target's |

`new` and `dose_change` allow one record per fact (409 `already_recorded`). A stop creates no record, so
its fact is not marked recorded and the same fact can be applied again to another target. An archived
patient answers 409 `archived`.

Audit `medication_from_fact`, value `"{factId} {action} {medicationId}"`.

---

## 6. Provider app (form)

`apps/provider/src/components/externalDocuments/MedicationFactModal.tsx`, opened from `DocumentFactsModal`:

- For a pending medication fact, Confirmar and Corrigir open the form instead of acting at once; other
  kinds keep the behaviour of chapter 27. "Confirmar todos" skips medication facts. Reviewed medication
  facts keep the plain inline Corrigir.
- The form is prefilled from the draft. When `activeMatches` exist it offers "Registrar troca de dose",
  "Registrar suspensão" or "Adicionar como novo", with a target select when several records match.
- Field errors (400) show on the fields; 409 codes show their own message. On `already_recorded` and
  `not_pending` the facts, external-documents and declared-facts queries are refreshed.
- After saving, the fact shows the badge "no prontuário", and the patient and timeline queries are
  invalidated.
- API client `packages/shared/src/api/medicationTimeline.ts`; copy under `documentFacts.medication`,
  `documentFacts.recorded` and `timeline` in `pt.json`.

---

## 7. Timeline

`MedicationTimeline` builds the payload; the dates of scale applications use the practice time zone
(America/Sao_Paulo), as the rest of the app.

```json
{ "medications": [ { "treatmentId": "...", "drug": "...",
    "segments": [ { "medicationId": "...", "dose": "...", "posology": "...", "startedOn": "2026-01-05",
      "endedOn": null,
      "origin": { "kind": "manual | document | document_deleted", "documentId": null, "documentKind": null,
                  "documentDate": null, "page": null, "excerptId": null } } ] } ],
  "scales": [ { "instrumentId": "...", "code": "PHQ-9", "name": "...", "minScore": 0, "maxScore": 27,
    "points": [ { "applicationId": "...", "score": 12, "appliedOn": "2026-02-01" } ] } ] }
```

- One lane per treatment; segments ordered by start; lanes ordered by first start then drug. The lane's
  `drug` is the one of its latest segment.
- Origin fields other than `kind` are null unless the kind is `document`. `documentDate` is the date
  entered at upload, else the upload date. `excerptId` feeds `GET providers/me/excerpts/{excerptId}`
  (chapter 25) for "ver no documento".
- Scale series are one per instrument with the applications in the period; removed applications are left
  out.

**Card** (`components/timeline/TimelineCard`, `TimelineChart`, `timelineGeometry`), placed after the
scales card on the patient record, full width:

- Medication lanes (one row per drug, one segment per dose, segments with no end run to the right edge)
  above scale series (one row per instrument, connected points), on one shared date axis. Period buttons
  6 meses, 1 ano and Tudo; the period is sent as dates only.
- Neutral colours with no value meaning, no severity bands, and no relation between medications and
  scores is stated anywhere.
- Clicking a segment or point opens an inline details panel under the chart (not a floating popover, so it
  works on mobile). For a segment the origin reads "digitado por você", the document kind with date and
  page (with "ver no documento", which opens the passage modal of chapter 27) or "documento excluído".
- SVG with no chart library, like the scale chart. Empty states for no medications and no scores.

---

## 8. Deleting a document

The facts go (chapter 27) and `SourceFactId` is cleared by the foreign key; the medication records stay
and their origin becomes `document_deleted`. The timeline test covers it.

---

## 9. Privacy rules

Audit values are ids and the action: never a drug, dose or fact text. The timeline request carries dates
only. Logs carry ids and counts. Everything is provider-scoped through the patient or the document (a
foreign id answers 404).

---

## 10. Tests

| Class | Covers |
|---|---|
| `Medications/MedicationPrefillTests` (pure) | drug, dose with comma, dates in numbers and in words, "suspenso em", fallbacks, blanks |
| `Medications/MedicationFactsTests` (real DB) | draft and matches, new, dose_change, stop, confirm versus correct, formatting-only differences, audit, scoping, archived 409, already_recorded, stop not marking the fact recorded, `recorded` flag on the facts list |
| `Medications/MedicationTimelineTests` (real DB) | merge of manual and document medications with scale series in the period, deleted document origin |
| `Data/MedicationTimelineMigrationTests` | migration and constraints |

The fixture classes need Aspire stopped. The provider app was checked with build and lint only.

---

## 11. Limits and known gaps

- A stop never shows "no prontuário" on its fact (no link exists for it); the fact stays confirmed.
- The prefill is text rules only, no model: unusual wording leaves fields blank, and a quote with two
  drugs is not split.
- The timeline lives only on the patient record; it is not in the Análises page yet (F-10).
- Only medications and applied scales are shown; no events, themes or notes layer (F-10 to F-13).
- Not exercised in a browser: the form, the card and the details panel are type-checked and built only.
- A document-deleted record loses the link to the document; only the label remains.
- The F-60 export does not include external documents, so the document origin of a medication is not
  exported either.

---

## 12. Where things are

| Piece | Path |
|---|---|
| Server | `anamnys-aspire.Server/Medications/` |
| Migration | `design/migrations/2026-10-27-medication-timeline.sql` |
| Tests | `anamnys-aspire.Tests/Medications/`, `anamnys-aspire.Tests/Data/MedicationTimelineMigrationTests.cs` |
| Form | `apps/provider/src/components/externalDocuments/MedicationFactModal.tsx` |
| Timeline card | `apps/provider/src/components/timeline/` |
| API client | `packages/shared/src/api/medicationTimeline.ts` |
