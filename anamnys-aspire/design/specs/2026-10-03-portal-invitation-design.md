# Patient Portal Invitation

**Date:** 2026-10-03
**Status:** Approved
**Scope:** Delivery 2 of 3 of the patient-portal work. A provider opts a patient into the
portal; the server emails a single-use invitation; the patient accepts it while signed in
with the same email, which links the provider's record to the patient's account
(`Patients.AccountId`, from `2026-10-02-patient-accounts-design.md`). Delivery 3 (the
portal showing sessions per provider) reads that link.

---

## 1. Context

- Records and portal accounts are separate; `Patients.AccountId` is the only link, and
  nothing sets it yet. Logins are never linked to records by matching emails
  (`2026-10-01-patient-records-design.md` §8).
- Every provider record has a required `ContactEmail`.
- Only Keycloak sends email today (verification, password reset), through Mailpit in dev or
  the developer's own Resend account. The server sends none.
- The product operates in Brazil: BAA (a HIPAA instrument) does not apply; under the LGPD
  an email provider is a data processor and needs a processor agreement (art. 39), which
  Resend offers.

## 2. Decisions

| Topic | Decision |
|---|---|
| Sender | The server, through the **Resend HTTP API**; in dev without a Resend key, through **Mailpit's HTTP send API**. No SMTP library. |
| Token | 32 random bytes, base64url, only in the emailed link; the database stores its SHA-256. Valid 7 days, single use. |
| Who can accept | Only an account whose verified email equals the invitation's email (case-insensitive). |
| Email content | Patient's first name, provider's name, the link and the expiry date. No clinical data. |
| Pending invitations | At most one per record; re-sending revokes the previous one. |
| Removing access | Clears `AccountId`; account and record both remain. |
| Creating with opt-in | The create modal calls create, then invite: two requests, so a failed send never undoes the record. |

## 3. Data

```sql
CREATE TABLE "PatientInvitations" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"Email" text NOT NULL,
	"TokenHash" bytea NOT NULL CONSTRAINT "PatientInvitations_TokenHash_key" UNIQUE,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ExpiresAt" timestamp with time zone NOT NULL,
	"AcceptedAt" timestamp with time zone,
	"AcceptedAccountId" uuid,
	"RevokedAt" timestamp with time zone,
	CONSTRAINT "PatientInvitations_Closed_ck" CHECK (("AcceptedAt" IS NULL) OR ("RevokedAt" IS NULL)),
	CONSTRAINT "PatientInvitations_Accepted_ck" CHECK ((("AcceptedAt" IS NULL) = ("AcceptedAccountId" IS NULL)))
);
CREATE UNIQUE INDEX "PatientInvitations_OnePending_key" ON "PatientInvitations" ("PatientId")
	WHERE "AcceptedAt" IS NULL AND "RevokedAt" IS NULL;
-- FKs: PatientId → Patients ON DELETE CASCADE; AcceptedAccountId → PatientAccounts ON DELETE SET NULL
```

Migration `design/migrations/2026-10-04-patient-invitations.sql` (idempotent) creates the
table, the index and the FKs. A "pending" row that is past `ExpiresAt` is still the one
pending row: re-sending revokes it like any other.

**Portal status** (computed for the provider): `active` when the record has an
`AccountId` whose account is not disabled; otherwise `invited` when a pending invitation is
unexpired, `expired` when the pending one has expired, `none` otherwise.

## 4. API

Provider routes (provider cookie; record resolved with `Id == id && ProviderId == session
provider`, otherwise `404`):

| Route | Effect |
|---|---|
| `POST /api/phi/providers/me/patients/{id}/invitation` | Revoke any pending, create a new invitation to the current `ContactEmail`, send it. `204`. |
| `DELETE /api/phi/providers/me/patients/{id}/invitation` | Revoke the pending invitation. `204` (also when none). |
| `DELETE /api/phi/providers/me/patients/{id}/portal-access` | `AccountId = null`. `204` (also when none). |

`POST …/invitation` answers `409 { message }` when the record is archived or already has
active access, `503` when no email provider is configured (nothing written), and `502` when
the provider rejects the send (the new invitation is revoked; nothing stays pending).

`GET /api/phi/providers/me/patients/{id}` gains `portalStatus` (`none` | `invited` |
`expired` | `active`), `invitationSentAt` and `invitationExpiresAt` (from the pending
invitation, if any).

Patient route (patient cookie):

