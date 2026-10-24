using System.Security.Claims;
using Anamnys.Server.Auth;
using Anamnys.Server.Data;

namespace Anamnys.Server.Themes;

public sealed record CreateThemeTermRequest(string? Code, string? Label, Guid? ParentId);

public sealed record RenameThemeTermRequest(string? Label);

public sealed record SplitThemeTermRequest(List<NewTermInput>? Terms);

public sealed record MergeThemeTermsRequest(List<Guid>? SourceIds, MergeTargetInput? Target);

public static class ThemeAdminEndpoints
{
    // Reads are open to every staff role (the admin group already requires one); mutations need owner or ops.
    public static void MapThemeAdminEndpoints(this RouteGroupBuilder admin)
    {
        var vocabulary = admin.MapGroup("theme-vocabulary");

        vocabulary.MapGet("", async (AnamnysDbContext db, CancellationToken ct) =>
        {
            var terms = await ThemeAdminService.ListAsync(db, ct);
            return Results.Ok(terms.Select(t => new
            {
                id = t.Id,
                code = t.Code,
                label = t.Label,
                parentId = t.ParentId,
                status = t.Status,
                introducedOn = t.IntroducedOn,
                successorIds = t.SuccessorIds,
                activeTagCount = t.ActiveTagCount,
            }));
        });

        var edit = vocabulary.MapGroup("")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(AuthSchemes.OwnerCookie)
                .RequireAuthenticatedUser()
                .RequireClaim("roles", "owner", "ops"));

        edit.MapPost("terms", async (
            CreateThemeTermRequest request, ClaimsPrincipal principal, AnamnysDbContext db, ThemeWarmupSignal warmup, CancellationToken ct) =>
        {
            if (principal.LocalIdOrNull() is not { } staffId)
            {
                return Results.Unauthorized();
            }
            var result = await ThemeAdminService.CreateTermAsync(
                db, staffId, new NewTermInput(request.Code, request.Label), request.ParentId, DateTimeOffset.UtcNow, ct);
            if (result.Outcome == ThemeAdminOutcome.Ok)
            {
                warmup.Request();
            }
            return result.Outcome == ThemeAdminOutcome.Ok
                ? Results.Json(new { id = result.Ids![0] }, statusCode: StatusCodes.Status201Created)
                : ToResult(result);
        });

        edit.MapPut("terms/{id:guid}/label", async (
            Guid id, RenameThemeTermRequest request, ClaimsPrincipal principal, AnamnysDbContext db, ThemeWarmupSignal warmup, CancellationToken ct) =>
        {
            if (principal.LocalIdOrNull() is not { } staffId)
            {
                return Results.Unauthorized();
            }
            var result = await ThemeAdminService.RenameAsync(db, staffId, id, request.Label, DateTimeOffset.UtcNow, ct);
            if (result.Outcome == ThemeAdminOutcome.Ok)
            {
                warmup.Request();
            }
            return result.Outcome == ThemeAdminOutcome.Ok ? Results.NoContent() : ToResult(result);
        });

        edit.MapPost("terms/{id:guid}/retire", async (
            Guid id, ClaimsPrincipal principal, AnamnysDbContext db, ThemeWarmupSignal warmup, CancellationToken ct) =>
        {
            if (principal.LocalIdOrNull() is not { } staffId)
            {
                return Results.Unauthorized();
            }
            var result = await ThemeAdminService.RetireAsync(db, staffId, id, DateTimeOffset.UtcNow, ct);
            if (result.Outcome == ThemeAdminOutcome.Ok)
            {
                warmup.Request();
            }
            return result.Outcome == ThemeAdminOutcome.Ok ? Results.NoContent() : ToResult(result);
        });

        edit.MapPost("terms/{id:guid}/split", async (
            Guid id, SplitThemeTermRequest request, ClaimsPrincipal principal, AnamnysDbContext db, ThemeWarmupSignal warmup, CancellationToken ct) =>
        {
            if (principal.LocalIdOrNull() is not { } staffId)
            {
                return Results.Unauthorized();
            }
            var result = await ThemeAdminService.SplitAsync(db, staffId, id, request.Terms, DateTimeOffset.UtcNow, ct);
            if (result.Outcome == ThemeAdminOutcome.Ok)
            {
                warmup.Request();
            }
            return result.Outcome == ThemeAdminOutcome.Ok ? Results.Ok(new { ids = result.Ids }) : ToResult(result);
        });

        edit.MapPost("merge", async (
            MergeThemeTermsRequest request, ClaimsPrincipal principal, AnamnysDbContext db, ThemeWarmupSignal warmup, CancellationToken ct) =>
        {
            if (principal.LocalIdOrNull() is not { } staffId)
            {
                return Results.Unauthorized();
            }
            var result = await ThemeAdminService.MergeAsync(db, staffId, request.SourceIds, request.Target, DateTimeOffset.UtcNow, ct);
            if (result.Outcome == ThemeAdminOutcome.Ok)
            {
                warmup.Request();
            }
            return result.Outcome == ThemeAdminOutcome.Ok ? Results.Ok(new { id = result.Ids![0] }) : ToResult(result);
        });
    }

    private static IResult ToResult(ThemeAdminResult result) => result.Outcome switch
    {
        ThemeAdminOutcome.Invalid => Results.ValidationProblem(result.Errors!.ToDictionary(e => e.Key, e => e.Value)),
        ThemeAdminOutcome.Conflict => Results.Conflict(new { code = result.Code }),
        _ => Results.NotFound(),
    };
}
