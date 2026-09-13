# Patient Self-Registration (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a patient self-register through Keycloak on `/get-started`, landing on the patient app with a real `Patients` row — no provider invite required — by folding `PatientAccounts` into `Patients` and adding a third (create) branch to `FirstLoginProvisioner.ResolvePatientAsync`.

**Architecture:** `Patients.ProviderId` becomes nullable and every column `PatientAccounts` had (`Email`, `Phone`, `ExternalSubject`, `LastLoginAt`, `TermsAcceptedAt`, `DisabledAt`) moves onto `Patients` as nullable columns — the same one-row-per-identity shape `Provider` already uses. `FirstLoginProvisioner.ResolvePatientAsync` keeps its existing bind-by-subject and bind-by-unclaimed-email branches (now reading/writing `Patients` directly, no join) and gains a third branch: when nothing matches, insert a new `Patients` row with `ProviderId = null`. The already-generic `/auth/{realm}/register` endpoint and `Register.tsx` theme page need no change — only the realm config and `apps/web`'s persona picker do.

**Tech Stack:** ASP.NET Core + EF Core (Npgsql), raw-SQL schema bootstrap (`DatabaseInitializer.cs` running `anamnys-db-script.sql`), xUnit + FluentAssertions (`InMemory` provider for `FirstLoginProvisionerTests`, real Aspire-hosted Postgres for the endpoint tests), Keycloak realm JSON, TanStack Router (file-based), React + i18next.

**Spec:** `anamnys-aspire/design/specs/2026-09-13-patient-self-registration-design.md`

## Global Constraints

- `PatientAccounts` is deleted, not deprecated — no code should reference it after Task 2.
- `Patients.ProviderId` stays null for self-registered patients; nothing in this plan ever sets it. Associating a patient with a provider later is explicitly out of scope (spec section 9).
- No new fields are collected at registration beyond email/first name/last name — `Phone`, `DateOfBirth`, `PreferredLanguage`, `TermsAcceptedAt` all stay null, same reasoning as the provider phase (`Specialty`/`PreferredNoteFormat` there).
- Every schema change requires the same local reset as any other change to `anamnys-db-script.sql` or a realm JSON: remove the Keycloak/Postgres Docker volumes so `DatabaseInitializer` and Keycloak's realm import both re-run against an empty state.
- Patient dev server: port 5274, `base: '/patient/'`. `/auth/*` is proxied there without `changeOrigin`, same reasoning as the provider dev server (Chapter/`get-started.tsx` comment already document this for `providerRegister`).

---

### Task 1: Schema — merge `PatientAccounts` into `Patients`, make `ProviderId` nullable

**Files:**
- Modify: `anamnys-aspire/anamnys-db-script.sql:460-490` (the `PatientAccounts` and `Patients` `CREATE TABLE` statements)
- Modify: `anamnys-aspire/anamnys-db-script.sql:931` (the `PatientAccounts_PatientId_fkey` line)

**Interfaces:**
- Consumes: nothing.
- Produces: a `Patients` table with columns `Id, ProviderId (nullable), FirstName, LastName, DateOfBirth, PreferredLanguage, LastVisit, Email (nullable, unique), Phone, ExternalSubject (nullable, unique), LastLoginAt, TermsAcceptedAt, DisabledAt, CreatedAt, UpdatedAt`. No `PatientAccounts` table. Task 2's `Patient` entity maps exactly these columns.

- [ ] **Step 1: Replace the two `CREATE TABLE` statements**

In `anamnys-aspire/anamnys-db-script.sql`, find:

```sql
CREATE TABLE "PatientAccounts" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL CONSTRAINT "PatientAccounts_PatientId_key" UNIQUE,
	"Email" text NOT NULL CONSTRAINT "PatientAccounts_Email_key" UNIQUE,
	"ExternalSubject" uuid CONSTRAINT "PatientAccounts_ExternalSubject_unique" UNIQUE,
	"Phone" text,
	"LastLoginAt" timestamp with time zone,
	"TermsAcceptedAt" timestamp with time zone,
	"DisabledAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL
);
```

Delete this block entirely — leave the `CREATE TABLE "PatientQuotes" (...)` block right after it untouched.

Then find the `Patients` table a few lines below:

```sql
CREATE TABLE "Patients" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid NOT NULL,
	"FirstName" text NOT NULL,
	"LastName" text NOT NULL,
	"DateOfBirth" date,
	"PreferredLanguage" text,
	"LastVisit" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "Patients_Id_ProviderId_key" UNIQUE("Id","ProviderId")
);
```

Replace it with:

