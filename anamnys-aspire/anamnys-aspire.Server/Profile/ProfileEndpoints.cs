using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Profile;

public static class ProfileEndpoints
{
    // The realm is in the path rather than inferred from "whichever cookie
    // authenticated": one browser can hold a provider and a patient session at once
    // (distinct cookie names, all at Path=/, all sent to /api), and guessing could edit
    // the wrong person's profile. Each handler authenticates its own realm's cookie.
    public static void MapProfileEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapGet("providers/me/profile", async (HttpContext httpContext, AnamnysDbContext db, CancellationToken cancellationToken) =>
        {
            if (await LocalIdForAsync(httpContext, AuthSchemes.ProviderCookie) is not { } localId)
            {
                return Results.Unauthorized();
            }

            var provider = await db.Providers.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
            return provider is null
                ? Results.NotFound()
                : Results.Ok(new ProviderProfileResponse(provider.Email, provider.Name, provider.CrpNumber, provider.CrpRegion));
        });

        phi.MapPut("providers/me/profile", async (
            UpdateProviderProfileRequest request,
            HttpContext httpContext,
            AnamnysDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (await LocalIdForAsync(httpContext, AuthSchemes.ProviderCookie) is not { } localId)
            {
                return Results.Unauthorized();
            }

            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var provider = await db.Providers.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
            if (provider is null)
            {
                return Results.NotFound();
            }

            provider.Name = ProfileText.Clean(request.Name)!;
            provider.CrpNumber = ProfileText.Clean(request.CrpNumber);
            provider.CrpRegion = ProfileText.Clean(request.CrpRegion);
            provider.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        phi.MapGet("patients/me/profile", async (HttpContext httpContext, AnamnysDbContext db, CancellationToken cancellationToken) =>
        {
            if (await LocalIdForAsync(httpContext, AuthSchemes.PatientCookie) is not { } localId)
            {
                return Results.Unauthorized();
            }

            var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
            return patient is null
                ? Results.NotFound()
                : Results.Ok(new PatientProfileResponse(
                    patient.Email, patient.FirstName, patient.LastName, patient.Phone, patient.DateOfBirth));
        });

        phi.MapPut("patients/me/profile", async (
            UpdatePatientProfileRequest request,
            HttpContext httpContext,
            AnamnysDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (await LocalIdForAsync(httpContext, AuthSchemes.PatientCookie) is not { } localId)
            {
                return Results.Unauthorized();
            }

            var errors = request.Validate(DateOnly.FromDateTime(DateTime.UtcNow));
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
            if (patient is null)
            {
                return Results.NotFound();
            }

            patient.FirstName = ProfileText.Clean(request.FirstName)!;
            patient.LastName = ProfileText.Clean(request.LastName)!;
            patient.Phone = ProfileText.Clean(request.Phone);
            patient.DateOfBirth = request.DateOfBirth;
            patient.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });
    }

    private static async Task<Guid?> LocalIdForAsync(HttpContext httpContext, string cookieScheme)
    {
        var result = await httpContext.AuthenticateAsync(cookieScheme);
        return result.Succeeded ? result.Principal!.LocalIdOrNull() : null;
    }
}
