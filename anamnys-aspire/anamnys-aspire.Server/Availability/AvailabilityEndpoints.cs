using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Availability;

// design/specs/2026-11-09-availability-design.md §4. PHI group (401 before anything), provider-scoped, 404 for another
// provider's block or an unknown version, every answer Cache-Control: no-store. Handlers are thin: resolve the provider
// from the session, delegate, map the outcome.
public static class AvailabilityEndpoints
{
    public static void MapAvailabilityEndpoints(this RouteGroupBuilder phi)
    {
        var me = phi.MapGroup("providers/me");

        me.MapGet("availability", async (HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await GetAvailabilityAsync(http, db, providerId, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPut("availability", async (
            SaveAvailabilityRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await SaveAvailabilityAsync(http, db, providerId, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapDelete("availability/upcoming/{effectiveFrom}", async (
            string effectiveFrom, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await CancelUpcomingAsync(http, db, providerId, effectiveFrom, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapGet("holiday-settings", async (HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await GetHolidaySettingsAsync(http, db, providerId, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPut("holiday-settings", async (
            SaveHolidaySettingsRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await SaveHolidaySettingsAsync(http, db, providerId, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapGet("blocks", async (
            DateTimeOffset? from, DateTimeOffset? to, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ListBlocksAsync(http, db, providerId, from, to, ct)
                : Results.Unauthorized());

        me.MapPost("blocks/preview", async (BlockRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await PreviewBlockAsync(http, db, providerId, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("blocks", async (
            BlockRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await CreateBlockAsync(http, db, providerId, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapDelete("blocks/{id:guid}", async (
            Guid id, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await DeleteBlockAsync(http, db, providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapGet("calendar-overlay", async (
            string? from, string? to, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await OverlayAsync(http, db, providerId, from, to, ct)
                : Results.Unauthorized());
    }

    internal static async Task<IResult> GetAvailabilityAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return Results.Ok(await AvailabilityRules.GetAsync(db, providerId, now, ct));
    }

    internal static async Task<IResult> SaveAvailabilityAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, SaveAvailabilityRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var outcome = await AvailabilityRules.SaveAsync(db, providerId, body, now, ct);
        return outcome == AvailabilityOutcome.Ok
            ? Results.Ok(await AvailabilityRules.GetAsync(db, providerId, now, ct))
            : Problem(outcome);
    }

    internal static async Task<IResult> CancelUpcomingAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, string effectiveFrom, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        if (!DateOnly.TryParseExact(effectiveFrom, "yyyy-MM-dd", out var date))
        {
            return Results.NotFound();
        }
        var outcome = await AvailabilityRules.CancelUpcomingAsync(db, providerId, date, now, ct);
        return outcome == AvailabilityOutcome.Ok ? Results.NoContent() : Problem(outcome);
    }

    internal static async Task<IResult> GetHolidaySettingsAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return Results.Ok(await AvailabilityRules.GetHolidaySettingsAsync(db, providerId, now, ct));
    }

    internal static async Task<IResult> SaveHolidaySettingsAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, SaveHolidaySettingsRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var outcome = await AvailabilityRules.SaveHolidaySettingsAsync(db, providerId, body, now, ct);
        return outcome == AvailabilityOutcome.Ok
            ? Results.Ok(await AvailabilityRules.GetHolidaySettingsAsync(db, providerId, now, ct))
            : Problem(outcome);
    }

    internal static async Task<IResult> ListBlocksAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        NoStore(http);
        if (from is null || to is null || to <= from || to - from > TimeSpan.FromDays(Blocks.MaxDays + 1))
        {
            return Problem(BlockOutcome.InvalidRange);
        }
        return Results.Ok(await Blocks.OverlappingAsync(db, providerId, from.Value, to.Value, ct));
    }

    internal static async Task<IResult> PreviewBlockAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, BlockRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var (outcome, items) = await Blocks.PreviewAsync(db, providerId, body, now, ct);
        return items is null ? Problem(outcome) : Results.Ok(items);
    }

    internal static async Task<IResult> CreateBlockAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, BlockRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var result = await Blocks.CreateAsync(db, providerId, body, now, ct);
        if (result.Outcome == BlockOutcome.Changed)
        {
            // The affected appointments are no longer the ones the provider saw: nothing was written; the current list goes back.
            var (_, current) = await Blocks.PreviewAsync(db, providerId, body, now, ct);
            return Results.Conflict(new
            {
                message = "As consultas afetadas mudaram desde a prévia. Revise a lista e confirme de novo.",
                code = "changed",
                appointments = current,
            });
        }
        return result.Outcome == BlockOutcome.Ok
            ? Results.Created($"/api/phi/providers/me/blocks/{result.Id}", new BlockCreatedResponse(result.Id!.Value, result.CancelledAppointmentIds!))
            : Problem(result.Outcome);
    }

    internal static async Task<IResult> DeleteBlockAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var outcome = await Blocks.DeleteAsync(db, providerId, id, now, ct);
        return outcome == BlockOutcome.Ok ? Results.NoContent() : Problem(outcome);
    }

    internal static async Task<IResult> OverlayAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, string? from, string? to, CancellationToken ct)
    {
        NoStore(http);
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", out var fromDate) || !DateOnly.TryParseExact(to, "yyyy-MM-dd", out var toDate)
            || toDate < fromDate || toDate.DayNumber - fromDate.DayNumber + 1 > AvailabilityOverlay.MaxRangeDays)
        {
            return Problem(BlockOutcome.InvalidRange);
        }
        return Results.Ok(await AvailabilityOverlay.ForRangeAsync(db, providerId, fromDate, toDate, ct));
    }

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    private static IResult Problem(AvailabilityOutcome outcome) => outcome switch
    {
        AvailabilityOutcome.InvalidIntervals => Results.BadRequest(new
        {
            message = "Revise os horários: cada intervalo começa antes de terminar, sem sobreposição, no máximo 6 por dia, em passos de 5 minutos.",
            code = "invalid_intervals",
        }),
        AvailabilityOutcome.EffectiveInPast => Results.BadRequest(new { message = "A data de início não pode estar no passado.", code = "effective_in_past" }),
        AvailabilityOutcome.InvalidEffectiveFrom => Results.BadRequest(new { message = "Informe a partir de quando vale.", code = "invalid_effective_from" }),
        AvailabilityOutcome.InvalidHolidays => Results.BadRequest(new { message = "Preferências de feriado inválidas.", code = "invalid_holidays" }),
        _ => Results.NotFound(),
    };

    private static IResult Problem(BlockOutcome outcome) => outcome switch
    {
        BlockOutcome.InvalidRange => Results.BadRequest(new { message = "Revise o período: o fim deve ser depois do início.", code = "invalid_range" }),
        BlockOutcome.InvalidKind => Results.BadRequest(new { message = "Escolha o tipo do bloqueio.", code = "invalid_kind" }),
        BlockOutcome.InvalidReason => Results.BadRequest(
            new { message = $"Use no máximo {Blocks.MaxReason} caracteres no motivo.", code = "invalid_reason" }),
        BlockOutcome.ExpectedRequired => Results.BadRequest(
            new { message = "Envie a lista de consultas afetadas mostrada na prévia.", code = "expected_appointments_required" }),
        BlockOutcome.Changed => Results.Conflict(
            new { message = "Uma consulta mudou enquanto você bloqueava o período. Atualize e tente de novo.", code = "changed" }),
        _ => Results.NotFound(),
    };
}
