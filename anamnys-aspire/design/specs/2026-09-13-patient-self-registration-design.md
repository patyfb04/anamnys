# Patient Self-Registration Design (Phase 2)

**Date:** 2026-09-13
**Status:** Proposed
**Scope:** Implements Phase 2 of `2026-09-07-provider-patient-registration-design.md` —
self-registration ("Comecar") for the `anamnys-patients` realm. Supersedes that spec's
section 5 sketch with a concrete design that accounts for the real runtime schema, and
folds `PatientAccounts` into `Patients` (see section 2) rather than keeping the two
tables `2026-09-07`'s sketch assumed.

---

## 1. Context

Phase 1 (`2026-09-07-provider-patient-registration-design.md`) wired provider
self-registration end-to-end and left Paciente disabled ("Em breve") on `/get-started`,
with a rough sketch of what Phase 2 would need.

That sketch assumed a patient's `PatientAccount` row could simply be created on demand,
the same way `FirstLoginProvisioner.ProvisionProviderAsync` creates a `Provider` row. It
did not account for the actual runtime schema: `DatabaseInitializer.cs` bootstraps the
database from the embedded `anamnys-db-script.sql`, not from `AnamnysDbContext`'s model,
and that script defines:

```sql
CREATE TABLE "Patients" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"FirstName" text NOT NULL,
	"LastName" text NOT NULL,
	...
);
CREATE TABLE "PatientAccounts" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL CONSTRAINT "PatientAccounts_PatientId_key" UNIQUE,
	...
	CONSTRAINT "PatientAccounts_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE
);
```

`Patients.ProviderId` is `NOT NULL` — every patient record was modeled as belonging to
exactly one provider (a provider's own patient list), consistent with every other
clinical table (`Appointments`, `Notes`, `Sessions`, ...) carrying a composite
`(PatientId, ProviderId)` foreign key back to `Patients`. A patient self-registering with
no prior relationship to any provider cannot satisfy that constraint as originally
written.

Confirmed with the product owner: a patient **can** self-register with no provider
association yet (a provider can also separately create a patient through the provider
portal — a different, already-broken path tracked as its own future fix, out of scope
here). The fix is to make `Patients.ProviderId` nullable; a self-registered patient
starts with no provider and is expected to be associated with one later through a flow
this spec does not build.

Also missing today: `AnamnysDbContext` has no `Patient` entity or `DbSet` at all — only
`PatientAccount`, which has always required its parent `Patient` row to already exist
(created by a provider invite, itself broken/unimplemented). This is the first code path
that ever creates a `Patients` row.

**Additional simplification, decided alongside the nullable-`ProviderId` fix:**
`PatientAccounts.PatientId` is already `UNIQUE` — the two tables have always been in a
strict 1:1 relationship, with no case anywhere in the codebase of one `Patients` row
having zero or multiple accounts. The split is real but adds a join for no behavioral
benefit: everything `PatientAccounts` represents (email, external subject, phone, login
timestamps, disablement) can live as nullable columns directly on `Patients`, exactly the
shape `Providers` already uses (a provider row and its login credential are one row,
never two). This spec folds `PatientAccounts` into `Patients` rather than adding a third
branch on top of the existing two-table shape — see section 2. Chapters 8-10 of
`documentation/` describe today's two-table split as intentional (clinical record vs.
portal login); that rationale doesn't argue against merging, it's the same distinction
this spec keeps, just as nullable columns on one row instead of a second table — the docs
get updated alongside the code (section 6).

## 2. Schema change: nullable `ProviderId`, and fold `PatientAccounts` into `Patients`

**`anamnys-db-script.sql`:**

- `Patients` gains every column `PatientAccounts` has today (`Email`, `Phone`,
  `ExternalSubject`, `LastLoginAt`, `TermsAcceptedAt`, `DisabledAt`), all nullable — a
  `Patients` row can exist with none of them set (a provider's clinical-only record, no
  portal access ever) just as easily as with all of them set (a bound, logged-in patient).
  `Email` and `ExternalSubject` each get a unique constraint, same as they have today on
  `PatientAccounts` (Postgres unique constraints allow any number of `NULL`s, so this
  imposes nothing on rows that never get an account).
- `Patients.ProviderId` drops `NOT NULL` — a self-registered patient starts with none.
  `Patients_Id_ProviderId_key UNIQUE("Id","ProviderId")` still holds with `ProviderId`
  null.
- `PatientAccounts` is dropped entirely, along with its FK to `Patients`.
- No other table is touched — every table with a `(PatientId, ProviderId)` composite FK
  back to `Patients` (`Appointments`, `Notes`, `Sessions`, `ExternalDocuments`,
  `ClinicalDocuments`) is only ever populated once a patient has a provider relationship,
  which this spec does not create.

