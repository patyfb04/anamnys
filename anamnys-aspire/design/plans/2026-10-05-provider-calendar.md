# Provider Calendar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Providers see their appointments in a week/day grid and create, reschedule, cancel and change the status of one-off appointments.

**Architecture:** A static `Appointments` class holds the provider-scoped logic over the existing `Appointments` table; thin minimal-API handlers under `/api/phi/providers/me/appointments` map its outcomes to HTTP. Overlap is enforced by the database constraint `Appointments_no_overlap` and translated to `409`. The provider app gets a `/calendar` route with an in-house Tailwind grid (no calendar library); view, date and status filter live in the URL.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, xUnit v3 + FluentAssertions; React 19, TanStack Router/Query, Tailwind 4, i18next, axios client in `packages/shared/src/api/client.ts`.

**Spec:** `design/specs/2026-10-05-provider-calendar-design.md`

## Global Constraints

- Every query filters by the provider id resolved from the provider cookie; another provider's appointment or patient is `404`, same as missing.
- No schema change. The `Appointments` table already exists.
- Appointments are created with `Timezone = 'America/Sao_Paulo'`, `Status = 'scheduled'`, `CreatedBy = 'provider'`.
- Duration 5–480 minutes; modality `online` | `presencial`; cancellation reason at most 500 characters; list range at most 42 days.
- Transitions: `scheduled` → `confirmed`|`attended`|`no_show`|`cancelled`; `confirmed` → `scheduled`|`attended`|`no_show`|`cancelled`; `attended`|`no_show` → `scheduled`; `cancelled` terminal. `attended`/`no_show` require `StartsAt <= now`.
- Overlap → `409 { "message": "Horário já ocupado." }`. Archived patient on create → `422 { "message": "Paciente arquivado." }`.
- `attended` sets `Patients.LastVisit = max(LastVisit, StartsAt)`; leaving `attended` recomputes it from the remaining attended appointments with this provider, unchanged if none.
- No PHI in URLs: the calendar URL holds only `view`, `date`, `status`.
- All UI strings in Portuguese in `packages/shared/src/lib/i18n/locales/pt.json`.
- Tests run with `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"` from `anamnys-aspire/`. **Never `dotnet test`.** Run `aspire stop` before building if the app is running.
- Frontend checks: `npm run build` and `npm run lint` from `anamnys-aspire/apps/provider/`. There are no frontend automated tests; UI is verified in the browser.

---

## File Structure

**Server (`anamnys-aspire/anamnys-aspire.Server/`)**
- Create `Appointments/AppointmentContracts.cs` — request/response records, `Validate()`, status constants, transition table.
- Create `Appointments/Appointments.cs` — list, create, update, status change; outcome enum.
- Create `Appointments/AppointmentEndpoints.cs` — HTTP handlers.
- Modify `Data/Entities/Appointment.cs` — writable columns.
- Modify `Program.cs` — `phi.MapAppointmentEndpoints();`.

**Tests (`anamnys-aspire/anamnys-aspire.Tests/Appointments/`)**
- Create `AppointmentValidationTests.cs` (unit), `AppointmentsTests.cs` (Postgres), `AppointmentEndpointTests.cs` (HTTP boundary).

**Frontend**
- Modify `packages/shared/src/lib/types.ts` — appointment types.
- Create `packages/shared/src/api/appointments.ts` — `appointmentsApi`.
- Modify `packages/shared/src/lib/i18n/locales/pt.json` — `calendar` block.
- Modify `packages/shared/src/ui/Sidenav.tsx` — calendar becomes always on.
- Create in `apps/provider/src/`:
  - `components/calendar/calendarTime.ts` — time-zone and date helpers (pure).
  - `components/calendar/status.ts` — status styles and client transition table.
  - `hooks/useAppointments.ts` — query + mutations.
  - `hooks/useIsMobile.ts` — media query hook.
  - `components/calendar/CalendarHeader.tsx`, `CalendarGrid.tsx`, `AppointmentCard.tsx`, `PatientPicker.tsx`, `AppointmentFormModal.tsx`, `AppointmentDetailsModal.tsx`.
  - `routes/_app/calendar.tsx` (+ regenerated `routeTree.gen.ts`, committed).

---

### Task 1: Contracts, validation and transition table

**Files:**
- Create: `anamnys-aspire/anamnys-aspire.Server/Appointments/AppointmentContracts.cs`
- Test: `anamnys-aspire/anamnys-aspire.Tests/Appointments/AppointmentValidationTests.cs`

**Interfaces:**
- Produces: `AppointmentStatus` (constants `Scheduled`, `Confirmed`, `Attended`, `Cancelled`, `NoShow`, `All`); `AppointmentTransitions.IsAllowed(string from, string to): bool`; `AppointmentRules` (`MinDuration = 5`, `MaxDuration = 480`, `MaxReason = 500`, `MaxRangeDays = 42`, `Modalities`); records `CreateAppointmentRequest(Guid? PatientId, DateTimeOffset? StartsAt, int? DurationMinutes, string? Modality)`, `UpdateAppointmentRequest(DateTimeOffset? StartsAt, int? DurationMinutes, string? Modality)`, `ChangeAppointmentStatusRequest(string? Status, string? Reason)` — each with `Dictionary<string, string[]> Validate()`; `AppointmentRange.Validate(DateTimeOffset? from, DateTimeOffset? to, string[]? statuses): Dictionary<string, string[]>`; `AppointmentItem(Guid Id, Guid PatientId, string PatientName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Timezone, string Modality, string Status, string? CancellationReason)`; `AppointmentListResponse(IReadOnlyList<AppointmentItem> Items)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Anamnys.Server.Appointments;
using FluentAssertions;

namespace Anamnys.Tests.Appointments;

public class AppointmentValidationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidRequest_HasNoErrors()
    {
        new CreateAppointmentRequest(Guid.NewGuid(), Start, 50, "online").Validate().Should().BeEmpty();
    }

    [Fact]
    public void Create_MissingFields_ReportsEachField()
    {
        new CreateAppointmentRequest(null, null, null, null).Validate().Keys
            .Should().BeEquivalentTo(["patientId", "startsAt", "durationMinutes", "modality"]);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(481)]
    public void Create_DurationOutOfBounds_IsInvalid(int minutes)
    {
        new CreateAppointmentRequest(Guid.NewGuid(), Start, minutes, "online").Validate().Keys.Should().Equal("durationMinutes");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(480)]
    public void Create_DurationAtBounds_IsValid(int minutes)
    {
        new CreateAppointmentRequest(Guid.NewGuid(), Start, minutes, "presencial").Validate().Should().BeEmpty();
    }

    [Fact]
    public void Create_UnknownModality_IsInvalid()
    {
        new CreateAppointmentRequest(Guid.NewGuid(), Start, 50, "telefone").Validate().Keys.Should().Equal("modality");
    }

    [Fact]
    public void Update_DoesNotRequirePatient()
    {
        new UpdateAppointmentRequest(Start, 50, "online").Validate().Should().BeEmpty();
    }

    [Fact]
    public void Status_Unknown_IsInvalid()
    {
        new ChangeAppointmentStatusRequest("done", null).Validate().Keys.Should().Equal("status");
    }

    [Fact]
    public void Status_ReasonWithoutCancel_IsInvalid()
    {
        new ChangeAppointmentStatusRequest("confirmed", "motivo").Validate().Keys.Should().Equal("reason");
    }

    [Fact]
    public void Status_ReasonTooLong_IsInvalid()
    {
        new ChangeAppointmentStatusRequest("cancelled", new string('x', 501)).Validate().Keys.Should().Equal("reason");
    }

    [Fact]
    public void Status_CancelWithReason_IsValid()
    {
        new ChangeAppointmentStatusRequest("cancelled", "Paciente pediu").Validate().Should().BeEmpty();
    }

    [Fact]
    public void Range_Valid_HasNoErrors()
    {
        AppointmentRange.Validate(Start, Start.AddDays(7), ["scheduled"]).Should().BeEmpty();
    }

    [Fact]
    public void Range_Missing_ReportsBoth()
    {
        AppointmentRange.Validate(null, null, null).Keys.Should().BeEquivalentTo(["from", "to"]);
    }

    [Fact]
    public void Range_ToNotAfterFrom_IsInvalid()
    {
        AppointmentRange.Validate(Start, Start, null).Keys.Should().Equal("to");
    }

    [Fact]
    public void Range_LongerThan42Days_IsInvalid()
    {
        AppointmentRange.Validate(Start, Start.AddDays(43), null).Keys.Should().Equal("to");
    }

    [Fact]
    public void Range_UnknownStatus_IsInvalid()
    {
        AppointmentRange.Validate(Start, Start.AddDays(1), ["done"]).Keys.Should().Equal("status");
    }

    [Theory]
    [InlineData("scheduled", "confirmed", true)]
    [InlineData("scheduled", "attended", true)]
    [InlineData("scheduled", "no_show", true)]
    [InlineData("scheduled", "cancelled", true)]
    [InlineData("scheduled", "scheduled", false)]
    [InlineData("confirmed", "scheduled", true)]
    [InlineData("confirmed", "attended", true)]
    [InlineData("confirmed", "no_show", true)]
    [InlineData("confirmed", "cancelled", true)]
    [InlineData("attended", "scheduled", true)]
    [InlineData("attended", "cancelled", false)]
    [InlineData("attended", "no_show", false)]
    [InlineData("no_show", "scheduled", true)]
    [InlineData("no_show", "attended", false)]
    [InlineData("cancelled", "scheduled", false)]
    [InlineData("cancelled", "confirmed", false)]
    public void Transitions_FollowTheSpecTable(string from, string to, bool allowed)
    {
        AppointmentTransitions.IsAllowed(from, to).Should().Be(allowed);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run (from `anamnys-aspire/`): `dotnet run --project anamnys-aspire.Tests -- -class "*AppointmentValidationTests*"`
Expected: build FAILS — `The type or namespace name 'Appointments' does not exist in the namespace 'Anamnys.Server'`.

- [ ] **Step 3: Write the implementation**

`anamnys-aspire/anamnys-aspire.Server/Appointments/AppointmentContracts.cs`:

```csharp
namespace Anamnys.Server.Appointments;

// See design/specs/2026-10-05-provider-calendar-design.md §3. Each request validates
// itself; the outcome rules that need the database live in Appointments.

public static class AppointmentStatus
{
    public const string Scheduled = "scheduled";
    public const string Confirmed = "confirmed";
    public const string Attended = "attended";
    public const string Cancelled = "cancelled";
    public const string NoShow = "no_show";

    public static readonly string[] All = [Scheduled, Confirmed, Attended, Cancelled, NoShow];
}

