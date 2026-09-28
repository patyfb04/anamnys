# Provider Patient List Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A provider-scoped, server-searched, filterable, sortable, paginated patient list
at `/patients` in the provider app, matching `specs/UI/PatientList`.

**Architecture:** One `POST /api/phi/providers/me/patients/search` endpoint. A single EF
LINQ query scopes by provider, projects next appointment and latest-note status through
correlated subqueries, filters, sorts and pages in SQL. The React page keeps filter state
in memory (never in the URL) and renders a table at `md+` and cards below.

**Tech Stack:** .NET 10 minimal APIs, EF Core 10 + Npgsql, PostgreSQL 18, xUnit v3 +
FluentAssertions; React 19, TanStack Query/Router, Tailwind 4, i18next.

**Spec:** `design/specs/2026-09-27-patient-list-design.md`

## Global Constraints

- Every query filters `ProviderId == <session local id>` first; the provider id never comes from the request.
- No PHI in URLs: filters travel in a JSON `POST` body; the frontend never writes filters to the route/search params.
- Note-status groups: `pending` = Draft/Processing/ReadyForReview, `signed` = Signed/Exported, `none` = no notes.
- Next appointment = min `StartsAt > now` with `Status IN ('scheduled','confirmed')`.
- Date filters are whole days in `America/Sao_Paulo`, inclusive both ends.
- Sort default `nextVisit asc`; nulls last in both directions; tie-break last name, first name, id.
- `pageSize` 1–100 (default 25); text filters ≤ 200 chars.
- `now` is a parameter to the query, never SQL `now()`.
- Validation messages in Portuguese, errors keyed by camelCase field name (matches `ProfileContracts`).

---

## File Structure

Server (`anamnys-aspire.Server/`):
- Create `Data/Entities/Appointment.cs`, `Data/Entities/Note.cs` — read-only column subsets.
- Modify `Data/AnamnysDbContext.cs` — map both.
- Create `Patients/PatientSearchContracts.cs` — request (+ `Validate()`), response, list item, status constants.
- Create `Patients/PatientSearchQuery.cs` — the query.
- Create `Patients/PatientEndpoints.cs` — route.
- Modify `Program.cs` — `phi.MapPatientEndpoints();`.
- Modify `../anamnys-db-script.sql` — `Notes_Patient_Created_idx`.

Tests (`anamnys-aspire.Tests/Patients/`):
- `PatientSearchValidationTests.cs`, `PatientSearchSeed.cs` (SQL seeding helper), `PatientSearchQueryTests.cs`, `PatientEndpointTests.cs`.

Frontend:
- Modify `packages/shared/src/lib/types.ts`, `packages/shared/src/api/patients.ts`, `packages/shared/src/lib/i18n/locales/pt.json`.
- Create `apps/provider/src/hooks/usePatientSearch.ts`.
- Create `apps/provider/src/components/patients/{NoteStatusBadge,PatientsTable,PatientCards,PatientFilterPanel,PaginationFooter,format}.tsx|ts`.
- Rewrite `apps/provider/src/routes/_app/patients/index.tsx`.

---

### Task 1: Contracts and validation

**Files:**
- Create: `anamnys-aspire.Server/Patients/PatientSearchContracts.cs`
- Test: `anamnys-aspire.Tests/Patients/PatientSearchValidationTests.cs`

**Interfaces — Produces:**
- `sealed record PatientSearchRequest` with init props `Search, Name, Email : string?`, `NoteStatus : string[]?`, `LastVisitFrom, LastVisitTo, NextVisitFrom, NextVisitTo : DateOnly?`, `SortBy : string? = "nextVisit"`, `SortDir : string? = "asc"`, `Page : int = 1`, `PageSize : int = 25`; `Dictionary<string,string[]> Validate()`.
- `sealed record PatientListItem(Guid Id, string FirstName, string LastName, string? Email, DateTimeOffset? LastVisit, DateTimeOffset? NextAppointmentAt, string NoteStatus)`.
- `sealed record PatientSearchResponse(IReadOnlyList<PatientListItem> Items, int TotalCount, int Page, int PageSize)`.
- `static class NoteStatusGroup { Pending="pending"; Signed="signed"; None="none"; All }`, `static class PatientSortBy { Name, LastVisit, NextVisit, NoteStatus, All }`.

