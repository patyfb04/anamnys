using Anamnys.Server.Data;
using Anamnys.Server.LiveSession;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Notes;

public sealed record EditSectionRequest(string Content, int ExpectedRevision);

public static class NoteEndpoints
{
    private const string LockedMessage = "A nota está sendo organizada ou já foi assinada.";

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

        notes.MapPut("{noteId:guid}/sections/{key}", async (Guid noteId, string key, EditSectionRequest request, HttpContext http,
            AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            if (request.Content is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = ["Conteúdo inválido."] });
            }
            var result = await NoteEditing.EditSectionAsync(db, providerId, noteId, key, request.Content, request.ExpectedRevision, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                EditOutcome.Saved => Results.Ok(new { revision = result.Revision }),
                EditOutcome.NotFound => Results.NotFound(),
                EditOutcome.Locked => Results.Conflict(new { message = LockedMessage, reason = "locked" }),
                EditOutcome.Stale => Results.Conflict(new { message = "Esta seção foi alterada em outra janela.", reason = "stale" }),
                _ => Results.ValidationProblem(new Dictionary<string, string[]> { ["content"] = ["Conteúdo inválido."] }),
            };
        });

        notes.MapPost("{noteId:guid}/sections/blank", async (Guid noteId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await NoteEditing.CreateBlankSectionsAsync(db, providerId, noteId, DateTimeOffset.UtcNow, ct) switch
            {
                BlankOutcome.Created => Results.StatusCode(StatusCodes.Status201Created),
                BlankOutcome.NotFound => Results.NotFound(),
                BlankOutcome.Locked => Results.Conflict(new { message = LockedMessage }),
                _ => Results.Conflict(new { message = "A nota já tem seções." }),
            };
        });

        notes.MapPost("{noteId:guid}/sign", async (Guid noteId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await NoteEditing.SignAsync(db, providerId, noteId, DateTimeOffset.UtcNow, ct) switch
            {
                SignOutcome.Signed => Results.Ok(),
                SignOutcome.NotFound => Results.NotFound(),
                SignOutcome.Locked => Results.Conflict(new { message = LockedMessage, reason = "locked" }),
                SignOutcome.NoSections => Results.Conflict(new { message = "A nota não tem seções para assinar.", reason = "no_sections" }),
                _ => Results.UnprocessableEntity(new { message = "Cadastre seu CRP no perfil para assinar.", reason = "missing_crp" }),
            };
        });
    }
}