public static class AppointmentTransitions
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [AppointmentStatus.Scheduled] = [AppointmentStatus.Confirmed, AppointmentStatus.Attended, AppointmentStatus.NoShow, AppointmentStatus.Cancelled],
        [AppointmentStatus.Confirmed] = [AppointmentStatus.Scheduled, AppointmentStatus.Attended, AppointmentStatus.NoShow, AppointmentStatus.Cancelled],
        [AppointmentStatus.Attended] = [AppointmentStatus.Scheduled],
        [AppointmentStatus.NoShow] = [AppointmentStatus.Scheduled],
        [AppointmentStatus.Cancelled] = [],
    };

    public static bool IsAllowed(string from, string to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
}

public static class AppointmentRules
{
    public const int MinDuration = 5;
    public const int MaxDuration = 480;
    public const int MaxReason = 500;
    public const int MaxRangeDays = 42;
    public static readonly string[] Modalities = ["online", "presencial"];

    internal static void ValidateSlot(Dictionary<string, string[]> errors, DateTimeOffset? startsAt, int? durationMinutes, string? modality)
    {
        if (startsAt is null)
        {
            errors["startsAt"] = ["Informe a data e o horário."];
        }
        if (durationMinutes is null or < MinDuration or > MaxDuration)
        {
            errors["durationMinutes"] = [$"A duração deve ficar entre {MinDuration} e {MaxDuration} minutos."];
        }
        if (modality is null || !Modalities.Contains(modality))
        {
            errors["modality"] = ["Escolha online ou presencial."];
        }
    }
}

public sealed record CreateAppointmentRequest(Guid? PatientId, DateTimeOffset? StartsAt, int? DurationMinutes, string? Modality)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (PatientId is null || PatientId == Guid.Empty)
        {
            errors["patientId"] = ["Escolha o paciente."];
        }
        AppointmentRules.ValidateSlot(errors, StartsAt, DurationMinutes, Modality);
        return errors;
    }
}

public sealed record UpdateAppointmentRequest(DateTimeOffset? StartsAt, int? DurationMinutes, string? Modality)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        AppointmentRules.ValidateSlot(errors, StartsAt, DurationMinutes, Modality);
        return errors;
    }
}

public sealed record ChangeAppointmentStatusRequest(string? Status, string? Reason)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Status is null || !AppointmentStatus.All.Contains(Status))
        {
            errors["status"] = ["Status inválido."];
        }
        if (!string.IsNullOrWhiteSpace(Reason))
        {
            if (Status != AppointmentStatus.Cancelled)
            {
                errors["reason"] = ["O motivo só se aplica ao cancelamento."];
            }
            else if (Reason.Trim().Length > AppointmentRules.MaxReason)
            {
                errors["reason"] = [$"Use no máximo {AppointmentRules.MaxReason} caracteres."];
            }
        }
        return errors;
    }
}

public static class AppointmentRange
{
    public static Dictionary<string, string[]> Validate(DateTimeOffset? from, DateTimeOffset? to, string[]? statuses)
    {
        var errors = new Dictionary<string, string[]>();
        if (from is null)
        {
            errors["from"] = ["Informe o início do período."];
        }
        if (to is null)
        {
            errors["to"] = ["Informe o fim do período."];
        }
        else if (from is not null && (to <= from || to - from > TimeSpan.FromDays(AppointmentRules.MaxRangeDays)))
        {
            errors["to"] = [$"O período deve ter entre 1 minuto e {AppointmentRules.MaxRangeDays} dias."];
        }
        if (statuses is not null && statuses.Any(s => !AppointmentStatus.All.Contains(s)))
        {
            errors["status"] = ["Status inválido."];
        }
        return errors;
    }
}

public sealed record AppointmentItem(
    Guid Id,
    Guid PatientId,
    string PatientName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Timezone,
    string Modality,
    string Status,
    string? CancellationReason);

public sealed record AppointmentListResponse(IReadOnlyList<AppointmentItem> Items);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project anamnys-aspire.Tests -- -class "*AppointmentValidationTests*"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server/Appointments/AppointmentContracts.cs anamnys-aspire/anamnys-aspire.Tests/Appointments/AppointmentValidationTests.cs
git commit -m "feat: add appointment contracts, validation and status transitions"
```

---

### Task 2: Entity mapping and provider-scoped appointment logic

**Files:**
- Modify: `anamnys-aspire/anamnys-aspire.Server/Data/Entities/Appointment.cs`
- Create: `anamnys-aspire/anamnys-aspire.Server/Appointments/Appointments.cs`
- Test: `anamnys-aspire/anamnys-aspire.Tests/Appointments/AppointmentsTests.cs`

**Interfaces:**
- Consumes: Task 1 records and constants; `PatientSearchSeed` (`CreateAsync`, `CreateDbContext`, `AddProviderAsync`, `AddPatientAsync(firstName, lastName, email?, lastVisit?, archivedAt?, providerId?, ct)`, `AddAppointmentAsync(patientId, startsAt, status = "scheduled", providerId?, ct)` — appointments are 50 minutes); `ProfileText.Clean`.
- Produces: `enum AppointmentOutcome { Ok, NotFound, Overlap, PatientArchived, InvalidTransition, NotStarted }`; `Appointments.PracticeTimezone = "America/Sao_Paulo"`; `Appointments.ListAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset from, DateTimeOffset to, string[] statuses, CancellationToken ct): Task<AppointmentListResponse>`; `Appointments.CreateAsync(db, providerId, CreateAppointmentRequest request, DateTimeOffset now, ct): Task<(AppointmentOutcome Outcome, Guid? Id)>`; `Appointments.UpdateAsync(db, providerId, Guid appointmentId, UpdateAppointmentRequest request, ct): Task<AppointmentOutcome>`; `Appointments.SetStatusAsync(db, providerId, Guid appointmentId, ChangeAppointmentStatusRequest request, DateTimeOffset now, ct): Task<AppointmentOutcome>`. Requests passed in are already validated.

- [ ] **Step 1: Write the failing tests**

`anamnys-aspire/anamnys-aspire.Tests/Appointments/AppointmentsTests.cs`:

```csharp
using Anamnys.Server.Appointments;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Appointments;

