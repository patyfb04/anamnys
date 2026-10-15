using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

// Serialises every mutation of one note: callers take the row lock first thing inside their
// transaction, then check conditions (design/specs/2026-10-13-note-review-and-signing-design.md).
public static class NoteLocks
{
    public static Task LockAsync(AnamnysDbContext db, Guid noteId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Notes\" WHERE \"Id\" = {noteId} FOR UPDATE", ct);
}
