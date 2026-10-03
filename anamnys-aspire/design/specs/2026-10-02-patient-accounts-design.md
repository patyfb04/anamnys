# Patient Accounts Separate from Patient Records

**Date:** 2026-10-02
**Status:** Approved
**Scope:** Delivery 1 of 3 of the patient-portal work. Split the patient-portal login
(`PatientAccounts`) from the provider's clinical record (`Patients`), one account linked to
at most one record per provider. No user-visible change. Delivery 2 (portal invitation by
single-use token) and delivery 3 (portal shows past/upcoming sessions per provider) build
on this and have their own specs.

---

## 1. Context

- `design/specs/2026-09-13-patient-self-registration-design.md` merged the former
  `PatientAccounts` into `Patients`: one row per person, holding both the portal login
  (`Email`, `ExternalSubject`, `LastLoginAt`, `TermsAcceptedAt`, `DisabledAt`) and the
  clinical record (`ProviderId`, names, `DateOfBirth`, `LastVisit`, …). `ProviderId`
  became nullable so a self-registered person could have a row with no provider.
- That shape cannot represent one person seen by two providers: `Email` is globally unique,
  so only one row (one provider) can ever carry it. It also mixes retention regimes: the
  record is clinical history kept for at least five years (Res. CFP 001/2009); the login
  is a credential. The Notion "Modelo de Dados" already described `patient_account` as
  separate "de propósito: o prontuário tem guarda de cinco anos, a credencial não".
- `2026-10-01-patient-records-design.md` §8 already stopped binding logins to records by
  email and introduced `ContactEmail` on the record. Linking now happens only through an
  explicit invitation (delivery 2).

## 2. Decisions

| Topic | Decision |
|---|---|
| Shape | New table `PatientAccounts`; `Patients.AccountId` (nullable FK). |
| Cardinality | An account links to many records, at most one per provider: `UNIQUE (AccountId, ProviderId)`. |
| Deleting an account | `ON DELETE SET NULL`: the record survives with no account. |
| `Patients.ProviderId` | Back to `NOT NULL`: a record always belongs to a provider; a self-registered person has only an account. |
| Personal data | The account keeps what the person maintains in the portal (names, phone, date of birth). The record keeps what the provider maintains. No synchronisation; providers do not see the account's personal data (only its login email, as today). |
| Session identity | The patient realm's `LocalId` claim is the account id. Migration reuses the old row id as the account id so live sessions stay valid. |
| Records from the portal | `FirstLoginProvisioner` never creates a record. Records are created by providers (and later by invitation acceptance / booking). |

