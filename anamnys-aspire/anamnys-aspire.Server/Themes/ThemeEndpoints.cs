using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Themes;

public sealed record AddThemeRequest(Guid? TermId, bool? FromSuggestion);

public static class ThemeEndpoints
{
    public static void MapThemeEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapGet("providers/me/theme-vocabulary", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is null)
            {
                return Results.Unauthorized();
            }
            var terms = await ThemeVocabularyQuery.ActiveVocabularyAsync(db, ct);
            return Results.Ok(terms.Select(t => new
            {
                id = t.Id,
                code = t.Code,
                label = t.Label,
                parentId = t.ParentId,
                status = t.Status,
                successorIds = t.SuccessorIds,
            }));
        });

        var themes = phi.MapGroup("providers/me/notes/{noteId:guid}/themes");

        themes.MapGet("", async (Guid noteId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var items = await NoteThemeService.ListAsync(db, providerId, noteId, ct);
            return items is null
                ? Results.NotFound()
                : Results.Ok(items.Select(i => new
                {
                    id = i.Id,
                    termId = i.TermId,
                    code = i.Code,
                    label = i.Label,
                    parentLabel = i.ParentLabel,
                    termStatus = i.TermStatus,
                    origin = i.Origin,
                    proposed = i.Proposed,
                    addedAt = i.AddedAt,
                }));
        });

        themes.MapPost("", async (Guid noteId, AddThemeRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            if (request.TermId is not { } termId)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["termId"] = ["Informe o tema."] });
            }
            var result = await NoteThemeService.AddAsync(
                db, providerId, noteId, termId, request.FromSuggestion ?? false, DateTimeOffset.UtcNow, ct);
            return result.Outcome == ThemeOutcome.Ok
                ? Results.Json(new { id = result.Id }, statusCode: StatusCodes.Status201Created)
                : ToResult(result);
        });

        themes.MapPost("suggest", async (
            Guid noteId, HttpContext http, AnamnysDbContext db, IThemeSuggester suggester, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await NoteThemeService.SuggestAsync(
                db, suggester, providerId, noteId, loggers.CreateLogger(typeof(ThemeEndpoints)), ct);
            return result.Outcome switch
            {
                ThemeSuggestOutcome.Ok => Results.Ok(new { terms = result.Terms.Select(t => new { id = t.Id, code = t.Code, label = t.Label }) }),
                ThemeSuggestOutcome.NotFound => Results.NotFound(),
                _ => Results.Json(new { message = "Não foi possível sugerir temas agora. Tente de novo em instantes.", code = "suggest_failed" },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        });

        themes.MapDelete("{tagId:guid}", async (Guid noteId, Guid tagId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await NoteThemeService.RemoveAsync(db, providerId, noteId, tagId, DateTimeOffset.UtcNow, ct);
            return result.Outcome == ThemeOutcome.Ok ? Results.NoContent() : ToResult(result);
        });
    }

    private static IResult ToResult(ThemeResult result) => result.Outcome switch
    {
        ThemeOutcome.Archived => Results.Conflict(new { message = "Desarquive o paciente para alterar os temas.", code = "archived" }),
        ThemeOutcome.TermInactive => Results.Conflict(new { message = "Este tema não está mais disponível.", code = "term_inactive" }),
        ThemeOutcome.AlreadyTagged => Results.Conflict(new { message = "Este tema já está na nota.", code = "already_tagged" }),
        _ => Results.NotFound(),
    };
}