- [ ] **Step 1: Write failing tests**

```csharp
using Anamnys.Server.Patients;
using FluentAssertions;

namespace Anamnys.Tests.Patients;

public class PatientSearchValidationTests
{
    [Fact]
    public void Validate_DefaultRequest_HasNoErrors()
    {
        new PatientSearchRequest().Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData("lastVisit")]
    [InlineData("nextVisit")]
    public void Validate_ReversedRange_ReportsTheToField(string range)
    {
        var from = new DateOnly(2026, 10, 2);
        var to = new DateOnly(2026, 10, 1);
        var request = range == "lastVisit"
            ? new PatientSearchRequest { LastVisitFrom = from, LastVisitTo = to }
            : new PatientSearchRequest { NextVisitFrom = from, NextVisitTo = to };

        request.Validate().Should().ContainKey($"{range}To");
    }

    [Fact]
    public void Validate_UnknownEnumValues_ReportsEachField()
    {
        var errors = new PatientSearchRequest { NoteStatus = ["pending", "draft"], SortBy = "age", SortDir = "up" }.Validate();

        errors.Keys.Should().BeEquivalentTo(["noteStatus", "sortBy", "sortDir"]);
    }

    [Fact]
    public void Validate_TextOver200Chars_ReportsEachField()
    {
        var longText = new string('a', 201);

        var errors = new PatientSearchRequest { Search = longText, Name = longText, Email = longText }.Validate();

        errors.Keys.Should().BeEquivalentTo(["search", "name", "email"]);
    }

    [Theory]
    [InlineData(0, 25, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 101, "pageSize")]
    public void Validate_PagingOutOfRange_ReportsTheField(int page, int pageSize, string key)
    {
        new PatientSearchRequest { Page = page, PageSize = pageSize }.Validate().Should().ContainKey(key);
    }
}
```

- [ ] **Step 2: Run — expect compile failure** — `dotnet test anamnys-aspire.Tests --filter-class "Anamnys.Tests.Patients.PatientSearchValidationTests"` (xUnit v3 MTP filter syntax; fall back to `-- --filter-class ...` if needed).

- [ ] **Step 3: Implement**

```csharp
using Anamnys.Server.Profile;

namespace Anamnys.Server.Patients;

public static class NoteStatusGroup
{
    public const string Pending = "pending";
    public const string Signed = "signed";
    public const string None = "none";
    public static readonly string[] All = [Pending, Signed, None];
}

public static class PatientSortBy
{
    public const string Name = "name";
    public const string LastVisit = "lastVisit";
    public const string NextVisit = "nextVisit";
    public const string NoteStatus = "noteStatus";
    public static readonly string[] All = [Name, LastVisit, NextVisit, NoteStatus];
}

// Every field is optional. Filters travel in a POST body, never a query string: name and
// email are PHI and query strings end up in proxy and server logs.
public sealed record PatientSearchRequest
{
    public const int MaxTextLength = 200;
    public const int MaxPageSize = 100;

    public string? Search { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string[]? NoteStatus { get; init; }
    public DateOnly? LastVisitFrom { get; init; }
    public DateOnly? LastVisitTo { get; init; }
    public DateOnly? NextVisitFrom { get; init; }
    public DateOnly? NextVisitTo { get; init; }
    public string? SortBy { get; init; } = PatientSortBy.NextVisit;
    public string? SortDir { get; init; } = "asc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        RequireMaxLength(errors, "search", Search);
        RequireMaxLength(errors, "name", Name);
        RequireMaxLength(errors, "email", Email);

        if (NoteStatus is not null && NoteStatus.Any(s => !NoteStatusGroup.All.Contains(s)))
        {
            errors["noteStatus"] = ["Use pending, signed ou none."];
        }
        if (SortBy is not null && !PatientSortBy.All.Contains(SortBy))
        {
            errors["sortBy"] = ["Use name, lastVisit, nextVisit ou noteStatus."];
        }
        if (SortDir is not null && SortDir is not ("asc" or "desc"))
        {
            errors["sortDir"] = ["Use asc ou desc."];
        }

        if (LastVisitFrom > LastVisitTo)
        {
            errors["lastVisitTo"] = ["A data final deve ser igual ou posterior à inicial."];
        }
        if (NextVisitFrom > NextVisitTo)
        {
            errors["nextVisitTo"] = ["A data final deve ser igual ou posterior à inicial."];
        }

        if (Page < 1)
        {
            errors["page"] = ["A página deve ser 1 ou maior."];
        }
        if (PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Use de 1 a {MaxPageSize} itens por página."];
        }

        return errors;
    }

    private static void RequireMaxLength(Dictionary<string, string[]> errors, string key, string? value)
    {
        if (ProfileText.Clean(value) is { Length: > MaxTextLength })
        {
            errors[key] = [$"Use no máximo {MaxTextLength} caracteres."];
        }
    }
}

public sealed record PatientListItem(
    Guid Id,
    string FirstName,
    string LastName,
    string? Email,
    DateTimeOffset? LastVisit,
    DateTimeOffset? NextAppointmentAt,
    string NoteStatus);

public sealed record PatientSearchResponse(IReadOnlyList<PatientListItem> Items, int TotalCount, int Page, int PageSize);
```