`POST /api/phi/patients/me/invitations/accept` with `{ "token": "…" }` →
`200 { "providerName": "…" }`, or `400 { "code": "…" }`:

- `invalid`: unknown token, revoked, expired or already accepted (one code, so a token
  reveals nothing);
- `email_mismatch`: the signed-in account's email differs from the invitation's;
- `already_linked`: the record already has an account, or this account already has a
  record of the same provider.

Acceptance runs in one transaction: set `Patients.AccountId`, set `AcceptedAt` and
`AcceptedAccountId`. The unique `(AccountId, ProviderId)` constraint is the final guard; a
violation maps to `already_linked`.

## 5. Email

`IEmailSender.SendAsync(EmailMessage message, CancellationToken)` with
`EmailMessage(string ToEmail, string ToName, string Subject, string Text, string Html)`.

- `ResendEmailSender`: typed `HttpClient`, `POST https://api.resend.com/emails`,
  `Authorization: Bearer {Email:ResendApiKey}`, body `{ from, to, subject, text, html }`.
- `MailpitEmailSender` (dev only): `POST {Email:MailpitBaseUrl}/api/v1/send`.
- `Email:Provider` selects one (`resend` | `mailpit`); absent → no sender registered and the
  invitation route answers `503`.
- AppHost: `Email:Provider = resend` with `Email:ResendApiKey` from the existing
  `Parameters:resend-api-key` when set, else `mailpit` with Mailpit's HTTP endpoint (dev
  runs only). `Email:From` reuses the Keycloak dev sender (`onboarding@resend.dev` /
  `noreply@anamnys.dev`), `Email:FromName = "Anamnys"`. `PatientPortal:BaseUrl` is the
  patient app's public base (`http://localhost:5274/patient` in dev).
- Resend's sandbox delivers only to the account owner's address until a domain is verified.
- Message (pt-BR), subject "Seu convite para o portal de pacientes Anamnys":
  "Olá, {first name}. {Provider name} convidou você para acessar o portal de pacientes
  Anamnys. Use o link abaixo para criar sua conta ou entrar com este e-mail. O convite vale
  até {date}." Link: `{PatientPortal:BaseUrl}/convite/{token}`.
- The token is never logged; failures are logged with the invitation id only.

## 6. Frontend

**Provider app.**

- Create modal, *Dados básicos*: checkbox "Convidar para o portal do paciente" (off),
  hint "Enviaremos um convite para o e-mail acima." Checked → after create, call invite; on
  failure the record page shows "Não foi possível enviar o convite" with a retry action.
- Record page, *Dados e contato* card, "Portal do paciente" block: status line and actions —
  none → **Convidar**; invited → "Convite enviado em {date}, vale até {date}", **Reenviar**,
  **Cancelar convite**; expired → **Reenviar**; active → "Acesso ativo ({login email})",
  **Remover acesso** (confirmation). No actions on an archived record.

**Patient app.**

- Route `/convite/$token` outside the authenticated layout. Signed out: explanation with
  **Entrar** and **Criar conta**, both returning to the same page. Signed in: accepts
  automatically and shows "Pronto! Você está conectada(o) a {provider}" with a link home.
  Errors: `invalid` → "Este convite não é mais válido. Peça um novo ao seu profissional.";
  `email_mismatch` → "Este convite foi enviado para outro e-mail. Entre com esse e-mail." with
  a sign-out action; `already_linked` → "Você já está conectada(o) a este profissional."

## 7. Testing

- Unit: token generation and hashing; message composition (no clinical fields, link
  shape).
- Postgres (shared fixture, fake `IEmailSender`): invite creates a pending row and sends to
  `ContactEmail` with the right link; re-send revokes the previous; cancel; remove access;
  archived / active / other provider refused; send failure revokes; accept succeeds and
  links; email mismatch; expired; revoked; already accepted; account already has a record of
  that provider; the plaintext token is never stored.
- HTTP end to end: as `dev.provider`, invite a throwaway record and read the message back
  from Mailpit's API (recipient, provider name, link), then delete the record. The test
  fixture forces Mailpit (empties `resend-api-key`) so tests never send real email.
- Browser: create with opt-in → message in Mailpit → open the link → register with the
  same email → accepted → record shows "Acesso ativo"; then the mismatch case.

## 8. Out of scope

The portal's list of providers and sessions (delivery 3); invitation reminders; rate
limiting re-sends; a verified sending domain and production email configuration.
