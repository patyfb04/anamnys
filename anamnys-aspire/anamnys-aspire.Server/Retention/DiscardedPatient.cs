using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace Anamnys.Server.Retention;

// The database refuses any new row for a discarded record (trigger "Patients_not_discarded", SQLSTATE ANDIS, in
// design/migrations/2026-10-28-retention-panel.sql). That includes a write that raced the discard and waited for it.
// For the caller the record no longer exists, so the request ends as 404 wherever it was raised.
public static class DiscardedPatient
{
    public const string SqlState = "ANDIS";

    public static bool Is(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: SqlState })
            {
                return true;
            }
        }
        return false;
    }
}

public sealed class DiscardedPatientExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (!DiscardedPatient.Is(exception) || http.Response.HasStarted)
        {
            return false;
        }
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        await http.Response.WriteAsJsonAsync(new { message = "Este prontuário foi descartado.", code = "discarded" }, ct);
        return true;
    }
}
