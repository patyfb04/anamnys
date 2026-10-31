using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Analysis;

// F-15 (design/specs/2026-10-30-patient-quotes-design.md §3). A patient, note or quote of another provider, or of a
// discarded record, is 404; an archived record is read-only (409 code "archived"), its quotes stay readable.
public static class PatientQuotesEndpoints
{
    public static void MapPatientQuotesEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapPost("notes/{noteId:guid}/quotes", async (Guid noteId, RecordQuoteRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            if (!request.IsComplete)
            {
                return ToResult(QuoteOutcome.InvalidSelection);
            }
            var result = await PatientQuotesService.RecordAsync(db, providerId, noteId, request, DateTimeOffset.UtcNow, ct);
            return result.Outcome == QuoteOutcome.Ok
                ? Results.Json(result.Quote, statusCode: StatusCodes.Status201Created)
                : ToResult(result.Outcome);
        });

        group.MapDelete("quotes/{id:guid}", async (Guid id, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return ToResult(await PatientQuotesService.RemoveAsync(db, providerId, id, DateTimeOffset.UtcNow, ct));
        });

        group.MapGet("notes/{noteId:guid}/quotes", async (Guid noteId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await PatientQuotesQuery.ForNoteAsync(db, providerId, noteId, ct) is { } items
                ? Results.Ok(new { items })
                : Results.NotFound();
        });

        group.MapGet("patients/{patientId:guid}/quotes", async (
            Guid patientId, DateOnly? from, DateOnly? to, Guid? termId, int? page, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            if (from > to)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["O início do período deve ser anterior ao fim."] });
            }
            return await PatientQuotesQuery.ListAsync(db, providerId, patientId, from, to, termId, page, ct) is { } quotes
                ? Results.Ok(quotes)
                : Results.NotFound();
        });
    }

    private static IResult ToResult(QuoteOutcome outcome) => outcome switch
    {
        QuoteOutcome.Ok => Results.NoContent(),
        QuoteOutcome.Archived => Results.Conflict(new { message = "Paciente arquivado: o prontuário é somente leitura.", code = "archived" }),
        QuoteOutcome.NoteNotReady => Results.Conflict(new { message = "Só é possível marcar falas em notas em revisão ou assinadas.", code = "note_not_ready" }),
        QuoteOutcome.InvalidSelection => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["offsetStart"] = ["O trecho selecionado não corresponde ao texto atual da nota."] },
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_selection" }),
        QuoteOutcome.TermInactive => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["termId"] = ["Escolha um tema ativo do vocabulário."] },
            extensions: new Dictionary<string, object?> { ["code"] = "term_inactive" }),
        _ => Results.NotFound(),
    };
}
