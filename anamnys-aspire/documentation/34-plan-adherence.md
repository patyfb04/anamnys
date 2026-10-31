# Chapter 34: Plan Adherence and Follow-ups (F-14)

How the objectives of a patient's treatment plan are compared with the themes recorded in signed sessions, how the
plan's last review is kept, and how points marked "para retomar" on a note are tracked until the provider marks
them resumed. Last checked against the code on 2026-10-30 (branch `feature/plan-adherence`).
Design: `design/specs/2026-10-30-plan-adherence-design.md`.

What a future change has to respect: the wording is observation only, with no alert colours and no notifications
(section 8); a theme counts by the same rules as the case analysis (section 4); follow-ups are resolved only by
the provider (section 6); and no patient data goes in a URL beyond ids (section 8).

---

## 1. Purpose

Done when (backlog): an objective defined in January and not mentioned since March is shown with both dates and
the elapsed interval. The card "Plano terapêutico" on "Análises" shows, per objective, when it was defined, when
its theme was last recorded and how long ago that was; it shows the date of the plan's last review; and it lists
the points marked for follow-up. Attendance patterns stay in the F-04 card.

---

## 2. Data and migration

Migration `design/migrations/2026-10-30-plan-adherence.sql`, idempotent and in one transaction, mirrored in
`anamnys-db-script.sql`. **It must be applied to any persistent environment.** It:

- adds `FollowUpItems."ProviderId"` (filled from the patient, then NOT NULL, composite FK (`PatientId`,
  `ProviderId`) to `Patients`), `"ExcerptRefId"` (FK to `ExcerptRefs`, ON DELETE SET NULL) and `"ResolvedById"`;
- adds the index `FollowUpItems_Patient_Resolved_idx` on (`PatientId`, `ResolvedAt`);
- re-creates the F-06 trigger `FollowUpItems_patient_not_discarded`, so a discarded patient cannot receive
  follow-ups;
- adds the audit actions `plan_reviewed`, `follow_up_created`, `follow_up_resolved` and `follow_up_reopened` to
  `AuditEntries_subject_ck`, with the same guard pattern as earlier migrations.

`PlanObjectives.TermId` and `TreatmentPlans.ReviewedAt` already existed. The F-06 discard already deletes
`FollowUpItems` (by patient, origin note and resolving note) and their `ExcerptRefs`.

---

## 3. Objective and theme

An objective has an optional theme (`TermId`). The objective endpoints (`POST` and `PUT
providers/me/patients/{id}/objectives[/{itemId}]`) take `termId`, which must be an active term of the active
vocabulary or null; otherwise the answer is 400 with `errors.termId`. On `PUT`, the term the objective already
has is still accepted if it was retired since (so the description can be edited), but only for that provider and
that patient. A `PUT` without `termId` clears the link, so the UI always sends the current one back. The patient
detail returns each objective as `{ id, description, createdAt, termId, termLabel }`.

Objective writes (add, change, remove) on an archived patient answer 409 `code: "archived"`. A record that is not
the provider's or was discarded answers 404, checked before the archived case.

---

## 4. Computations

`PlanAdherenceQuery` (`Analysis/PlanAdherence.cs`) shares two helpers with the case analysis: the dating of the
signed history (`DatedNotesAsync`) and the rule for which tags count (`CountedTags`). The rules are those of
chapter 31.

- **Which sessions.** Signed notes only. The session date is the actual start, else the appointment, else the
  signing day, in the practice's time zone.
- **Which tags.** Not removed and not an untouched AI proposal.
- **Theme or child.** A term counts when it, or any of its descendants at any depth, has a counted tag. The
  term's current status does not matter for the query, and the whole signed history counts, including mentions
  from before the objective was defined.
- **Objective with a term and mentions.** `firstOn` and `lastOn` are the dates of the first and last signed
  session that recorded the term; `lastNoteId` is the last such note. `sessionsSince` is the number of signed
  sessions after the last one. `daysSince` is today (practice-local) minus `lastOn`.
- **Never recorded.** `firstOn`, `lastOn` and `lastNoteId` are null. `sessionsSince` counts the signed sessions
  after `createdAt` and `daysSince` counts the days since the practice-local day of `createdAt`, so the interval
  is measured from the definition.
- **Objective without a term.** `term` and the five interval fields are all null.
- **Which plan.** The open plan (latest with `ClosedAt` null); objectives by `createdAt`. A patient with no plan
  gets `"plan": { "planId": null, "reviewedAt": null }` and no objectives.
