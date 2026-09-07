# Chapter 16: Testing Strategy

## The project setup, and the trap in the obvious command

`anamnys-aspire.Tests` is a genuinely unusual test project to a newcomer's eye, and its
`.csproj` explains why:

```xml
<OutputType>Exe</OutputType>
<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
```

```xml
<PackageReference Include="Aspire.Hosting.Testing" Version="13.5.3" />
<PackageReference Include="FluentAssertions" Version="8.8.0" />
<PackageReference Include="xunit.v3.mtp-v2" Version="4.0.0" />
```

This project runs on **Microsoft.Testing.Platform (MTP)**, xUnit v3's modern
runner, not the older VSTest-based pipeline `dotnet test` was originally built around. The
test project compiles to an executable (`OutputType=Exe`) that *is* the test runner, rather
than a library VSTest loads.

**`dotnet test` does not work in this solution — this is a documented trap, not an
oversight to fix.** It fails with a VSTest-style error that reads like something is
misconfigured, but the real cause is that `Microsoft.Testing.Platform.MSBuild`'s
`_SupportsGlobalJsonTestRunner` check requires an explicit `global.json` test-runner
opt-in, and this repository deliberately doesn't have one. The actual command is:

```bash
dotnet run --project anamnys-aspire.Tests
```

To run a subset, filter with `-- -method "*Pattern*"`. If you ever find yourself debugging
why `dotnet test` "can't find any tests" here, this is almost certainly why — don't spend
time investigating project references or test discovery; just use the real command.

One more thing worth flagging, because it's a small but genuine gap between `CLAUDE.md`'s
own "planned" section and what's actually in the tree: `CLAUDE.md` lists "xUnit +
FluentAssertions for testing" under **planned** backend work. Looking at the actual
`.csproj` and test files, both are already in active use today — this project has moved
faster than that one line of documentation. It's a small, harmless example of the same
principle Chapter 12 already illustrated with a stale code comment: verify against the
code itself before trusting a status claim, even one in a file whose whole purpose is to
state current status.

## `SharedAppHostFixture`: one real AppHost, shared across a whole test collection

The most distinctive thing about this project is that its integration tests don't mock
Keycloak, Postgres, or the server — they boot a **real `DistributedApplication`**, via
`Aspire.Hosting.Testing`, and run assertions against it over real HTTP. `SharedAppHostFixture`
is the piece that makes this practical rather than prohibitively slow, and its own code
comment explains a real bug it was written to fix:

> Two independent AppHosts each doing `postgres.WithDataVolume()` against the same named
> volume corrupted Postgres with "lock file postmaster.pid is empty" — that is the actual
> defect behind the original "serialize the whole suite" workaround. One shared AppHost per
> run removes the race outright, so xunit's default collection parallelism can stay on.

In other words: running one full AppHost per test *class* seemed like the natural
isolation boundary, but two of them racing to write to the same Postgres data volume
actually corrupted the database. The fix isn't "run tests serially" (a workaround that
would have kept the underlying race intact and just made it rare instead of eliminating
it) — it's `ICollectionFixture<SharedAppHostFixture>`, xUnit's mechanism for sharing one
instance across every test class in a named collection (`SharedAppHostCollection`), so
there's only ever one AppHost, one Postgres volume, and no race to begin with.

Two other details in this fixture are worth knowing if you're debugging a test failure
here specifically:

- **Fixed ports don't mean fixed ports under test.** `AppHost.cs` asks for Keycloak on port
  8080, but under `Aspire.Hosting.Testing`, the underlying orchestrator (DCP) allocates its
  own ephemeral host port for container endpoints rather than honoring that literal
  request. The fixture resolves whatever port the test run actually got via
  `_app.CreateHttpClient("keycloak", "http")` rather than hardcoding `localhost:8080` —
  and it resolves the `"http"` endpoint specifically, because Keycloak's own HTTPS listener
  in a full `aspire run` is really the CLI dashboard's TLS-terminating tunnel in front of
  it, which doesn't exist inside an xunit process at all.
