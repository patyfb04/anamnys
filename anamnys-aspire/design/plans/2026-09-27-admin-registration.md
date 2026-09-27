# Admin Registration and Login Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Anyone can register on the owners realm from the admin app. A new account signs in as **pending** with no admin access until a staff role is assigned in the Keycloak console.

**Architecture:** `Staff.Role` becomes nullable (`NULL` = pending). `FirstLoginProvisioner` stops refusing role-less owners logins: it creates a pending row, and it activates or updates the row when the token later carries a role. `/api/admin` adds a `roles` claim requirement, so a pending session gets 403 there. The admin app replaces its auto-redirect with three screens: anonymous (Entrar / Criar conta), pending, and active.

**Tech Stack:** .NET 10 minimal APIs, EF Core 10 (PostgreSQL; InMemory in unit tests), xUnit v3 + FluentAssertions, Keycloak 26 realm JSON, React 19 + TanStack Router, Tailwind 4, i18next.

**Spec:** `anamnys-aspire/design/specs/2026-09-27-admin-registration-design.md`

## Global Constraints

- All paths below are relative to `anamnys-aspire/` (the solution directory).
- `dotnet test` does not work in this solution. Run tests with `dotnet run --project anamnys-aspire.Tests -- -method "*Pattern*"`.
- Integration tests start their own AppHost on the pinned ports (5273–5276). **Run `aspire stop` before running them.** A running Aspire app locks `bin/`; an `MSB3021`/`MSB3027`/`MSB3491` build error means "stop the app first".
- Realm JSON and `anamnys-db-script.sql` changes only take effect on fresh volumes. Before running integration tests after Task 1 or Task 2: `aspire stop`, then `docker volume ls | grep -E 'keycloak-data|postgres-data'` and `docker volume rm` each one, **one at a time**.
- C#: 4-space indent, file-scoped namespaces, primary constructors, always pass `CancellationToken`, no XML doc comments, no exceptions for business-logic errors, EF Core directly.
- Tests: class `<ClassName>Tests`, methods `<MethodName>_<Conditions>_<AssertedOutcome>` (no `Async` suffix), `// Arrange` / `// Act` / `// Assert` comments.
- TypeScript: 2-space indent. User-facing text goes in `packages/shared/src/lib/i18n/locales/pt.json` (the only locale).
- No PHI in logs, tokens or URLs: log row ids only, never an email address.
- The staff roles are exactly `owner`, `support`, `ops`.
- Never weaken `keycloak/strip-dev-seed.jq` or the `INCLUDE_DEV_SEED` gate.
- Commit format `type: description`, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

---

## File Structure

**Server**
- Modify `anamnys-db-script.sql` — `Staff.Role` nullable.
- Modify `anamnys-aspire.Server/Data/Entities/Staff.cs` — `string? Role`.
- Modify `anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs` — public `StaffRoles` list; pending/activation logic.
- Modify `anamnys-aspire.Server/Program.cs` — role requirement on `/api/admin`.
- Modify `anamnys-aspire.Server/Auth/DevOnlyTestClientGuard.cs` — forbid the new `dev.pending` seed user outside dev.
- Modify `keycloak/realms/anamnys-owners.json` — `registrationAllowed: true`; seed `dev.pending` with no roles.

**Tests**
- Modify `anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs`.
- Create `anamnys-aspire.Tests/Auth/PendingOwnerTests.cs`.

**Frontend**
- Modify `apps/admin/src/routes/index.tsx` — three states.
- Create `apps/admin/src/components/AdminLanding.tsx`, `apps/admin/src/components/PendingApproval.tsx`.
- Modify `packages/shared/src/lib/i18n/locales/pt.json` — `admin` section.

**Docs**
- Modify `CLAUDE.md` — how to approve a pending admin; `dev.pending` in the seed-credential gotcha.

---

### Task 1: Pending staff rows in the provisioner

**Files:**
- Modify: `anamnys-db-script.sql` (the `CREATE TABLE "Staff"` block, around line 701)
- Modify: `anamnys-aspire.Server/Data/Entities/Staff.cs`
- Modify: `anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs`
- Test: `anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs`

**Interfaces:**
- Produces: `public static class StaffRoles { public static readonly string[] All; }` in namespace `Anamnys.Server.Auth` (file `FirstLoginProvisioner.cs`), holding `["owner", "support", "ops"]`. Task 2 uses it in `Program.cs`.
- Produces: `Staff.Role` is `string?`; `null` means pending.

