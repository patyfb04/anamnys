using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Documents;

// Appends to the audit trail in the caller's unit of work; never saves by itself, so the
// entry commits or rolls back together with the mutation it records.
public static class DocumentAudit
{
    public static void Add(
        AnamnysDbContext db, Guid documentId, Guid? actorId, string action,
        string? field, string? previous, string? next, DateTimeOffset at) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            ActorId = actorId,
            Action = action,
            FieldChanged = field,
            PreviousValue = previous,
            NewValue = next,
            At = at,
        });
}

// Serialises every mutation of one document: callers take the row lock first thing inside
// their transaction, then check conditions.
public static class DocumentLocks
{
    public static Task LockAsync(AnamnysDbContext db, Guid providerId, Guid documentId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM \"ClinicalDocuments\" WHERE \"Id\" = {documentId} AND \"ProviderId\" = {providerId} FOR UPDATE", ct);
}
