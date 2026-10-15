using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.LiveSession;

[Authorize(AuthenticationSchemes = AuthSchemes.ProviderCookie)]
public sealed class ProgressHub(AnamnysDbContext db, TranscriptionQueue queue) : Hub
{
    public static string Group(Guid noteId) => $"note:{noteId}";

    public async Task<NoteSnapshot?> SubscribeToNote(Guid noteId)
    {
        var providerId = Context.User!.LocalId();
        var note = await db.Notes.AsNoTracking()
            .Where(n => n.Id == noteId && n.ProviderId == providerId)
            .Select(n => new { n.Status, n.FailureReason })
            .SingleOrDefaultAsync(Context.ConnectionAborted);
        if (note is null)
        {
            return null;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(noteId), Context.ConnectionAborted);
        return new NoteSnapshot(note.Status, queue.PositionOf(noteId), note.FailureReason);
    }
}