```sql
CREATE TABLE "Patients" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ProviderId" uuid,
	"FirstName" text NOT NULL,
	"LastName" text NOT NULL,
	"DateOfBirth" date,
	"PreferredLanguage" text,
	"LastVisit" timestamp with time zone,
	"Email" text CONSTRAINT "Patients_Email_key" UNIQUE,
	"Phone" text,
	"ExternalSubject" uuid CONSTRAINT "Patients_ExternalSubject_key" UNIQUE,
	"LastLoginAt" timestamp with time zone,
	"TermsAcceptedAt" timestamp with time zone,
	"DisabledAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "Patients_Id_ProviderId_key" UNIQUE("Id","ProviderId")
);
```

(`ProviderId uuid` with no `NOT NULL` is the only change to that column; the new columns are inserted between `LastVisit` and `CreatedAt` so `CreatedAt`/`UpdatedAt` stay last, matching every other table's convention in this file.)

- [ ] **Step 2: Remove the now-dangling foreign key**

Find and delete this line entirely:

```sql
ALTER TABLE "PatientAccounts" ADD CONSTRAINT "PatientAccounts_PatientId_fkey" FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
```

- [ ] **Step 3: Manual verification**

There's no automated schema test in this repo (confirmed by Chapter 9/the Phase 1 plan — realm/schema JSON changes are verified by hand). From `anamnys-aspire/`:

```bash
aspire stop
docker volume ls | grep -E 'keycloak-data|postgres-data'
docker volume rm <postgres-data>   # and <keycloak-data> if present
aspire start
```

Then, once Postgres is up:

```bash
docker exec -it <postgres-container> psql -U postgres -d anamnys -c '\d "Patients"'
```

Expected: `ProviderId` shows no `not null`; the column list includes `Email`, `Phone`, `ExternalSubject`, `LastLoginAt`, `TermsAcceptedAt`, `DisabledAt`; there is no `\d "PatientAccounts"` table at all (`psql` reports "Did not find any relation" if you try). Task 7's full test run is the broader regression check — a broken script would fail every test that boots the full Aspire host, not just this one.

- [ ] **Step 4: Commit**

```bash
git add anamnys-aspire/anamnys-db-script.sql
git commit -m "feat: fold PatientAccounts into Patients, make ProviderId nullable

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: Replace the `PatientAccount` entity with `Patient` (no behavior change)

**Files:**
- Delete: `anamnys-aspire/anamnys-aspire.Server/Data/Entities/PatientAccount.cs`
- Create: `anamnys-aspire/anamnys-aspire.Server/Data/Entities/Patient.cs`
- Modify: `anamnys-aspire/anamnys-aspire.Server/Data/AnamnysDbContext.cs:9,24-30`
- Modify: `anamnys-aspire/anamnys-aspire.Server/Auth/AuthEndpoints.cs:53-61`
- Modify: `anamnys-aspire/anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs:159-221` (`ResolvePatientAsync`)
- Modify: `anamnys-aspire/anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs` (every `PatientAccount` reference)

**Interfaces:**
- Consumes: nothing new.
- Produces: `Patient` entity (`Id, ProviderId?, FirstName, LastName, DateOfBirth?, PreferredLanguage?, LastVisit?, Email?, Phone?, ExternalSubject?, LastLoginAt?, TermsAcceptedAt?, DisabledAt?, CreatedAt, UpdatedAt`), `AnamnysDbContext.Patients` (`DbSet<Patient>`). Task 3 adds a third branch to `ResolvePatientAsync`, which this task leaves with its existing two branches, just retargeted at `Patients`.

This is a pure rename/retarget — behavior must stay identical to today (bind-by-subject, bind-by-unclaimed-email, throw otherwise). It's verified by the existing test suite passing unchanged (aside from mechanical `PatientAccount` → `Patient` renames), not by new tests.

- [ ] **Step 1: Delete `PatientAccount.cs`, create `Patient.cs`**

Delete `anamnys-aspire/anamnys-aspire.Server/Data/Entities/PatientAccount.cs`.

Create `anamnys-aspire/anamnys-aspire.Server/Data/Entities/Patient.cs`:

```csharp
namespace Anamnys.Server.Data.Entities;

public class Patient
{
    public Guid Id { get; set; }
    public Guid? ProviderId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string? PreferredLanguage { get; set; }
    public DateTimeOffset? LastVisit { get; set; }

    // Everything below used to live on the separate PatientAccounts table — a patient's
    // portal login is now just nullable columns on their clinical record, the same shape
    // Provider already uses. Null on all of them means a provider created this patient
    // with no portal invite; Email + ExternalSubject both set means a bound, logged-in
    // patient.
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

- [ ] **Step 2: Update `AnamnysDbContext.cs`**

Change line 9 from:

```csharp
    public DbSet<PatientAccount> PatientAccounts => Set<PatientAccount>();
```

to:

```csharp
    public DbSet<Patient> Patients => Set<Patient>();
```

Change the `modelBuilder.Entity<PatientAccount>(...)` block (lines 24-30):

```csharp
        modelBuilder.Entity<PatientAccount>(e =>
        {
            e.ToTable("PatientAccounts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });
```

to:

```csharp
        modelBuilder.Entity<Patient>(e =>
        {
            e.ToTable("Patients");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });
```

- [ ] **Step 3: Update `AuthEndpoints.cs`'s `/api/auth/me` patient branch**

Change:

```csharp
            var patientAuth = await httpContext.AuthenticateAsync(AuthSchemes.PatientCookie);
            if (patientAuth.Succeeded)
            {
                return await LoadMe(patientAuth.Principal!, Realms.Patients, async localId =>
                {
                    var account = await db.PatientAccounts.SingleOrDefaultAsync(a => a.Id == localId, cancellationToken);
                    return account is null
                        ? null
                        : new MeResponse(account.Id, account.Email, account.Email, Realms.Patients, []);
                });
            }
```

to:

```csharp
            var patientAuth = await httpContext.AuthenticateAsync(AuthSchemes.PatientCookie);
            if (patientAuth.Succeeded)
            {
                return await LoadMe(patientAuth.Principal!, Realms.Patients, async localId =>
                {
                    var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
                    // Email is nullable on Patient (a provider-created, never-invited row has
                    // none), but every row reachable through the patient cookie scheme was
                    // bound or created by ResolvePatientAsync, both of which always set Email.
                    return patient is null
                        ? null
                        : new MeResponse(patient.Id, patient.Email!, patient.Email!, Realms.Patients, []);
                });
            }
```

- [ ] **Step 4: Rewrite `ResolvePatientAsync` to read/write `Patients` directly**

Replace the entire `ResolvePatientAsync` method (lines 159-221 of `FirstLoginProvisioner.cs`) with:

```csharp
    // Patients are never created here yet — that's Task 3. A Patients row exists only
    // because a provider already created it and invited that patient, so a login with no
    // matching row is an error, not a signal to create one.
    private async Task<Guid> ResolvePatientAsync(
        ClaimsPrincipal principal,
        Guid subject,
        string email,
        CancellationToken cancellationToken)
    {
        var bySubject = await db.Patients
            .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken);
        if (bySubject is not null)
        {
            return bySubject.DisabledAt is null
                ? bySubject.Id
                : throw new InvalidOperationException("This patient account is disabled.");
        }

        // Binding an unclaimed Patients row to whichever subject presents its email is
        // only safe if Keycloak itself has verified that email belongs to this subject —
        // otherwise any account with a known email and an unverified address at the same
        // IdP could claim it.
        var emailVerified = string.Equals(
            principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        if (!emailVerified)
        {
            throw new InvalidOperationException("Cannot bind a patient account to an unverified email.");
        }

        var normalizedEmail = email.Trim().ToUpperInvariant();
        var byEmail = await db.Patients
            .SingleOrDefaultAsync(
                p => p.ExternalSubject == null && p.Email != null && p.Email.ToUpper() == normalizedEmail,
                cancellationToken)
            ?? throw new InvalidOperationException("No patient account matches this login.");

        if (byEmail.DisabledAt is not null)
        {
            throw new InvalidOperationException("This patient account is disabled.");
        }

        byEmail.ExternalSubject = subject;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Someone else claimed the same unclaimed row (or the same subject logged in
            // twice concurrently) between our read and our write. Re-read by subject: if
            // it is now bound, that is success; otherwise this login genuinely lost the
            // race for an unclaimed row and should fail rather than silently retry.
            db.Entry(byEmail).State = EntityState.Detached;
            var afterRace = await db.Patients
                .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken)
                ?? throw new InvalidOperationException("No patient account matches this login.");

            return afterRace.DisabledAt is null
                ? afterRace.Id
                : throw new InvalidOperationException("This patient account is disabled.");
        }

        return byEmail.Id;
    }
```

(This is the same two branches as before, just reading/writing `db.Patients` with `p.Email != null &&` guarding the `.ToUpper()` call — `Email` is nullable now, and the `InMemory` EF provider throws a `NullReferenceException` evaluating `.ToUpper()` on a null string client-side, unlike real Postgres, which translates it to `UPPER(NULL) = NULL`. Task 3 replaces this `?? throw` with the create branch.)

- [ ] **Step 5: Update the existing tests to the renamed type**

`Patient.FirstName`/`LastName` default to `""` (see Task 2 Step 1), so existing fixtures
don't need to start setting them — only the type name, the dropped `PatientId` field, and
`db.PatientAccounts` → `db.Patients` change. Replace all four affected tests in
`FirstLoginProvisionerTests.cs` with:

```csharp
    [Fact]
    public async Task ProvisionAsync_WhenPatientAccountDoesNotExist_ThrowsRatherThanCreating()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(Guid.NewGuid(), "nobody@example.com", "Nobody"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        // The InMemory provider enforces neither the NOT NULL on FirstName/LastName nor
        // any Postgres constraint, so this suite cannot catch an insert structurally the
        // way Postgres would — the explicit zero-row assertion below is the only guard.
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.Patients.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientEmailNotVerified_ThrowsRatherThanBinding()
    {
        // Arrange
        await using var db = NewContext();
        var unclaimed = new Patient
        {
            Id = Guid.NewGuid(),
            Email = "patient@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Patients.Add(unclaimed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(Guid.NewGuid(), "patient@example.com", "A Patient", emailVerified: false),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        var row = await db.Patients.SingleAsync(TestContext.Current.CancellationToken);
        row.ExternalSubject.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientEmailVerified_BindsUnclaimedAccountCaseInsensitively()
    {
        // Arrange
        await using var db = NewContext();
        var unclaimed = new Patient
        {
            Id = Guid.NewGuid(),
            Email = "Patient@Example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Patients.Add(unclaimed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "patient@example.com", "A Patient", emailVerified: true),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        localId.Should().Be(unclaimed.Id);
        var row = await db.Patients.SingleAsync(TestContext.Current.CancellationToken);
        row.ExternalSubject.Should().Be(subject);
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientAccountDisabled_ThrowsRatherThanReturningId()
    {
        // Arrange
        await using var db = NewContext();
        var subject = Guid.NewGuid();
        var disabled = new Patient
        {
            Id = Guid.NewGuid(),
            Email = "patient@example.com",
            ExternalSubject = subject,
            DisabledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Patients.Add(disabled);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(subject, "patient@example.com", "A Patient"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
```

(`ProvisionAsync_WhenPatientAccountDoesNotExist_ThrowsRatherThanCreating` is renamed and
rewritten again in Task 3 once the create branch exists — this version, asserting a
throw, is only correct for the two-branch behavior this task produces.)

- [ ] **Step 6: Run the full test suite**

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*FirstLoginProvisionerTests*"` (from `anamnys-aspire/`)
Expected: all PASS, unchanged from before this task — this is a pure retarget, no new assertions.

- [ ] **Step 7: Build the server project**

Run: `dotnet build anamnys-aspire.Server` (from `anamnys-aspire/`)
Expected: succeeds — confirms nothing else in the server project still references `PatientAccount`/`PatientAccounts`.

- [ ] **Step 8: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server/Data/Entities/Patient.cs \
        anamnys-aspire/anamnys-aspire.Server/Data/AnamnysDbContext.cs \
        anamnys-aspire/anamnys-aspire.Server/Auth/AuthEndpoints.cs \
        anamnys-aspire/anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs \
        anamnys-aspire/anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs
git rm anamnys-aspire/anamnys-aspire.Server/Data/Entities/PatientAccount.cs
git commit -m "refactor: replace PatientAccount entity with merged Patient entity

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: Patient self-registration — create branch in `FirstLoginProvisioner` (TDD)

**Files:**
- Modify: `anamnys-aspire/anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs` (`ResolvePatientAsync`, written by Task 2)
- Modify: `anamnys-aspire/anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs`

**Interfaces:**
- Consumes: `Patient` entity and `db.Patients` (Task 2).
- Produces: `ResolvePatientAsync` now returns a new `Patient.Id` instead of throwing when no row matches subject or email. Nothing outside this method depends on the new branch directly — it's reached the same way the existing two are, through `ProvisionAsync(principal, Realms.Patients, ct)`.

- [ ] **Step 1: Add `givenName`/`familyName` to the test helper**

In `FirstLoginProvisionerTests.cs`, change `PrincipalFor`'s signature and body from:

```csharp
    private static ClaimsPrincipal PrincipalFor(
        Guid subject,
        string email,
        string name,
        bool emailVerified = true,
        string? crpNumber = null,
        string? crpRegion = null,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", subject.ToString()),
            new("email", email),
            new("name", name),
            new("email_verified", emailVerified ? "true" : "false"),
        };
        if (crpNumber is not null)
        {
            claims.Add(new Claim("crpNumber", crpNumber));
        }
        if (crpRegion is not null)
        {
            claims.Add(new Claim("crpRegion", crpRegion));
        }
        claims.AddRange(roles.Select(role => new Claim("roles", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
```

to:

```csharp
    private static ClaimsPrincipal PrincipalFor(
        Guid subject,
        string email,
        string name,
        bool emailVerified = true,
        string? crpNumber = null,
        string? crpRegion = null,
        string? givenName = null,
        string? familyName = null,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", subject.ToString()),
            new("email", email),
            new("name", name),
            new("email_verified", emailVerified ? "true" : "false"),
        };
        if (crpNumber is not null)
        {
            claims.Add(new Claim("crpNumber", crpNumber));
        }
        if (crpRegion is not null)
        {
            claims.Add(new Claim("crpRegion", crpRegion));
        }
        if (givenName is not null)
        {
            claims.Add(new Claim("given_name", givenName));
        }
        if (familyName is not null)
        {
            claims.Add(new Claim("family_name", familyName));
        }
        claims.AddRange(roles.Select(role => new Claim("roles", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
```

- [ ] **Step 2: Replace the now-obsolete "throws" test and add the new failing tests**

Replace `ProvisionAsync_WhenPatientAccountDoesNotExist_ThrowsRatherThanCreating` (already retargeted at `db.Patients` in Task 2) with:

```csharp
    [Fact]
    public async Task ProvisionAsync_WhenNoPatientMatches_CreatesNewRowWithNullProviderId()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "newpatient@example.com", "A Patient", givenName: "A", familyName: "Patient"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Patients.SingleAsync(TestContext.Current.CancellationToken);
        row.Id.Should().Be(localId);
        row.ExternalSubject.Should().Be(subject);
        row.Email.Should().Be("newpatient@example.com");
        row.ProviderId.Should().BeNull();
        row.FirstName.Should().Be("A");
        row.LastName.Should().Be("Patient");
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientTokenCarriesNoNameClaims_LeavesNamesEmpty()
    {
        // Arrange — mirrors ProvisionAsync_WhenProviderTokenCarriesNoCrpClaims_LeavesBothNull:
        // a missing claim must not throw, it's a realm-config gap to notice later, not a
        // reason to fail the login.
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "newpatient@example.com", "A Patient"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Patients.SingleAsync(TestContext.Current.CancellationToken);
        row.FirstName.Should().Be("");
        row.LastName.Should().Be("");
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientSelfRegistersTwiceForSameSubject_DoesNotCreateSecondRow()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();
        var principal = PrincipalFor(subject, "newpatient@example.com", "A Patient", givenName: "A", familyName: "Patient");

        // Act
        var first = await provisioner.ProvisionAsync(principal, Realms.Patients, TestContext.Current.CancellationToken);
        var second = await provisioner.ProvisionAsync(principal, Realms.Patients, TestContext.Current.CancellationToken);

        // Assert
        second.Should().Be(first);
        (await db.Patients.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }
```

- [ ] **Step 3: Run the new tests to verify they fail**

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*FirstLoginProvisionerTests*"` (from `anamnys-aspire/`)
Expected: `ProvisionAsync_WhenNoPatientMatches_CreatesNewRowWithNullProviderId`,
`ProvisionAsync_WhenPatientTokenCarriesNoNameClaims_LeavesNamesEmpty`, and
`ProvisionAsync_WhenPatientSelfRegistersTwiceForSameSubject_DoesNotCreateSecondRow` all FAIL
(today's code still throws `InvalidOperationException` when no row matches). Every other
test in the file still PASSes.

- [ ] **Step 4: Add the create branch**

In `FirstLoginProvisioner.cs`, replace:

```csharp
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var byEmail = await db.Patients
            .SingleOrDefaultAsync(
                p => p.ExternalSubject == null && p.Email != null && p.Email.ToUpper() == normalizedEmail,
                cancellationToken)
            ?? throw new InvalidOperationException("No patient account matches this login.");

        if (byEmail.DisabledAt is not null)
        {
            throw new InvalidOperationException("This patient account is disabled.");
        }

        byEmail.ExternalSubject = subject;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Someone else claimed the same unclaimed row (or the same subject logged in
            // twice concurrently) between our read and our write. Re-read by subject: if
            // it is now bound, that is success; otherwise this login genuinely lost the
            // race for an unclaimed row and should fail rather than silently retry.
            db.Entry(byEmail).State = EntityState.Detached;
            var afterRace = await db.Patients
                .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken)
                ?? throw new InvalidOperationException("No patient account matches this login.");

            return afterRace.DisabledAt is null
                ? afterRace.Id
                : throw new InvalidOperationException("This patient account is disabled.");
        }

        return byEmail.Id;
    }
```

with:

```csharp
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var byEmail = await db.Patients
            .SingleOrDefaultAsync(
                p => p.ExternalSubject == null && p.Email != null && p.Email.ToUpper() == normalizedEmail,
                cancellationToken);

        if (byEmail is not null)
        {
            if (byEmail.DisabledAt is not null)
            {
                throw new InvalidOperationException("This patient account is disabled.");
            }

            byEmail.ExternalSubject = subject;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Someone else claimed the same unclaimed row (or the same subject logged
                // in twice concurrently) between our read and our write. Re-read by
                // subject: if it is now bound, that is success; otherwise this login
                // genuinely lost the race for an unclaimed row and should fail rather
                // than silently retry.
                db.Entry(byEmail).State = EntityState.Detached;
                var afterBindRace = await db.Patients
                    .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken)
                    ?? throw new InvalidOperationException("No patient account matches this login.");

                return afterBindRace.DisabledAt is null
                    ? afterBindRace.Id
                    : throw new InvalidOperationException("This patient account is disabled.");
            }

            return byEmail.Id;
        }

        // Nobody invited this patient — this is a genuine self-registration. No provider
        // relationship exists yet (ProviderId stays null until some future flow, e.g.
        // booking a first appointment, sets it). given_name/family_name come from the same
        // profile scope the provider realm already relies on for its own name claim; a
        // missing claim here is a realm-config gap to notice later, not a reason to fail
        // the login (mirrors how ProvisionProviderAsync tolerates missing crpNumber/
        // crpRegion).
        var givenName = principal.FindFirstValue("given_name") ?? "";
        var familyName = principal.FindFirstValue("family_name") ?? "";

        var now = DateTimeOffset.UtcNow;
        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            FirstName = givenName,
            LastName = familyName,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Patients.Add(patient);

        // Two concurrent first logins for the same subject both miss every lookup above
        // and both try to insert; the unique index on ExternalSubject lets exactly one
        // succeed. Rather than 500 the loser, re-read: the winner's row is now there.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(patient).State = EntityState.Detached;
            var afterCreateRace = await db.Patients
                .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken);
            if (afterCreateRace is null)
            {
                throw;
            }

            return afterCreateRace.Id;
        }

        return patient.Id;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*FirstLoginProvisionerTests*"` (from `anamnys-aspire/`)
Expected: all PASS, including the three new tests.

- [ ] **Step 6: Run the full auth test suite as a regression check**

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*Anamnys.Tests.Auth*"` (from `anamnys-aspire/`)
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs \
        anamnys-aspire/anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs
git commit -m "feat: patient self-registration creates a new Patients row

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: Enable self-registration on the patients realm

**Files:**
- Modify: `anamnys-aspire/keycloak/realms/anamnys-patients.json:7`

**Interfaces:**
- Consumes: nothing.
- Produces: a realm where `GET /realms/anamnys-patients/protocol/openid-connect/registrations` renders Keycloak's registration form. Task 5's `get-started.tsx` link depends on this being `true`.

- [ ] **Step 1: Flip `registrationAllowed`**

In `anamnys-aspire/keycloak/realms/anamnys-patients.json`, change line 7 from:

```json
  "registrationAllowed": false,
```

to:

```json
  "registrationAllowed": true,
```

No `registrationEmailAsUsername` addition and no user-profile component — this realm has no declarative user-profile override today (unlike providers), so Keycloak's default profile (email, firstName, lastName) already applies and already matches `Register.tsx`'s expectations.

- [ ] **Step 2: Manual verification**

```bash
# from anamnys-aspire/, after Task 1's volume removal already forced a re-import
```

Open `http://localhost:8080/realms/anamnys-patients/protocol/openid-connect/registrations?client_id=anamnys-web&response_type=code&scope=openid&redirect_uri=http://localhost:5274/signin-oidc-patient` in a browser — it should render a registration form (default Keycloak theme is fine here; the theme is already wired generically by Phase 1's `Register.tsx`), not a "Registration not allowed" error page.

- [ ] **Step 3: Commit**

```bash
git add anamnys-aspire/keycloak/realms/anamnys-patients.json
git commit -m "feat: allow self-registration on the patients realm

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: `apps/web` — activate the Paciente card on `/get-started`

**Files:**
- Modify: `anamnys-aspire/packages/shared/src/lib/appUrls.ts`
- Modify: `anamnys-aspire/apps/web/src/routes/_marketing/get-started.tsx`

**Interfaces:**
- Consumes: `appUrls` (adds `patientRegister`).
- Produces: Paciente card on `/get-started` links to `/auth/patient/register` (absolute `http://localhost:5274/...` in dev, same pattern as `providerRegister`).

- [ ] **Step 1: Add `patientRegister` to `appUrls.ts`**

Change:

```ts
const providerDevOrigin = "http://localhost:5273";

export const appUrls = {
  web: "/",
  provider: "/provider/",
  providerLogin: "/provider/login",
  providerRegister: import.meta.env.DEV
    ? `${providerDevOrigin}/auth/provider/register`
    : "/auth/provider/register",
  getStarted: "/get-started",
  patient: "/patient/",
} as const;
```

to:

```ts
const providerDevOrigin = "http://localhost:5273";
const patientDevOrigin = "http://localhost:5274";

export const appUrls = {
  web: "/",
  provider: "/provider/",
  providerLogin: "/provider/login",
  providerRegister: import.meta.env.DEV
    ? `${providerDevOrigin}/auth/provider/register`
    : "/auth/provider/register",
  getStarted: "/get-started",
  patient: "/patient/",
  patientRegister: import.meta.env.DEV
    ? `${patientDevOrigin}/auth/patient/register`
    : "/auth/patient/register",
} as const;
```

- [ ] **Step 2: Activate the Paciente card**

In `anamnys-aspire/apps/web/src/routes/_marketing/get-started.tsx`, change:

```tsx
        <Card padded={false} className="h-full p-7 opacity-60 cursor-not-allowed" aria-disabled="true">
          <div className="flex items-center justify-between mb-3">
            <HeartPulse size={28} className="text-primary" />
            <span className="text-label-sm text-onSurfaceVariant bg-surfaceContainerHigh rounded-radii-full px-2.5 py-1">
              {t('welcome.comingSoon')}
            </span>
          </div>
          <p className="text-headline-sm text-onSurface mb-1">{t('common.patient')}</p>
          <p className="text-body-md text-onSurfaceVariant">{t('welcome.getStartedPage.patientBody')}</p>
        </Card>
```

to:

```tsx
        <a href={appUrls.patientRegister} className="block">
          <Card padded={false} className="h-full p-7 hover:border-primary/50 transition-colors">
            <HeartPulse size={28} className="text-primary mb-3" />
            <p className="text-headline-sm text-onSurface mb-1">{t('common.patient')}</p>
            <p className="text-body-md text-onSurfaceVariant">{t('welcome.getStartedPage.patientBody')}</p>
          </Card>
        </a>
```

Also update the file's leading comment (currently: `Paciente is phase 2: its realm still has registrationAllowed: false, so it renders disabled rather than linking anywhere that would just fail.`) to drop the now-stale phase-2/disabled framing — both cards work the same way now.

- [ ] **Step 3: Build and lint**

Run: `npm run build -w @anamnys/web` then `npm run lint -w @anamnys/web` (from `anamnys-aspire/`)
Expected: both succeed.

- [ ] **Step 4: Commit**

```bash
git add anamnys-aspire/packages/shared/src/lib/appUrls.ts \
        anamnys-aspire/apps/web/src/routes/_marketing/get-started.tsx
git commit -m "feat: activate Paciente self-registration on /get-started

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 6: Documentation — update Chapters 8, 9, 10

**Files:**
- Modify: `anamnys-aspire/documentation/08-session-lifecycle-in-code.md`
- Modify: `anamnys-aspire/documentation/09-data-access-and-ef-core-today.md`
- Modify: `anamnys-aspire/documentation/10-the-full-data-model.md`

**Interfaces:** none — prose only, no code depends on these files.

- [ ] **Step 1: Chapter 8 — describe three branches instead of bind-only**

Change the heading and paragraph:

```
**Patients: bind-only, never create.** This is the one that most surprises people coming
from the "just create an account on first login" mental model the other two realms use, and
the comment in the source states the reasoning plainly: *a `PatientAccount` exists only
because a provider already created it and invited that patient* — so a login with no
matching account is treated as an error, not as an invitation to create one on the spot.
The binding logic looks first for an existing row already bound to this subject; if none
exists, it looks for an **unclaimed** row (`ExternalSubject == null`) matching the token's
email — but only binds it if the token explicitly asserts `email_verified: true`. That
check is not optional politeness: binding an unclaimed account to whoever merely *presents*
a matching email would let anyone who controls an unverified address at the same identity
provider claim someone else's patient record. As with provider provisioning, a race on the
bind (two logins claiming the same row concurrently) is caught and resolved by re-reading
rather than failing the request outright.
```

to:

```
**Patients: bind first, create only as a last resort.** The binding logic looks first for
an existing `Patients` row already bound to this subject; if none exists, it looks for an
**unclaimed** row (`ExternalSubject == null`) matching the token's email — but only binds
it if the token explicitly asserts `email_verified: true`. That check is not optional
politeness: binding an unclaimed row to whoever merely *presents* a matching email would
let anyone who controls an unverified address at the same identity provider claim someone
else's patient record. As with provider provisioning, a race on the bind (two logins
claiming the same row concurrently) is caught and resolved by re-reading rather than
failing the request outright. Only when neither check finds a row does it create one —
patient self-registration, with no `ProviderId` set — the same create-and-handle-the-race
shape the Providers branch uses, on the same table.

`Patients` and `PatientAccounts` used to be two tables (a clinical record and a separate
portal-login row); they're one table now — `Patients` carries nullable `Email`/
`ExternalSubject`/etc. columns directly, the same shape `Providers` already used. A row
with all of those null is a provider's patient with no portal access; a row with `Email`
and `ExternalSubject` both set is a bound, logged-in patient.
```

- [ ] **Step 2: Chapter 8 — the `DisabledAt` sentence**

Change:

```
Every one of these three paths also checks for `DisabledAt` where the entity supports it
(`Staff`, `PatientAccount`) and rejects a disabled account with an explicit exception rather
than silently provisioning around it.
```

to:

```
Every one of these three paths also checks for `DisabledAt` where the entity supports it
(`Staff`, `Patient`) and rejects a disabled account with an explicit exception rather
than silently provisioning around it.
```

- [ ] **Step 3: Chapter 8 — the `/api/auth/me` table list**

Change:

```
whichever one succeeds determines both the realm and which local table (`Providers`,
`Staff`, or `PatientAccounts`) to look the row up in.
```

to:

```
whichever one succeeds determines both the realm and which local table (`Providers`,
`Staff`, or `Patients`) to look the row up in.
```

- [ ] **Step 4: Chapter 9 — the `AnamnysDbContext` listing**

Change:

```csharp
public class AnamnysDbContext(DbContextOptions<AnamnysDbContext> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<PatientAccount> PatientAccounts => Set<PatientAccount>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<BreakGlassGrant> BreakGlassGrants => Set<BreakGlassGrant>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();
    // ...
}
```

to:

```csharp
public class AnamnysDbContext(DbContextOptions<AnamnysDbContext> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<BreakGlassGrant> BreakGlassGrants => Set<BreakGlassGrant>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();
    // ...
}
```

And just below it, change:

```
If these five names look familiar, it's because you met all of them in Part 4:
`Providers`, `PatientAccounts`, and `Staff` are exactly the three tables
`FirstLoginProvisioner` (Chapter 8) provisions rows into, one per realm.
```

to:

```
If these five names look familiar, it's because you met all of them in Part 4:
`Providers`, `Patients`, and `Staff` are exactly the three tables `FirstLoginProvisioner`
(Chapter 8) provisions rows into, one per realm.
```

- [ ] **Step 5: Chapter 9 — the index-shape sentence**

Change:

```
`Provider`, `PatientAccount`, and `Staff` all get the same shape of index: a unique index
on `ExternalSubject` (so the database itself enforces that a Keycloak subject can map to
at most one local row — the same guarantee `FirstLoginProvisioner`'s
`SingleOrDefaultAsync` + unique-index-driven race handling from Chapter 8 relies on) and a
unique index on `Email`.
```

to:

```
`Provider`, `Patient`, and `Staff` all get the same shape of index: a unique index on
`ExternalSubject` (so the database itself enforces that a Keycloak subject can map to at
most one local row — the same guarantee `FirstLoginProvisioner`'s `SingleOrDefaultAsync` +
unique-index-driven race handling from Chapter 8 relies on) and a unique index on `Email`
— both nullable on `Patient`, since a provider-created patient with no portal access yet
has neither, but still unique whenever they are set.
```

- [ ] **Step 6: Chapter 10 — the `Patients, providers, and professional identity` section**

Change:

```
Beyond the `Providers` and `PatientAccounts` tables from Chapter 9: `Patients` (the actual
clinical patient record — `ProviderId`, name, date of birth, preferred language, distinct
from `PatientAccounts`, which is specifically the *portal login* for a patient, per Chapter
8's binding logic), `ProviderProfiles` (a provider's public-facing profile — slug, display
```

to:

```
Beyond the `Providers` and `Patients` tables from Chapter 9 — the latter carrying both the
clinical record (`ProviderId`, name, date of birth, preferred language) and, nullable, the
patient's own portal login (`Email`, `ExternalSubject`, `Phone`, per Chapter 8's binding
logic) on the same row: `ProviderProfiles` (a provider's public-facing profile — slug, display
```

- [ ] **Step 7: Commit**

```bash
git add anamnys-aspire/documentation/08-session-lifecycle-in-code.md \
        anamnys-aspire/documentation/09-data-access-and-ef-core-today.md \
        anamnys-aspire/documentation/10-the-full-data-model.md
git commit -m "docs: update chapters 8-10 for the Patients/PatientAccounts merge

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 7: Full-repo verification and manual click-through

**Files:** none (verification only).

**Interfaces:** none.

- [ ] **Step 1: Root build and lint**

Run, from `anamnys-aspire/`:

```bash
npm run build
npm run lint
```

Expected: both succeed across every workspace.

- [ ] **Step 2: Full C# test suite**

Run: `dotnet run --project anamnys-aspire.Tests` (from `anamnys-aspire/`)
Expected: all tests PASS, including every test touched or added in Tasks 2-3, and the
pre-existing `RegisterEndpointTests`, `SchemeIsolationTests`, `OwnerSessionCookieTests`,
`BreakGlassGrantConstraintTests` (these boot the real Aspire host against the schema from
Task 1 — a broken `anamnys-db-script.sql` edit would surface here as every one of them
failing to start, not just a `Patients`-specific test).

- [ ] **Step 3: Manual click-through**

With Docker running and `aspire start` up (after Task 1/Task 4's volume removal, so both
the schema and the realm re-import with this task's changes):

1. Open the web app, click "Comecar" — lands on `/get-started`.
2. Click the Paciente card — full-page nav to `/auth/patient/register`, then to Keycloak's
   registration form.
3. Fill it in with a new email, submit.
4. Expected: redirected back through `/signin-oidc-patient`, and the browser ends up on
   the patient app's `/`, showing the existing "Patient portal..." placeholder copy.
5. Confirm in Postgres that a `Patients` row now exists for that email with `ProviderId`
   null and `ExternalSubject` set.

- [ ] **Step 4: Report results**

No commit for this task — if Steps 1-3 all pass, the plan is done; report that plainly.
If anything fails, stop and fix it in the task that owns the failing file before moving on.
