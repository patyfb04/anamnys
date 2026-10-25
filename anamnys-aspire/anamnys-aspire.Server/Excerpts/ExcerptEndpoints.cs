using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Excerpts;

public static class ExcerptEndpoints
{
    public static void MapExcerptEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapGet("providers/me/excerpts/{id:guid}", async (Guid id, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var passage = await ExcerptService.GetPassageAsync(db, providerId, id, ct);
            return passage is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    id = passage.Id,
                    kind = passage.Kind,
                    noteId = passage.NoteId,
                    patientId = passage.PatientId,
                    sessionStartsAt = passage.SessionStartsAt,
                    datedAt = passage.DatedAt,
                    sectionKey = passage.SectionKey,
                    sectionTitle = passage.SectionTitle,
                    sectionText = passage.SectionText,
                    start = passage.Start,
                    end = passage.End,
                });
        });
    }
}
