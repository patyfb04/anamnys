using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Retention;

// The daily run (design/specs/2026-10-28-retention-panel-design.md §1, §3): recompute every clock, then one in-app
// bell notification per new item of the unsigned-notes and retention blocks. The dedupe key names the item
// (unsigned:{noteId}, retention:{patientId}:{retentionUntil}) and is unique per provider, so a re-run, a second
// instance or a later day never repeats one; a moved deadline is a new item. No e-mail. providerId narrows a run
// to one provider (tests); production passes null.
public static class RetentionJobSteps
{
    public const string UnsignedKind = "unsigned_note";
    public const string RetentionKind = "retention_due";
    public const string NoteSubject = "note";
    public const string PatientSubject = "patient";

    // Returns the number of notifications created.
    public static async Task<int> RunAsync(AnamnysDbContext db, DateTimeOffset now, CancellationToken ct, Guid? providerId = null)
    {
        await RetentionClock.RecomputeAsync(db, ct, providerId: providerId);

        var providers = await db.Providers.AsNoTracking()
            .Where(p => providerId == null || p.Id == providerId)
            .Select(p => new { p.Id, p.UnsignedNoteDays })
            .ToListAsync(ct);
        var created = 0;
        foreach (var provider in providers)
        {
            foreach (var note in await ComplianceQuery.UnsignedNotesAsync(db, provider.Id, provider.UnsignedNoteDays, now, ct))
            {
                created += await NotifyAsync(db, provider.Id, UnsignedKind, NoteSubject, note.NoteId, $"unsigned:{note.NoteId}", now, ct);
            }
            foreach (var item in await ComplianceQuery.RetentionAsync(db, provider.Id, now, ct))
            {
                created += await NotifyAsync(db, provider.Id, RetentionKind, PatientSubject, item.PatientId,
                    $"retention:{item.PatientId}:{item.RetentionUntil:yyyy-MM-dd}", now, ct);
            }
        }
        return created;
    }

    private static Task<int> NotifyAsync(
        AnamnysDbContext db, Guid providerId, string kind, string subjectType, Guid subjectId, string dedupeKey, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Notifications" ("ProviderId","Kind","SubjectType","SubjectId","Channel","ScheduledFor","SentAt","DeliveryStatus","DedupeKey")
            VALUES ({providerId}, {kind}, {subjectType}, {subjectId}, 'in_app', {nowUtc}, {nowUtc}, 'sent', {dedupeKey})
            ON CONFLICT ("ProviderId","DedupeKey") WHERE "DedupeKey" IS NOT NULL DO NOTHING
            """, ct);
    }
}