Rejected: keeping a provider-less `Patients` row as the "account" with records pointing to it
(every clinical query would have to exclude account rows — a forgotten filter leaks data);
an account↔record link table (a record never has two accounts, and "one record per
provider per account" is harder to enforce).

## 3. Schema

```sql
CREATE TABLE "PatientAccounts" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ExternalSubject" uuid NOT NULL CONSTRAINT "PatientAccounts_ExternalSubject_key" UNIQUE,
	"Email" text NOT NULL,
	"FirstName" text NOT NULL,
	"LastName" text NOT NULL,
	"Phone" text,
	"DateOfBirth" date,
	"LastLoginAt" timestamp with time zone,
	"TermsAcceptedAt" timestamp with time zone,
	"DisabledAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE UNIQUE INDEX "PatientAccounts_Email_key" ON "PatientAccounts" (lower("Email"));
```

`Patients`:

- adds `"AccountId" uuid` with
  `FOREIGN KEY ("AccountId") REFERENCES "PatientAccounts"("Id") ON DELETE SET NULL` and
  `CONSTRAINT "Patients_Account_Provider_key" UNIQUE ("AccountId", "ProviderId")`
  (Postgres treats NULL `AccountId`s as distinct, so unlinked records are unconstrained);
- `ProviderId` becomes `NOT NULL`;
- drops `Email`, `ExternalSubject`, `LastLoginAt`, `TermsAcceptedAt`, `DisabledAt`,
  `Patients_Email_key`, `Patients_ExternalSubject_key` and `Patients_Login_ck`;
- keeps `Phone` (provider-maintained) and `ContactEmail`.

### Migration — `design/migrations/2026-10-03-patient-accounts.sql`

Idempotent, one transaction, run after the two earlier migrations:

1. Create `PatientAccounts` if missing.
2. For every `Patients` row with `ExternalSubject`: insert an account with **the same
   `Id`**, copying subject, email, names, phone, date of birth and the login timestamps.
3. Rows with a `ProviderId` and a login get `AccountId = Id`.
4. Rows without a `ProviderId` (self-registrations) are deleted from `Patients` — but first
   the script raises an exception and rolls back if any of them is referenced by a clinical
   table (`Notes`, `Appointments`, `Sessions`, `ClinicalDocuments`, `ExternalDocuments`,
   `Dossiers`, `DisclosureAuthorizations`, `DisposalRecords`, `ScaleApplications`,
   `IntakeResponses`, `RecordingConsents`, `MedicationEntries`, `PatientDiagnoses`,
   `TreatmentPlans`, `FollowUpItems`, `BookingHolds`, `InsurerAuthorizations`,
   `AppointmentSeries`).
5. Add the FK and unique constraint, set `ProviderId NOT NULL`, drop the login columns
   and constraints.

A second run finds nothing to copy, nothing to delete, and every object already in place.

## 4. Server

- **Entities.** New `PatientAccount`. `Patient` loses the login properties and gains
  `Guid? AccountId`. `AnamnysDbContext` maps `PatientAccounts` (unique `ExternalSubject`)
  and the `Patient → PatientAccount` FK with `DeleteBehavior.SetNull`, plus the unique
  `(AccountId, ProviderId)` index.
- **`FirstLoginProvisioner.ResolvePatientAsync`** works on `PatientAccounts` only:
  1. account by subject → refuse if `DisabledAt` set → sync verified email (clash with
     another account keeps the old value, logged by id only) → return its id;
  2. otherwise require a verified email and insert a new account (names from
     `given_name`/`family_name`); on `DbUpdateException` re-read by subject, and if still
     absent the email belongs to another account → refuse.
- **`/api/auth/me`** (patient branch) and **`patients/me/profile`** (GET/PUT) read and
  write `PatientAccounts`.
- **`PatientRecords`.** `HasPortalAccount = AccountId != null && account not disabled`;
  `PortalEmail` from the joined account (null when disabled). The delete rule checks
  `AccountId` instead of `ExternalSubject`.
- No portal route reads `Patients` in this delivery.

## 5. Out of scope

- Invitation, acceptance, statuses (delivery 2). Portal views of records (delivery 3).
- Self-service account deletion / LGPD erasure (the `SET NULL` FK is ready for it).
- Updating the Notion "Modelo de Dados" (`patient` row: link to `patient_account`).

## 6. Testing

- **Provisioner** (EF InMemory): a first login creates only an account and no `Patient`;
  repeat login returns the same account; disabled account refused; unverified email
  refused; changed verified email synced; email held by another account not synced.
- **Postgres** (shared fixture): deleting an account leaves the record with
  `AccountId` null; a second record for the same account and provider is refused, for
  another provider accepted; account emails are unique case-insensitively;
  `Patients.ProviderId` is `NOT NULL`.
- **`PatientRecords`**: portal status and email come from the account; a record with an
  account cannot be deleted. The test seed's `BindPortalAccountAsync` creates an account
  and sets `AccountId`.
- **Migration**: a test creates a throwaway database, loads the previous schema
  (`anamnys-aspire.Tests/Data/Fixtures/schema-2026-10-02.sql`, the bootstrap script as of
  this spec), seeds an old-shape self-registration, a linked record and an unlinked record,
  runs the migration twice and asserts the result; a second case seeds a self-registration
  with a clinical row and asserts the migration fails and changes nothing.
- **HTTP**: patient routes still answer 401 to a provider-only session.
- **Browser**: register a patient through the portal (verification mail in Mailpit), sign
  in, view and edit the profile; the provider app is unchanged.