(`DateOnly? > DateOnly?` is false when either side is null — the lifted comparison is what we want.)

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** `feat: add patient search contracts with validation`.

---

### Task 2: Entities, index, and the query

**Files:**
- Create: `Data/Entities/Appointment.cs`, `Data/Entities/Note.cs`, `Patients/PatientSearchQuery.cs`
- Modify: `Data/AnamnysDbContext.cs`, `anamnys-db-script.sql`
- Test: `Patients/PatientSearchSeed.cs`, `Patients/PatientSearchQueryTests.cs`

**Interfaces:**
- Consumes: Task 1 contracts.
- Produces: `static Task<PatientSearchResponse> PatientSearchQuery.ExecuteAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset now, PatientSearchRequest request, CancellationToken ct)`; `DbSet<Appointment> Appointments`, `DbSet<Note> Notes`.

- [ ] **Step 1: Seeding helper** (`PatientSearchSeed.cs`). Raw SQL via Npgsql, because the EF entities map only a column subset (e.g. `Appointments.EndsAt` is NOT NULL and unmapped). Each call creates a fresh provider so tests are isolated without cleanup.

```csharp
using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Tests.Patients;

// Seeds one fresh provider per test: the provider's own Guid isolates every test's rows
// from every other test's (and from the dev data) without any cleanup.
public sealed class PatientSearchSeed(NpgsqlConnection connection) : IAsyncDisposable
{
    public Guid ProviderId { get; private set; }

    public static async Task<PatientSearchSeed> CreateAsync(SharedAppHostFixture fixture, CancellationToken ct)
    {
        var connectionString = await fixture.GetConnectionStringAsync("anamnysdb", ct)
            ?? throw new InvalidOperationException("No connection string for anamnysdb.");
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var seed = new PatientSearchSeed(connection);
        seed.ProviderId = await seed.AddProviderAsync(ct);
        return seed;
    }

    public AnamnysDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AnamnysDbContext>().UseNpgsql(connection.ConnectionString).Options);

    public async Task<Guid> AddProviderAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@id, @email, @sub, 'Test Provider')""",
            ct, ("id", id), ("email", $"{id:N}@test.local"), ("sub", Guid.NewGuid()));
        return id;
    }

    public async Task<Guid> AddPatientAsync(
        string firstName, string lastName, string? email = null, DateTimeOffset? lastVisit = null,
        Guid? providerId = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName","Email","LastVisit")
            VALUES (@id, @provider, @first, @last, @email, @lastVisit)
            """,
            ct, ("id", id), ("provider", providerId ?? ProviderId), ("first", firstName), ("last", lastName),
            ("email", (object?)email ?? DBNull.Value), ("lastVisit", (object?)lastVisit?.ToUniversalTime() ?? DBNull.Value));
        return id;
    }

    public Task AddAppointmentAsync(
        Guid patientId, DateTimeOffset startsAt, string status = "scheduled", Guid? providerId = null, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            INSERT INTO "Appointments" ("ProviderId","PatientId","StartsAt","EndsAt","Status","CancelledAt")
            VALUES (@provider, @patient, @starts, @starts + interval '50 minutes', @status,
                    CASE WHEN @status = 'cancelled' THEN now() END)
            """,
            ct, ("provider", providerId ?? ProviderId), ("patient", patientId), ("starts", startsAt.ToUniversalTime()), ("status", status));

    public Task AddNoteAsync(
        Guid patientId, string status, DateTimeOffset createdAt, Guid? providerId = null, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            INSERT INTO "Notes" ("ProviderId","PatientId","Status","InputMode","SignedAt","CreatedAt")
            VALUES (@provider, @patient, @status, 'Text',
                    CASE WHEN @status IN ('Signed','Exported') THEN @created END, @created)
            """,
            ct, ("provider", providerId ?? ProviderId), ("patient", patientId), ("status", status), ("created", createdAt.ToUniversalTime()));

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
```

