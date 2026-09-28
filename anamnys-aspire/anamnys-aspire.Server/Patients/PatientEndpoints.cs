using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authentication;

namespace Anamnys.Server.Patients;

public static class PatientEndpoints
{
    // POST, not GET: the name and email filters are PHI and must stay out of the URL.
    // The realm is in the path and the provider cookie is authenticated explicitly, as in
    // ProfileEndpoints: one browser can hold a provider and a patient session at once.
    public static void MapPatientEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapPost("providers/me/patients/search", async (
            PatientSearchRequest request,
            HttpContext httpContext,
            AnamnysDbContext db,
            CancellationToken cancellationToken) =>
        {
            var auth = await httpContext.AuthenticateAsync(AuthSchemes.ProviderCookie);
            if (!auth.Succeeded || auth.Principal!.LocalIdOrNull() is not { } providerId)
            {
                return Results.Unauthorized();
            }

            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var response = await PatientSearchQuery.ExecuteAsync(db, providerId, DateTimeOffset.UtcNow, request, cancellationToken);
            return Results.Ok(response);
        });
    }
}