- [ ] **Step 1: Replace the "no role throws" test and add activation tests**

In `FirstLoginProvisionerTests.cs`, delete `ProvisionAsync_WhenStaffTokenCarriesNoRecognisedRole_ThrowsRatherThanDefaulting` and add these four tests after `ProvisionAsync_WhenStaffTokenCarriesRealmRole_CreatesRowWithThatRole`:

```csharp
    [Fact]
    public async Task ProvisionAsync_WhenStaffTokenCarriesNoRecognisedRole_CreatesPendingRow()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "default-roles-anamnys-owners"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Staff.SingleAsync(TestContext.Current.CancellationToken);
        row.Id.Should().Be(localId);
        row.Role.Should().BeNull("realm membership alone confers no staff role; the row waits for approval");
    }

    [Fact]
    public async Task ProvisionAsync_WhenPendingStaffReturnsWithRole_ActivatesRow()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        var pendingId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "support"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        localId.Should().Be(pendingId);
        var row = await db.Staff.SingleAsync(TestContext.Current.CancellationToken);
        row.Role.Should().Be("support");
    }

    [Fact]
    public async Task ProvisionAsync_WhenStaffTokenRoleChanges_UpdatesStoredRole()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "support"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "ops"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Staff.SingleAsync(TestContext.Current.CancellationToken);
        row.Role.Should().Be("ops");
    }

    [Fact]
    public async Task ProvisionAsync_WhenActiveStaffTokenLosesRole_KeepsStoredRoleAndStillLogsIn()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        var activeId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "owner"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert — authorization reads the token, so the stale row grants nothing.
        localId.Should().Be(activeId);
        var row = await db.Staff.SingleAsync(TestContext.Current.CancellationToken);
        row.Role.Should().Be("owner");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*ProvisionAsync_When*Staff*"`
Expected: `CreatesPendingRow` and `ActivatesRow` fail with `InvalidOperationException` ("carries none of the required staff roles"); `UpdatesStoredRole` fails (`Role` is still `"support"`). The `KeepsStoredRole` test also fails with the same exception. Existing staff tests pass.

- [ ] **Step 3: Make `Role` nullable in schema and entity**

`anamnys-db-script.sql`, inside `CREATE TABLE "Staff"`:

```sql
	"Role" text,
```

(was `"Role" text NOT NULL,`; leave `Staff_Role_ck` unchanged — a `CHECK` passes on `NULL`).

`anamnys-aspire.Server/Data/Entities/Staff.cs`:

```csharp
    public string? Role { get; set; }
```

(was `public string Role { get; set; } = "support";`).

- [ ] **Step 4: Rewrite `ProvisionStaffAsync`**

In `FirstLoginProvisioner.cs`, add below `AnamnysClaims`:

```csharp
public static class StaffRoles
{
    public static readonly string[] All = ["owner", "support", "ops"];
}
```

Delete the private `StaffRoles` field in `FirstLoginProvisioner`, and replace the whole `ProvisionStaffAsync` method with:

```csharp
    // Owners-realm registration is open, so realm membership alone confers nothing: a
    // token with none of the staff roles gets a pending row (Role null) and a session
    // that /api/admin refuses. Someone grants a role in the Keycloak console; the next
    // login's token carries it and this activates the row. Permissions are read from
    // the token, never from Role, so a token that lost its role leaves Role untouched.
    private async Task<Guid> ProvisionStaffAsync(
        ClaimsPrincipal principal,
        Guid subject,
        string email,
        string name,
        CancellationToken cancellationToken)
    {
        var tokenRoles = principal.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var role = StaffRoles.All.FirstOrDefault(tokenRoles.Contains);

        var existing = await db.Staff
            .SingleOrDefaultAsync(s => s.ExternalSubject == subject, cancellationToken);
        if (existing is not null)
        {
            return await ResolveExistingStaffAsync(existing, role, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var staff = new Staff
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            Name = name,
            Role = role,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Staff.Add(staff);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(staff).State = EntityState.Detached;
            existing = await db.Staff
                .SingleOrDefaultAsync(s => s.ExternalSubject == subject, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return await ResolveExistingStaffAsync(existing, role, cancellationToken);
        }

        return staff.Id;
    }

    private async Task<Guid> ResolveExistingStaffAsync(Staff existing, string? role, CancellationToken cancellationToken)
    {
        if (existing.DisabledAt is not null)
        {
            throw new InvalidOperationException("This staff account is disabled.");
        }

        if (role is not null && role != existing.Role)
        {
            existing.Role = role;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Staff {StaffId} role set from token.", existing.Id);
        }

        return existing.Id;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*FirstLoginProvisionerTests*"`
