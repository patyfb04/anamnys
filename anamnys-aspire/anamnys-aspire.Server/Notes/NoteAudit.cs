using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Notes;

// Appends to the audit trail in the caller's unit of work; never saves by itself, so the
// entry commits or rolls back together with the mutation it records.
public static class NoteAudit
{
    public static void Add(
        AnamnysDbContext db, Guid noteId, Guid? actorId, string action,
        string? field, string? previous, string? next, DateTimeOffset at) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            NoteId = noteId,
            ActorId = actorId,
            Action = action,
            FieldChanged = field,
            PreviousValue = previous,
            NewValue = next,
            At = at,
        });
}