- [ ] **Step 2: Failing query tests** (`PatientSearchQueryTests.cs`). Fixed `Now = 2026-10-01T12:00Z`. Each test: `await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct); await using var db = seed.CreateDbContext();` then `PatientSearchQuery.ExecuteAsync(db, seed.ProviderId, Now, request, Ct)`. Cases (one `[Fact]` each):
  1. `Scoping_OtherProvidersRowsNeverAppear` — second provider's patient, and its appointment/note, absent; `TotalCount` = own count.
  2. `NextAppointment_IsEarliestFutureScheduledOrConfirmed` — past scheduled, future cancelled, future attended, future no_show, future confirmed (+2d), future scheduled (+5d) → +2d.
  3. `NextAppointment_IsNullWithoutAFutureOne`.
  4. `NoteStatus_GroupsTheMostRecentNote` — patients with latest `ReadyForReview` → pending, `Exported` → signed, no notes → none, older Signed + newer Draft → pending.
  5. `Search_MatchesFullNameOrEmail_CaseInsensitive` — "ana silva" matches Ana Silva; "@CLINIC" matches by email; `%` in search is literal (no match on "100%").
  6. `NameAndEmailFilters_MatchOnlyTheirField`.
  7. `NoteStatusFilter_CombinesValuesWithOr`.
  8. `LastVisitRange_IncludesBothBoundaryDaysInSaoPaulo` — LastVisit at 2026-09-10T02:59Z (= 09-09 23:59 SP) excluded from `from=09-10`; 2026-09-10T03:00Z included; 2026-09-21T02:59Z (09-20 23:59 SP) included in `to=09-20`; 2026-09-21T03:00Z excluded.
  9. `NextVisitRange_FromOnlyAndToOnly` — `from` only excludes earlier and patients without appointment; `to` only likewise.
  10. `Sort_DefaultIsNextVisitAscWithNullsLast`.
  11. `Sort_EachColumnBothDirections_NullsLast` — `[Theory]` over (sortBy, sortDir) asserting the order of 3 patients where one has nulls.
  12. `Sort_TiesBreakByLastNameFirstNameId`.
  13. `Paging_CountsAllMatchesAndSlices` — 5 patients, pageSize 2: page 1 → 2 items, TotalCount 5; page 3 → 1; page 4 → 0 items, TotalCount 5.

- [ ] **Step 3: Run — expect compile failure.**

- [ ] **Step 4: Implement entities + DbContext mapping**

```csharp
namespace Anamnys.Server.Data.Entities;

// Read-only column subset of "Appointments" — the patient list needs only the next
// start time. Nothing writes appointments through EF yet; the table has NOT NULL columns
// (EndsAt) this entity does not map, so an insert through it would fail by design.
public class Appointment
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public string Status { get; set; } = "";
}
```

```csharp
namespace Anamnys.Server.Data.Entities;

// Read-only column subset of "Notes" — the patient list needs only the latest status.
// Nothing writes notes through EF yet; InputMode is NOT NULL and unmapped.
public class Note
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
```

In `AnamnysDbContext`: `public DbSet<Appointment> Appointments => Set<Appointment>();`, `public DbSet<Note> Notes => Set<Note>();` and in `OnModelCreating`:

```csharp
modelBuilder.Entity<Appointment>(e =>
{
    e.ToTable("Appointments");
    e.HasKey(x => x.Id);
});

modelBuilder.Entity<Note>(e =>
{
    e.ToTable("Notes");
    e.HasKey(x => x.Id);
});
```

Schema, after `Notes_ProviderId_idx`:

```sql
CREATE INDEX "Notes_Patient_Created_idx" ON "Notes" ("PatientId","CreatedAt" DESC);
```

- [ ] **Step 5: Implement `PatientSearchQuery`**

```csharp
using Anamnys.Server.Data;
using Anamnys.Server.Profile;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Patients;

// See design/specs/2026-09-27-patient-list-design.md §3. One data query plus one COUNT.
// Cost is bounded by one provider's rows: two index probes per patient
// (Appointments_Patient_Starts_idx, Notes_Patient_Created_idx).
public static class PatientSearchQuery
{
    private static readonly TimeZoneInfo PracticeTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static async Task<PatientSearchResponse> ExecuteAsync(
        AnamnysDbContext db, Guid providerId, DateTimeOffset now, PatientSearchRequest request, CancellationToken cancellationToken)
    {
        var nowUtc = now.ToUniversalTime();

        // Scope first, always — the subqueries repeat the provider filter as defence in depth.
        var rows = db.Patients.AsNoTracking()
            .Where(p => p.ProviderId == providerId)
            .Select(p => new PatientRow
            {
                Id = p.Id,
                FirstName = p.FirstName,
                LastName = p.LastName,
                Email = p.Email,
                LastVisit = p.LastVisit,
                NextAppointmentAt = db.Appointments
                    .Where(a => a.PatientId == p.Id && a.ProviderId == providerId
                        && a.StartsAt > nowUtc && (a.Status == "scheduled" || a.Status == "confirmed"))
                    .Min(a => (DateTimeOffset?)a.StartsAt),
                LatestNoteStatus = db.Notes
                    .Where(n => n.PatientId == p.Id && n.ProviderId == providerId)
                    .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
                    .Select(n => n.Status)
                    .FirstOrDefault(),
            })
            .Select(r => new PatientRow
            {
                Id = r.Id,
                FirstName = r.FirstName,
                LastName = r.LastName,
                Email = r.Email,
                LastVisit = r.LastVisit,
                NextAppointmentAt = r.NextAppointmentAt,
                LatestNoteStatus = r.LatestNoteStatus,
                NoteGroup = r.LatestNoteStatus == null ? NoteStatusGroup.None
                    : r.LatestNoteStatus == "Signed" || r.LatestNoteStatus == "Exported" ? NoteStatusGroup.Signed
                    : NoteStatusGroup.Pending,
            });

        rows = ApplyFilters(rows, request);

        var totalCount = await rows.CountAsync(cancellationToken);

        var items = await ApplySort(rows, request)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(r => new PatientListItem(
                r.Id, r.FirstName, r.LastName, r.Email, r.LastVisit, r.NextAppointmentAt, r.NoteGroup!))
            .ToListAsync(cancellationToken);

        return new PatientSearchResponse(items, totalCount, request.Page, request.PageSize);
    }

    private static IQueryable<PatientRow> ApplyFilters(IQueryable<PatientRow> rows, PatientSearchRequest request)
    {
        if (ContainsPattern(request.Search) is { } search)
        {
            rows = rows.Where(r => EF.Functions.ILike(r.FirstName + " " + r.LastName, search, "\\")
                || (r.Email != null && EF.Functions.ILike(r.Email, search, "\\")));
        }
        if (ContainsPattern(request.Name) is { } name)
        {
            rows = rows.Where(r => EF.Functions.ILike(r.FirstName + " " + r.LastName, name, "\\"));
        }
        if (ContainsPattern(request.Email) is { } email)
        {
            rows = rows.Where(r => r.Email != null && EF.Functions.ILike(r.Email, email, "\\"));
        }
        if (request.NoteStatus is { Length: > 0 } statuses)
        {
            rows = rows.Where(r => statuses.Contains(r.NoteGroup!));
        }
        if (StartOfDay(request.LastVisitFrom) is { } lastFrom)
        {
            rows = rows.Where(r => r.LastVisit >= lastFrom);
        }
        if (StartOfDay(request.LastVisitTo?.AddDays(1)) is { } lastToExclusive)
        {
            rows = rows.Where(r => r.LastVisit < lastToExclusive);
        }
        if (StartOfDay(request.NextVisitFrom) is { } nextFrom)
        {
            rows = rows.Where(r => r.NextAppointmentAt >= nextFrom);
        }
        if (StartOfDay(request.NextVisitTo?.AddDays(1)) is { } nextToExclusive)
        {
            rows = rows.Where(r => r.NextAppointmentAt < nextToExclusive);
        }
        return rows;
    }

    // Nulls last in both directions; ties broken by last name, first name, id so paging is stable.
    private static IQueryable<PatientRow> ApplySort(IQueryable<PatientRow> rows, PatientSearchRequest request)
    {
        var desc = request.SortDir == "desc";
        IOrderedQueryable<PatientRow> ordered = (request.SortBy ?? PatientSortBy.NextVisit) switch
        {
            PatientSortBy.Name => desc
                ? rows.OrderByDescending(r => r.LastName).ThenByDescending(r => r.FirstName)
                : rows.OrderBy(r => r.LastName).ThenBy(r => r.FirstName),
            PatientSortBy.LastVisit => desc
                ? rows.OrderBy(r => r.LastVisit == null).ThenByDescending(r => r.LastVisit)
                : rows.OrderBy(r => r.LastVisit == null).ThenBy(r => r.LastVisit),
            PatientSortBy.NoteStatus => desc
                ? rows.OrderByDescending(r => r.NoteGroup == NoteStatusGroup.Pending ? 0 : r.NoteGroup == NoteStatusGroup.Signed ? 1 : 2)
                : rows.OrderBy(r => r.NoteGroup == NoteStatusGroup.Pending ? 0 : r.NoteGroup == NoteStatusGroup.Signed ? 1 : 2),
            _ => desc
                ? rows.OrderBy(r => r.NextAppointmentAt == null).ThenByDescending(r => r.NextAppointmentAt)
                : rows.OrderBy(r => r.NextAppointmentAt == null).ThenBy(r => r.NextAppointmentAt),
        };
        return ordered.ThenBy(r => r.LastName).ThenBy(r => r.FirstName).ThenBy(r => r.Id);
    }

    // Case-insensitive "contains" with the user's % _ \ taken literally.
    private static string? ContainsPattern(string? value) =>
        ProfileText.Clean(value) is { } cleaned
            ? "%" + cleaned.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"
            : null;

    // Midnight of the given day in the practice time zone, as UTC (Npgsql only writes
    // offset-zero DateTimeOffset values to timestamptz).
    private static DateTimeOffset? StartOfDay(DateOnly? day)
    {
        if (day is not { } d)
        {
            return null;
        }
        var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, PracticeTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    // Settable members, not a positional record: EF can only compose further Where/OrderBy
    // over a projection built with member initialisation.
    private sealed class PatientRow
    {
        public Guid Id { get; init; }
        public string FirstName { get; init; } = "";
        public string LastName { get; init; } = "";
        public string? Email { get; init; }
        public DateTimeOffset? LastVisit { get; init; }
        public DateTimeOffset? NextAppointmentAt { get; init; }
        public string? LatestNoteStatus { get; init; }
        public string? NoteGroup { get; init; }
    }
}
```