This requires the same local reset as any schema change in this repo: drop and recreate
the Postgres volume so `DatabaseInitializer` re-runs the script (it only runs against an
empty database).

## 3. `Patient` entity replaces `PatientAccount`

`anamnys-aspire.Server/Data/Entities/PatientAccount.cs` is deleted.
`anamnys-aspire.Server/Data/Entities/Patient.cs` (new) is the single entity for the
merged table:

```csharp
public class Patient
{
    public Guid Id { get; set; }
    public Guid? ProviderId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string? PreferredLanguage { get; set; }
    public DateTimeOffset? LastVisit { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public Guid? ExternalSubject { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`AnamnysDbContext`: `DbSet<PatientAccount> PatientAccounts` is replaced by
`DbSet<Patient> Patients`, and its `modelBuilder.Entity<PatientAccount>(...)` block by:

```csharp
modelBuilder.Entity<Patient>(e =>
{
    e.ToTable("Patients");
    e.HasKey(x => x.Id);
    e.HasIndex(x => x.ExternalSubject).IsUnique();
    e.HasIndex(x => x.Email).IsUnique();
});
```

Same shape as `Provider`'s config — no coincidence, it's now the same pattern.

## 4. `FirstLoginProvisioner.ResolvePatientAsync` — rewritten against `Patients`

Same three branches as today, reading and writing `db.Patients` instead of
`db.PatientAccounts` (no join, no separate row to create-and-link):

1. **Bound by subject** — unchanged logic, `db.Patients.SingleOrDefaultAsync(p =>
   p.ExternalSubject == subject)`.
2. **Unverified email** — unchanged, throws before looking at any row.
3. **Unclaimed row matches email** — unchanged: binds `ExternalSubject` onto the existing
   `Patients` row (the provider-invite path, once that's fixed, creates exactly this kind
   of row — `ExternalSubject` null, everything else set).
4. **New — nothing matches:** insert one new `Patients` row directly — `ProviderId =
   null`, `FirstName`/`LastName` from the token's `given_name`/`family_name` claims
   (already emitted by both realms' default `profile` scope, same mappers used for the
   provider's `name` claim), `Email = email`, `ExternalSubject = subject`. `Phone`,
   `DateOfBirth`, `PreferredLanguage`, `TermsAcceptedAt` stay null — nothing else is
   collected at registration. One `SaveChangesAsync` call, one row. Race handling mirrors
   `ProvisionProviderAsync`: on `DbUpdateException`, detach and re-query by
   `ExternalSubject`; if still not found, rethrow.

Today's branch 3 ends with `?? throw new InvalidOperationException("No patient account
matches this login.")` when no unclaimed row matches — that throw is replaced by the new
create branch.

`given_name`/`family_name` are expected to be present (both realms request the standard
OIDC `profile` scope); no fallback splitting of a combined `name` claim is added — YAGNI,
and a missing claim here would indicate a realm misconfiguration worth surfacing, not
silently working around.

`AuthEndpoints.cs`'s `/api/auth/me` patient branch, which today reads
`db.PatientAccounts.SingleOrDefaultAsync(a => a.Id == localId)`, switches to
`db.Patients` the same way.

## 5. Realm config

`keycloak/realms/anamnys-patients.json`: `registrationAllowed: false` → `true`. No
`registrationEmailAsUsername` and no custom user-profile component — this realm has no
existing declarative user-profile override (unlike providers, which added `crpNumber`/
`crpRegion`), so Keycloak's default profile (email, firstName, lastName) already applies
and already matches what `Register.tsx` needs.

## 6. Front-end

- **`apps/keycloak-theme/src/login/pages/Register.tsx`** — no change. It already renders
  whatever fields the realm's user profile declares via `UserProfileFormFields`; nothing
  in it is provider-specific.
- **`packages/shared/src/lib/appUrls.ts`** — add `patientRegister`, mirroring
  `providerRegister`'s dev/prod split (patient's dev server is a fixed port, `5274`, and
  only that dev server's proxy accepts `/auth` with the right `Host` header):

  ```ts
  const patientDevOrigin = "http://localhost:5274";
  ...
  patientRegister: import.meta.env.DEV
    ? `${patientDevOrigin}/auth/patient/register`
    : "/auth/patient/register",
  ```

- **`apps/web/src/routes/_marketing/get-started.tsx`** — the Paciente `Card` stops being
  disabled: drop `opacity-60 cursor-not-allowed`/`aria-disabled`/the "Em breve" badge,
  wrap it in `<a href={appUrls.patientRegister}>` exactly like the Profissional card.
- No dashboard/landing page work needed: `apps/patient/src/routes/index.tsx` is already a
  standing placeholder ("Patient portal. Booking, rescheduling and cancelling appointments
  will live here.") and is a perfectly fine post-registration landing — unlike Phase 1,
  where the provider app's default redirect actively pointed at the wrong screen
  (`/patients`), the patient app has nothing wrong to fix here.
- i18n: `welcome.getStartedPage.patientBody` already exists and stays as-is;
  `welcome.comingSoon` stops being referenced by this card (still used elsewhere, not
  removed).

## 7. Documentation updates

`documentation/08-session-lifecycle-in-code.md`, `09-data-access-and-ef-core-today.md`,
and `10-the-full-data-model.md` all describe `PatientAccounts` as a table distinct from
`Patients`. Each gets a small edit reflecting the merge:

- Chapter 8: the "Patients: bind-only, never create" section's `PatientAccount`
  references become `Patients`; the heading itself changes to describe three branches
  (bind, unverified-throw, create) since bind-only is no longer accurate.
- Chapter 9: `DbSet<PatientAccount> PatientAccounts` → `DbSet<Patient> Patients` in the
  quoted code, and the "five tables wired into EF Core" list updates its naming.
- Chapter 10: the `Patients`/`PatientAccounts` distinction in "Patients, providers, and
  professional identity" is rewritten to describe one table carrying both clinical and
  account fields, nullable until each applies.

## 8. Testing

`anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs`:

- Every `PatientAccount` reference (test fixtures, assertions) becomes `Patient`,
  querying `db.Patients` instead of `db.PatientAccounts`.
- `ProvisionAsync_WhenPatientAccountDoesNotExist_ThrowsRatherThanCreating` is renamed and
  rewritten to `ProvisionAsync_WhenNoPatientMatches_CreatesNewRow` — the old assertion
  (throws, zero rows) is now wrong; the new one asserts a `Patients` row now exists with
  `ProviderId` null and `FirstName`/`LastName` from the token's `given_name`/`family_name`
  claims.
- `PrincipalFor(...)` test helper gains optional `givenName`/`familyName` parameters.
- A new race test mirroring
  `ProvisionAsync_WhenCalledTwiceForSameSubject_DoesNotCreateSecondRow` (provider) for the
  patient create branch: calling `ProvisionAsync` twice with the same subject and no
  existing row creates exactly one `Patients` row.
- No new `RegisterEndpointTests` — `/auth/patient/register` already exists and is already
  exercised generically by the existing provider-scoped tests proving the endpoint-building
  code itself is realm-agnostic; only the realm's `registrationAllowed` flag (Keycloak's own
  gate, not application code) differs, and that's config, not logic.

Manual click-through (after the Postgres volume reset for the schema change and realm
re-import): open `/get-started`, click Paciente, fill in Keycloak's registration form,
confirm redirect lands on the patient app's `/` showing the placeholder copy, and confirm
a `Patients` row now exists with `ProviderId` null, `Email` set, and `ExternalSubject`
bound.

## 9. Out of scope

- **Provider invites a patient** (`apps/provider/src/routes/_app/patients/new.tsx` →
  `POST /patients`, which doesn't exist) — unchanged, still its own future spec. Once
  fixed, it will create a `Patients` row with a real `ProviderId` and no `ExternalSubject`
  (an unclaimed invite), which branch 3 (unchanged by this spec) already binds correctly.
- **Associating a self-registered (provider-less) patient with a provider later** — e.g.
  when they book their first appointment. Not designed here; `Patient.ProviderId` simply
  stays null until some future flow sets it.
- **`DateOfBirth`/`PreferredLanguage`/`Phone`/`TermsAcceptedAt` collection** — none of
  these are collected at registration, same reasoning as Phase 1 (`Specialty`/
  `PreferredNoteFormat` for providers): sensible nulls now, editable later where relevant.

## 10. Risks

- **Schema drift between `anamnys-db-script.sql` and any other copy of this schema** (a
  staging/prod database already provisioned with the old two-table shape) — this spec
  only changes the bootstrap script; an already-initialized database needs the
  corresponding `ALTER TABLE`/data-migration run by hand. Local dev and CI both bootstrap
  fresh, so this only matters if a persistent shared environment exists — flagged here,
  not resolved, since none is known to exist yet.
- **`given_name`/`family_name` absent from an older/misconfigured token** — the create
  branch has no fallback and will insert empty strings (the entity's default `= ""`)
  rather than throw; acceptable since both realms already request `profile` scope by
  default and this mirrors how the Provider branch already tolerates missing `crpNumber`/
  `crpRegion` by leaving them null rather than guessing.
- **Merging two tables the moment before adding a new write path to one of them** compounds
  two changes at once. Kept together deliberately, not as an oversight: the merge only
  removes indirection the new branch would otherwise have to route through immediately
  (create a `Patient` row, then a `PatientAccount` row pointing at it), so doing it after
  would mean writing the two-table version now and rewriting it again right after.
