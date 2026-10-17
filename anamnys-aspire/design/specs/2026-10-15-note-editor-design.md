# Note Editor — Note List and Typed Notes

**Date:** 2026-10-15
**Status:** Implemented
**Scope:** Closes the Phase 1 note loop outside the live session. The provider gets a list of all
their notes at `/note-editor` (today the sidebar link leads nowhere) and can create a note by
typing: either free text that the AI organises into the chosen format (Notion flow 03), or empty
sections written by hand. A typed note can be linked to an appointment, which creates the clinical
session and marks the appointment attended — the same chain the live session builds, now also
moving the appointment status. Builds on `2026-10-10-live-session-transcription-design.md`,
`2026-10-12-note-structuring-design.md` and `2026-10-13-note-review-and-signing-design.md`.

Out of scope: PDF export (next delivery), audit history view, correcting a signed note through a
new version, dictation uploaded after the session (flow 01 audio), chunking of texts longer than
the model context.

---

## 1. Context

- `Notes."InputMode"` already allows `'Text'` (`Notes_InputMode_ck`); only `'VoiceLive'` is written
  today. `Notes."Status"` allows `'Draft'`, which nothing uses.
- `NoteStructuringJob` structures `Notes.RawTranscript` into sections when `StructureStatus =
  'pending'`; it is driven by `StructureJob` on the in-process `TranscriptionQueue`.
- `NoteEditing.CreateBlankSectionsAsync` creates a format's empty sections (audited).
- `LiveRecordings.SessionForAsync` creates or reuses the `Sessions` row of an appointment and sets
  `Attendance = 'attended'`, but the appointment status stays as it was (gap).
- `Appointments.SetStatusAsync` owns the status transition side effects (reminders through
  `AppointmentNotifications.OnLeftActiveAsync`, `Patients.LastVisit`) and calls `SaveChanges`
  itself.
- `PatientNotesView` (`/patients/$id/notes`) calls `notesApi.list` → `GET /api/notes`, which does
  not exist. `NoteCard` is typed against an old `Note` shape (snake-case status, transcript
  preview).
- The note page header shows "Rascunho gerado por IA" for hand-created sections (gap): the client
  has no signal for who wrote the sections.
- Note creation at finalize is not audited (gap); two concurrent live finalizes on one appointment
  orphan the first session (gap).
- Notion flow 03 rule: the system never completes what was not written; an empty section stays
  empty.

## 2. Decisions

| Topic | Decision |
|---|---|
| Typed note modes | Both, chosen at creation: **"Organizar com IA"** (free text → `StructureJob`) and **"Escrever nas seções"** (empty sections of the format, no AI). |
| Where the typed text lives | `Notes.RawTranscript`, `InputMode = 'Text'`. No new column: structuring, restructure, edit, sign and audit work unchanged. |
| Appointment link | Optional. Candidates are the patient's appointments from the last 14 days up to now, not cancelled and not `no_show`, without a note. The create endpoint enforces the same window and statuses (`409 appointment_unavailable`). Linking creates or reuses the session (`attended`) and moves a `scheduled`/`confirmed` appointment to `attended`. |
| Live session parity | The live finalize uses the same session helper, so it also moves the appointment to `attended`, takes the same appointment lock and audits `note_created`. |
| List | All of the provider's notes, newest first, filter chips (Pendentes default, Assinadas, Com falha, Todas), patient name search, "Carregar mais". The patient notes page uses the same endpoint filtered by patient. |
| Status of a typed note | `ReadyForReview` from creation (there is no transcription step). `Draft` stays unused. |
| Text limit | 20,000 characters for the AI mode; longer texts are refused at the API. Texts within the limit that still exceed the model context fail as `too_long`, as today. |
| Prompt | A typed-text variant of `NotePrompt`: label "Anotações" instead of "Ditado"; rules 1, 4 and 6 say "escrito" instead of "ditado"; rule 3 drops the filler-word removal; rule 5 is rewritten without mentioning automatic transcription. The rules against adding facts are unchanged. |
| AI badge | Shown only when the current sections came from the AI: the latest audit entry among `ai_generated` and `sections_created` is `ai_generated` (`StructureStatus` cannot tell, since blank sections also set it to `done`). Closes the hand-written-sections gap. |
| Patient names in requests | Never in a URL (CLAUDE.md: no PHI in URLs): the list is a POST search with the filters in the body, like the patient search. |