// Appointments against the real Postgres of the shared AppHost. Each test seeds its own
// provider, so ownership and the overlap constraint are exercised on every call.
[Collection(SharedAppHostCollection.Name)]
public class AppointmentsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Tomorrow10 = new(2026, 10, 6, 13, 0, 0, TimeSpan.Zero); // 10:00 BRT
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateAppointmentRequest Create(Guid patientId, DateTimeOffset startsAt, int minutes = 50) =>
        new(patientId, startsAt, minutes, "online");

    [Fact]
    public async Task Create_InsertsScheduledAppointmentInPracticeZone()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var (outcome, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        var row = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
        row.EndsAt.Should().Be(Tomorrow10.AddMinutes(50));
        row.Status.Should().Be("scheduled");
        row.Timezone.Should().Be("America/Sao_Paulo");
        row.CreatedBy.Should().Be("provider");
        row.Modality.Should().Be("online");
    }

    [Fact]
    public async Task Create_ForAnotherProvidersPatient_IsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var foreign = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var (outcome, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(foreign, Tomorrow10), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.NotFound);
        id.Should().BeNull();
    }

    [Fact]
    public async Task Create_ForArchivedPatient_IsRejected()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", archivedAt: Now.AddDays(-1), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var (outcome, _) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.PatientArchived);
    }

    [Fact]
    public async Task Create_Overlapping_IsOverlap_ButBackToBackIsAllowed()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, cancellationToken: Ct); // 10:00–10:50

        // Act
        (AppointmentOutcome Outcome, Guid? Id) overlapping, backToBack;
        await using (var db = seed.CreateDbContext())
        {
            overlapping = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10.AddMinutes(30)), Now, Ct);
        }
        await using (var db = seed.CreateDbContext())
        {
            backToBack = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10.AddMinutes(50)), Now, Ct);
        }

        // Assert
        overlapping.Outcome.Should().Be(AppointmentOutcome.Overlap);
        backToBack.Outcome.Should().Be(AppointmentOutcome.Ok);
    }

    [Fact]
    public async Task Cancelling_FreesTheSlot()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        Guid first;
        await using (var db = seed.CreateDbContext())
        {
            first = (await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct)).Id!.Value;
        }

        // Act
        AppointmentOutcome cancel;
        (AppointmentOutcome Outcome, Guid? Id) again;
        await using (var db = seed.CreateDbContext())
        {
            cancel = await Appointments.SetStatusAsync(db, seed.ProviderId, first, new ChangeAppointmentStatusRequest("cancelled", " Paciente pediu "), Now, Ct);
        }
        await using (var db = seed.CreateDbContext())
        {
            again = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);
        }

        // Assert
        cancel.Should().Be(AppointmentOutcome.Ok);
        again.Outcome.Should().Be(AppointmentOutcome.Ok);
        await using var check = seed.CreateDbContext();
        var row = await check.Appointments.AsNoTracking().SingleAsync(a => a.Id == first, Ct);
        row.Status.Should().Be("cancelled");
        row.CancelledAt.Should().Be(Now);
        row.CancelledBy.Should().Be("provider");
        row.CancellationReason.Should().Be("Paciente pediu");
    }

    [Fact]
    public async Task Update_ReschedulesAndChangesModality()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        Guid id;
        await using (var db = seed.CreateDbContext())
        {
            id = (await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct)).Id!.Value;
        }

        // Act
        AppointmentOutcome outcome;
        await using (var db = seed.CreateDbContext())
        {
            outcome = await Appointments.UpdateAsync(db, seed.ProviderId, id, new UpdateAppointmentRequest(Tomorrow10.AddHours(2), 30, "presencial"), Ct);
        }

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        await using var check = seed.CreateDbContext();
        var row = await check.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
        row.StartsAt.Should().Be(Tomorrow10.AddHours(2));
        row.EndsAt.Should().Be(Tomorrow10.AddHours(2).AddMinutes(30));
        row.Modality.Should().Be("presencial");
    }

    [Fact]
    public async Task Update_OntoAnotherAppointment_IsOverlap()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10.AddHours(2), cancellationToken: Ct);
        Guid id;
        await using (var db = seed.CreateDbContext())
        {
            id = (await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct)).Id!.Value;
        }
        await using var db2 = seed.CreateDbContext();

        // Act
        var outcome = await Appointments.UpdateAsync(db2, seed.ProviderId, id, new UpdateAppointmentRequest(Tomorrow10.AddHours(2), 50, "online"), Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Overlap);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("attended")]
    public async Task Update_WhenNotScheduledOrConfirmed_IsInvalidTransition(string status)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(-1), status, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == patient).Select(a => a.Id).SingleAsync(Ct);

        // Act
        var outcome = await Appointments.UpdateAsync(db, seed.ProviderId, id, new UpdateAppointmentRequest(Tomorrow10, 50, "online"), Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.InvalidTransition);
    }

    [Fact]
    public async Task Attended_SetsLastVisit_AndUndoRecomputesIt()
    {
        // Arrange — an earlier attended visit two days ago, and today's appointment at 10:00 BRT.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var earlier = Now.AddDays(-2);
        var patient = await seed.AddPatientAsync("Ana", "Silva", lastVisit: earlier, cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, earlier, "attended", cancellationToken: Ct);
        var todayStart = Now.AddHours(-2);
        await seed.AddAppointmentAsync(patient, todayStart, cancellationToken: Ct);
        Guid id;
        await using (var db = seed.CreateDbContext())
        {
            id = await db.Appointments.Where(a => a.PatientId == patient && a.StartsAt == todayStart).Select(a => a.Id).SingleAsync(Ct);
        }

        // Act + Assert
        await using (var db = seed.CreateDbContext())
        {
            (await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("attended", null), Now, Ct))
                .Should().Be(AppointmentOutcome.Ok);
        }
        (await LastVisitAsync(seed, patient)).Should().Be(todayStart);

        await using (var db = seed.CreateDbContext())
        {
            (await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("scheduled", null), Now, Ct))
                .Should().Be(AppointmentOutcome.Ok);
        }
        (await LastVisitAsync(seed, patient)).Should().Be(earlier);
    }

    [Fact]
    public async Task Attended_OnOlderAppointment_KeepsLaterLastVisit()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", lastVisit: Now.AddHours(-1), cancellationToken: Ct);
        var older = Now.AddDays(-7);
        await seed.AddAppointmentAsync(patient, older, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == patient).Select(a => a.Id).SingleAsync(Ct);

        // Act
        await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("attended", null), Now, Ct);

        // Assert
        (await LastVisitAsync(seed, patient)).Should().Be(Now.AddHours(-1));
    }

    [Theory]
    [InlineData("attended")]
    [InlineData("no_show")]
    public async Task AttendedOrNoShow_OnFutureAppointment_IsNotStarted(string status)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == patient).Select(a => a.Id).SingleAsync(Ct);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest(status, null), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.NotStarted);
    }

    [Fact]
    public async Task Status_FromCancelled_IsInvalidTransition()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, "cancelled", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == patient).Select(a => a.Id).SingleAsync(Ct);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("scheduled", null), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.InvalidTransition);
    }

    [Fact]
    public async Task EveryOperation_OnAnotherProvidersAppointment_IsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var foreignPatient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await seed.AddAppointmentAsync(foreignPatient, Tomorrow10, providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == foreignPatient).Select(a => a.Id).SingleAsync(Ct);

        // Act + Assert
        (await Appointments.UpdateAsync(db, seed.ProviderId, id, new UpdateAppointmentRequest(Tomorrow10, 50, "online"), Ct))
            .Should().Be(AppointmentOutcome.NotFound);
        (await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("confirmed", null), Now, Ct))
            .Should().Be(AppointmentOutcome.NotFound);
        (await Appointments.ListAsync(db, seed.ProviderId, Tomorrow10.AddDays(-1), Tomorrow10.AddDays(1), [], Ct))
            .Items.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ReturnsOnlyTheRangeAndRespectsStatusFilter()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10.AddHours(2), "cancelled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10.AddDays(10), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var from = Tomorrow10.AddHours(-1);
        var to = Tomorrow10.AddDays(1);

        // Act
        var all = await Appointments.ListAsync(db, seed.ProviderId, from, to, [], Ct);
        var scheduledOnly = await Appointments.ListAsync(db, seed.ProviderId, from, to, ["scheduled"], Ct);

        // Assert
        all.Items.Select(i => i.Status).Should().Equal("scheduled", "cancelled");
        all.Items[0].PatientName.Should().Be("Ana Silva");
        all.Items[0].PatientId.Should().Be(patient);
        scheduledOnly.Items.Should().ContainSingle().Which.StartsAt.Should().Be(Tomorrow10);
    }

    private static async Task<DateTimeOffset?> LastVisitAsync(PatientSearchSeed seed, Guid patientId)
    {
        await using var db = seed.CreateDbContext();
        return await db.Patients.AsNoTracking().Where(p => p.Id == patientId).Select(p => p.LastVisit).SingleAsync(Ct);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project anamnys-aspire.Tests -- -class "*AppointmentsTests*"`
Expected: build FAILS — `The name 'Appointments' does not exist in the current context` (and `Appointment` lacks `CreatedBy`).

- [ ] **Step 3: Map the writable columns**

Replace `anamnys-aspire/anamnys-aspire.Server/Data/Entities/Appointment.cs` with:

```csharp
namespace Anamnys.Server.Data.Entities;

// Column subset of "Appointments" used by the provider calendar
// (design/specs/2026-10-05-provider-calendar-design.md) and read by the patient list,
// record and portal. Unmapped columns (series, session, hold, offering, connection, price,
// external event) are nullable or have defaults, so inserts through this entity work.
// "Slot" is generated by the database and deliberately left unmapped.
public class Appointment
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Timezone { get; set; } = "";
    public string Modality { get; set; } = "";
    public string Status { get; set; } = "";
    public string CreatedBy { get; set; } = "provider";
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Step 4: Write the logic**

`anamnys-aspire/anamnys-aspire.Server/Appointments/Appointments.cs`:

```csharp
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Profile;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Appointments;

public enum AppointmentOutcome { Ok, NotFound, Overlap, PatientArchived, InvalidTransition, NotStarted }

// Provider-scoped appointment operations. See
// design/specs/2026-10-05-provider-calendar-design.md §3. Every call resolves the
// appointment (or patient) with ProviderId == providerId, so a foreign id is
// indistinguishable from a missing one. Overlap is never pre-checked: the database's
// Appointments_no_overlap constraint decides, and its violation becomes Overlap.
public static class Appointments
{
    public const string PracticeTimezone = "America/Sao_Paulo";
    private const string OverlapConstraint = "Appointments_no_overlap";

    public static async Task<AppointmentListResponse> ListAsync(
        AnamnysDbContext db, Guid providerId, DateTimeOffset from, DateTimeOffset to, string[] statuses, CancellationToken cancellationToken)
    {
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        var query =
            from a in db.Appointments.AsNoTracking()
            join p in db.Patients.AsNoTracking() on a.PatientId equals p.Id
            where a.ProviderId == providerId && p.ProviderId == providerId
                && a.StartsAt < toUtc && a.EndsAt > fromUtc
            select new { a, p };
        if (statuses.Length > 0)
        {
            query = query.Where(x => statuses.Contains(x.a.Status));
        }

        var items = await query
            .OrderBy(x => x.a.StartsAt)
            .Select(x => new AppointmentItem(
                x.a.Id, x.a.PatientId, x.p.FirstName + " " + x.p.LastName,
                x.a.StartsAt, x.a.EndsAt, x.a.Timezone, x.a.Modality, x.a.Status, x.a.CancellationReason))
            .ToListAsync(cancellationToken);
        return new AppointmentListResponse(items);
    }

    public static async Task<(AppointmentOutcome Outcome, Guid? Id)> CreateAsync(
        AnamnysDbContext db, Guid providerId, CreateAppointmentRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == request.PatientId && p.ProviderId == providerId)
            .Select(p => new { p.ArchivedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (patient is null)
        {
            return (AppointmentOutcome.NotFound, null);
        }
        if (patient.ArchivedAt is not null)
        {
            return (AppointmentOutcome.PatientArchived, null);
        }

        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            PatientId = request.PatientId!.Value,
            StartsAt = startsAt,
            EndsAt = startsAt.AddMinutes(request.DurationMinutes!.Value),
            Timezone = PracticeTimezone,
            Modality = request.Modality!,
            Status = AppointmentStatus.Scheduled,
            CreatedBy = "provider",
            CreatedAt = now.ToUniversalTime(),
        };
        db.Appointments.Add(appointment);

        var outcome = await SaveAsync(db, cancellationToken);
        return (outcome, outcome == AppointmentOutcome.Ok ? appointment.Id : null);
    }

    public static async Task<AppointmentOutcome> UpdateAsync(
        AnamnysDbContext db, Guid providerId, Guid appointmentId, UpdateAppointmentRequest request, CancellationToken cancellationToken)
    {
        var appointment = await db.Appointments
            .SingleOrDefaultAsync(a => a.Id == appointmentId && a.ProviderId == providerId, cancellationToken);
        if (appointment is null)
        {
            return AppointmentOutcome.NotFound;
        }
        if (appointment.Status is not (AppointmentStatus.Scheduled or AppointmentStatus.Confirmed))
        {
            return AppointmentOutcome.InvalidTransition;
        }

        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        appointment.StartsAt = startsAt;
        appointment.EndsAt = startsAt.AddMinutes(request.DurationMinutes!.Value);
        appointment.Modality = request.Modality!;
        return await SaveAsync(db, cancellationToken);
    }

    public static async Task<AppointmentOutcome> SetStatusAsync(
        AnamnysDbContext db, Guid providerId, Guid appointmentId, ChangeAppointmentStatusRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var appointment = await db.Appointments
            .SingleOrDefaultAsync(a => a.Id == appointmentId && a.ProviderId == providerId, cancellationToken);
        if (appointment is null)
        {
            return AppointmentOutcome.NotFound;
        }

        var from = appointment.Status;
        var to = request.Status!;
        if (!AppointmentTransitions.IsAllowed(from, to))
        {
            return AppointmentOutcome.InvalidTransition;
        }
        var nowUtc = now.ToUniversalTime();
        if ((to is AppointmentStatus.Attended or AppointmentStatus.NoShow) && appointment.StartsAt > nowUtc)
        {
            return AppointmentOutcome.NotStarted;
        }

        appointment.Status = to;
        if (to == AppointmentStatus.Cancelled)
        {
            appointment.CancelledAt = nowUtc;
            appointment.CancelledBy = "provider";
            appointment.CancellationReason = ProfileText.Clean(request.Reason);
        }

        if (to == AppointmentStatus.Attended || from == AppointmentStatus.Attended)
        {
            var patient = await db.Patients
                .SingleAsync(p => p.Id == appointment.PatientId && p.ProviderId == providerId, cancellationToken);
            if (to == AppointmentStatus.Attended)
            {
                if (patient.LastVisit is null || patient.LastVisit < appointment.StartsAt)
                {
                    patient.LastVisit = appointment.StartsAt;
                }
            }
            else
            {
                // Undoing "attended": the latest remaining attended visit, if any.
                var latest = await db.Appointments
                    .Where(a => a.PatientId == appointment.PatientId && a.ProviderId == providerId
                        && a.Id != appointment.Id && a.Status == AppointmentStatus.Attended)
                    .MaxAsync(a => (DateTimeOffset?)a.StartsAt, cancellationToken);
                if (latest is not null)
                {
                    patient.LastVisit = latest;
                }
            }
        }

        // One SaveChanges: the status and LastVisit change in one transaction.
        return await SaveAsync(db, cancellationToken);
    }

    // The constraint is "UNIQUE ... WITHOUT OVERLAPS" (PostgreSQL 18); match it by name rather
    // than by SQLSTATE so the exact code the server reports for it does not matter.
    private static async Task<AppointmentOutcome> SaveAsync(AnamnysDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return AppointmentOutcome.Ok;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { ConstraintName: OverlapConstraint })
        {
            db.ChangeTracker.Clear();
            return AppointmentOutcome.Overlap;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet run --project anamnys-aspire.Tests -- -class "*AppointmentsTests*"`
Expected: all PASS. Then run the existing readers of `Appointments` to confirm the entity change broke nothing:
`dotnet run --project anamnys-aspire.Tests -- -class "*PatientRecordsTests*"` and `-- -class "*PatientPortalQueriesTests*"` and `-- -class "*PatientSearchQueryTests*"`. Expected: all PASS.

If the overlap tests fail with an unhandled `DbUpdateException`, print `((PostgresException)e.InnerException).ConstraintName` in a scratch test — the constraint name must be `Appointments_no_overlap` per `anamnys-db-script.sql:48`.

- [ ] **Step 6: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server/Data/Entities/Appointment.cs anamnys-aspire/anamnys-aspire.Server/Appointments/Appointments.cs anamnys-aspire/anamnys-aspire.Tests/Appointments/AppointmentsTests.cs
git commit -m "feat: add provider-scoped appointment create, reschedule and status changes"
```

---

### Task 3: HTTP endpoints

**Files:**
- Create: `anamnys-aspire/anamnys-aspire.Server/Appointments/AppointmentEndpoints.cs`
- Modify: `anamnys-aspire/anamnys-aspire.Server/Program.cs` (after `phi.MapPatientPortalEndpoints();`, line 98)
- Test: `anamnys-aspire/anamnys-aspire.Tests/Appointments/AppointmentEndpointTests.cs`

**Interfaces:**
- Consumes: Task 1 and Task 2 APIs; `PatientRecordEndpoints.ProviderIdAsync(HttpContext): Task<Guid?>` (internal, same assembly); `BrowserSession` from `Anamnys.Tests.Auth`.
- Produces: `GET /api/phi/providers/me/appointments?from&to&status` → `200 AppointmentListResponse`; `POST /api/phi/providers/me/appointments` → `201 { id }`; `PUT /api/phi/providers/me/appointments/{id}` → `204`; `POST /api/phi/providers/me/appointments/{id}/status` → `204`. Errors: `400` ValidationProblem, `401`, `404`, `409 { message }`, `422 { message }`.

- [ ] **Step 1: Write the failing tests**

The rules are covered in `AppointmentsTests`; this covers the HTTP boundary only and writes nothing, so the dev database stays clean (a created appointment would make its patient undeletable).

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Appointments;

[Collection(SharedAppHostCollection.Name)]
public class AppointmentEndpointTests
{
    private const string BasePath = "/api/phi/providers/me/appointments";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private const string AnyId = "00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("GET", "?from=2026-10-05T03:00:00Z&to=2026-10-12T03:00:00Z")]
    [InlineData("POST", "")]
    [InlineData("PUT", "/" + AnyId)]
    [InlineData("POST", "/" + AnyId + "/status")]
    public async Task Routes_WithoutSession_Return401(string method, string suffix)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(new HttpMethod(method), BasePath + suffix);
        if (method != "GET")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_ForAWeek_Returns200WithItems()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await SendAsync(session, HttpMethod.Get,
            $"{BasePath}?from=2026-10-05T03:00:00Z&to=2026-10-12T03:00:00Z&status=scheduled&status=confirmed");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task List_WithoutRange_Returns400()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await SendAsync(session, HttpMethod.Get, BasePath);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await ReadJsonAsync(response)).GetProperty("errors");
        errors.TryGetProperty("from", out _).Should().BeTrue();
        errors.TryGetProperty("to", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Create_WithInvalidBody_Returns400WithFieldErrors()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await SendAsync(session, HttpMethod.Post, BasePath, new { durationMinutes = 1, modality = "x" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await ReadJsonAsync(response)).GetProperty("errors");
        foreach (var key in new[] { "patientId", "startsAt", "durationMinutes", "modality" })
        {
            errors.TryGetProperty(key, out _).Should().BeTrue(key);
        }
    }

    [Fact]
    public async Task UnknownIds_Return404()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var slot = new { startsAt = "2026-10-06T13:00:00Z", durationMinutes = 50, modality = "online" };

        // Act
        using var create = await SendAsync(session, HttpMethod.Post, BasePath,
            new { patientId = Guid.NewGuid(), slot.startsAt, slot.durationMinutes, slot.modality });
        using var update = await SendAsync(session, HttpMethod.Put, $"{BasePath}/{Guid.NewGuid()}", slot);
        using var status = await SendAsync(session, HttpMethod.Post, $"{BasePath}/{Guid.NewGuid()}/status", new { status = "confirmed" });

        // Assert
        new[] { create, update, status }.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
    }

    private static async Task<HttpResponseMessage> SendAsync(BrowserSession session, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await session.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project anamnys-aspire.Tests -- -class "*AppointmentEndpointTests*"`
Expected: FAIL — routes return `404` (not mapped) instead of `401`/`400`/`200`.

- [ ] **Step 3: Write the endpoints**

`anamnys-aspire/anamnys-aspire.Server/Appointments/AppointmentEndpoints.cs`:

```csharp
using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Microsoft.AspNetCore.Mvc;

namespace Anamnys.Server.Appointments;

// See design/specs/2026-10-05-provider-calendar-design.md §3. Handlers are thin: resolve the
// provider from the session, validate, delegate to Appointments, map the outcome.
public static class AppointmentEndpoints
{
    public static void MapAppointmentEndpoints(this RouteGroupBuilder phi)
    {
        var appointments = phi.MapGroup("providers/me/appointments");

        appointments.MapGet("", async (
            DateTimeOffset? from, DateTimeOffset? to, [FromQuery(Name = "status")] string[]? status,
            HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = AppointmentRange.Validate(from, to, status);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return Results.Ok(await Appointments.ListAsync(db, providerId, from!.Value, to!.Value, status ?? [], ct));
        });

        appointments.MapPost("", async (CreateAppointmentRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var (outcome, id) = await Appointments.CreateAsync(db, providerId, request, DateTimeOffset.UtcNow, ct);
            return outcome == AppointmentOutcome.Ok
                ? Results.Created($"/api/phi/providers/me/appointments/{id}", new { id })
                : ToResult(outcome);
        });

        appointments.MapPut("{appointmentId:guid}", async (Guid appointmentId, UpdateAppointmentRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return ToResult(await Appointments.UpdateAsync(db, providerId, appointmentId, request, ct));
        });

        appointments.MapPost("{appointmentId:guid}/status", async (Guid appointmentId, ChangeAppointmentStatusRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return ToResult(await Appointments.SetStatusAsync(db, providerId, appointmentId, request, DateTimeOffset.UtcNow, ct));
        });
    }

    private static IResult ToResult(AppointmentOutcome outcome) => outcome switch
    {
        AppointmentOutcome.Ok => Results.NoContent(),
        AppointmentOutcome.Overlap => Results.Conflict(new { message = "Horário já ocupado." }),
        AppointmentOutcome.InvalidTransition => Results.Conflict(new { message = "Esta alteração não é permitida no status atual." }),
        AppointmentOutcome.NotStarted => Results.Conflict(new { message = "A consulta ainda não começou." }),
        AppointmentOutcome.PatientArchived => Results.UnprocessableEntity(new { message = "Paciente arquivado." }),
        _ => Results.NotFound(),
    };
}
```

In `anamnys-aspire/anamnys-aspire.Server/Program.cs`, add `using Anamnys.Server.Appointments;` with the other usings and, after `phi.MapPatientPortalEndpoints();`:

```csharp
phi.MapAppointmentEndpoints();
```

- [ ] **Step 4: Run tests to verify they pass**

Run `aspire stop` first if the app is running, then:
`dotnet run --project anamnys-aspire.Tests -- -class "*AppointmentEndpointTests*"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server/Appointments/AppointmentEndpoints.cs anamnys-aspire/anamnys-aspire.Server/Program.cs anamnys-aspire/anamnys-aspire.Tests/Appointments/AppointmentEndpointTests.cs
git commit -m "feat: expose provider appointment endpoints"
```

---

### Task 4: Shared types, API client and strings

**Files:**
- Modify: `anamnys-aspire/packages/shared/src/lib/types.ts` (append at end)
- Create: `anamnys-aspire/packages/shared/src/api/appointments.ts`
- Modify: `anamnys-aspire/packages/shared/src/lib/i18n/locales/pt.json` (new top-level `calendar` block)

**Interfaces:**
- Consumes: Task 3 HTTP contract.
- Produces: TS types `AppointmentStatus`, `AppointmentModality`, `Appointment`, `CreateAppointmentRequest`, `UpdateAppointmentRequest`; `appointmentsApi.list(from: Date, to: Date, statuses: AppointmentStatus[]): Promise<Appointment[]>`, `.create(req): Promise<string>`, `.update(id, req): Promise<void>`, `.setStatus(id, status, reason?): Promise<void>`; i18n keys under `calendar.*` listed below.

- [ ] **Step 1: Types** — append to `packages/shared/src/lib/types.ts`:

```ts
// Provider calendar (design/specs/2026-10-05-provider-calendar-design.md).
export type AppointmentStatus = "scheduled" | "confirmed" | "attended" | "cancelled" | "no_show";
export type AppointmentModality = "online" | "presencial";

export interface Appointment {
  id: string;
  patientId: string;
  patientName: string;
  startsAt: string; // ISO instant
  endsAt: string;
  timezone: string;
  modality: AppointmentModality;
  status: AppointmentStatus;
  cancellationReason: string | null;
}

export interface UpdateAppointmentRequest {
  startsAt: string; // ISO instant
  durationMinutes: number;
  modality: AppointmentModality;
}

export interface CreateAppointmentRequest extends UpdateAppointmentRequest {
  patientId: string;
}
```

- [ ] **Step 2: API client** — `packages/shared/src/api/appointments.ts`:

```ts
import api from "@anamnys/shared/api/client";
import type {
  Appointment,
  AppointmentStatus,
  CreateAppointmentRequest,
  UpdateAppointmentRequest,
} from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me/appointments";

export const appointmentsApi = {
  // Repeated `status` keys, not axios's default `status[]=`, which the server does not bind.
  list: async (from: Date, to: Date, statuses: AppointmentStatus[]): Promise<Appointment[]> => {
    const params = new URLSearchParams({ from: from.toISOString(), to: to.toISOString() });
    statuses.forEach((s) => params.append("status", s));
    const { data } = await api.get<{ items: Appointment[] }>(BASE, { params });
    return data.items;
  },
  create: async (request: CreateAppointmentRequest): Promise<string> => {
    const { data } = await api.post<{ id: string }>(BASE, request);
    return data.id;
  },
  update: async (id: string, request: UpdateAppointmentRequest): Promise<void> => {
    await api.put(`${BASE}/${id}`, request);
  },
  setStatus: async (id: string, status: AppointmentStatus, reason?: string): Promise<void> => {
    await api.post(`${BASE}/${id}/status`, { status, reason: reason || undefined });
  },
};
```

- [ ] **Step 3: Strings** — add a top-level `"calendar"` object to `pt.json` (keep valid JSON: comma after the preceding block):

```json
"calendar": {
  "title": "Agenda",
  "today": "Hoje",
  "previous": "Período anterior",
  "next": "Próximo período",
  "views": { "day": "Dia", "week": "Semana" },
  "newAppointment": "Nova consulta",
  "statusFilter": { "label": "Status", "all": "Todos os status" },
  "status": {
    "scheduled": "Agendada",
    "confirmed": "Confirmada",
    "attended": "Realizada",
    "cancelled": "Cancelada",
    "no_show": "Falta"
  },
  "modality": { "online": "Online", "presencial": "Presencial" },
  "loadFailed": "Falha ao carregar a agenda.",
  "form": {
    "createTitle": "Nova consulta",
    "editTitle": "Editar consulta",
    "patient": "Paciente",
    "patientSearch": "Buscar paciente por nome…",
    "patientNone": "Nenhum paciente encontrado.",
    "patientRequired": "Escolha o paciente.",
    "patientChange": "Trocar",
    "date": "Data",
    "time": "Início",
    "duration": "Duração (min)",
    "modality": "Modalidade",
    "save": "Salvar consulta",
    "created": "Consulta agendada.",
    "updated": "Consulta atualizada.",
    "saveFailed": "Não foi possível salvar a consulta."
  },
  "details": {
    "openRecord": "Abrir prontuário",
    "reason": "Motivo do cancelamento",
    "confirm": "Confirmar",
    "attended": "Realizada",
    "noShow": "Falta",
    "cancel": "Cancelar consulta",
    "cancelConfirm": "Confirmar cancelamento",
    "reasonPlaceholder": "Motivo (opcional)",
    "undo": "Desfazer",
    "edit": "Editar",
    "statusChanged": "Status atualizado.",
    "statusFailed": "Não foi possível alterar o status."
  }
}
```

- [ ] **Step 4: Verify** — from `anamnys-aspire/apps/provider/`: `npm run build` → succeeds; `node -e "JSON.parse(require('fs').readFileSync('../../packages/shared/src/lib/i18n/locales/pt.json','utf8'))"` → no error.

- [ ] **Step 5: Commit**

```bash
git add anamnys-aspire/packages/shared/src/lib/types.ts anamnys-aspire/packages/shared/src/api/appointments.ts anamnys-aspire/packages/shared/src/lib/i18n/locales/pt.json
git commit -m "feat: add appointment types, API client and calendar strings"
```

---

### Task 5: Read-only calendar page (header, grid, cards)

**Files:**
- Create: `apps/provider/src/components/calendar/calendarTime.ts`, `components/calendar/status.ts`, `hooks/useIsMobile.ts`, `hooks/useAppointments.ts`, `components/calendar/CalendarHeader.tsx`, `components/calendar/CalendarGrid.tsx`, `components/calendar/AppointmentCard.tsx`, `routes/_app/calendar.tsx`
- Modify: `apps/provider/src/routeTree.gen.ts` (regenerated), `packages/shared/src/ui/Sidenav.tsx:33`

(All `apps/provider/...` paths are under `anamnys-aspire/`.)

**Interfaces:**
- Consumes: Task 4 types and `appointmentsApi`.
- Produces (used by Task 6):
  - `calendarTime.ts`: `PRACTICE_ZONE`, `todayIn(zone): string`, `addDays(date: string, n: number): string`, `weekStart(date: string): string`, `visibleDays(view: CalendarView, date: string): string[]`, `zonedToInstant(date: string, minutes: number, zone): Date`, `zonedParts(instant: Date, zone): { date: string; minutes: number }`, `offsetLabel(zone, at: Date): string`, `formatTime(instant: Date, zone): string`, `type CalendarView = "week" | "day"`.
  - `status.ts`: `STATUS_STYLE: Record<AppointmentStatus, string>`, `NEXT_STATUSES: Record<AppointmentStatus, AppointmentStatus[]>`, `isStatus(v: unknown): v is AppointmentStatus`.
  - `useAppointments.ts`: `useAppointmentsQuery(days: string[], status?: AppointmentStatus)`, `useAppointmentMutations()` returning `{ create, update, setStatus }` TanStack mutations, all invalidating `["appointments"]`.
  - `CalendarGrid` props: `{ days: string[]; appointments: Appointment[]; onSlotClick(date: string, minutes: number): void; onAppointmentClick(a: Appointment): void }`.
  - Route search: `{ view?: "week" | "day"; date?: string; status?: AppointmentStatus }`.

- [ ] **Step 1: Time helpers** — `components/calendar/calendarTime.ts`:

```ts
// Calendar dates are "YYYY-MM-DD" strings on the practice's wall clock, never Date objects:
// the browser's own time zone must not move an appointment to another day or hour.
export const PRACTICE_ZONE = "America/Sao_Paulo";
export type CalendarView = "week" | "day";

function partsIn(instant: Date, zone: string) {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit",
    hour: "2-digit", minute: "2-digit", hourCycle: "h23",
  }).formatToParts(instant);
  const get = (type: string) => parts.find((p) => p.type === type)!.value;
  return { date: `${get("year")}-${get("month")}-${get("day")}`, hour: Number(get("hour")), minute: Number(get("minute")) };
}

export function zonedParts(instant: Date, zone: string): { date: string; minutes: number } {
  const p = partsIn(instant, zone);
  return { date: p.date, minutes: p.hour * 60 + p.minute };
}

export function todayIn(zone: string): string {
  return partsIn(new Date(), zone).date;
}

// Offset of `zone` from UTC at `instant`, in minutes (e.g. -180 for São Paulo).
function offsetMinutes(instant: Date, zone: string): number {
  const p = partsIn(instant, zone);
  const [y, m, d] = p.date.split("-").map(Number);
  const asUtc = Date.UTC(y, m - 1, d, p.hour, p.minute);
  return Math.round((asUtc - Math.floor(instant.getTime() / 60000) * 60000) / 60000);
}

export function zonedToInstant(date: string, minutes: number, zone: string): Date {
  const [y, m, d] = date.split("-").map(Number);
  const guess = Date.UTC(y, m - 1, d, 0, minutes);
  return new Date(guess - offsetMinutes(new Date(guess), zone) * 60000);
}

export function addDays(date: string, n: number): string {
  const [y, m, d] = date.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, d + n)).toISOString().slice(0, 10);
}

// Weeks start on Monday, as in the mockup.
export function weekStart(date: string): string {
  const [y, m, d] = date.split("-").map(Number);
  const weekday = new Date(Date.UTC(y, m - 1, d)).getUTCDay(); // 0 = Sunday
  return addDays(date, weekday === 0 ? -6 : 1 - weekday);
}

export function visibleDays(view: CalendarView, date: string): string[] {
  if (view === "day") return [date];
  const start = weekStart(date);
  return Array.from({ length: 7 }, (_, i) => addDays(start, i));
}

export function offsetLabel(zone: string, at: Date): string {
  const hours = offsetMinutes(at, zone) / 60;
  return `GMT${hours >= 0 ? "+" : ""}${hours}`;
}

export function formatTime(instant: Date, zone: string): string {
  return new Intl.DateTimeFormat("pt-BR", { hour: "2-digit", minute: "2-digit", timeZone: zone }).format(instant);
}

export function isWeekend(date: string): boolean {
  const [y, m, d] = date.split("-").map(Number);
  const weekday = new Date(Date.UTC(y, m - 1, d)).getUTCDay();
  return weekday === 0 || weekday === 6;
}
```

Sanity check in the browser console after Step 9 (no test runner exists for the frontend): `zonedToInstant("2026-10-06", 600, "America/Sao_Paulo").toISOString()` must be `"2026-10-06T13:00:00.000Z"`, and `weekStart("2026-10-05")` must be `"2026-10-05"` (a Monday); `weekStart("2026-10-11")` → `"2026-10-05"`.

- [ ] **Step 2: Status styles** — `components/calendar/status.ts`:

```ts
import type { AppointmentStatus } from "@anamnys/shared/lib/types";

// Card and chip colors per status (spec §4).
export const STATUS_STYLE: Record<AppointmentStatus, string> = {
  scheduled: "bg-primaryContainer/15 border-l-primary text-onSurface",
  confirmed: "bg-secondaryContainer border-l-secondary text-onSecondaryContainer",
  attended: "bg-surfaceContainer border-l-outline text-onSurfaceVariant",
  no_show: "bg-errorContainer border-l-error text-onErrorContainer",
  cancelled: "bg-surfaceContainerLow border-l-outlineVariant text-onSurfaceVariant line-through opacity-70",
};

// Mirrors AppointmentTransitions on the server; the server stays the authority.
export const NEXT_STATUSES: Record<AppointmentStatus, AppointmentStatus[]> = {
  scheduled: ["confirmed", "attended", "no_show", "cancelled"],
  confirmed: ["scheduled", "attended", "no_show", "cancelled"],
  attended: ["scheduled"],
  no_show: ["scheduled"],
  cancelled: [],
};

const ALL: AppointmentStatus[] = ["scheduled", "confirmed", "attended", "cancelled", "no_show"];
export const ALL_STATUSES = ALL;

export function isStatus(value: unknown): value is AppointmentStatus {
  return typeof value === "string" && (ALL as string[]).includes(value);
}
```

- [ ] **Step 3: Hooks** — `hooks/useIsMobile.ts`:

```ts
import { useSyncExternalStore } from "react";

const QUERY = "(max-width: 767px)";

export function useIsMobile(): boolean {
  return useSyncExternalStore(
    (onChange) => {
      const media = window.matchMedia(QUERY);
      media.addEventListener("change", onChange);
      return () => media.removeEventListener("change", onChange);
    },
    () => window.matchMedia(QUERY).matches,
  );
}
```

`hooks/useAppointments.ts`:

```ts
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { appointmentsApi } from "@anamnys/shared/api/appointments";
import type {
  AppointmentStatus,
  CreateAppointmentRequest,
  UpdateAppointmentRequest,
} from "@anamnys/shared/lib/types";
import { PRACTICE_ZONE, addDays, zonedToInstant } from "@/components/calendar/calendarTime";

const KEY = "appointments";

export function useAppointmentsQuery(days: string[], status?: AppointmentStatus) {
  const from = zonedToInstant(days[0], 0, PRACTICE_ZONE);
  const to = zonedToInstant(addDays(days[days.length - 1], 1), 0, PRACTICE_ZONE);
  return useQuery({
    queryKey: [KEY, from.toISOString(), to.toISOString(), status ?? "all"],
    queryFn: () => appointmentsApi.list(from, to, status ? [status] : []),
    placeholderData: keepPreviousData,
  });
}

export function useAppointmentMutations() {
  const queryClient = useQueryClient();
  const onSuccess = () => queryClient.invalidateQueries({ queryKey: [KEY] });
  return {
    create: useMutation({ mutationFn: (r: CreateAppointmentRequest) => appointmentsApi.create(r), onSuccess }),
    update: useMutation({
      mutationFn: ({ id, request }: { id: string; request: UpdateAppointmentRequest }) => appointmentsApi.update(id, request),
      onSuccess,
    }),
    setStatus: useMutation({
      mutationFn: ({ id, status, reason }: { id: string; status: AppointmentStatus; reason?: string }) =>
        appointmentsApi.setStatus(id, status, reason),
      onSuccess,
    }),
  };
}
```

`useAppointmentMutations` is used from Task 6; it lives here so all appointment data access is in one file.

- [ ] **Step 4: Appointment card** — `components/calendar/AppointmentCard.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import { User, Video } from "lucide-react";
import type { Appointment } from "@anamnys/shared/lib/types";
import { formatTime } from "./calendarTime";
import { STATUS_STYLE } from "./status";

interface Props {
  appointment: Appointment;
  top: number;
  height: number;
  onClick: () => void;
}

export default function AppointmentCard({ appointment: a, top, height, onClick }: Props) {
  const { t } = useTranslation();
  const ModalityIcon = a.modality === "online" ? Video : User;
  const start = formatTime(new Date(a.startsAt), a.timezone);
  const end = formatTime(new Date(a.endsAt), a.timezone);
  return (
    <button
      type="button"
      onClick={(e) => {
        e.stopPropagation();
        onClick();
      }}
      style={{ top, height }}
      className={`absolute inset-x-1 overflow-hidden rounded-radii-md border-l-4 px-2 py-1 text-left text-label-md shadow-sm hover:brightness-95 ${STATUS_STYLE[a.status]}`}
    >
      <div className="flex items-center justify-between gap-1">
        <span className="font-semibold">{start}–{end}</span>
        <ModalityIcon size={14} aria-label={t(`calendar.modality.${a.modality}`)} />
      </div>
      <div className="truncate text-body-md">{a.patientName}</div>
      <div className="truncate uppercase tracking-wide">{t(`calendar.status.${a.status}`)}</div>
    </button>
  );
}
```

- [ ] **Step 5: Grid** — `components/calendar/CalendarGrid.tsx`:

```tsx
import { useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import type { Appointment } from "@anamnys/shared/lib/types";
import AppointmentCard from "./AppointmentCard";
import { PRACTICE_ZONE, isWeekend, offsetLabel, todayIn, zonedParts } from "./calendarTime";

const HOUR_PX = 56;
const DEFAULT_START_HOUR = 7;
const DEFAULT_END_HOUR = 21;
const SNAP_MINUTES = 15;

interface Props {
  days: string[];
  appointments: Appointment[];
  onSlotClick: (date: string, minutes: number) => void;
  onAppointmentClick: (appointment: Appointment) => void;
}

export default function CalendarGrid({ days, appointments, onSlotClick, onAppointmentClick }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 60_000);
    return () => clearInterval(timer);
  }, []);
  const today = todayIn(PRACTICE_ZONE);

  // Place each appointment on its start day; extend the hour range to fit any outliers.
  const placed = useMemo(
    () =>
      appointments.map((a) => {
        const start = zonedParts(new Date(a.startsAt), PRACTICE_ZONE);
        const minutes = (new Date(a.endsAt).getTime() - new Date(a.startsAt).getTime()) / 60000;
        return { a, date: start.date, startMin: start.minutes, endMin: start.minutes + minutes };
      }),
    [appointments],
  );
  const startHour = Math.min(DEFAULT_START_HOUR, ...placed.map((p) => Math.floor(p.startMin / 60)));
  const endHour = Math.min(24, Math.max(DEFAULT_END_HOUR, ...placed.map((p) => Math.ceil(p.endMin / 60))));
  const hours = Array.from({ length: endHour - startHour }, (_, i) => startHour + i);
  const toPx = (minutes: number) => ((minutes - startHour * 60) / 60) * HOUR_PX;

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: (DEFAULT_START_HOUR - startHour) * HOUR_PX });
  }, [startHour]);

  const nowParts = zonedParts(now, PRACTICE_ZONE);
  const weekday = new Intl.DateTimeFormat("pt-BR", { weekday: "short", timeZone: "UTC" });

  const handleColumnClick = (date: string, e: MouseEvent<HTMLDivElement>) => {
    const y = e.clientY - e.currentTarget.getBoundingClientRect().top;
    const minutes = startHour * 60 + Math.floor(((y / HOUR_PX) * 60) / SNAP_MINUTES) * SNAP_MINUTES;
    onSlotClick(date, minutes);
  };

  return (
    <div className="rounded-radii-xl border border-outlineVariant bg-surfaceContainerLowest overflow-hidden">
      <div className="grid border-b border-outlineVariant" style={{ gridTemplateColumns: `4rem repeat(${days.length}, minmax(0, 1fr))` }}>
        <div className="flex items-end justify-center pb-2 text-label-md text-onSurfaceVariant">{offsetLabel(PRACTICE_ZONE, now)}</div>
        {days.map((date) => (
          <div
            key={date}
            className={`py-3 text-center ${date === today ? "bg-primaryFixed/40 text-primary" : isWeekend(date) ? "text-onSurfaceVariant/60" : "text-onSurface"}`}
          >
            <div className="text-label-md uppercase">{weekday.format(new Date(`${date}T12:00:00Z`))}</div>
            <div className="text-headline-md">{Number(date.slice(8))}</div>
          </div>
        ))}
      </div>
      <div ref={scrollRef} className="max-h-[70vh] overflow-y-auto">
        <div className="relative grid" style={{ gridTemplateColumns: `4rem repeat(${days.length}, minmax(0, 1fr))` }}>
          <div>
            {hours.map((h) => (
              <div key={h} style={{ height: HOUR_PX }} className="pr-2 text-right text-label-md text-onSurfaceVariant">
                {String(h).padStart(2, "0")}:00
              </div>
            ))}
          </div>
          {days.map((date) => (
            <div
              key={date}
              onClick={(e) => handleColumnClick(date, e)}
              style={{ height: hours.length * HOUR_PX }}
              className={`relative cursor-pointer border-l border-outlineVariant ${date === today ? "bg-primaryFixed/15" : isWeekend(date) ? "bg-surfaceContainerLow" : ""}`}
            >
              {hours.map((h) => (
                <div key={h} style={{ top: (h - startHour) * HOUR_PX }} className="absolute inset-x-0 border-t border-outlineVariant/60" />
              ))}
              {placed
                .filter((p) => p.date === date)
                .map((p) => (
                  <AppointmentCard
                    key={p.a.id}
                    appointment={p.a}
                    top={toPx(p.startMin)}
                    height={Math.max(toPx(Math.min(p.endMin, endHour * 60)) - toPx(p.startMin), 24)}
                    onClick={() => onAppointmentClick(p.a)}
                  />
                ))}
              {date === today && nowParts.minutes >= startHour * 60 && nowParts.minutes <= endHour * 60 && (
                <div style={{ top: toPx(nowParts.minutes) }} className="pointer-events-none absolute inset-x-0 z-10 flex items-center">
                  <span className="-ml-1 size-2 rounded-full bg-primary" />
                  <span className="h-px flex-1 bg-primary" />
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
```

- [ ] **Step 6: Header** — `components/calendar/CalendarHeader.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import { ChevronLeft, ChevronRight, Plus } from "lucide-react";
import type { AppointmentStatus } from "@anamnys/shared/lib/types";
import type { CalendarView } from "./calendarTime";
import { ALL_STATUSES } from "./status";

interface Props {
  days: string[];
  view: CalendarView;
  showViewToggle: boolean;
  status?: AppointmentStatus;
  onPrevious: () => void;
  onNext: () => void;
  onToday: () => void;
  onViewChange: (view: CalendarView) => void;
  onStatusChange: (status?: AppointmentStatus) => void;
  onNew: () => void;
}

function periodLabel(days: string[]): string {
  const fmt = (date: string, opts: Intl.DateTimeFormatOptions) =>
    new Intl.DateTimeFormat("pt-BR", { ...opts, timeZone: "UTC" }).format(new Date(`${date}T12:00:00Z`));
  const first = days[0];
  const last = days[days.length - 1];
  if (first === last) return fmt(first, { weekday: "long", day: "numeric", month: "long", year: "numeric" });
  return `${fmt(first, { day: "numeric", month: "short" })} – ${fmt(last, { day: "numeric", month: "short", year: "numeric" })}`;
}

export default function CalendarHeader(props: Props) {
  const { t } = useTranslation();
  const { days, view, showViewToggle, status } = props;
  const toggleClass = (active: boolean) =>
    `px-4 py-1.5 rounded-radii-md text-label-lg ${active ? "bg-surfaceContainerLowest text-primary shadow-sm" : "text-onSurfaceVariant"}`;

  return (
    <div className="mb-6 flex flex-col gap-4">
      <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
        <div className="flex flex-wrap items-center gap-4">
          <h1 className="text-headline-lg text-onSurface">{t("calendar.title")}</h1>
          <div className="flex items-center gap-1 rounded-radii-lg border border-outlineVariant bg-surfaceContainerLowest px-2 py-1">
            <button type="button" onClick={props.onPrevious} aria-label={t("calendar.previous")} className="p-1.5 rounded-radii-full hover:bg-surfaceContainer">
              <ChevronLeft size={18} />
            </button>
            <span className="min-w-48 text-center text-label-lg text-onSurface">{periodLabel(days)}</span>
            <button type="button" onClick={props.onNext} aria-label={t("calendar.next")} className="p-1.5 rounded-radii-full hover:bg-surfaceContainer">
              <ChevronRight size={18} />
            </button>
          </div>
          <button type="button" onClick={props.onToday} className="rounded-radii-lg border border-outlineVariant px-3 py-1.5 text-label-lg text-onSurface hover:bg-surfaceContainer">
            {t("calendar.today")}
          </button>
        </div>
        <div className="flex items-center gap-3">
          {showViewToggle && (
            <div className="flex rounded-radii-lg bg-surfaceContainerHigh p-1">
              <button type="button" className={toggleClass(view === "day")} onClick={() => props.onViewChange("day")}>{t("calendar.views.day")}</button>
              <button type="button" className={toggleClass(view === "week")} onClick={() => props.onViewChange("week")}>{t("calendar.views.week")}</button>
            </div>
          )}
          <button type="button" onClick={props.onNew} className="flex items-center gap-2 rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary hover:brightness-110">
            <Plus size={18} />
            {t("calendar.newAppointment")}
          </button>
        </div>
      </div>
      <div className="flex items-center gap-3 rounded-radii-lg border border-outlineVariant bg-surfaceContainerLowest px-4 py-2">
        <label className="flex items-center gap-2 text-label-md text-onSurfaceVariant">
          {t("calendar.statusFilter.label")}
          <select
            value={status ?? ""}
            onChange={(e) => props.onStatusChange((e.target.value || undefined) as AppointmentStatus | undefined)}
            className="rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-2 py-1 text-body-md text-onSurface"
          >
            <option value="">{t("calendar.statusFilter.all")}</option>
            {ALL_STATUSES.map((s) => (
              <option key={s} value={s}>{t(`calendar.status.${s}`)}</option>
            ))}
          </select>
        </label>
      </div>
    </div>
  );
}
```

- [ ] **Step 7: Route** — `routes/_app/calendar.tsx` (dialogs are wired in Task 6; here the slot and card clicks are no-ops):

```tsx
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import type { AppointmentStatus } from "@anamnys/shared/lib/types";
import CalendarHeader from "@/components/calendar/CalendarHeader";
import CalendarGrid from "@/components/calendar/CalendarGrid";
import { PRACTICE_ZONE, addDays, todayIn, visibleDays, type CalendarView } from "@/components/calendar/calendarTime";
import { isStatus } from "@/components/calendar/status";
import { useAppointmentsQuery } from "@/hooks/useAppointments";
import { useIsMobile } from "@/hooks/useIsMobile";

// No PHI here: only the view, a date and a status filter.
interface CalendarSearch {
  view?: CalendarView;
  date?: string;
  status?: AppointmentStatus;
}

export const Route = createFileRoute("/_app/calendar")({
  validateSearch: (search: Record<string, unknown>): CalendarSearch => ({
    view: search.view === "day" || search.view === "week" ? search.view : undefined,
    date: typeof search.date === "string" && /^\d{4}-\d{2}-\d{2}$/.test(search.date) ? search.date : undefined,
    status: isStatus(search.status) ? search.status : undefined,
  }),
  component: CalendarPage,
});

// Provider calendar: week/day grid of appointments.
// See design/specs/2026-10-05-provider-calendar-design.md.
function CalendarPage() {
  const { t } = useTranslation();
  const navigate = useNavigate({ from: Route.fullPath });
  const search = Route.useSearch();
  const isMobile = useIsMobile();
  const view: CalendarView = isMobile ? "day" : (search.view ?? "week");
  const date = search.date ?? todayIn(PRACTICE_ZONE);
  const days = visibleDays(view, date);
  const query = useAppointmentsQuery(days, search.status);

  const setSearch = (next: Partial<CalendarSearch>) =>
    navigate({ search: (prev) => ({ ...prev, ...next }), replace: true });
  const step = view === "day" ? 1 : 7;

  return (
    <div className="p-4 md:p-8">
      <CalendarHeader
        days={days}
        view={view}
        showViewToggle={!isMobile}
        status={search.status}
        onPrevious={() => setSearch({ date: addDays(date, -step) })}
        onNext={() => setSearch({ date: addDays(date, step) })}
        onToday={() => setSearch({ date: undefined })}
        onViewChange={(v) => setSearch({ view: v })}
        onStatusChange={(s) => setSearch({ status: s })}
        onNew={() => {}}
      />
      {query.isError && <p className="mb-4 text-body-md text-error">{t("calendar.loadFailed")}</p>}
      <CalendarGrid
        days={days}
        appointments={query.data ?? []}
        onSlotClick={() => {}}
        onAppointmentClick={() => {}}
      />
    </div>
  );
}
```

- [ ] **Step 8: Ship the nav item** — in `packages/shared/src/ui/Sidenav.tsx:33`, remove `feature: "calendar"` so the item is always on (per the comment on `NavItem.feature`: undefined = real, shipped route):

```ts
  { href: "/calendar", Icon: Calendar, labelKey: "nav.calendar", matchPrefix: "/calendar" },
```

Leave `"calendar"` in `featureFlags.ts`; other apps may still read it. Grep `feature: "calendar"` and `"calendar"` in `packages/shared/src` to confirm nothing else depends on the flag for the provider nav.

- [ ] **Step 9: Build, regenerate routes, lint, look**

From `anamnys-aspire/apps/provider/`: `npx vite build` (regenerates `src/routeTree.gen.ts`), then `npm run build` and `npm run lint` → both succeed with no errors.
Start the app (`aspire start` from `anamnys-aspire/`), sign in as `dev.provider@anamnys.local`, open `/provider/calendar`. Expected: week grid of the current week, today highlighted, current-time line, seeded appointments (if any for dev.provider) in place; ‹ › and "Hoje" move the period; Dia/Semana switch; status filter narrows the cards; at 390px width the Day view is forced and the toggle hidden. Run the console sanity checks from Step 1.

- [ ] **Step 10: Commit**

```bash
git add anamnys-aspire/apps/provider/src/components/calendar anamnys-aspire/apps/provider/src/hooks/useAppointments.ts anamnys-aspire/apps/provider/src/hooks/useIsMobile.ts anamnys-aspire/apps/provider/src/routes/_app/calendar.tsx anamnys-aspire/apps/provider/src/routeTree.gen.ts anamnys-aspire/packages/shared/src/ui/Sidenav.tsx
git commit -m "feat: add provider calendar week and day views"
```

---

### Task 6: Create, edit and status actions

**Files:**
- Create: `apps/provider/src/components/calendar/PatientPicker.tsx`, `components/calendar/AppointmentFormModal.tsx`, `components/calendar/AppointmentDetailsModal.tsx`
- Modify: `apps/provider/src/routes/_app/calendar.tsx`

**Interfaces:**
- Consumes: `useAppointmentMutations()` (Task 5), `NEXT_STATUSES`, `STATUS_STYLE`, `calendarTime` helpers, `Modal` (`@/components/patients/Modal`: props `title`, `onClose`, `children`, `footer?`), `FormField` and `inputClass` (`@/components/patients/FormField`), `toast` (`@anamnys/shared/ui/Toaster`), `ApiError` (`@anamnys/shared/api/client`: `status`, `errors`, `message`), `patientsApi.search(request: PatientSearchRequest)` (POST, PHI stays out of the URL; omitting `archived` returns active patients).
- Produces: `AppointmentFormModal` props `{ mode: { kind: "create"; date: string; minutes: number } | { kind: "edit"; appointment: Appointment }; onClose(): void }`; `AppointmentDetailsModal` props `{ appointment: Appointment; onClose(): void; onEdit(): void }`.

- [ ] **Step 1: Patient picker** — `components/calendar/PatientPicker.tsx`:

```tsx
import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { patientsApi } from "@anamnys/shared/api/patients";
import { inputClass } from "@/components/patients/FormField";

interface Props {
  value: { id: string; name: string } | null;
  onChange: (value: { id: string; name: string } | null) => void;
  error?: string[];
}

const MIN_CHARS = 2;

// Searches the provider's active patients by name. Uses the POST search endpoint so the
// typed name never appears in a URL.
export default function PatientPicker({ value, onChange, error }: Props) {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const [debounced, setDebounced] = useState("");
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(text.trim()), 300);
    return () => clearTimeout(timer);
  }, [text]);

  const query = useQuery({
    queryKey: ["calendar-patient-search", debounced],
    queryFn: () => patientsApi.search({ search: debounced, sortBy: "name", sortDir: "asc", page: 1, pageSize: 8 }),
    enabled: !value && debounced.length >= MIN_CHARS,
  });

  if (value) {
    return (
      <div className="flex items-center justify-between rounded-radii-md border border-outlineVariant px-3 py-2">
        <span className="text-body-md text-onSurface">{value.name}</span>
        <button type="button" onClick={() => onChange(null)} className="text-label-md text-primary">
          {t("calendar.form.patientChange")}
        </button>
      </div>
    );
  }

  const items = query.data?.items ?? [];
  return (
    <div className="flex flex-col gap-1">
      <input
        className={inputClass}
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder={t("calendar.form.patientSearch")}
        aria-invalid={error ? true : undefined}
        autoFocus
      />
      {debounced.length >= MIN_CHARS && query.isSuccess && (
        <ul className="max-h-56 overflow-y-auto rounded-radii-md border border-outlineVariant">
          {items.length === 0 && <li className="px-3 py-2 text-body-md text-onSurfaceVariant">{t("calendar.form.patientNone")}</li>}
          {items.map((p) => (
            <li key={p.id}>
              <button
                type="button"
                onClick={() => onChange({ id: p.id, name: `${p.firstName} ${p.lastName}` })}
                className="w-full px-3 py-2 text-left text-body-md text-onSurface hover:bg-surfaceContainer"
              >
                {p.firstName} {p.lastName}
              </button>
            </li>
          ))}
        </ul>
      )}
      {error && <span className="text-label-md text-error">{error[0]}</span>}
    </div>
  );
}
```

- [ ] **Step 2: Form modal** — `components/calendar/AppointmentFormModal.tsx`:

```tsx
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type { Appointment, AppointmentModality } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import FormField, { inputClass } from "@/components/patients/FormField";
import { useAppointmentMutations } from "@/hooks/useAppointments";
import { PRACTICE_ZONE, zonedParts, zonedToInstant } from "./calendarTime";
import PatientPicker from "./PatientPicker";

export type FormMode =
  | { kind: "create"; date: string; minutes: number }
  | { kind: "edit"; appointment: Appointment };

interface Props {
  mode: FormMode;
  onClose: () => void;
}

const toTime = (minutes: number) => `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;
const fromTime = (time: string) => {
  const [h, m] = time.split(":").map(Number);
  return h * 60 + m;
};

function initialState(mode: FormMode) {
  if (mode.kind === "create") {
    return { patient: null, date: mode.date, time: toTime(mode.minutes), duration: 50, modality: "online" as AppointmentModality };
  }
  const a = mode.appointment;
  const start = zonedParts(new Date(a.startsAt), PRACTICE_ZONE);
  const duration = (new Date(a.endsAt).getTime() - new Date(a.startsAt).getTime()) / 60000;
  return { patient: { id: a.patientId, name: a.patientName }, date: start.date, time: toTime(start.minutes), duration, modality: a.modality };
}

export default function AppointmentFormModal({ mode, onClose }: Props) {
  const { t } = useTranslation();
  const { create, update } = useAppointmentMutations();
  const [state, setState] = useState(() => initialState(mode));
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const pending = create.isPending || update.isPending;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrors({});
    const slot = {
      startsAt: zonedToInstant(state.date, fromTime(state.time), PRACTICE_ZONE).toISOString(),
      durationMinutes: Number(state.duration),
      modality: state.modality,
    };
    try {
      if (mode.kind === "create") {
        if (!state.patient) {
          setErrors({ patientId: [t("calendar.form.patientRequired")] });
          return;
        }
        await create.mutateAsync({ ...slot, patientId: state.patient.id });
        toast.success(t("calendar.form.created"));
      } else {
        await update.mutateAsync({ id: mode.appointment.id, request: slot });
        toast.success(t("calendar.form.updated"));
      }
      onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 400 && err.errors) setErrors(err.errors);
      else if (err instanceof ApiError && (err.status === 409 || err.status === 422)) toast.error(err.message);
      else toast.error(t("calendar.form.saveFailed"));
    }
  };

  return (
    <Modal
      title={t(mode.kind === "create" ? "calendar.form.createTitle" : "calendar.form.editTitle")}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose} className="px-4 py-2 rounded-radii-lg text-label-lg text-onSurfaceVariant hover:bg-surfaceContainer">
            {t("common.cancel")}
          </button>
          <button type="submit" form="appointment-form" disabled={pending} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60">
            {t("calendar.form.save")}
          </button>
        </>
      }
    >
      <form id="appointment-form" onSubmit={submit} className="flex flex-col gap-4 p-5">
        <div className="flex flex-col gap-1">
          <span className="text-label-md text-onSurfaceVariant">{t("calendar.form.patient")}</span>
          {mode.kind === "create" ? (
            <PatientPicker value={state.patient} onChange={(patient) => setState((s) => ({ ...s, patient }))} error={errors.patientId} />
          ) : (
            <span className="text-body-md text-onSurface">{state.patient?.name}</span>
          )}
        </div>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <FormField label={t("calendar.form.date")} type="date" required value={state.date}
            onChange={(e) => setState((s) => ({ ...s, date: e.target.value }))} error={errors.startsAt} />
          <FormField label={t("calendar.form.time")} type="time" step={900} required value={state.time}
            onChange={(e) => setState((s) => ({ ...s, time: e.target.value }))} />
          <FormField label={t("calendar.form.duration")} type="number" min={5} max={480} required value={state.duration}
            onChange={(e) => setState((s) => ({ ...s, duration: Number(e.target.value) }))} error={errors.durationMinutes} />
        </div>
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("calendar.form.modality")}
          <select className={inputClass} value={state.modality}
            onChange={(e) => setState((s) => ({ ...s, modality: e.target.value as AppointmentModality }))}>
            <option value="online">{t("calendar.modality.online")}</option>
            <option value="presencial">{t("calendar.modality.presencial")}</option>
          </select>
        </label>
      </form>
    </Modal>
  );
}
```

- [ ] **Step 3: Details modal** — `components/calendar/AppointmentDetailsModal.tsx`:

```tsx
import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import type { Appointment, AppointmentStatus } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import Modal from "@/components/patients/Modal";
import { inputClass } from "@/components/patients/FormField";
import { useAppointmentMutations } from "@/hooks/useAppointments";
import { formatTime } from "./calendarTime";
import { NEXT_STATUSES, STATUS_STYLE } from "./status";

