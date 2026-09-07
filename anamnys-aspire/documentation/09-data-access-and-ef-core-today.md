# Chapter 9: Data Access & EF Core Today

## The gap between the schema and the model — and why it's a feature, not a bug

Before looking at any code, hold onto one fact from this chapter: `anamnys-db-script.sql`
defines **68 tables**. `AnamnysDbContext` maps **5** of them. This isn't a codebase that's
behind on its own schema — it's a codebase whose data model was designed up front, in full,
for the entire product vision described in Chapter 1, while the application code is being
built incrementally against it. Chapter 10 tours the other 63; this chapter covers the five
that are live today, because they happen to be exactly the ones the authentication system
from Part 4 depends on.

## `AnamnysDbContext`

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

If these five names look familiar, it's because you met all of them in Part 4:
`Providers`, `PatientAccounts`, and `Staff` are exactly the three tables
`FirstLoginProvisioner` (Chapter 8) provisions rows into, one per realm.
`BreakGlassGrants` and `AccessLogs` are exactly the two tables the September 5 Keycloak
implementation design (Chapter 6) specified for the owners-realm break-glass mechanism.
This is not a coincidence — this project's EF Core model was built to exactly the scope
identity needed, and no further, following the "phase 1 includes the data layer" decision
that design document made explicitly.

`OnModelCreating` configures each entity's table name, primary key, and indexes:

```csharp
modelBuilder.Entity<Provider>(e =>
{
    e.ToTable("Providers");
    e.HasKey(x => x.Id);
    e.HasIndex(x => x.ExternalSubject).IsUnique();
    e.HasIndex(x => x.Email).IsUnique();
});
```

`Provider`, `PatientAccount`, and `Staff` all get the same shape of index: a unique index
on `ExternalSubject` (so the database itself enforces that a Keycloak subject can map to
at most one local row — the same guarantee `FirstLoginProvisioner`'s
`SingleOrDefaultAsync` + unique-index-driven race handling from Chapter 8 relies on) and a
unique index on `Email`. `BreakGlassGrants` and `AccessLogs` get non-unique indexes on
their respective foreign-key-shaped columns (`StaffId`/`ProviderId`, and `ProviderId`
respectively) — supporting lookups, not uniqueness constraints.

### A concrete example of "the schema knows more than the model"

Compare the `Provider` entity class against the actual `Providers` table columns in
`anamnys-db-script.sql`:

| In the C# `Provider` entity | In the SQL `Providers` table, but *not* in the entity |
|---|---|
| `Id`, `ExternalSubject`, `Email`, `Name`, `Specialty`, `PreferredNoteFormat`, `CreatedAt`, `UpdatedAt` | `CrpNumber`, `CrpRegion`, `BillingSystem` |

`CrpNumber` and `CrpRegion` are a provider's registration number and region with Brazil's
Conselho Regional de Psicologia — the professional licensing body for psychologists there,
which tells you something concrete about the product's target market (see Chapter 10's
tour of the billing tables for more evidence of this — `TissGuides` and `Insurers.
AnsRegistry` point the same direction). These columns exist in the database today, but
querying through `AnamnysDbContext.Providers` simply cannot see them — EF Core only knows
about the columns you've told it about. If you're debugging something that seems to
involve a provider's licensing info and it isn't showing up through the `DbContext`, this
is why: **the table has the column; the entity doesn't yet.** This is a pattern worth
generalizing — whenever something in the SQL schema doesn't show up in a C# entity, that's
not necessarily a bug, it's usually just scope that hasn't been mapped yet. Chapter 10
will point out several more examples as it tours the rest of the schema.

## `DatabaseInitializer`: bootstrapping from the raw SQL file, not migrations

This project does not use EF Core migrations. Instead, `DatabaseInitializer` — registered
as an `IHostedService` in `Program.cs`, so it runs once at application startup — checks
whether the schema already exists, and if not, executes the entire `anamnys-db-script.sql`
file as one script:

```csharp
var exists = await db.Database
    .SqlQuery<bool>($"SELECT to_regclass('public.\"Providers\"') IS NOT NULL AS \"Value\"")
    .SingleAsync(cancellationToken);

if (exists)
{
    logger.LogInformation("Schema already present; skipping bootstrap.");
    return;
}
```

