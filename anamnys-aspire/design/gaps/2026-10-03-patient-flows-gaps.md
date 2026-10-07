# Known gaps — patient list, records, accounts and portal invitation

**Date:** 2026-10-03
**Status:** Open. The official backlog is the Notion page "Plano de Implementação"; move
these items there when the workspace has free blocks, then delete this file.

Covers what is still missing after `2026-09-27-patient-list`, `2026-10-01-patient-records`,
`2026-10-02-patient-accounts` and `2026-10-03-portal-invitation`.

## To call these flows complete

1. ~~**Delivery 3 — patient portal.**~~ Done (`2026-10-04-patient-portal-sessions`): "Meus
   profissionais" and per-provider upcoming/history. Sessions only appear once appointments
   can be created (see "Appointments have no write path" below).
2. ~~**Provider app accepts a patient-only session.**~~ Fixed (`fix/realm-scoped-session`):
   `/api/auth/me?realm=…` answers only for that realm's session, and each app asks for its
   own; the patient portal and admin app had the same flaw.
3. **Email in production.** Verify a sending domain in Resend and configure `Email__*` in the
   deployed environment. Keycloak has no production SMTP either: account verification and
   password reset do not work outside dev. Until a domain is verified, Resend's sandbox only
   delivers to the account owner's own address.

## Gaps inside the existing flows

- ~~**Appointments have no write path.**~~ Done (`2026-10-05-provider-calendar`): the provider
  calendar creates, reschedules and changes status; "Realizada" updates `Patients.LastVisit`.
  Still open for scheduling: availability, recurrence, patient self-booking and Google
  Calendar sync (deliveries 2–5 of that spec).
- **Patients with appointments cannot be deleted.** Any appointment, even a cancelled one,
  makes a patient undeletable (`PatientRecords.HasClinicalRecordsAsync` counts appointments),
  so archiving is the only option left.
- **Archiving leaves future appointments live.** An archived patient keeps their future
  appointments in their slots, and the portal still shows them as the next session.
- **Undoing "Realizada" may leave `LastVisit` stale.** When no other attended visit exists,
  `LastVisit` stays on the undone date (spec section 3 semantics); revisit.
- **Notes.** "Notas recentes" on the record links to the note editor, still a placeholder;
  `PatientNotesView` (`/patients/$id/notes`) calls `notesApi` routes that do not exist.
- **Patient rights (LGPD).** The portal cannot list or remove provider links, and the
  patient cannot delete their account. `Patients.AccountId ON DELETE SET NULL` is ready.
- **PHI read logging.** `AccessLogs` exists; nothing writes to it.
- **Mobile check** of the provider-side "Portal do paciente" block and the invitation page at
  390px (the portal session pages were checked).
- **No frontend automated tests**; UI is verified manually in the browser only.
- **Invitations:** no re-send rate limit; no reminder before expiry; the registration form
  does not prefill the invited email.
- **Dev only:** patient registration must start from the patient app origin (5274); from
  5273 Keycloak refuses the `redirect_uri`.

## Provider calendar — deferred review items

Found during the review of `2026-10-05-provider-calendar`; none blocks use, all to revisit.

- **Appointments crossing midnight** are drawn only on their start day and cut at 24:00;
  one that started the day before the visible range is returned but not drawn.
- **Flaky test:** the first full suite run on the branch had one failure whose name was not
  captured; five later runs passed. If it recurs, keep the full log and name it.
- **Accessibility:** the Dia/Semana toggle has no `aria-pressed`, today's header no
  `aria-current="date"`, the modality icon no `role="img"`; the patient search field and the
  cancellation reason have no label; creating by clicking the grid is mouse-only (the "Nova
  consulta" button is the keyboard path); cancelled cards have low contrast.
- **Grid layout:** with a visible scrollbar (Windows) the header columns drift from the body
  columns (`scrollbar-gutter: stable` on both); while a new week loads, the previous week's
  appointments can briefly stretch the hour range.
- **Form errors:** a 400 on a field with no visible error slot (time, modality) shows
  nothing; status changes show a generic toast for 400/422 instead of the server message;
  an empty duration field becomes 0.
- **Time zone helpers:** `zonedToInstant` corrects the offset once, so it can be an hour off
  inside a DST transition, and `offsetLabel` prints half-hour zones as "GMT+5.5". Harmless
  while everything is `America/Sao_Paulo` (no DST since 2019).
- **Tests to add:** exact boundaries (42-day range, 500-character reason, empty patient id);
  success paths for Confirmar, Falta and Falta → Agendada; another provider's patient through
  the list join; the HTTP list filtering by repeated `status` keys.
- **Hardening:** `AppointmentStatus.All` and `AppointmentRules.Modalities` are public mutable
  arrays; the `LastVisit` update has no concurrency guard.
- **`anamnys-aspire.Server.http`** has no requests for the appointment (or patient)
  endpoints.

## Documentation and housekeeping

- **`anamnys-aspire/CLAUDE.md` route tree gotcha is stale:** it says each app's
  `src/routeTree.gen.ts` must stay committed, but `.gitignore` ignores `**/src/routeTree.gen.ts`
  and no app tracks one. Decide which is right and align the other.

- **Notion "Modelo de Dados":** add `PatientAccounts` (split from `Patients`),
  `PatientDiagnoses`, `PatientInvitations` and `Patients.ArchivedAt` / `ContactEmail`.
  Blocked by the workspace's block limit.
- **`anamnys-aspire/CLAUDE.md`** frames HIPAA/BAA as hard requirements; the product operates
  under the LGPD (processor agreements, art. 39). Align the wording.
