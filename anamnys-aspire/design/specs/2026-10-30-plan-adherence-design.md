# Plan Adherence and Follow-ups (F-14)

**Date:** 2026-10-30
**Status:** Approved for implementation
**Scope:** Backlog F-14. Treatment-plan objectives linked to a theme are compared with the themes recorded in
signed sessions; the plan's last review is shown; points marked "para retomar" on a note are tracked until the
provider marks them resumed. Attendance patterns stay in the F-04 card.

Done when (backlog): an objective defined in January and not mentioned since March is shown with both dates and
the elapsed interval.

## 1. Decisions (agreed with the user, 2026-10-30)

| Topic | Decision |
|---|---|
| Scope | Objective ↔ theme, plan review, follow-ups. No dashboard summary. |
| Objective ↔ theme | Optional theme (active terms) on create/edit of an objective. A theme counts if the term or any of its children was recorded (confirmed tags, signed notes — same rules as F-10). |
| Plan review | "Marcar plano como revisado" sets `TreatmentPlans.ReviewedAt` (audited); shown as "Plano revisado em …, há N dias". No deadline, no colour. |
| Follow-ups | "Marcar para retomar" on the note view (review or signed): short description, prefilled from the selected text, which is kept as the origin passage (ExcerptRef, like themes). Resolved only by the provider, from a later note ("retomada nesta nota") or from the plan card; can be reopened. |
| Where | Card "Plano terapêutico" on "Análises"; theme field in the existing objective form; follow-up button and open list on the note view. |
| Language | Observation only: "definido em …; última menção em …; sem registro há N sessões (D dias)", "sem tema ligado", "marcada em …, há N sessões". No alert colours, no notifications. |

## 2. Data

Migration `design/migrations/2026-10-30-plan-adherence.sql` (idempotent, mirrored in `anamnys-db-script.sql`,
appended to every migration test chain after `2026-10-29-theme-absence.sql`):

- `FollowUpItems` gains `"ProviderId" uuid` (backfilled from the patient, then NOT NULL, FK to Patients
  (Id, ProviderId)), `"ExcerptRefId" uuid` FK → `ExcerptRefs(Id)` ON DELETE SET NULL, `"ResolvedById" uuid`;
  index (`PatientId`, `ResolvedAt`).
- The F-06 discard trigger (`Patients_not_discarded`) also on `FollowUpItems`; the F-06 discard deletes them.
- Audit actions `plan_reviewed`, `follow_up_created`, `follow_up_resolved`, `follow_up_reopened` added to
  `AuditEntries_subject_ck` with the same guard pattern as earlier migrations.
- `PlanObjectives.TermId` and `TreatmentPlans.ReviewedAt` already exist.

## 3. Server

- Objective create/update (existing endpoints in `Patients/PatientRecords.cs` or wherever objectives are
  written) accept `termId?` (active term of the active vocabulary, else validation problem).
- `GET providers/me/patients/{id}/plan-adherence` → `{ plan: { planId?, reviewedAt? }, objectives: [{ objectiveId,
  description, createdAt, term?: { termId, label }, firstOn?, lastOn?, lastNoteId?, sessionsSince?, daysSince? }],
  followUps: [{ id, description, createdAt, noteId?, excerpt?: { excerptId, section, quote }, resolvedAt?,
  resolvedNoteId?, sessionsSince }] }`; open first then resolved, each by date. 404 for another provider's or a
  discarded patient.
- `POST providers/me/patients/{id}/plan/review` → sets ReviewedAt (creates the plan row if none).
- `POST providers/me/notes/{noteId}/follow-ups { description, sectionKey?, offsetStart?, offsetEnd?, quote? }`;
  `POST providers/me/follow-ups/{id}/resolve { noteId? }`; `POST providers/me/follow-ups/{id}/reopen`. Archived
  patients → 409 like other writes. Audit with ids only.

## 4. Provider app

- Objective form: optional "Tema ligado" select (active vocabulary, children indented).
- "Análises": card "Plano terapêutico" below "Trajetória de escala": review line + button, objectives, open
  follow-ups with "Resolver", resolved collapsed with "Reabrir".
- Note view: "Marcar para retomar" (uses selected text when present) and a list of the patient's open follow-ups
  with "retomada nesta nota".
- Copy in pt.json with plurals; hover classes; invalidate `["plan-adherence", patientId]` and the note's list.

## 5. Dev seed

`dev-seed/analysis-samples.sql`: an objective linked to Sono (silent since note 8), one to Ansiedade (recent),
one without theme, and two follow-ups (one open, one resolved) for the seeded patients.

## 6. Documentation

Chapter `documentation/34-plan-adherence.md`; index; status (F-14 done; group D analyses closed; flow 10 built);
gaps; Notion.

## 7. Testing

Real DB: objective dates and sessions with child terms; objective without term; plan review; follow-up create
(with and without passage), resolve, reopen, archived 409, scoping, discarded patient blocked; migration
(re-run, chains). Frontend: build + lint.