## 3. Data

No schema change. Writes:

- `Notes`: `InputMode = 'Text'`, `Status = 'ReadyForReview'`, `Format`, `RawTranscript` (AI mode
  text, or the optional manual-mode text), `StructureStatus = 'pending'` (AI mode) or `'done'`
  (manual, as `CreateBlankSectionsAsync` sets it), `SessionId` when linked.
- `NoteSections`: empty sections in manual mode (`CreateBlankSectionsAsync` rules).
- `Sessions`, `Appointments.SessionId`, `Appointments.Status`, `Patients.LastVisit`, `Reminders`:
  only when an appointment is linked (§4.3).
- `AuditEntries`: `note_created` (`NewValue` = input mode and format) for typed notes and live
  finalizes; manual mode also writes the existing `sections_created` entry.

## 4. Server

All routes are in the `/api/phi` group and resolve the provider server-side; every query filters
by `ProviderId`.

### 4.1 `POST providers/me/notes/search`

Body: `{ status?: "pending" | "signed" | "failed", patientId?, name?, page? }` (absent status = all;
`name` is matched like the patient search, `ILIKE` on first + last name, at most 100 characters;
`page` is 1-based, 25 per page).

- `pending` = `Draft`, `Processing`, `ReadyForReview` without a structure failure; `signed` = `Signed`, `Exported`;
  `failed` = `Failed` **or** `StructureStatus = 'failed'`.