- [ ] **Step 6: Run query tests — expect PASS.** If EF fails to translate something, fix the expression (not the test) and note why in a comment.
- [ ] **Step 7: Commit** `feat: add provider-scoped patient search query`.

---

### Task 3: Endpoint

**Files:** Create `Patients/PatientEndpoints.cs`; modify `Program.cs`; test `Patients/PatientEndpointTests.cs`.

**Interfaces:** Consumes `PatientSearchQuery.ExecuteAsync`. Produces `MapPatientEndpoints(this RouteGroupBuilder phi)`.

- [ ] **Step 1: Failing tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Patients;

[Collection(SharedAppHostCollection.Name)]
public class PatientEndpointTests
{
    private const string Path = "/api/phi/providers/me/patients/search";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Fact]
    public async Task Search_WithoutSession_Returns401()
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);

        using var response = await client.PostAsJsonAsync(Path, new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_AsProvider_ReturnsAPage()
    {
        using var session = await LoginAsProviderAsync();

        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(new { pageSize = 5 }) },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
        body.RootElement.GetProperty("totalCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        body.RootElement.GetProperty("page").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("pageSize").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Search_WithInvalidBody_Returns400WithFieldErrors()
    {
        using var session = await LoginAsProviderAsync();

        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(new { sortBy = "age", pageSize = 500 }) },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var errors = body.RootElement.GetProperty("errors");
        errors.TryGetProperty("sortBy", out _).Should().BeTrue();
        errors.TryGetProperty("pageSize", out _).Should().BeTrue();
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);
}
```

- [ ] **Step 2: Run — expect 404s/compile failure.**
- [ ] **Step 3: Implement**

```csharp
using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authentication;

