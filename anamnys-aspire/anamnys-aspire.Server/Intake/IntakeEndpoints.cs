using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Intakes;

// design/specs/2026-11-12-structured-intake-design.md §4. PHI group (401 before anything), provider-scoped, 404 for
// another provider's template, patient, intake or transfer, every answer Cache-Control: no-store. Handlers are thin:
// resolve the provider from the session, validate, delegate, map the outcome.
public static class IntakeEndpoints
{
    public static void MapIntakeEndpoints(this RouteGroupBuilder phi)
    {
        var me = phi.MapGroup("providers/me");

        me.MapGet("intake-templates", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ListTemplatesAsync(http, db, providerId, ct)
                : Results.Unauthorized());

        me.MapGet("intake-templates/{id:guid}", async (Guid id, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await GetTemplateAsync(http, db, providerId, id, ct)
                : Results.Unauthorized());

        me.MapPost("intake-templates/{id:guid}/adapt", async (
            Guid id, AdaptIntakeTemplateRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await AdaptTemplateAsync(http, db, providerId, id, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPut("intake-templates/{id:guid}", async (
            Guid id, UpdateIntakeTemplateRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await UpdateTemplateAsync(http, db, providerId, id, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("intake-templates/{id:guid}/archive", async (
            Guid id, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ArchiveTemplateAsync(http, db, providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPut("intake-settings", async (IntakeSettingsRequest? body, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await SetSettingsAsync(http, db, providerId, body, ct)
                : Results.Unauthorized());

        me.MapGet("patients/{patientId:guid}/intakes", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ListAsync(http, db, providerId, patientId, ct)
                : Results.Unauthorized());

        me.MapPost("patients/{patientId:guid}/intakes", async (
            Guid patientId, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await StartAsync(http, db, providerId, patientId, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapGet("intakes/{id:guid}", async (Guid id, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await GetAsync(http, db, providerId, id, ct)
                : Results.Unauthorized());

        me.MapPut("intakes/{id:guid}/answers", async (
            Guid id, List<IntakeAnswerInput?>? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await SaveAnswersAsync(http, db, providerId, id, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("intakes/{id:guid}/note", async (Guid id, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await GenerateNoteAsync(http, db, providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapDelete("intakes/{id:guid}", async (Guid id, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await DiscardAsync(http, db, providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("intakes/{id:guid}/unlock", async (Guid id, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await UnlockAsync(http, db, providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapGet("intakes/{id:guid}/transfers", async (Guid id, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ListTransfersAsync(http, db, providerId, id, ct)
                : Results.Unauthorized());

        me.MapPost("intakes/{id:guid}/transfers/{transferId:guid}/confirm", async (
            Guid id, Guid transferId, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await DecideAsync(http, db, providerId, id, transferId, confirm: true, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("intakes/{id:guid}/transfers/{transferId:guid}/ignore", async (
            Guid id, Guid transferId, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await DecideAsync(http, db, providerId, id, transferId, confirm: false, clock.GetUtcNow(), ct)
                : Results.Unauthorized());
    }

    internal static async Task<IResult> ListTemplatesAsync(HttpContext http, AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        NoStore(http);
        return Results.Ok(await IntakeTemplates.ListAsync(db, providerId, ct));
    }

    internal static async Task<IResult> GetTemplateAsync(HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, CancellationToken ct)
    {
        NoStore(http);
        return await IntakeTemplates.GetAsync(db, providerId, id, ct) is { } template ? Results.Ok(template) : Results.NotFound();
    }

    internal static async Task<IResult> AdaptTemplateAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, AdaptIntakeTemplateRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        if (body?.Name is { } name && (string.IsNullOrWhiteSpace(name) || name.Trim().Length > UpdateIntakeTemplateRequest.MaxName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"Informe um nome de até {UpdateIntakeTemplateRequest.MaxName} caracteres."],
            });
        }
        var result = await IntakeTemplates.AdaptAsync(db, providerId, id, body, now, ct);
        return result.Outcome == IntakeOutcome.Ok
            ? Results.Created($"/api/phi/providers/me/intake-templates/{result.Value!.Id}", result.Value)
            : Problem(result);
    }

    internal static async Task<IResult> UpdateTemplateAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, UpdateIntakeTemplateRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        if (body is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Envie as perguntas do modelo."] });
        }
        var errors = body.Validate();
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        var result = await IntakeTemplates.UpdateAsync(db, providerId, id, body, now, ct);
        return result.Outcome == IntakeOutcome.Ok ? Results.Ok(result.Value) : Problem(result);
    }

    internal static async Task<IResult> ArchiveTemplateAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var result = await IntakeTemplates.ArchiveAsync(db, providerId, id, now, ct);
        return result.Outcome == IntakeOutcome.Ok ? Results.Ok(result.Value) : Problem(result);
    }

    internal static async Task<IResult> SetSettingsAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, IntakeSettingsRequest? body, CancellationToken ct)
    {
        NoStore(http);
        var result = await IntakeTemplates.SetDefaultAsync(db, providerId, body?.DefaultTemplateId, ct);
        return result.Outcome == IntakeOutcome.Ok ? Results.Ok(result.Value) : Problem(result);
    }

    internal static async Task<IResult> ListAsync(HttpContext http, AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct)
    {
        NoStore(http);
        return await IntakeService.ListForPatientAsync(db, providerId, patientId, ct) is { } list ? Results.Ok(list) : Results.NotFound();
    }

    internal static async Task<IResult> StartAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var result = await IntakeService.StartAsync(db, providerId, patientId, now, ct);
        return result.Outcome switch
        {
            IntakeOutcome.Ok => Results.Created($"/api/phi/providers/me/intakes/{result.Value!.Id}", result.Value),
            // Spec section 4: starting on an archived record is a 400, unlike the other archived-patient answers (409).
            IntakeOutcome.PatientArchived => Results.BadRequest(new { code = "patient_archived", message = "Paciente arquivado." }),
            _ => Problem(result),
        };
    }

    internal static async Task<IResult> GetAsync(HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, CancellationToken ct)
    {
        NoStore(http);
        return await IntakeService.GetAsync(db, providerId, id, ct) is { } intake ? Results.Ok(intake) : Results.NotFound();
    }

    internal static async Task<IResult> SaveAnswersAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, IReadOnlyList<IntakeAnswerInput?>? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        if (body is null || body.Count > IntakeService.MaxBatch)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = [$"Envie de 0 a {IntakeService.MaxBatch} respostas."],
            });
        }
        var result = await IntakeService.SaveAnswersAsync(db, providerId, id, body, now, ct);
        return result.Outcome == IntakeOutcome.Ok ? Results.Ok(result.Value) : Problem(result);
    }

    internal static async Task<IResult> GenerateNoteAsync(HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var result = await IntakeNote.GenerateAsync(db, providerId, id, now, ct);
        return result.Outcome == IntakeOutcome.Ok ? Results.Ok(result.Value) : Problem(result);
    }

    internal static async Task<IResult> DiscardAsync(HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var result = await IntakeNote.DiscardAsync(db, providerId, id, now, ct);
        return result.Outcome == IntakeOutcome.Ok ? Results.NoContent() : Problem(result);
    }

    // "Editar respostas": 200 with the intake (as GET) once its unsigned note is gone.
    internal static async Task<IResult> UnlockAsync(HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var result = await IntakeNote.UnlockAsync(db, providerId, id, now, ct);
        if (result.Outcome != IntakeOutcome.Ok)
        {
            return Problem(result);
        }
        return await IntakeService.GetAsync(db, providerId, id, ct) is { } intake ? Results.Ok(intake) : Results.NotFound();
    }

    internal static async Task<IResult> ListTransfersAsync(HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, CancellationToken ct)
    {
        NoStore(http);
        return await IntakeTransfers.ListAsync(db, providerId, id, ct) is { } list ? Results.Ok(list) : Results.NotFound();
    }

    internal static async Task<IResult> DecideAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, Guid transferId, bool confirm, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var result = confirm
            ? await IntakeTransfers.ConfirmAsync(db, providerId, id, transferId, now, ct)
            : await IntakeTransfers.IgnoreAsync(db, providerId, id, transferId, now, ct);
        return result.Outcome == IntakeOutcome.Ok ? Results.Ok(result.Value) : Problem(result);
    }

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    private static IResult Problem<T>(IntakeResult<T> result) => result.Outcome switch
    {
        IntakeOutcome.Forbidden => Results.Json(
            new { code = "template_read_only", message = "O modelo padrão não pode ser alterado. Use \"Adaptar\"." }, statusCode: StatusCodes.Status403Forbidden),
        IntakeOutcome.NameTaken => Results.Conflict(new { code = "name_taken", message = "Já existe um modelo com este nome." }),
        IntakeOutcome.TemplateArchived => Results.Conflict(new { code = "template_archived", message = "Este modelo foi arquivado." }),
        IntakeOutcome.InProgress => Results.Conflict(new { code = "intake_in_progress", message = "Já há uma anamnese em preenchimento para este paciente." }),
        IntakeOutcome.PatientArchived => Results.Conflict(new { code = "patient_archived", message = "Paciente arquivado." }),
        IntakeOutcome.NotDraft => Results.Conflict(new { code = "not_draft", message = "Esta anamnese já foi assinada." }),
        IntakeOutcome.AnswersLocked => Results.Conflict(new
        {
            code = "answers_locked",
            message = "A nota já foi gerada. Use \"Editar respostas\" para alterar as respostas (as edições da nota serão perdidas).",
        }),
        IntakeOutcome.InvalidAnswer => Results.BadRequest(new
        {
            code = "invalid_answer",
            message = "Revise as respostas destacadas.",
            questionIds = result.QuestionIds,
        }),
        IntakeOutcome.RequiredMissing => Results.BadRequest(new
        {
            code = "required_missing",
            message = "Responda as perguntas obrigatórias antes de gerar a nota.",
            questionIds = result.QuestionIds,
        }),
        IntakeOutcome.NotSigned => Results.Conflict(new { code = "not_signed", message = "Assine a nota da anamnese antes de levar itens ao prontuário." }),
        IntakeOutcome.NotPending => Results.Conflict(new { code = "not_pending", message = "Este item já foi decidido." }),
        _ => Results.NotFound(),
    };
}
