# Patient Self-Registration Design (Phase 2)

**Date:** 2026-09-13
**Status:** Proposed
**Scope:** Implements Phase 2 of `2026-09-07-provider-patient-registration-design.md` —
self-registration ("Comecar") for the `anamnys-patients` realm. Supersedes that spec's
section 5 sketch with a concrete design that accounts for the real runtime schema.

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

## 2. Schema change

**`anamnys-db-script.sql`** — `Patients.ProviderId` drops `NOT NULL`. No other column or
constraint changes: `Patients_Id_ProviderId_key UNIQUE("Id","ProviderId")` still holds
with `ProviderId` null (SQL unique constraints treat NULL as distinct from any other
value, including other NULLs). No other table in the script is touched — every table
with a `(PatientId, ProviderId)` composite FK back to `Patients` (`Appointments`, `Notes`,
`Sessions`, `ExternalDocuments`, `ClinicalDocuments`) is only ever populated once a
patient has a provider relationship, which this spec does not create.

This requires the same local reset as any realm/schema change in this repo: drop and
recreate the Postgres volume so `DatabaseInitializer` re-runs the script (it only runs
against an empty database).

## 3. New `Patient` entity

`anamnys-aspire.Server/Data/Entities/Patient.cs` (new), mapping the existing `Patients`
table:

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
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`AnamnysDbContext` gains `DbSet<Patient> Patients` and:

```csharp
modelBuilder.Entity<Patient>(e =>
{
    e.ToTable("Patients");
    e.HasKey(x => x.Id);
});
```

No unique index beyond the primary key — `ProviderId` is not unique, and the composite
`(Id, ProviderId)` uniqueness is enforced by Postgres, not by anything this provisioner
needs EF to check.

## 4. `FirstLoginProvisioner.ResolvePatientAsync` — third branch

Branch order becomes:

1. **Bound by subject** — unchanged, returns immediately (or throws if disabled).
2. **Unverified email** — unchanged, throws before looking at any row.
3. **Unclaimed row matches email** — unchanged: binds `ExternalSubject` to the existing
   row (this is the provider-invite path, once that's fixed).
4. **New — nothing matches:** create a `Patient` row (`ProviderId = null`, `FirstName`/
   `LastName` from the token's `given_name`/`family_name` claims — already emitted by
   both realms' default `profile` scope, same mappers used for the provider's `name`
   claim) and a `PatientAccount` row referencing it (`ExternalSubject = subject`,
   `Email = email`, `Phone`/`TermsAcceptedAt` left null, same reasoning as the original
   Phase 2 sketch: nothing else is collected at registration). Both inserts go through
   one `SaveChangesAsync` call (implicit single transaction). Race handling mirrors
   `ProvisionProviderAsync`: on `DbUpdateException`, detach and re-query by
   `ExternalSubject`; if still not found, rethrow.

Today's branch 3 ends with `?? throw new InvalidOperationException("No patient account
matches this login.")` when no unclaimed row matches — that throw is replaced by the new
create branch.

`given_name`/`family_name` are expected to be present (both realms request the standard
OIDC `profile` scope); no fallback splitting of a combined `name` claim is added — YAGNI,
and a missing claim here would indicate a realm misconfiguration worth surfacing, not
silently working around.

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

## 7. Testing

`anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs`:

- `ProvisionAsync_WhenPatientAccountDoesNotExist_ThrowsRatherThanCreating` is renamed and
  rewritten to `ProvisionAsync_WhenNoPatientAccountMatches_CreatesPatientAndAccount` —
  the old assertion (throws, zero rows) is now wrong; the new one asserts a `Patient` and
  a bound `PatientAccount` both exist, `Patient.ProviderId` is null, and `FirstName`/
  `LastName` come from the token's `given_name`/`family_name` claims.
- `PrincipalFor(...)` test helper gains optional `givenName`/`familyName` parameters.
- A new race test mirroring
  `ProvisionAsync_WhenCalledTwiceForSameSubject_DoesNotCreateSecondRow` (provider) for the
  patient create branch: calling `ProvisionAsync` twice with the same subject and no
  existing account creates exactly one `Patient`/`PatientAccount` pair.
- No new `RegisterEndpointTests` — `/auth/patient/register` already exists and is already
  exercised generically by the existing provider-scoped tests proving the endpoint-building
  code itself is realm-agnostic; only the realm's `registrationAllowed` flag (Keycloak's own
  gate, not application code) differs, and that's config, not logic.

Manual click-through (after the Postgres volume reset for the schema change and realm
re-import): open `/get-started`, click Paciente, fill in Keycloak's registration form,
confirm redirect lands on the patient app's `/` showing the placeholder copy, and confirm
a `Patients` row (`ProviderId` null) and a bound `PatientAccounts` row now exist.

## 8. Out of scope

- **Provider invites a patient** (`apps/provider/src/routes/_app/patients/new.tsx` →
  `POST /patients`, which doesn't exist) — unchanged, still its own future spec. Once
  fixed, it will create a `Patient` row with a real `ProviderId` and an unclaimed
  `PatientAccount`, which branch 3 (unchanged by this spec) already binds correctly.
- **Associating a self-registered (provider-less) patient with a provider later** — e.g.
  when they book their first appointment. Not designed here; `Patient.ProviderId` simply
  stays null until some future flow sets it.
- **`DateOfBirth`/`PreferredLanguage`/`Phone`/`TermsAcceptedAt` collection** — none of
  these are collected at registration, same reasoning as Phase 1 (`Specialty`/
  `PreferredNoteFormat` for providers): sensible nulls now, editable later where relevant.

## 9. Risks

- **Schema drift between `anamnys-db-script.sql` and any other copy of this schema** (a
  staging/prod database already provisioned with `ProviderId NOT NULL`) — this spec only
  changes the bootstrap script; an already-initialized database needs an explicit
  `ALTER TABLE "Patients" ALTER COLUMN "ProviderId" DROP NOT NULL` run by hand. Local dev
  and CI both bootstrap fresh, so this only matters if a persistent shared environment
  exists — flagged here, not resolved, since none is known to exist yet.
- **`given_name`/`family_name` absent from an older/misconfigured token** — the create
  branch has no fallback and will insert empty strings (the entity's default `= ""`)
  rather than throw; acceptable since both realms already request `profile` scope by
  default and this mirrors how the Provider branch already tolerates missing `crpNumber`/
  `crpRegion` by leaving them null rather than guessing.