interface Props {
  appointment: Appointment;
  onClose: () => void;
  onEdit: () => void;
}

const ACTION_LABEL: Record<AppointmentStatus, string> = {
  confirmed: "calendar.details.confirm",
  attended: "calendar.details.attended",
  no_show: "calendar.details.noShow",
  cancelled: "calendar.details.cancel",
  scheduled: "calendar.details.undo",
};

export default function AppointmentDetailsModal({ appointment: a, onClose, onEdit }: Props) {
  const { t } = useTranslation();
  const { setStatus } = useAppointmentMutations();
  const [cancelling, setCancelling] = useState(false);
  const [reason, setReason] = useState("");

  const started = new Date(a.startsAt) <= new Date();
  const actions = NEXT_STATUSES[a.status].filter((s) => (s === "attended" || s === "no_show" ? started : true));
  const editable = a.status === "scheduled" || a.status === "confirmed";
  const day = new Intl.DateTimeFormat("pt-BR", { weekday: "long", day: "numeric", month: "long", timeZone: a.timezone })
    .format(new Date(a.startsAt));

  const change = async (status: AppointmentStatus) => {
    try {
      await setStatus.mutateAsync({ id: a.id, status, reason: status === "cancelled" ? reason.trim() : undefined });
      toast.success(t("calendar.details.statusChanged"));
      onClose();
    } catch (err) {
      toast.error(err instanceof ApiError && err.status === 409 ? err.message : t("calendar.details.statusFailed"));
    }
  };

  const button = "px-3 py-2 rounded-radii-lg border border-outlineVariant text-label-lg text-onSurface hover:bg-surfaceContainer disabled:opacity-60";

  return (
    <Modal title={a.patientName} onClose={onClose}>
      <div className="flex flex-col gap-4 p-5">
        <div className="flex flex-col gap-1 text-body-md text-onSurface">
          <span className="capitalize">{day}</span>
          <span>{formatTime(new Date(a.startsAt), a.timezone)}–{formatTime(new Date(a.endsAt), a.timezone)} · {t(`calendar.modality.${a.modality}`)}</span>
          <span className={`self-start rounded-radii-full border-l-4 px-3 py-0.5 text-label-md ${STATUS_STYLE[a.status]}`}>
            {t(`calendar.status.${a.status}`)}
          </span>
          {a.cancellationReason && (
            <span className="text-onSurfaceVariant">{t("calendar.details.reason")}: {a.cancellationReason}</span>
          )}
          <Link to="/patients/$patientId" params={{ patientId: a.patientId }} className="self-start text-label-lg text-primary">
            {t("calendar.details.openRecord")}
          </Link>
        </div>

        {cancelling ? (
          <div className="flex flex-col gap-2">
            <textarea className={inputClass} maxLength={500} rows={3} value={reason}
              placeholder={t("calendar.details.reasonPlaceholder")} onChange={(e) => setReason(e.target.value)} />
            <div className="flex gap-2">
              <button type="button" className={button} onClick={() => setCancelling(false)}>{t("common.cancel")}</button>
              <button type="button" disabled={setStatus.isPending} onClick={() => change("cancelled")}
                className="px-3 py-2 rounded-radii-lg bg-error text-label-lg text-onError disabled:opacity-60">
                {t("calendar.details.cancelConfirm")}
              </button>
            </div>
          </div>
        ) : (
          <div className="flex flex-wrap gap-2">
            {editable && <button type="button" className={button} onClick={onEdit}>{t("calendar.details.edit")}</button>}
            {actions.map((s) => (
              <button key={s} type="button" disabled={setStatus.isPending} className={button}
                onClick={() => (s === "cancelled" ? setCancelling(true) : change(s))}>
                {t(ACTION_LABEL[s])}
              </button>
            ))}
          </div>
        )}
      </div>
    </Modal>
  );
}
```

- [ ] **Step 4: Wire into the route** — in `routes/_app/calendar.tsx` add imports and dialog state, replacing the no-op handlers:

```tsx
import { useState } from "react";
import type { Appointment } from "@anamnys/shared/lib/types";
import AppointmentFormModal, { type FormMode } from "@/components/calendar/AppointmentFormModal";
import AppointmentDetailsModal from "@/components/calendar/AppointmentDetailsModal";
```

Inside `CalendarPage`, after `const query = ...`:

```tsx
  const [form, setForm] = useState<FormMode | null>(null);
  const [selected, setSelected] = useState<Appointment | null>(null);
  // "Nova consulta" from the header: today at 09:00 when today is visible, else the first visible day.
  const defaultSlot = (): FormMode => {
    const today = todayIn(PRACTICE_ZONE);
    return { kind: "create", date: days.includes(today) ? today : days[0], minutes: 9 * 60 };
  };
