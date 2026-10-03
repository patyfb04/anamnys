# Known gaps — patient list, records, accounts and portal invitation

**Date:** 2026-10-03
**Status:** Open. The official backlog is the Notion page "Plano de Implementação"; move
these items there when the workspace has free blocks, then delete this file.

Covers what is still missing after `2026-09-27-patient-list`, `2026-10-01-patient-records`,
`2026-10-02-patient-accounts` and `2026-10-03-portal-invitation`.

## To call these flows complete

1. **Delivery 3 — patient portal.** After accepting an invitation the patient lands on the
   placeholder home. Build "Meus profissionais" with past and upcoming sessions per linked
   provider, scheduling data only, never clinical content (third spec of the portal work).
2. **Provider app accepts a patient-only session.** `apps/provider/src/routes/_app.tsx`
   gates on `/api/auth/me`, which answers for any realm. A browser holding only a patient
   session opens the provider app; every PHI call returns 401, so nothing leaks, but the gate
   should require the providers realm and send the user to provider login otherwise.
3. **Email in production.** Verify a sending domain in Resend and configure `Email__*` in the
   deployed environment. Keycloak has no production SMTP either: account verification and
   password reset do not work outside dev. Until a domain is verified, Resend's sandbox only
   delivers to the account owner's own address.

## Gaps inside the existing flows

- **Appointments have no write path.** "Próximo atendimento" and "Último atendimento" only
  show seeded data; there is no scheduling UI, and nothing updates `Patients.LastVisit`.
- **Notes.** "Notas recentes" on the record links to the note editor, still a placeholder;
  `PatientNotesView` (`/patients/$id/notes`) calls `notesApi` routes that do not exist.
- **Patient rights (LGPD).** The portal cannot list or remove provider links, and the
  patient cannot delete their account. `Patients.AccountId ON DELETE SET NULL` is ready.
- **PHI read logging.** `AccessLogs` exists; nothing writes to it.
- **Mobile check** of the "Portal do paciente" block and the invitation page at 390px.
- **No frontend automated tests**; UI is verified manually in the browser only.
- **Invitations:** no re-send rate limit; no reminder before expiry; the registration form
  does not prefill the invited email.
- **Dev only:** patient registration must start from the patient app origin (5274); from
  5273 Keycloak refuses the `redirect_uri`.

## Documentation and housekeeping

- **Notion "Modelo de Dados":** add `PatientAccounts` (split from `Patients`),
  `PatientDiagnoses`, `PatientInvitations` and `Patients.ArchivedAt` / `ContactEmail`.
  Blocked by the workspace's block limit.
- **`anamnys-aspire/CLAUDE.md`** frames HIPAA/BAA as hard requirements; the product operates
  under the LGPD (processor agreements, art. 39). Align the wording.