Expected: all pass, including `ProvisionAsync_WhenStaffAccountDisabled_ThrowsRatherThanReturningId`.

- [ ] **Step 6: Build the solution to catch other `Role` usages**

Run: `dotnet build anamnys-aspire.sln`
Expected: 0 errors. If a warning/error points at code treating `Staff.Role` as non-null, fix it there (null means pending).

- [ ] **Step 7: Commit**

```bash
git add anamnys-db-script.sql anamnys-aspire.Server/Data/Entities/Staff.cs anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs
git commit -m "feat: sign role-less owners-realm accounts in as pending staff"
```

---

### Task 2: Open owners registration and gate `/api/admin` on a staff role

**Files:**
- Modify: `keycloak/realms/anamnys-owners.json`
- Modify: `anamnys-aspire.Server/Program.cs:92-95`
- Modify: `anamnys-aspire.Server/Auth/DevOnlyTestClientGuard.cs:31-35`
- Test: `anamnys-aspire.Tests/Auth/PendingOwnerTests.cs`

**Interfaces:**
- Consumes: `StaffRoles.All` (Task 1).
- Produces: seeded dev user `dev.pending` / `dev.pending@anamnys.local` / password `PendingStaff!2026Dev`, owners realm, no staff role.

- [ ] **Step 1: Write the failing integration tests**

Create `anamnys-aspire.Tests/Auth/PendingOwnerTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// Owners-realm registration is open, so a signed-in owners session is not by itself
// staff: /api/admin also requires a staff role in the token. Drives the seeded
// dev.pending (no realm roles, see keycloak/realms/anamnys-owners.json).
[Collection(SharedAppHostCollection.Name)]
public class PendingOwnerTests
{
    // Same fixed-port requirement as OwnerSessionCookieTests: only
    // http://localhost:5276 is a registered redirect origin for anamnys-admin-web.
    private static readonly Uri AdminBaseAddress = new("http://localhost:5276/");

    [Fact]
    public async Task AdminProbe_WithPendingOwnerSession_Returns403AndMeReportsNoRoles()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            AdminBaseAddress, "/admin/", "owner", "dev.pending@anamnys.local", "PendingStaff!2026Dev",
            TestContext.Current.CancellationToken);

        // Act
        using var meResponse = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"), TestContext.Current.CancellationToken);
        using var probeResponse = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/admin/probe"), TestContext.Current.CancellationToken);

        // Assert — authenticated (me is 200), but not permitted on the admin group.
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK, "a pending account still signs in");
        using var me = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        me.RootElement.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetString())
            .Should().NotContain(["owner", "support", "ops"]);
        probeResponse.StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "a pending owners session is authenticated but holds no staff role");
    }

    [Fact]
    public async Task OwnerRegisterEndpoint_RendersKeycloaksRegistrationForm()
    {
        // Arrange
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { BaseAddress = AdminBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/admin/", TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(
            "/auth/owner/register?returnUrl=/admin/", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert — with registrationAllowed false Keycloak answers with its error page instead.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("register.ftl", "Keycloakify embeds the rendered page id in kcContext");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run `aspire stop`, then: `dotnet run --project anamnys-aspire.Tests -- -method "*PendingOwnerTests*"`
Expected: both fail — login as `dev.pending` fails (user does not exist) and the register page is Keycloak's "registration not allowed" error.

- [ ] **Step 3: Update the owners realm**

In `keycloak/realms/anamnys-owners.json`:

```json
  "registrationAllowed": true,
```

Append to the `users` array, after `dev.owner`:

```json
    {
      "username": "dev.pending",
      "email": "dev.pending@anamnys.local",
      "firstName": "Dev",
      "lastName": "Pending",
      "enabled": true,
      "emailVerified": true,
      "realmRoles": [],
      "credentials": [
        {
          "type": "password",
          "value": "PendingStaff!2026Dev",
          "temporary": false
        }
      ]
    }
```

Do not add a `defaultRoles`/`default-roles-anamnys-owners` composite containing `owner`, `support` or `ops`.

- [ ] **Step 4: Require a staff role on `/api/admin`**

`Program.cs`:

```csharp
// Owners-realm registration is open: an owners cookie proves identity, a staff role
// in the token proves access. Missing role = 403; another realm's cookie stays 401
// because the scheme list is unchanged.
var admin = app.MapGroup("/api/admin")
    .RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(AuthSchemes.OwnerCookie)
        .RequireAuthenticatedUser()
        .RequireClaim("roles", StaffRoles.All));
