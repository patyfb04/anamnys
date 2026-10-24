using Anamnys.Server.Data;
using Anamnys.Server.LiveSession;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public sealed record EditSectionRequest(string Content, int ExpectedRevision);

public static class NoteEndpoints
{
    private const string LockedMessage = "A nota está sendo organizada ou já foi assinada.";

    public static void MapNoteEndpoints(this RouteGroupBuilder phi)
    {
        var notes = phi.MapGroup("providers/me/notes");

        notes.MapPost("search", async (NoteSearchRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            if (errors.Count == 0 && request.TermId is { } termId && !await db.ThemeTerms.AnyAsync(t => t.Id == termId, ct))
            {
                errors["termId"] = ["Tema inválido."];
            }
            return errors.Count > 0
                ? Results.ValidationProblem(errors)
                : Results.Ok(await NoteList.SearchAsync(db, providerId, request, ct));
        });

        notes.MapPost("", async (CreateNoteRequest request, HttpContext http, AnamnysDbContext db, TranscriptionQueue queue, CancellationToken ct) =>
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
            var result = await NoteCreation.CreateAsync(db, queue, providerId, request, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                CreateNoteOutcome.Created => Results.Json(new { noteId = result.NoteId }, statusCode: StatusCodes.Status201Created),
                CreateNoteOutcome.NotFound => Results.NotFound(),
                CreateNoteOutcome.Archived => Results.Conflict(new { message = "Desarquive o paciente para criar notas.", code = "archived" }),
                _ => Results.Conflict(new { message = "Esta consulta foi cancelada ou já tem uma nota.", code = "appointment_unavailable" }),
            };
        });

        phi.MapGet("providers/me/patients/{patientId:guid}/note-candidates", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var candidates = await NoteCreation.CandidatesAsync(db, providerId, patientId, DateTimeOffset.UtcNow, ct);
            return candidates is null ? Results.NotFound() : Results.Ok(candidates);
        });

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

        notes.MapPost("{noteId:guid}/export", async (Guid noteId, HttpContext http, AnamnysDbContext db, INoteFileStore store, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await NoteExport.ExportAsync(db, store, providerId, noteId, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                ExportOutcome.Ok => Results.Ok(new { url = result.Url, expiresAt = result.ExpiresAt }),
                ExportOutcome.NotFound => Results.NotFound(),
                ExportOutcome.NotSigned => Results.Conflict(new { message = "Assine a nota antes de exportar.", code = "not_signed" }),
                _ => Results.Json(new { message = "Armazenamento indisponível. Tente de novo em instantes.", code = "storage_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        });
    }
}
