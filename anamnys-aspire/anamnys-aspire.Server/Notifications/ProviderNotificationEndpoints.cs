using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Notifications;

// Thin handlers: resolve the provider from the session, validate, delegate.
public static class ProviderNotificationEndpoints
{
    public static void MapProviderNotificationEndpoints(this RouteGroupBuilder phi)
    {
        var notifications = phi.MapGroup("providers/me/notifications");

        notifications.MapGet("", async (int? page, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await ProviderNotifications.ListAsync(db, providerId, page ?? 1, ct));
        });

        notifications.MapPost("{id:guid}/read", async (Guid id, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await ProviderNotifications.MarkReadAsync(db, providerId, id, DateTimeOffset.UtcNow, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        notifications.MapPost("read-all", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            await ProviderNotifications.MarkAllReadAsync(db, providerId, DateTimeOffset.UtcNow, ct);
            return Results.NoContent();
        });

        var policy = phi.MapGroup("providers/me/booking-policy");

        policy.MapGet("", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await BookingPolicies.GetAsync(db, providerId, ct));
        });

        policy.MapPut("", async (BookingPolicyRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            await BookingPolicies.SaveAsync(db, providerId, request, ct);
            return Results.NoContent();
        });
    }
}