- Order: `CreatedAt` desc, then `Id` desc.
- Response: `{ items: NoteListItem[], hasMore }`, where `NoteListItem` = id, patientId,
  patientName, status, structureStatus, inputMode, format, createdAt, signedAt, appointmentStartsAt
  (via the session's appointment, nullable).

### 4.2 `GET providers/me/patients/{patientId}/note-candidates`

The patient's appointments with `StartsAt` in the last 14 days and `StartsAt <= now`, status not
`cancelled` or `no_show`, and no note on their session. Newest first. 404 for another provider's patient.

### 4.3 `POST providers/me/notes`

Body: `{ patientId, appointmentId?, format, mode: "ai" | "manual", text? }`.

One transaction (execution strategy, like the other note writes):

1. Validate the body (§6). Load the patient (provider-scoped); refuse archived.
2. If `appointmentId`: lock the appointment row (`SELECT … FOR UPDATE`), check it belongs to the
   patient, has `StartsAt` within the last 14 days and not in the future, is neither cancelled nor
   `no_show`, and its session (if any) has no note (the same rule as §4.2; otherwise
   `409 appointment_unavailable`).
3. Session: shared helper extracted from `LiveRecordings.SessionForAsync` (create or reuse; set
   `Attendance = 'attended'`; for a typed note `ActualStart`/`ActualEnd` stay null when the session
   is new). If the appointment is `scheduled` or `confirmed`, apply the attended transition through
   a helper extracted from `Appointments.SetStatusAsync` that does not save (reminders,
   `LastVisit`).
4. Insert the note (§3) and the `note_created` audit entry; manual mode then creates the blank
   sections in the same transaction.
5. Commit; then, in AI mode, `queue.Enqueue(new StructureJob(noteId, providerId, format))`.

Response `201 { noteId }`.

`NoteDetail` (the note page) gains `sectionsByAi` (§2, AI badge).

`TranscriptionWorker.RunStructureAsync` picks the prompt variant from `Notes.InputMode`.

### 4.4 Live finalize

`LiveRecordings.FinalizeAsync` switches to the shared session helper (appointment lock, attended
transition) and writes `note_created`.

## 5. Provider app

### 5.1 `/note-editor` (list)

- Header "Notas" with a "Nova nota" button.
- Filter chips: **Pendentes** (default), Assinadas, Com falha, Todas. Search field (patient name).
- Cards: patient, date, format, status badge, origin ("Sessão ao vivo" / "Digitada"). `NoteCard`
  is retyped to `NoteListItem`; click opens `/notes/$noteId`.
- "Carregar mais" while `hasMore`. Empty and error states with their own text.
- `PatientNotesView` uses the same endpoint with `patientId`.

### 5.2 `/note-editor/new` (create)

- **Paciente:** `PatientPicker` from the calendar.
- **Consulta (opcional):** select fed by note-candidates, plus "Sem consulta"; preselects an
  appointment of today when there is one.
- **Formato:** `FormatCards` from the live session, default DAP.
- **Modo:** two cards, "Organizar com IA" and "Escrever nas seções". AI mode shows a required
  textarea with a 20,000-character counter and the hint "A IA organiza o que você escreveu; não
  acrescenta nada." In manual mode the page offers no text field and sends `text = null`.
- "Criar nota" → `/notes/$noteId` (AI mode shows the existing structuring progress there).
- Search params `patientId` and `appointmentId` preselect.

### 5.3 Entry points

- Sidebar "Editor de Notas" → `/note-editor`.
- Mobile tab "Nova nota" → `/note-editor/new` (today `/patients`).
- Patient record: "Nova nota" button → `/note-editor/new?patientId=…`.
- Calendar details panel: "Escrever nota" only for an appointment inside the 14-day window that is neither cancelled nor
  no-show and has no note →
  `/note-editor/new?patientId=…&appointmentId=…`. The appointment list response gains a
  `hasNote` flag for this.

### 5.4 Note page (`/notes/$noteId`)

- For `InputMode = 'Text'` the transcript block is labelled "Texto original".
- The "Rascunho gerado por IA" badge shows only when `sectionsByAi` is true and no section was edited.

## 6. Failure handling

| Case | Response |
|---|---|
| Invalid format or mode; AI mode without text (or whitespace only); text over 20,000 characters | 400, field error |
| Patient or appointment of another provider, or appointment of another patient | 404 (no existence leak) |
| Archived patient | 409 `archived`, "Desarquive o paciente para criar notas." |
| Appointment cancelled, no-show, in the future, older than 14 days, or already with a note | 409 `appointment_unavailable` |
| Two creates for one appointment at the same time | The appointment lock serialises them: one note, one 409 |
| Structuring fails or the server restarts | Existing paths: `failed` (`model_error` / `too_long` / `server_restart`), "Reorganizar" and "Escrever nas seções" in the editor |

## 7. Testing

Automated (`dotnet run --project anamnys-aspire.Tests`):

- List: each status filter, `patientId`, name search, paging and order, another provider's notes
  never returned.
- Create: AI mode (note `pending`, job enqueued), manual mode (blank sections), `note_created`
  audit, every row of §6, concurrent creates on one appointment yield one note.
- Appointment link: new session (`attended`) or reused one; appointment → `attended`, pending
  reminder cancelled, `LastVisit` updated; the same for the live finalize.
- Candidates: 14-day window, excludes cancelled and already-noted appointments, another provider's
  patient is 404.
- Prompt: the text variant uses "Anotações", does not mention transcription, still strips the
  control tokens.

Manual browser check: list and filters; create in both modes; entry from the patient record and
from the calendar; the "Nova nota" tab at 390px; AI badge and "Texto original" label.

`anamnys-aspire.Server.http` gains the three new requests.

Note page: `sectionsByAi` is true after structuring, false after "Escrever nas seções", true again after a restructure.