- **Follow-up `sessionsSince`.** Signed sessions dated after the origin note's session date (or after `createdAt`
  if the note is gone), not counting the origin note. For a resolved item, only sessions up to the resolution
  (the resolving note's date, otherwise `ResolvedAt`).
- **`originOn`.** The origin note's session day by the same date rule; null when the origin note no longer
  exists.
- **Order.** Open follow-ups first, oldest first; then resolved ones, latest resolution first.

---

## 5. Plan review

`POST providers/me/patients/{id}/plan/review` (no body) sets `TreatmentPlans.ReviewedAt` to now. It locks the
patient row (`FOR UPDATE`), so two reviews cannot open two plans, and creates the open plan when there is none.
It audits `plan_reviewed` with ids only (NoteId null, field `plan`, value `"{patientId} {planId}"`). There is no
deadline and no reminder: the line "Plano revisado em …, há N dias" only reports the date.

---

## 6. Follow-ups

A follow-up is a short description attached to a note, optionally with the passage that originated it (an
`ExcerptRef`, like a theme tag).

- **Create.** `POST providers/me/notes/{noteId}/follow-ups`. The note must be the provider's and its patient not
  discarded (404), the patient not archived (409 `archived`), and the note in review or signed (`ReadyForReview`,
  `Signed` or `Exported`; otherwise 409 `note_not_ready`). The description is trimmed, required and at most 500
  characters. A passage is created only when `sectionKey`, offsets or `quote` are sent, through the same
  excerpt service as themes: a selection (`sectionKey`, `offsetStart`, `offsetEnd`) gives an exact reference, a
  `sectionKey` alone a section reference, and a `quote` alone is located in the text. A quote that is not found
  (also with a `sectionKey`), half a selection or an unknown section is refused with 400 `invalid_selection`
  (`errors.offsetStart`); there is no silent fallback to a whole section. Without any of these there is no
  passage.
- **Resolve.** `POST providers/me/follow-ups/{id}/resolve` with an optional `{ "noteId": "…" }`. Without a note,
  it is resolved from the plan card. With a note, the note must belong to the same patient, must not be the
  origin note and must not be dated before it (same session-date rule); otherwise 400 `invalid_note`
  (`errors.noteId`). A note dated at the same instant as the origin or later is accepted, including a note still
  in review. It sets `ResolvedAt`, `ResolvedNoteId` and `ResolvedById` and audits `follow_up_resolved` (NoteId =
  the resolving note, or the origin note when none was given). Resolving an item that is already resolved is a
  204 no-op with no audit.
- **Reopen.** `POST providers/me/follow-ups/{id}/reopen` clears the three `Resolved*` fields and audits
  `follow_up_reopened` (NoteId = the origin note). Reopening an open item is a 204 no-op.
- **Archived patients.** Create, resolve, reopen and plan review answer 409 `code: "archived"`.
- **Audit.** Ids only, never the description or the quote.

Only the provider resolves a follow-up. Nothing resolves one automatically when a theme reappears.

---

## 7. Endpoints

All under `/api/phi`, provider-scoped, camelCase JSON, dates `yyyy-MM-dd` unless a timestamp, null fields sent as
`null`.

`GET providers/me/patients/{id}/plan-adherence` returns 200, or 404 for another provider's, a missing or a
discarded patient.

```json
{
  "plan": { "planId": "7a0c…", "reviewedAt": "2026-10-20T14:03:11+00:00" },
  "objectives": [
    { "objectiveId": "1f…", "description": "Dormir melhor", "createdAt": "2026-01-05T15:00:00+00:00",
      "term": { "termId": "aa…", "label": "Sono" },
      "firstOn": "2026-01-12", "lastOn": "2026-02-23", "lastNoteId": "c3…", "sessionsSince": 4, "daysSince": 220 },
    { "objectiveId": "3d…", "description": "Retomar exercícios", "createdAt": "2026-02-05T15:00:00+00:00",
      "term": null, "firstOn": null, "lastOn": null, "lastNoteId": null, "sessionsSince": null, "daysSince": null }
  ],
  "followUps": [
    { "id": "f1…", "description": "Sono", "createdAt": "2026-10-01T12:01:00+00:00", "noteId": "n3…",
      "excerpt": { "excerptId": "e1…", "section": "evolucao", "quote": "acordar de madrugada" },
      "resolvedAt": null, "resolvedNoteId": null, "sessionsSince": 3, "originOn": "2026-01-19" }
  ]
}
```

`POST providers/me/patients/{id}/plan/review` returns 200 `{ "planId": "…", "reviewedAt": "…" }`.

`POST providers/me/notes/{noteId}/follow-ups`:

```json
{ "description": "Retomar a relação com o pai", "sectionKey": "evolucao", "offsetStart": 12, "offsetEnd": 40 }
```

Returns 201 `{ "id": "…" }`. `POST providers/me/follow-ups/{id}/resolve` takes `{ "noteId": "…" }` or nothing
and returns 204; `POST providers/me/follow-ups/{id}/reopen` returns 204.

Errors. A 400 is a validation problem with `errors` and also a top-level `code` (read by `ApiError.code`):

| Case | Status | `code` | `errors` key |
|---|---|---|---|
| Objective term not accepted | 400 | | `termId` |
| Missing, blank or over-long description | 400 | | `description` |
| Quote over the maximum length | 400 | | `quote` |
| Quote not found, half a selection, unknown section | 400 | `invalid_selection` | `offsetStart` |
| Resolve with the origin note, an earlier note or a note of another patient | 400 | `invalid_note` | `noteId` |
| Another provider's or discarded patient, note or follow-up | 404 | | |
| Create on a note not in review or signed | 409 | `note_not_ready` | |
| Any write on an archived patient (objectives, review, follow-ups) | 409 | `archived` | |

There is no note-scoped list endpoint. The note view reads `plan-adherence` by patient id and keeps the open
items and the ones whose `resolvedNoteId` is that note.

---

## 8. Provider app

- **Objective form.** An optional "Tema ligado" select (`TermSelect.tsx`): active terms of the active vocabulary,
  children indented; a linked term that was retired stays as an option. The form always sends `termId` on edit.
- **Card "Plano terapêutico"** (`PlanCard.tsx`, model in `planModel.ts`) on "Análises", below "Trajetória de
  escala", in both the available and the unavailable state. It has the review line with the button "Marcar plano
  como revisado", the objectives, the open follow-ups with "Resolver" and the resolved ones, collapsed, with
  "Reabrir". Objective lines read "definido em …; última menção em …; sem registro há N sessões (D dias)", or
  "sem tema ligado", or, for a theme never recorded, the count since the definition. Follow-ups read "marcada
  em …, há N sessões".
- **Note view.** A second button "Marcar para retomar" next to "Marcar tema neste trecho" (`NoteSections.tsx`)
  reuses the same selection capture. The selected text prefills the description (up to 500 characters) and is
  sent as the passage; with no selection no passage is sent. `NoteFollowUps.tsx` lists the patient's open
  follow-ups and those resolved by this note. "Retomada nesta nota" is offered for items whose origin note is a
  different one and whose `createdAt` is earlier than the current note's.
- **Observation only.** No alert colours, no notifications and no wording that judges the interval. All write
  actions are hidden for an archived patient.
- **Copy and cache.** Texts in `planAdherence.*` and `followUps.*` of `pt.json` (with `_zero` keys, because pt
  plural rules treat 0 as "one"); error texts are looked up by `code`. Mutations invalidate
  `["plan-adherence", patientId]` and the patient detail. Hover styling uses classes.
- **URLs.** Only ids; the passage link uses the existing `?excerpt=<uuid>`.

---

## 9. Dev seed

`dev-seed/analysis-samples.sql` has a third block for the seeded patients: an open plan if there is none and
three objectives created about 340 days ago, "Regularizar o sono (exemplo)" linked to Sono (silent since note 8),
"Reduzir a ansiedade antes de reuniões (exemplo)" linked to Ansiedade (recent) and "Fortalecer a rede de apoio
(exemplo)" with no theme; and two follow-ups, "Retomar a conversa com a mãe (exemplo)" open on note 9 and
"Retomar o conflito com o colega (exemplo)" on note 6, resolved on note 8. A patient that already has the first
objective is skipped. Apply the migration before the seed.

---

## 10. Tests

On the server, with real PostgreSQL:

- `PlanAdherenceTests` (scratch database built from the bootstrap script): child terms, removed, AI and unsigned
  tags not counting, objectives without a term and never recorded, no plan, scoping, discarded patient, plan
  review (creating then updating, audit, archived, foreign), and for follow-ups create with and without a
  passage, quote passages, note states, resolve (origin and earlier notes refused, same and later accepted),
  reopen, the no-ops, order, `sessionsSince`, `originOn`, ids-only audit, archived, foreign and discarded.
- `PatientRecordsTests`: the term link on objectives, the retired-term exception scoped to the provider and the
  patient, and objective writes refused on an archived patient.
- `PlanAdherenceMigrationTests` (re-run, old chain against the bootstrap, backfill, audit actions, trigger and
  composite FK). The new migration is appended to the chains of the other migration tests, after
  `2026-10-29-theme-absence.sql`. `DiscardServiceTests` covers the follow-up deletion.

The provider app was checked with build and lint only.

---

## 11. Limits and known gaps

- **No reminder.** The plan review has no deadline and no notification, by decision.
- **A follow-up without a selection has no passage,** because the note view has no section picker.
- **No dashboard summary** of plan adherence.
- **Review days are computed in the browser's time zone** from `reviewedAt`, not by the server.
- **Not exercised in a browser;** build and lint only.

---

## 12. Where things are

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-10-30-plan-adherence.sql` |
| Query and contracts | `anamnys-aspire.Server/Analysis/PlanAdherence.cs` |
| Review and follow-up service | `anamnys-aspire.Server/Analysis/PlanAdherenceService.cs` |
| Endpoints | `anamnys-aspire.Server/Analysis/PlanAdherenceEndpoints.cs` |
| Objective term link | `anamnys-aspire.Server/Patients/PatientRecords.cs` |
| Card | `apps/provider/src/components/analysis/PlanCard.tsx`, `planModel.ts` |
| Note view | `apps/provider/src/components/notes/NoteFollowUps.tsx` |
| Theme select | `apps/provider/src/components/themes/TermSelect.tsx` |
| API client | `packages/shared/src/api/analysis.ts` (`planAdherenceApi`) |
| Copy | `planAdherence.*` and `followUps.*` in `packages/shared/src/lib/i18n/locales/pt.json` |
| Seed | `dev-seed/analysis-samples.sql` |
