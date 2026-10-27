using Anamnys.Server.Data;
using Anamnys.Server.DocumentReading;
using Anamnys.Server.NoteStructuring;
using Anamnys.Server.Patients;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.DocumentFacts;

public sealed record FactCorrection(string? Value);

public static class FactEndpoints
{
    // DocumentFacts:Extractor = "llama" (default, the in-process model) or "none" (nothing extracted:
    // the test AppHost, where the model must never load).
    public static void AddDocumentFacts(this WebApplicationBuilder builder)
    {
        var extractor = builder.Configuration["DocumentFacts:Extractor"] ?? "llama";
        switch (extractor)
        {
            case "llama":
                builder.Services.AddSingleton<IFactExtractor>(sp => new LlamaFactExtractor(
                    sp.GetRequiredService<LlmModelProvider>(), sp.GetRequiredService<IOptions<NoteStructuringOptions>>(),
                    sp.GetRequiredService<LlmGate>()));
                break;
            case "none":
                builder.Services.AddSingleton<IFactExtractor, NoFactExtractor>();
                break;
            default:
                throw new InvalidOperationException("DocumentFacts:Extractor must be \"llama\" or \"none\".");
        }
        builder.Services.AddSingleton<FactQueue>();
        builder.Services.AddHostedService<FactWorker>();
    }

    public static void MapFactEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapGet("external-documents/{id:guid}/facts", async (Guid id, HttpContext http, AnamnysDbContext db, FactQueue queue, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await FactService.ListAsync(db, providerId, id, queue.IsBusy(id), ct) is { } view
                ? Results.Ok(view)
                : Results.NotFound();
        });

        group.MapPost("external-documents/{id:guid}/facts/{factId:guid}/confirm", async (
            Guid id, Guid factId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Result(await FactService.ConfirmAsync(db, providerId, id, factId, DateTimeOffset.UtcNow, ct));
        });

        group.MapPost("external-documents/{id:guid}/facts/{factId:guid}/correct", async (
            Guid id, Guid factId, FactCorrection? body, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Result(await FactService.CorrectAsync(db, providerId, id, factId, body?.Value, DateTimeOffset.UtcNow, ct));
        });

        group.MapPost("external-documents/{id:guid}/facts/{factId:guid}/discard", async (
            Guid id, Guid factId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Result(await FactService.DiscardAsync(db, providerId, id, factId, DateTimeOffset.UtcNow, ct));
        });

        group.MapPost("external-documents/{id:guid}/extract", async (
            Guid id, HttpContext http, AnamnysDbContext db, ReadQueue reads, FactQueue facts, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await FactService.RequestExtractionAsync(db, reads, facts, providerId, id, ct);
            return result.Outcome == FactOutcome.Ok
                ? Results.Accepted(value: new { readStatus = result.ReadStatus })
                : Result(result.Outcome);
        });

        group.MapGet("patients/{patientId:guid}/declared-facts", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await FactService.DeclaredForPatientAsync(db, providerId, patientId, ct) is { } facts
                ? Results.Ok(facts)
                : Results.NotFound();
        });
    }

    private static IResult Result(FactOutcome outcome) => outcome switch
    {
        FactOutcome.Ok => Results.NoContent(),
        FactOutcome.NotFound => Results.NotFound(),
        FactOutcome.Archived => Results.Conflict(new { message = "Paciente arquivado: o prontuário é somente leitura.", code = "archived" }),
        FactOutcome.NotPending => Results.Conflict(new { message = "Este fato já foi revisado.", code = "not_pending" }),
        FactOutcome.NotAvailable => Results.Conflict(new { message = "O documento ainda não está disponível.", code = "not_available" }),
        _ => Results.ValidationProblem(new Dictionary<string, string[]> { ["value"] = ["Valor inválido para este tipo de fato."] }),
    };
}