```

Change the handlers and render the dialogs:

```tsx
        onNew={() => setForm(defaultSlot())}
      ...
      <CalendarGrid
        days={days}
        appointments={query.data ?? []}
        onSlotClick={(d, minutes) => setForm({ kind: "create", date: d, minutes })}
        onAppointmentClick={setSelected}
      />
      {form && <AppointmentFormModal mode={form} onClose={() => setForm(null)} />}
      {selected && (
        <AppointmentDetailsModal
          appointment={selected}
          onClose={() => setSelected(null)}
          onEdit={() => {
            setForm({ kind: "edit", appointment: selected });
            setSelected(null);
          }}
        />
      )}
```

- [ ] **Step 5: Build and lint** — from `anamnys-aspire/apps/provider/`: `npm run build` and `npm run lint` → no errors.

- [ ] **Step 6: Browser check** — as `dev.provider`, on `/provider/calendar`:
  1. Click an empty cell at Tue 10:00 → "Nova consulta" opens with that date and `10:00`; pick a patient by typing 2+ letters; save → toast "Consulta agendada.", card appears 10:00–10:50.
  2. Create another at 10:30 the same day → toast "Horário já ocupado.", dialog stays open.
  3. Open the first card → Editar → move to 11:00, Presencial → card moves, person icon.
  4. Confirmar → green card. Cancelar consulta → reason → card struck through; creating at the same slot now succeeds.
  5. On a past appointment: Realizada → patient record (`Abrir prontuário`) shows "Último atendimento" at that date; Desfazer → back to Agendada.
  6. An archived patient does not appear in the picker.
  7. At 390px: day view, dialogs full-screen, all actions reachable.

- [ ] **Step 7: Commit**

```bash
git add anamnys-aspire/apps/provider/src/components/calendar anamnys-aspire/apps/provider/src/routes/_app/calendar.tsx
git commit -m "feat: create, reschedule and change status of appointments from the calendar"
```

---

### Task 7: Full verification and docs

**Files:**
- Modify: `anamnys-aspire/design/gaps/2026-10-03-patient-flows-gaps.md`
- Modify: `anamnys-aspire/documentation/13-provider-app-walkthrough.md`

- [ ] **Step 1: Full suite** — `aspire stop`, then from `anamnys-aspire/`: `dotnet run --project anamnys-aspire.Tests` → all PASS. From the repo's `anamnys-aspire/`: `npm run build` and `npm run lint` (all workspaces) → no errors. Report any failure with its output; do not mark done otherwise.

- [ ] **Step 2: Cross-surface check** — with the app running, as the linked dev patient in the patient app, the provider's "Próxima sessão" and the sessions list reflect an appointment created in Task 6; the provider patient list's "Próximo atendimento" shows it too.

- [ ] **Step 3: Gaps file** — in `design/gaps/2026-10-03-patient-flows-gaps.md`, replace the "Appointments have no write path" bullet with:

```markdown
- ~~**Appointments have no write path.**~~ Done (`2026-10-05-provider-calendar`): the provider
  calendar creates, reschedules and changes status; "Realizada" updates `Patients.LastVisit`.
  Still open for scheduling: availability, recurrence, patient self-booking and Google
  Calendar sync (deliveries 2–5 of that spec).
```

- [ ] **Step 4: Walkthrough** — add a "Agenda" section to `documentation/13-provider-app-walkthrough.md` following the chapter's existing style: route `/calendar`, week/day views, URL params, create/edit/status actions, the transition table, `409`/`422` messages, and that overlap is enforced by `Appointments_no_overlap`.

- [ ] **Step 5: Commit**

```bash
git add anamnys-aspire/design/gaps/2026-10-03-patient-flows-gaps.md anamnys-aspire/documentation/13-provider-app-walkthrough.md
git commit -m "docs: document the provider calendar and close the appointments gap"
```
