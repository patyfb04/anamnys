using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Themes;

// Optional anchor (F-03): a selection { sectionKey, start, end }, a section { sectionKey }, or a quote
// (optional; the suggest dialog no longer returns one). Body only: quotes never go in a URL.
public sealed record AddThemeRequest(
    Guid? TermId, bool? FromSuggestion, string? SectionKey = null, int? Start = null, int? End = null, string? Quote = null)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (TermId is null)
        {
            errors["termId"] = ["Informe o tema."];
        }
        if ((Start is null) != (End is null) || (Start is not null && SectionKey is null))
        {
            errors["start"] = ["Informe a seção e o início e o fim do trecho."];
        }
        if (Quote is { Length: > Excerpts.ExcerptQueries.MaxQuoteLength })
        {
            errors["quote"] = [$"Use no máximo {Excerpts.ExcerptQueries.MaxQuoteLength} caracteres."];
        }
        return errors;
    }
}

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
                    excerptId = i.ExcerptId,
                    excerptKind = i.ExcerptKind,
                }));
        });

        themes.MapPost("", async (
            Guid noteId, AddThemeRequest request, HttpContext http, AnamnysDbContext db, ThemeLocateQueue locates, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await AddAsync(db, locates, providerId, noteId, request, DateTimeOffset.UtcNow, ct)
                : Results.Unauthorized());

        // "Localizar trechos": queues a background locate for the note's tags that only point at a
        // section (F-03); 202, the theme list shows the result.
        themes.MapPost("locate", async (
            Guid noteId, HttpContext http, AnamnysDbContext db, ThemeLocateQueue locates, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await LocateAsync(db, locates, providerId, noteId, ct)
                : Results.Unauthorized());

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
                ThemeSuggestOutcome.Ok => Results.Ok(new
                {
                    terms = result.Terms.Select(t => new { id = t.Id, code = t.Code, label = t.Label }),
                }),
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

    // POST …/themes for an authenticated provider (split out so tests can call it with a real queue).
    internal static async Task<IResult> AddAsync(
        AnamnysDbContext db, ThemeLocateQueue locates, Guid providerId, Guid noteId, AddThemeRequest request,
        DateTimeOffset now, CancellationToken ct)
    {
        var errors = request.Validate();
        if (errors.Count > 0 || request.TermId is not { } termId)
        {
            return Results.ValidationProblem(errors);
        }
        var anchor = new ThemeAnchor(request.SectionKey, request.Start, request.End, request.Quote);
        var result = await NoteThemeService.AddAsync(db, providerId, noteId, termId, request.FromSuggestion ?? false, anchor, now, ct);
        if (result.Outcome != ThemeOutcome.Ok)
        {
            return ToResult(result);
        }
        // No selection (accepted suggestion, plain or section add): the passage is looked for in the
        // background (only in the chosen section when the provider picked one); the highlight appears
        // once it is found.
        if (result.ExcerptKind == "section" && result.Id is { } tagId)
        {
            locates.Enqueue(new ThemeLocateJob(providerId, noteId, tagId, SectionOnly: request.SectionKey is not null));
        }
        return Results.Json(new { id = result.Id, excerptKind = result.ExcerptKind }, statusCode: StatusCodes.Status201Created);
    }

    // POST …/themes/locate for an authenticated provider: checks the note by id (provider-scoped) and
    // the patient's archived state only, then queues.
    internal static async Task<IResult> LocateAsync(
        AnamnysDbContext db, ThemeLocateQueue locates, Guid providerId, Guid noteId, CancellationToken ct)
    {
        switch (await NoteThemeService.PatientArchivedAsync(db, providerId, noteId, ct))
        {
            case null:
                return Results.NotFound();
            case true:
                return Results.Conflict(new { message = "Desarquive o paciente para alterar os temas.", code = "archived" });
        }
        locates.Enqueue(new ThemeLocateJob(providerId, noteId));
        return Results.Accepted(value: new { queued = true });
    }

    private static IResult ToResult(ThemeResult result) => result.Outcome switch
    {
        ThemeOutcome.Archived => Results.Conflict(new { message = "Desarquive o paciente para alterar os temas.", code = "archived" }),
        ThemeOutcome.TermInactive => Results.Conflict(new { message = "Este tema não está mais disponível.", code = "term_inactive" }),
        ThemeOutcome.AlreadyTagged => Results.Conflict(new { message = "Este tema já está na nota.", code = "already_tagged" }),
        ThemeOutcome.InvalidSelection => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["start"] = ["O trecho selecionado não corresponde ao texto atual da nota."],
        }),
        _ => Results.NotFound(),
    };
}
