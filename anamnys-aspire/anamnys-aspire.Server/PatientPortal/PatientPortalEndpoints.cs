using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authentication;

namespace Anamnys.Server.PatientPortal;

// Patient-portal reads. Patient cookie only: a provider or owner session is not a portal
// account. See design/specs/2026-10-04-patient-portal-sessions-design.md §3.
public static class PatientPortalEndpoints
{
    public static void MapPatientPortalEndpoints(this RouteGroupBuilder phi)
    {
        var me = phi.MapGroup("patients/me");

        me.MapGet("providers", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await AccountIdAsync(http) is { } accountId
                ? Results.Ok(await PatientPortalQueries.ProvidersAsync(db, accountId, DateTimeOffset.UtcNow, ct))
                : Results.Unauthorized());

        me.MapGet("providers/{providerId:guid}/sessions", async (
            Guid providerId, string? scope, int? page, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await AccountIdAsync(http) is not { } accountId)
            {
                return Results.Unauthorized();
            }

            SessionScope? parsed = scope switch
            {
                "upcoming" => SessionScope.Upcoming,
                "past" => SessionScope.Past,
                _ => null,
            };
            var pageNumber = page ?? 1;
            if (parsed is null || pageNumber < 1)
            {
                return Results.BadRequest(new { message = "Use scope=upcoming|past e page >= 1." });
            }

            var result = await PatientPortalQueries.SessionsAsync(db, accountId, providerId, parsed.Value, pageNumber, DateTimeOffset.UtcNow, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }

    internal static async Task<Guid?> AccountIdAsync(HttpContext http)
    {
        var auth = await http.AuthenticateAsync(AuthSchemes.PatientCookie);
        return auth.Succeeded ? auth.Principal!.LocalIdOrNull() : null;
    }
}
