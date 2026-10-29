using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Excerpts;
using Anamnys.Server.NoteStructuring;
using Anamnys.Server.Retention;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public enum EditOutcome { Saved, NotFound, Locked, Stale, Invalid }

public record EditResult(EditOutcome Outcome, int Revision);

public enum BlankOutcome { Created, NotFound, Locked, AlreadyHasSections }

public enum SignOutcome { Signed, NotFound, Locked, NoSections, MissingCrp }

// Provider edits of note sections (design/specs/2026-10-13-note-review-and-signing-design.md).
public static class NoteEditing
{
    public const int MaxContentLength = 20_000;

    public static async Task<EditResult> EditSectionAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, string key, string content,
        int expectedRevision, DateTimeOffset now, CancellationToken ct)
    {
        var next = content.Trim();
        if (next.Length > MaxContentLength)
        {
            return new EditResult(EditOutcome.Invalid, 0);
        }

        var result = new EditResult(EditOutcome.NotFound, 0);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            result = new EditResult(EditOutcome.NotFound, 0);
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await NoteLocks.LockAsync(db, noteId, ct);

            var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId && n.ProviderId == providerId, ct);
            if (note is null)
            {
                return;
            }
            if (!IsEditable(note))
            {
                result = new EditResult(EditOutcome.Locked, 0);
                return;
            }
            var section = await db.NoteSections.SingleOrDefaultAsync(s => s.NoteId == noteId && s.Key == key, ct);
            if (section is null)
            {
                return;
            }
            if (section.Revision != expectedRevision)
            {
                result = new EditResult(EditOutcome.Stale, section.Revision);
                return;
            }
            if (section.Content == next)
            {
                result = new EditResult(EditOutcome.Saved, section.Revision);
                return;
            }

            var previous = section.Content;
            section.Content = next;
            section.EditedAt = now;
            section.Revision++;
            note.UpdatedAt = now;
            NoteAudit.Add(db, noteId, providerId, "section_edited", key, previous, next, now);
            await ExcerptService.RelocateSectionAsync(db, noteId, key, next, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            result = new EditResult(EditOutcome.Saved, section.Revision);
        });
        return result;
    }

    public static async Task<BlankOutcome> CreateBlankSectionsAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, DateTimeOffset now, CancellationToken ct)
    {
        var outcome = BlankOutcome.NotFound;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            outcome = BlankOutcome.NotFound;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await NoteLocks.LockAsync(db, noteId, ct);

            var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId && n.ProviderId == providerId, ct);
            if (note is null)
            {
                return;
            }
            if (!IsEditable(note) || !NoteFormats.IsStructurable(note.Format))
            {
                outcome = BlankOutcome.Locked;
                return;
            }
            if (await db.NoteSections.AnyAsync(s => s.NoteId == noteId, ct))
            {
                outcome = BlankOutcome.AlreadyHasSections;
                return;
            }

            AddBlankSections(db, note, providerId, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            outcome = BlankOutcome.Created;
        });
        return outcome;
    }

    internal static void AddBlankSections(AnamnysDbContext db, Note note, Guid providerId, DateTimeOffset now)
    {
        var keys = NoteFormats.KeysFor(note.Format);
        db.NoteSections.AddRange(keys.Select((k, i) => new NoteSection
        {
            Id = Guid.NewGuid(),
            NoteId = note.Id,
            Key = k,
            Content = "",
            Position = i,
            GeneratedAt = now,
            Revision = 0,
        }));
        // A failed structuring is superseded by the provider writing the note by hand.
        note.StructureStatus = "done";
        note.StructureFailure = null;
        note.UpdatedAt = now;
        NoteAudit.Add(db, note.Id, providerId, "sections_created", null, null, string.Join(",", keys), now);
    }

    public static async Task<SignOutcome> SignAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, DateTimeOffset now, CancellationToken ct)
    {
        var outcome = SignOutcome.NotFound;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            outcome = SignOutcome.NotFound;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await NoteLocks.LockAsync(db, noteId, ct);

            var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId && n.ProviderId == providerId, ct);
            if (note is null)
            {
                return;
            }
            if (!IsEditable(note))
            {
                outcome = SignOutcome.Locked;
                return;
            }
            var keys = await db.NoteSections.AsNoTracking()
                .Where(s => s.NoteId == noteId)
                .OrderBy(s => s.Position)
                .Select(s => s.Key)
                .ToListAsync(ct);
            if (keys.Count == 0)
            {
                outcome = SignOutcome.NoSections;
                return;
            }
            // Defence in depth: never make sections of one format permanent under another format's name.
            if (NoteFormats.IsStructurable(note.Format) && !keys.SequenceEqual(NoteFormats.KeysFor(note.Format)))
            {
                outcome = SignOutcome.Locked;
                return;
            }
            var provider = await db.Providers.AsNoTracking()
                .Where(p => p.Id == providerId)
                .Select(p => new { p.CrpNumber, p.CrpRegion })
                .SingleOrDefaultAsync(ct);
            if (provider?.CrpNumber is null || provider.CrpRegion is null)
            {
                outcome = SignOutcome.MissingCrp;
                return;
            }

            var crp = $"{provider.CrpNumber}/{provider.CrpRegion}";
            note.Status = "Signed";
            note.SignedAt = now;
            note.SignedByCrp = crp;
            note.UpdatedAt = now;
            NoteAudit.Add(db, noteId, providerId, "signed", null, null, crp, now);
            await db.SaveChangesAsync(ct);
            await RetentionClock.PushAsync(db, providerId, note.PatientId, now, ct);
            await tx.CommitAsync(ct);
            outcome = SignOutcome.Signed;
        });
        return outcome;
    }

    private static bool IsEditable(Note note) =>
        note.Status == "ReadyForReview" && note.SignedAt == null && note.StructureStatus != "pending";
}
