using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Microsoft.AspNetCore.Mvc;

namespace Anamnys.Server.Appointments;

// See design/specs/2026-10-05-provider-calendar-design.md §3. Handlers are thin: resolve the
// provider from the session, validate, delegate to Appointments, map the outcome.
public static class AppointmentEndpoints
{
    public static void MapAppointmentEndpoints(this RouteGroupBuilder phi)
    {
        var appointments = phi.MapGroup("providers/me/appointments");

        appointments.MapGet("", async (
            DateTimeOffset? from, DateTimeOffset? to, [FromQuery(Name = "status")] string[]? status,
            HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = AppointmentRange.Validate(from, to, status);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return Results.Ok(await Appointments.ListAsync(db, providerId, from!.Value, to!.Value, status ?? [], ct));
        });

        appointments.MapPost("", async (CreateAppointmentRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
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
            var (outcome, id) = await Appointments.CreateAsync(db, providerId, request, DateTimeOffset.UtcNow, ct);
            return outcome == AppointmentOutcome.Ok
                ? Results.Created($"/api/phi/providers/me/appointments/{id}", new { id })
                : ToResult(outcome);
        });

        appointments.MapPut("{appointmentId:guid}", async (Guid appointmentId, UpdateAppointmentRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
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
            return ToResult(await Appointments.UpdateAsync(db, providerId, appointmentId, request, DateTimeOffset.UtcNow, ct));
        });

        appointments.MapPost("{appointmentId:guid}/status", async (Guid appointmentId, ChangeAppointmentStatusRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
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
            return ToResult(await Appointments.SetStatusAsync(db, providerId, appointmentId, request, DateTimeOffset.UtcNow, ct));
        });
    }

    private static IResult ToResult(AppointmentOutcome outcome) => outcome switch
    {
        AppointmentOutcome.Ok => Results.NoContent(),
        AppointmentOutcome.Overlap => Results.Conflict(new { message = "Horário já ocupado." }),
        AppointmentOutcome.InvalidTransition => Results.Conflict(new { message = "Esta alteração não é permitida no status atual." }),
        AppointmentOutcome.Changed => Results.Conflict(new { message = "A consulta mudou enquanto você editava. Atualize e tente de novo." }),
        AppointmentOutcome.NotStarted => Results.Conflict(new { message = "A consulta ainda não começou." }),
        AppointmentOutcome.PatientArchived => Results.UnprocessableEntity(new { message = "Paciente arquivado." }),
        _ => Results.NotFound(),
    };
}
