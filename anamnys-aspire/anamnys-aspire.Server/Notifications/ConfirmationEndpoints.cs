using Anamnys.Server.Data;
using Anamnys.Server.PatientPortal;

namespace Anamnys.Server.Notifications;

public static class ConfirmationEndpoints
{
    public const string RateLimitPolicy = "public";

    public static void MapConfirmationEndpoints(this WebApplication app, RouteGroupBuilder phi)
    {
        var confirmations = app.MapGroup("/api/public/appointment-confirmations").RequireRateLimiting(RateLimitPolicy);

        confirmations.MapGet("{token}", async (string token, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await AppointmentConfirmations.ViewAsync(db, token, clock.GetUtcNow(), ct)));

        confirmations.MapPost("{token}", async (string token, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await AppointmentConfirmations.ConfirmByTokenAsync(db, token, clock.GetUtcNow(), ct) == TokenConfirmOutcome.Confirmed
                ? Results.NoContent()
                : Results.Json(new { message = "Este link não é mais válido." }, statusCode: StatusCodes.Status410Gone));

        phi.MapPost("patients/me/appointments/{appointmentId:guid}/confirm", async (
            Guid appointmentId, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (await PatientPortalEndpoints.AccountIdAsync(http) is not { } accountId)
            {
                return Results.Unauthorized();
            }
            return await AppointmentConfirmations.ConfirmFromPortalAsync(db, accountId, appointmentId, clock.GetUtcNow(), ct) switch
            {
                PortalConfirmOutcome.Ok => Results.NoContent(),
                PortalConfirmOutcome.InvalidState => Results.Conflict(new { message = "Esta consulta não pode mais ser confirmada." }),
                _ => Results.NotFound(),
            };
        });
    }
}