namespace Anamnys.Server.Patients;

public static class PatientEndpoints
{
    // POST, not GET: name and email filters are PHI and must stay out of the URL.
    // Realm in the path and the provider cookie authenticated explicitly, as in
    // ProfileEndpoints — a browser can hold provider and patient sessions at once.
    public static void MapPatientEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapPost("providers/me/patients/search", async (
            PatientSearchRequest request,
            HttpContext httpContext,
            AnamnysDbContext db,
            CancellationToken cancellationToken) =>
        {
            var auth = await httpContext.AuthenticateAsync(AuthSchemes.ProviderCookie);
            if (!auth.Succeeded || auth.Principal!.LocalIdOrNull() is not { } providerId)
            {
                return Results.Unauthorized();
            }

            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var response = await PatientSearchQuery.ExecuteAsync(db, providerId, DateTimeOffset.UtcNow, request, cancellationToken);
            return Results.Ok(response);
        });
    }
}
```

`Program.cs`: add `using Anamnys.Server.Patients;` and `phi.MapPatientEndpoints();` after `phi.MapProfileEndpoints();`.

- [ ] **Step 4: Run all `Patients` tests — expect PASS.**
- [ ] **Step 5: Commit** `feat: expose patient search at /api/phi/providers/me/patients/search`.

---

### Task 4: Frontend data layer

**Files:** Modify `packages/shared/src/lib/types.ts`, `packages/shared/src/api/patients.ts`. Create `apps/provider/src/hooks/usePatientSearch.ts`.

**Interfaces — Produces:**

```ts
export type NoteStatusGroup = "pending" | "signed" | "none";
export type PatientSortBy = "name" | "lastVisit" | "nextVisit" | "noteStatus";
export type SortDir = "asc" | "desc";

export interface PatientFilters {
  name?: string;
  email?: string;
  noteStatus?: NoteStatusGroup[];
  lastVisitFrom?: string; // "YYYY-MM-DD"
  lastVisitTo?: string;
  nextVisitFrom?: string;
  nextVisitTo?: string;
}

export interface PatientSearchRequest extends PatientFilters {
  search?: string;
  sortBy?: PatientSortBy;
  sortDir?: SortDir;
  page?: number;
  pageSize?: number;
}

export interface PatientListItem {
  id: string;
  firstName: string;
  lastName: string;
  email: string | null;
  lastVisit: string | null;
  nextAppointmentAt: string | null;
  noteStatus: NoteStatusGroup;
}

export interface PatientSearchResponse {
  items: PatientListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}