```

- [ ] **Step 5: Forbid `dev.pending` outside Development**

`DevOnlyTestClientGuard.cs`:

```csharp
    private static readonly (string Realm, string Username)[] ForbiddenUsers =
    [
        (Realms.Providers, "dev.provider"),
        (Realms.Owners, "dev.owner"),
        (Realms.Owners, "dev.pending"),
    ];
```

- [ ] **Step 6: Wipe volumes and run the tests**

`aspire stop`; `docker volume ls | grep -E 'keycloak-data|postgres-data'`; `docker volume rm` each, one at a time. Then:

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*PendingOwnerTests*"`
Expected: PASS.

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*OwnerSessionCookieTests*"` and `-- -method "*SchemeIsolationTests*"` and `-- -method "*DevOnlyTestClientGuard*"`
Expected: PASS (`dev.owner` has `support`, so its `/api/admin/probe` stays 200; other realms stay 401).

- [ ] **Step 7: Commit**

```bash
git add keycloak/realms/anamnys-owners.json anamnys-aspire.Server/Program.cs anamnys-aspire.Server/Auth/DevOnlyTestClientGuard.cs anamnys-aspire.Tests/Auth/PendingOwnerTests.cs
git commit -m "feat: open owners-realm registration and require a staff role on /api/admin"
```

---

### Task 3: Admin app landing, pending and active screens

**Files:**
- Create: `apps/admin/src/components/AdminLanding.tsx`
- Create: `apps/admin/src/components/PendingApproval.tsx`
- Modify: `apps/admin/src/routes/index.tsx`
- Modify: `packages/shared/src/lib/i18n/locales/pt.json`

**Interfaces:**
- Consumes: `useAuthStore` (`user`, `isLoading`, `loadUser`, `logout`) from `@anamnys/shared/lib/store/authStore`; `AuthUser.roles: string[]`.
- Produces: nothing used by later tasks.

- [ ] **Step 1: Add the strings**

