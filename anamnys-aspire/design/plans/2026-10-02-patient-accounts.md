# Patient Accounts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the patient-portal login (`PatientAccounts`) from the provider's clinical record (`Patients`), with no user-visible change.

**Architecture:** New table + nullable `Patients.AccountId` FK (`SET NULL`), unique per `(AccountId, ProviderId)`. An idempotent SQL migration moves existing logins into accounts reusing the row id. The patient realm's `LocalId` becomes the account id; provisioner, `/api/auth/me` and the patient profile routes read accounts; provider-side code reads the link.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, PostgreSQL 18, xUnit v3 + FluentAssertions.

**Spec:** `design/specs/2026-10-02-patient-accounts-design.md`

## Global Constraints

- Apply the migration to the local dev database **before** running Postgres tests: the test fixture shares the dev volume.
- `aspire stop` before building if the app is running. Tests: `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"`.
- No email address in logs (PHI); log ids only.
- A record from the portal is never created by login code.

---

### Task 1: Migration, bootstrap script, migration tests

**Files:** Create `design/migrations/2026-10-03-patient-accounts.sql`, `anamnys-aspire.Tests/Data/Fixtures/schema-2026-10-02.sql` (= `git show develop:anamnys-aspire/anamnys-db-script.sql`), `anamnys-aspire.Tests/Data/PatientAccountsMigrationTests.cs`. Modify `anamnys-db-script.sql`, `anamnys-aspire.Tests/anamnys-aspire.Tests.csproj` (copy fixtures and the migrations folder to output).

- [ ] Tests (each creates `mig_<guid>` database via the fixture's connection, drops it in `finally`):
  - `Migration_MovesLoginsToAccounts_AndIsIdempotent`: old-shape rows — self-registration (no provider, subject+email), linked record (provider+subject+email), unlinked record (provider only). Run twice. Assert: two accounts with the original ids and data; linked record has `AccountId = Id`; self-registration row gone from `Patients`; unlinked record untouched; `Patients` has no `Email` column; `ProviderId` is not nullable.
  - `Migration_SelfRegistrationWithClinicalRow_AbortsAndChangesNothing`: self-registration + a `MedicationEntries` row for it → script throws; `PatientAccounts` table absent, `Patients.Email` column still present.
- [ ] Write the migration (single `BEGIN … COMMIT`; `DO` blocks guarded by `information_schema` checks).
- [ ] Update `anamnys-db-script.sql` to the new shape.
- [ ] Run, apply migration to dev DB twice, commit `feat: add patient accounts migration and schema`.

### Task 2: Entities, mapping, constraint tests, test seed

**Files:** Create `Data/Entities/PatientAccount.cs`, `anamnys-aspire.Tests/Data/PatientAccountConstraintTests.cs`. Modify `Data/Entities/Patient.cs`, `Data/AnamnysDbContext.cs`, `anamnys-aspire.Tests/Patients/PatientSearchSeed.cs` (`BindPortalAccountAsync` inserts an account and sets `AccountId`), delete `anamnys-aspire.Tests/Data/PatientLoginConstraintTests.cs`.

- [ ] Constraint tests: delete account → record keeps `AccountId` null; duplicate `(AccountId, ProviderId)` refused, other provider accepted; email unique case-insensitively; `ProviderId` null refused.
- [ ] Entities + mapping (`HasOne<PatientAccount>().WithMany().HasForeignKey(AccountId).OnDelete(SetNull)`, unique index `(AccountId, ProviderId)`, `PatientAccounts` unique `ExternalSubject`).
- [ ] Server compiles only after Tasks 3–5; do Tasks 2–5 before running the suite, commit per task.

### Task 3: FirstLoginProvisioner on accounts

- [ ] Rewrite patient tests in `FirstLoginProvisionerTests.cs` against `db.PatientAccounts` (creates account and no patient; same subject twice → same id; disabled refused; unverified refused; verified email change synced; email held by another account not synced).
- [ ] Rewrite `ResolvePatientAsync` / `SyncPatientEmailAsync` on `PatientAccount`.

### Task 4: `/api/auth/me` and patient profile on accounts

- [ ] `AuthEndpoints` patient branch and `ProfileEndpoints` `patients/me/profile` GET/PUT read/write `db.PatientAccounts`.

### Task 5: PatientRecords portal status and delete rule

- [ ] `GetAsync`: left-join account; `HasPortalAccount = account != null && account.DisabledAt == null`; `PortalEmail` likewise. `DeleteAsync`: conflict when `AccountId != null`.
- [ ] Existing `PatientRecordsTests` (portal blocker case, via the new seed) must pass; add `Get_PortalStatusComesFromLinkedAccount`.
- [ ] Full suite; commit Tasks 2–5 as `feat: separate patient portal accounts from provider records`.

### Task 6: Verify and document

- [ ] Browser: self-register a patient in the portal (Mailpit verification), sign in, view and edit profile; provider app unchanged.
- [ ] Docs: chapter on data model / patient app mentions accounts vs records; Patient.cs comments.
- [ ] Commit `docs: …`.