- **Each readiness wait gets its own bounded, independent timeout**, deliberately not one
  shared `CancellationTokenSource` for both the server-health wait and the Keycloak-ready
  poll. Sharing one budget meant a slow server start (image pull included) could consume
  the whole allowance and leave the Keycloak check with effectively one attempt before
  cancellation — a subtle way for an unrelated slowdown to make a completely different
  wait look like it's the one that's broken.

## Reading an actual test: `SchemeIsolationTests`

This class is worth reading end to end, because it's the concrete proof of Chapter 6's
central claim — that a wrong-realm credential produces 401, not 403:

```csharp
[Fact]
public async Task PhiEndpoint_WithNoCredential_Returns401()
{
    // Arrange
    var client = fixture.CreateServerClient();

    // Act
    var response = await client.GetAsync("/api/phi/probe", TestContext.Current.CancellationToken);

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
}
```

Two further tests in the same class are worth understanding specifically because their
names describe *why* they expect the result they expect, not just what the result is:
`PhiEndpoint_WithProviderRealmBearerToken_Returns401BecauseBearerIsNotMounted` and its
owner-realm counterpart. These use a **genuinely valid** token — correct issuer, correct
audience, from the matching realm — and still expect 401, because Chapter 8 told you
exactly why: the bearer schemes are registered but not mounted on any endpoint group
today, since `AnamnysClaims.LocalId` is only ever minted on the OIDC/cookie path. The
comment on this test is explicit about its own future: when the spec's mobile client
ships and the bearer schemes get mounted, *this exact test* is expected to change — and
only in the same commit that adds `LocalId` minting to `JwtBearerEvents.OnTokenValidated`.
That's a good model for how a test in this codebase should be written: not just asserting
current behavior, but stating in its own name and comments the specific condition under
which that assertion is expected to flip.

`KeycloakTestTokens` and `KeycloakTestHttpClient` are the supporting infrastructure that
obtains these tokens — using the `anamnys-test-provider`/`anamnys-test-owner` service
accounts from Chapters 7–8, which exist for exactly this purpose (a `client_credentials`
grant, since every real BFF client disables it) and are stripped from anything that isn't
a local `aspire run` in Development.

## Testing a database constraint directly

`BreakGlassGrantConstraintTests` is a good example of testing a guarantee at the layer
where it actually lives, rather than through the application code that happens to sit on
top of it:

```csharp
var act = async () => await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

var exception = await act.Should().ThrowAsync<PostgresException>();
exception.Which.ConstraintName.Should().Be("BreakGlassGrants_TwoPerson_ck");
```

This connects directly to something raw ADO.NET, not EF Core, and attempts an insert
where `AuthorizedBy` equals `StaffId` — exactly the two-person rule violation Chapter 6
described. The test isn't just checking that *some* error occurs; it asserts the specific
constraint name that rejected it, which is the right level of precision for proving "the
database itself enforces this, not just application code that could have a bug in it."

## The pattern for any new test

Every test file in this project follows the same shape you've now seen twice: Arrange,
Act, Assert, each explicitly commented (matching the convention from `CLAUDE.md`'s code
style section), and a method name shaped
`<MethodName>_<Conditions>_<AssertedOutcome>` — deliberately never with an `Async` suffix,
even though every test method here is `async`. If you're adding a test for a new
PHI-touching feature, the design document's own testing section (Chapter 6) is a good
checklist to test *against*: does a wrong-realm credential 401 rather than 403; is
cross-provider access genuinely denied when the provider id is resolved from the session;
is first-login provisioning idempotent under a concurrent race; does a failed token refresh
challenge rather than 500. These aren't hypothetical concerns — Chapter 8 showed you the
exact code written to satisfy each one, and this is where that code gets held accountable.

Chapter 17 moves from verifying the system to shipping it.