```

`patientsApi.search(request: PatientSearchRequest): Promise<PatientSearchResponse>` → `api.post("/phi/providers/me/patients/search", request)`. Remove `list`.

`usePatientSearch()` returns
`{ search, setSearch, filters, setFilters, sortBy, sortDir, toggleSort(column), setSort(sortBy, sortDir), page, setPage, activeFilterCount, query }`
where `query` is the `useQuery` result. Implementation: `useState` for each; `useDeferredDebounce` via `useEffect` + `setTimeout(300)` producing `debouncedSearch`; setters for search/filters/sort also `setPage(1)`; `toggleSort(c)` flips `sortDir` when `c === sortBy`, else sets `c`/`asc`; request object built with `useMemo`, empty strings dropped; `useQuery({ queryKey: ["patients", "search", request], queryFn: () => patientsApi.search(request), placeholderData: keepPreviousData })`. `activeFilterCount` counts non-empty filter fields (each date range counts once).

- [ ] **Step 1:** Write types, API, hook.
- [ ] **Step 2:** `npm run build -w @anamnys/provider` fails on the old page's `patientsApi.list` (expected until Task 5).
- [ ] **Step 3:** Commit together with Task 5 (the build is red in between).

---

### Task 5: Page and components

**Files:** Create in `apps/provider/src/components/patients/`: `format.ts`, `NoteStatusBadge.tsx`, `PatientsTable.tsx`, `PatientCards.tsx`, `PatientFilterPanel.tsx`, `PaginationFooter.tsx`. Rewrite `routes/_app/patients/index.tsx`. Update `pt.json`.

**Interfaces:**
- `format.ts`: `formatDate(iso: string | null, locale: string): string` (`"—"` for null; `dateStyle: "medium"`), `formatTime(iso, locale)` (`timeStyle: "short"`), both via `Intl.DateTimeFormat`, timezone `America/Sao_Paulo`.
- `NoteStatusBadge({ status }: { status: NoteStatusGroup })` — shared `Badge`: pending → `neutral` + dot icon, signed → `mint` + dot, none → `neutral` with `opacity-60`.
- `PatientsTable({ items, sortBy, sortDir, onSort, onOpen })` — `<table>` semantics (not div grid) for a11y; header `<th>` buttons with `aria-sort`; columns per spec §6; `hidden md:block` wrapper.
- `PatientCards({ items, sortBy, sortDir, onSort, onOpen })` — `md:hidden`; sort `<select>` with options `nextVisit|lastVisit|name|noteStatus` × asc/desc.
- `PatientFilterPanel({ open, filters, onApply, onClear, onClose })` — local draft state initialised from `filters` on open; desktop: absolutely positioned popover (`md:absolute md:right-0 md:top-full md:w-96`), mobile: `fixed inset-0` sheet; closes on Escape and outside click (desktop).
- `PaginationFooter({ page, pageSize, totalCount, onPage })` — "Mostrando {{from}}–{{to}} de {{total}} pacientes"; prev disabled on page 1, next disabled when `page * pageSize >= totalCount`.
- Page: header (title, subtitle, Filtrar with count badge, Adicionar paciente → `/patients/new`), `TextField` search with `Search` icon, then loading skeleton / error / empty (no filters) / empty (filters) / table + cards + footer. Row click → `navigate({ to: "/patients/$patientId", params: { patientId } })`.

i18n keys under `patients.list`: `title`, `subtitle`, `filter`, `addPatient`, `searchPlaceholder` (kept), `columns.{patient,lastVisit,nextVisit,details,status}`, `viewDetails`, `noPortal`, `status.{pending,signed,none}`, `sortLabel`, `sort.{nextVisit,lastVisit,name,noteStatus}`, `dir.{asc,desc}`, `filters.{title,name,email,status,lastVisit,nextVisit,from,to,apply,clear,close}`, `showing` ("Mostrando {{from}}–{{to}} de {{total}} pacientes"), `previousPage`, `nextPage`, `emptyTitle`, `emptyBody`, `noResults`, `clearFilters`, `failedToLoad` (kept). Remove `recent`, `last48h`, `allPatients`, `noPatientsFound`, `lastVisit`, `dob`.

- [ ] **Step 1:** Write components, page, i18n.
- [ ] **Step 2:** `npm run build -w @anamnys/provider` and `npm run lint -w @anamnys/provider` — clean.
- [ ] **Step 3:** Commit Tasks 4+5 `feat: rebuild the provider patient list on server-side search`.

---

### Task 6: End-to-end check

- [ ] Run the full `Patients` test set and the rest of the suite (`dotnet test anamnys-aspire.Tests`).
- [ ] Start the app (`run` skill), seed a handful of patients/appointments/notes for `dev.provider` via SQL, and check `/provider/patients` at desktop and 390px widths: search, each filter, each sort header, paging, empty states, row navigation.
- [ ] Update `documentation/` chapter that lists PHI endpoints if one exists (grep `providers/me/profile` in `documentation/`).
