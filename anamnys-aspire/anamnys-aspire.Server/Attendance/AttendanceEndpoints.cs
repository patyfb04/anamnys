using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Attendance;

public static class AttendanceEndpoints
{
    public static void MapAttendanceEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapGet("patients/{patientId:guid}/attendance", async (
            Guid patientId, DateOnly? from, DateOnly? to, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            if (from > to)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["O início do período deve ser anterior ao fim."] });
            }
            return await AttendanceQuery.SummaryAsync(db, providerId, patientId, from, to, DateTimeOffset.UtcNow, ct) is { } summary
                ? Results.Ok(summary)
                : Results.NotFound();
        });
    }
}