It checks for the existence of a single, specific table (`Providers`) as a proxy for "has
the whole schema been created," rather than checking all 68 — a reasonable simplification
given the script is one atomic unit that either fully succeeds or is expected to be
investigated and fixed, not partially applied.

The SQL file itself isn't read from disk at runtime — it's embedded as a resource in the
compiled assembly (`Assembly.GetManifestResourceStream("anamnys-db-script.sql")`), so the
schema-bootstrap logic doesn't depend on a particular working directory or deployment
layout being able to find a loose `.sql` file.

### Why raw ADO.NET, not `ExecuteSqlRawAsync`

The execution mechanism is worth understanding, because the reason for it is a genuinely
easy mistake to make and a genuinely confusing one to debug if you hit it blind:

```csharp
// EF Core's ExecuteSqlRawAsync(string, CancellationToken) resolves to the
// params-object[] overload, which treats the SQL as a composite format string
// and throws FormatException on the literal "{}" JSONB defaults in the script.
// Executing on the raw ADO.NET connection avoids that parsing entirely.
await db.Database.OpenConnectionAsync(cancellationToken);
var connection = db.Database.GetDbConnection();
await using var command = connection.CreateCommand();
command.CommandText = sql;
command.CommandTimeout = 120;
await command.ExecuteNonQueryAsync(cancellationToken);
```

`anamnys-db-script.sql` contains JSONB column defaults written as literal `{}` — perfectly
ordinary Postgres syntax. But `DbContext.Database.ExecuteSqlRawAsync(string, CancellationToken)`
resolves to an overload that treats its input as a **composite format string** (the same
kind `string.Format` uses), where `{}` is invalid format-string syntax and throws a
`FormatException` — a completely unrelated-looking failure for what is, in the SQL itself,
correct and ordinary. The fix here is to sidestep EF Core's raw-SQL execution path
entirely and run the script directly against the underlying ADO.NET `DbConnection`, which
has no opinion about format strings at all. If you're ever tempted to "simplify" this back
to `ExecuteSqlRawAsync`, this is exactly the failure you'd reintroduce.

### The five defects already fixed in the committed script

One more thing worth knowing if you ever need to regenerate or touch
`anamnys-db-script.sql`: as committed, it has already had five pre-existing defects fixed
that, before the fix, meant the script had never once successfully replayed against a
fresh Postgres database:

- a `CREATE SCHEMA "public"` statement (errors on a fresh database, where `public` already
  exists)
- 69 redundant `_pkey` indexes
- 28 redundant `_key`/`_unique` indexes
- two malformed `gist` overlap-exclusion statements
- a `TABLESPACE public` clause attached to a view (views don't take a tablespace argument)

**If this file is ever regenerated from whatever tool originally produced it, all five of
these defects will reappear.** Don't blindly overwrite the committed file from that tool
again without reapplying the fixes — this is exactly the kind of thing that looks like an
obviously safe "just regenerate it" operation and quietly breaks the one thing
`DatabaseInitializer` depends on: that this script actually runs cleanly.

## Provider-scoping, made concrete

Chapter 1 introduced provider-scoping as the system's primary access control; Chapter 8
showed you exactly where a trustworthy provider id comes from (`principal.LocalId()`, and
nowhere else). Here's what that principle looks like once it reaches a query, using the
shape every future PHI-touching endpoint should follow:

```csharp
// Correct: the provider id comes from the authenticated session.
var patients = await db.Patients
    .Where(p => p.ProviderId == principal.LocalId())
    .ToListAsync(cancellationToken);
```

Every table in Chapter 10's tour that carries a `ProviderId` column — which is most of
them — is a table this pattern applies to. A query that instead filtered on a
client-supplied provider id (a route parameter, a request body field) would compile,
would often even *work* during casual testing, and would be a HIPAA-severity bug: it would
let one provider request another provider's patients simply by changing an id in the
request. There is no framework-level guard against this mistake today — it is a discipline
the codebase asks of every developer who touches PHI-adjacent code, not something enforced
by a compiler or a query filter. Chapter 18 revisits this as a concrete review checklist
item.

With the currently-wired five tables covered, Chapter 10 takes you through the other 63 —
the shape of the whole product, in schema form.
