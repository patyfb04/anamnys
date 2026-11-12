using Anamnys.Server.Availability;
using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Appointments.Series;

// design/specs/2026-11-09-appointment-series-design.md §4. PHI group (401 before anything), provider-scoped, 404 for
// another provider's series, occurrence or patient, every answer Cache-Control: no-store. Handlers are thin: resolve
// the provider from the session, validate, delegate to SeriesService, map the outcome.
public static class SeriesEndpoints
{
    public static void MapSeriesEndpoints(this RouteGroupBuilder phi)
    {
        var me = phi.MapGroup("providers/me");

        me.MapPost("appointment-series", async (
            CreateSeriesRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await CreateAsync(http, db, providerId, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapGet("appointment-series/{id:guid}", async (
            Guid id, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await GetAsync(http, db, providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapGet("patients/{patientId:guid}/appointment-series", async (
            Guid patientId, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ListForPatientAsync(http, db, providerId, patientId, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("appointment-series/{id:guid}/split/preview", async (
            Guid id, SplitSeriesRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await PreviewSplitAsync(http, db, providerId, id, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("appointment-series/{id:guid}/split", async (
            Guid id, SplitSeriesRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await SplitAsync(http, db, providerId, id, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("appointment-series/{id:guid}/end", async (
            Guid id, EndSeriesRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await EndAsync(http, db, providerId, id, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());
    }

    internal static async Task<IResult> CreateAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, CreateSeriesRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        if (body is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Envie os dados da série."] });
        }
        var errors = body.Validate();
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        var result = await SeriesService.CreateAsync(db, providerId, body, now, ct);
        return result.Outcome == SeriesOutcome.Ok
            ? Results.Created($"/api/phi/providers/me/appointment-series/{result.Value!.SeriesId}", result.Value)
            : Problem(result.Outcome, result.Reasons);
    }

    internal static async Task<IResult> GetAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return await SeriesService.GetAsync(db, providerId, id, now, ct) is { } series ? Results.Ok(series) : Results.NotFound();
    }

    internal static async Task<IResult> ListForPatientAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return await SeriesService.ListForPatientAsync(db, providerId, patientId, now, ct) is { } list ? Results.Ok(list) : Results.NotFound();
    }

    internal static async Task<IResult> PreviewSplitAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, SplitSeriesRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        if (Invalid(body) is { } invalid)
        {
            return invalid;
        }
        var result = await SeriesService.PreviewSplitAsync(db, providerId, id, body!, now, ct);
        return result.Outcome == SeriesOutcome.Ok ? Results.Ok(result.Value) : Problem(result.Outcome, result.Reasons);
    }

    internal static async Task<IResult> SplitAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, SplitSeriesRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        if (Invalid(body) is { } invalid)
        {
            return invalid;
        }
        var result = await SeriesService.SplitAsync(db, providerId, id, body!, now, ct);
        return result.Outcome == SeriesOutcome.Ok ? Results.Ok(result.Value) : Problem(result.Outcome, result.Reasons);
    }

    internal static async Task<IResult> EndAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, EndSeriesRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var result = await SeriesService.EndAsync(db, providerId, id, body ?? new EndSeriesRequest(null), now, ct);
        return result.Outcome == SeriesOutcome.Ok ? Results.Ok(result.Value) : Problem(result.Outcome, result.Reasons);
    }

    private static IResult? Invalid(SplitSeriesRequest? body)
    {
        if (body is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Envie os dados da alteração."] });
        }
        var errors = body.Validate();
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    private static IResult Problem(SeriesOutcome outcome, IReadOnlyList<OutsideAvailabilityReason>? reasons) => outcome switch
    {
        SeriesOutcome.InvalidInterval => Results.BadRequest(new { message = "Escolha toda semana ou a cada 2 semanas.", code = "invalid_interval" }),
        SeriesOutcome.InvalidEnd => Results.BadRequest(new
        {
            message = $"Revise o fim da série: uma data a partir do início ou de 1 a {SeriesPattern.MaxOccurrences} sessões, não os dois.",
            code = "invalid_end",
        }),
        SeriesOutcome.InvalidDate => Results.BadRequest(new { message = "Informe uma data a partir de hoje.", code = "invalid_date" }),
        SeriesOutcome.StartInPast => Results.BadRequest(new { message = "Escolha um horário de início que ainda não passou.", code = "start_in_past" }),
        SeriesOutcome.InvalidOccurrence => Results.Conflict(new
        {
            message = "Escolha uma sessão futura, marcada ou confirmada, e um novo horário que ainda não passou.",
            code = "invalid_occurrence",
        }),
        SeriesOutcome.SeriesEnded => Results.Conflict(new { message = "Esta série já foi encerrada.", code = "series_ended" }),
        SeriesOutcome.Overlap => Results.Conflict(new { message = "Horário já ocupado.", code = "overlap" }),
        SeriesOutcome.OutsideAvailability => Results.Conflict(new
        {
            code = "outside_availability",
            message = "Fora da sua disponibilidade.",
            reasons,
        }),
        SeriesOutcome.PatientArchived => Results.UnprocessableEntity(new { message = "Paciente arquivado." }),
        _ => Results.NotFound(),
    };
}
