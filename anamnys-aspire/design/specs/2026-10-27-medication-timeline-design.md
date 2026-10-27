# Medication Timeline (F-09)

**Date:** 2026-10-27
**Status:** Approved for implementation
**Scope:** Backlog F-09. Confirmed medication facts from received documents (F-08) become medication
records on the patient's record through a short prefilled form; a "Linha do tempo" card on the patient
record shows medication lanes and applied-scale scores on one shared time axis, without stating any
relation between them. First of the longitudinal views; the timeline payload is shaped for reuse by F-10.

Done when (backlog): prescriptions and PHQ-9 scores appear on the same time axis.

## 1. Decisions (agreed with the user, 2026-10-27)

| Topic | Decision |
|---|---|
| From fact to record | Confirming or correcting a medication fact opens a prefilled form (drug, dose, posology, start, end); the provider saves it as a medication record linked to the fact. If the same drug is active on the record, the form offers "Registrar troca de dose" (closes the old record, opens a new one from the date), "Registrar suspensão" (closes it) or "Adicionar como novo". |
| Prefill | Simple text rules: drug before the dose; dose `\d+([.,]\d+)? (mg|mcg|g|ml|UI)`; dates `dd/mm/aaaa` or "10 de março de 2026"; start = date quoted with "início/desde/em uso desde", else the document's issue_date fact, else the document date entered at upload; end = date after "suspens…/interromp…/descontinu…". Unknown → blank. Suggestion only. |
| Display | Card "Linha do tempo" on the patient record, full width: medication lanes (one row per drug, segments per dose, origin on each segment) above scale series (one row per instrument, connected points), shared date axis, period 6 m / 1 y / all; neutral colours with no value meaning; no severity bands; details on click with origin ("digitado por você" or document kind + date + page with "ver no documento"). SVG, no chart library (like ScaleChart). |
| Future | Same timeline grows with F-10 layers and may move into the "Análises" page. |

## 2. Data

Migration `design/migrations/2026-10-27-medication-timeline.sql` (idempotent), mirrored in
`anamnys-db-script.sql`: `MedicationEntries` gains `"TreatmentId" uuid` (groups the records of one
treatment: dose changes share it; new records get their own), `"CreatedAt" timestamptz`, FK
`SourceFactId` → `ExtractedFacts(Id)` ON DELETE SET NULL; backfill `TreatmentId = Id` for existing rows,
then NOT NULL. Medication records keep provider scoping through the patient. Deleting a document deletes
its facts (F-08); the medication records stay (they are the provider's own record) with `SourceFactId`
cleared and their origin shown as "documento excluído".

`"FromDocument" boolean NOT NULL DEFAULT false` marks the records made from a document (it is what tells "documento excluído" from a typed record once `SourceFactId` is cleared), and a unique partial index on `SourceFactId` allows one record per fact (a second new/dose_change from the same fact: 409 `already_recorded`). A suspension creates no record, so it does not mark its fact as recorded.

## 3. Server

- `Medications/MedicationPrefill` (pure): parse drug, dose, posology, start, end from the fact value and
  quote, with the document's issue_date and upload date as fallbacks.
- `GET providers/me/external-documents/{id}/facts/{factId}/medication-draft` → prefill + matching active
  records of the same drug (case/accents-insensitive) for the choice.
- `POST providers/me/external-documents/{id}/facts/{factId}/medication` `{ action: "new" |
  "dose_change" | "stop", targetMedicationId?, drug, dose?, posology?, startedOn, endedOn? }` →
  confirms (or corrects, if the fact value differs) the fact and writes the medication change in one
  transaction; audited (`medication_from_fact`). Validation: dates, endedOn ≥ startedOn, dose_change
  date ≥ target start; archived → 409.
- `GET providers/me/patients/{id}/timeline?from=&to=` (dates only, no PHI) → `{ medications: [{
  treatmentId, drug, segments: [{ medicationId, dose, posology, startedOn, endedOn?, origin: { kind:
  "manual" | "document" | "document_deleted", documentId?, documentKind?, documentDate?, page?,
  excerptId? } }] }], scales: [{ instrumentId, name, maxScore, points: [{ applicationId, score,
  appliedOn }] }] }`.
- Manual medications (existing card) get `TreatmentId` = own id; the existing edit/end flows keep working.

## 4. Provider app

- Facts panel: for medication facts, Confirmar/Corrigir opens the medication form (prefill, choice when
  an active match exists); other kinds keep the current behaviour. A fact turned into a record shows
  "no prontuário".
- Patient record: card "Linha do tempo" (spec §1 display), placed after the scales card; empty states;
  click details; "ver no documento" opens the document passage modal from F-08.
- Copy in pt.json; hover classes; no PHI in URLs (period as dates only).

## 5. Documentation

Chapter `documentation/28-medication-timeline.md`; index; status (F-09 done; flow 09 partial; Next =
analyses); gaps; Notion.

## 6. Testing

- Pure: prefill on the sample documents' sentences (dates in numbers and words, "suspenso em", doses
  with comma, missing parts → blank).
- Service (real DB): new / dose_change / stop from a fact; fact confirmed/corrected accordingly; audit;
  scoping; archived 409; deleted document keeps records with origin `document_deleted`; timeline merges
  manual and document medications and scale series in the period.
- Frontend: build + lint; manual check with the sample documents.
