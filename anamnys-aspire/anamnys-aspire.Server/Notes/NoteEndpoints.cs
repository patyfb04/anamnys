using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Notes;

public static class NoteEndpoints
{
    public static void MapNoteEndpoints(this RouteGroupBuilder phi)
    {
        var notes = phi.MapGroup("providers/me/notes");

        notes.MapGet("{noteId:guid}", async (Guid noteId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var note = await NoteQueries.GetAsync(db, providerId, noteId, ct);
            return note is null ? Results.NotFound() : Results.Ok(note);
        });
    }
}
