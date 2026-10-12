using Anamnys.Server.Data;
using Anamnys.Server.LiveSession;
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

        notes.MapPost("{noteId:guid}/restructure", async (Guid noteId, RestructureRequest request, HttpContext http,
            AnamnysDbContext db, TranscriptionQueue queue, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await NoteRestructure.RequestAsync(db, queue, providerId, noteId, request.Format, DateTimeOffset.UtcNow, ct) switch
            {
                RestructureOutcome.Accepted => Results.Accepted(),
                RestructureOutcome.NotFound => Results.NotFound(),
                RestructureOutcome.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["Formato inválido."] }),
                _ => Results.Conflict(new { message = "A nota não pode ser reorganizada agora." }),
            };
        });
    }
}