In `pt.json`, add a top-level `admin` section (keep the file's existing key order otherwise):

```json
  "admin": {
    "landing": {
      "title": "Anamnys Interno",
      "subtitle": "Acesso restrito à equipe Anamnys.",
      "signIn": "Entrar",
      "register": "Criar conta"
    },
    "pending": {
      "title": "Conta aguardando aprovação",
      "body": "Sua conta foi criada. Um administrador precisa liberar seu acesso. Depois da aprovação, saia e entre novamente.",
      "signOut": "Sair"
    }
  },
```

- [ ] **Step 2: Create `AdminLanding.tsx`**

```tsx
import { useTranslation } from "react-i18next";
import Card from "@anamnys/shared/ui/Card";

// Plain <a href> on purpose: /auth/owner/* answer with a 302 to Keycloak, which the
// whole document has to follow. Relative URLs work in dev too — this app's Vite
// server proxies /auth to the BFF with the Host header Keycloak expects.
export default function AdminLanding() {
  const { t } = useTranslation();

  return (
    <main className="min-h-screen flex items-center justify-center bg-surfaceContainerLow p-4">
      <Card className="w-full max-w-md text-center">
        <h1 className="text-headline-md text-onSurface mb-2">{t("admin.landing.title")}</h1>
        <p className="text-body-lg text-onSurfaceVariant mb-8">{t("admin.landing.subtitle")}</p>
        <div className="flex flex-col gap-3">
          <a
            href="/auth/owner/login?returnUrl=/admin/"
            className="block rounded-radii-full bg-primary text-onPrimary py-3.5 px-6 text-label-lg hover:opacity-90 transition-opacity"
          >
            {t("admin.landing.signIn")}
          </a>
          <a
            href="/auth/owner/register?returnUrl=/admin/"
            className="block rounded-radii-full border border-primary text-primary py-3.5 px-6 text-label-lg hover:bg-primary/5 transition-colors"
          >
            {t("admin.landing.register")}
          </a>
        </div>
      </Card>
    </main>
  );
}
```

Check `packages/shared/src/ui/Card.tsx` accepts `className` (it does in `apps/web/src/routes/_marketing/sign-in.tsx`); if the token names `text-onPrimary` / `rounded-radii-full` differ, copy the classes from `packages/shared/src/ui/Button.tsx`'s primary and outline variants.

- [ ] **Step 3: Create `PendingApproval.tsx`**

```tsx
import { useTranslation } from "react-i18next";
import Card from "@anamnys/shared/ui/Card";
import Button from "@anamnys/shared/ui/Button";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";

// Terminal until someone grants a role in the Keycloak console. Sign-out is the only
// action: the role only reaches the session through a fresh login.
export default function PendingApproval({ email }: { email: string }) {
  const { t } = useTranslation();
  const logout = useAuthStore((s) => s.logout);

  return (
    <main className="min-h-screen flex items-center justify-center bg-surfaceContainerLow p-4">
      <Card className="w-full max-w-md text-center">
        <h1 className="text-headline-md text-onSurface mb-2">{t("admin.pending.title")}</h1>
        <p className="text-body-md text-onSurfaceVariant mb-1">{email}</p>
        <p className="text-body-lg text-onSurfaceVariant mb-8">{t("admin.pending.body")}</p>
        <Button title={t("admin.pending.signOut")} variant="outline" onClick={() => logout("owner")} />
      </Card>
    </main>
  );
}
```

- [ ] **Step 4: Replace `routes/index.tsx`**

```tsx
import { useEffect } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import { hasAuthError } from "@anamnys/shared/lib/authError";
import AuthErrorNotice from "@anamnys/shared/ui/AuthErrorNotice";
import AdminLanding from "@/components/AdminLanding";
import PendingApproval from "@/components/PendingApproval";

export const Route = createFileRoute("/")({
  component: AdminHome,
});

const STAFF_ROLES = ["owner", "support", "ops"];

function AdminHome() {
  const { user, isLoading, loadUser } = useAuthStore();
  const authError = hasAuthError();

  useEffect(() => {
    void loadUser();
  }, [loadUser]);

  // No auto-redirect to login: anonymous visitors choose between Entrar and Criar conta.
  if (authError) return <AuthErrorNotice />;
  if (isLoading) return null;
  if (!user) return <AdminLanding />;
  if (!user.roles.some((r) => STAFF_ROLES.includes(r))) return <PendingApproval email={user.email} />;

  return (
    <div className="min-h-screen bg-surfaceContainerLow p-8">
      <h1 className="text-headline-lg text-onSurface mb-2">Anamnys Internal</h1>
      <p className="text-body-lg text-onSurfaceVariant">
        Signed in as {user.email} ({user.roles.join(", ")}).
      </p>
    </div>
  );
}
```

If the `@/` alias does not resolve under `tsc -b` (check `apps/admin/tsconfig.app.json` for `paths`), use relative imports `../components/AdminLanding`.

- [ ] **Step 5: Build and lint**

Run (from `apps/admin/`): `npm run build` then `npm run lint`
Expected: both succeed with no errors.

- [ ] **Step 6: Manual check in the running app**

`aspire start`; open `http://localhost:5276/admin/`.
- Anonymous: landing with **Entrar** and **Criar conta**.
- **Criar conta** → Keycloak registration form (no site nav), register with a new email, verify via Mailpit, land back on `/admin/` → "Conta aguardando aprovação".
- **Sair** → back to the landing.
- Sign in as `dev.owner@anamnys.local` / `SupportStaff!2026Dev` → active home.
- Open `http://localhost:5275/sign-in` and `/get-started`: no admin/owner option.

- [ ] **Step 7: Commit**

```bash
git add apps/admin/src packages/shared/src/lib/i18n/locales/pt.json
git commit -m "feat: add admin landing with sign-in/register and a pending-approval screen"
```

---

### Task 4: Document approval and the new seed user

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update the seed-credential gotcha**

In the `INCLUDE_DEV_SEED` bullet, change `(\`dev.provider\`, \`dev.owner\`)` to `(\`dev.provider\`, \`dev.owner\`, \`dev.pending\`)`.

- [ ] **Step 2: Add an approval gotcha**

Append to `## Gotchas`:

```markdown
- **Owners-realm registration is open; access is not.** Anyone can create an account from
  the admin app, and `FirstLoginProvisioner` gives it a `Staff` row with `Role` null
  (pending). `/api/admin` requires a `roles` claim of `owner`, `support` or `ops`, so a
  pending session gets 403 there. To approve: Keycloak admin console → realm
  `anamnys-owners` → Users → the user → Role mapping → assign the role. The user must
  sign out and back in; the role only reaches the session through a new token. Never add
  a staff role to the realm's default roles — that would make every registrant staff.
```

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: describe owners-realm registration and approving pending staff"
```
