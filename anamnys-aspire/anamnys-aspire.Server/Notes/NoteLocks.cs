using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

// Serialises every mutation of one note: callers take the row lock first thing inside their
// transaction, then check conditions (design/specs/2026-10-13-note-review-and-signing-design.md).
public static class NoteLocks
{
    public static Task LockAsync(AnamnysDbContext db, Guid noteId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT 1 FROM \"Notes\" WHERE \"Id\" = {noteId} FOR UPDATE", ct);

    // For a write that adds an ExcerptRef or a row to a note: taken before LockAsync, so it follows the discard's order
    // (patient row, then notes). The discard deletes the references early and holds the patient FOR UPDATE, so without this a
    // reference added after that delete would survive it with its quote. FOR KEY SHARE waits for an open discard and makes a
    // new one wait for this transaction, and conflicts with nothing else (like the Patients_not_discarded trigger). The checks
    // that follow run after the wait: once a discard has committed, the note no longer exists and they answer not found.
    public static Task LockPatientAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            SELECT 1 FROM "Patients"
             WHERE "ProviderId" = {providerId}
               AND "Id" = (SELECT "PatientId" FROM "Notes" WHERE "Id" = {noteId} AND "ProviderId" = {providerId})
               FOR KEY SHARE
            """, ct);
}
