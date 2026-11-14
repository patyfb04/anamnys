using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notes;
using Anamnys.Server.NoteStructuring;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Intakes;

// The Anamnese note of an intake (design/specs/2026-11-12-structured-intake-design.md §3 Note generation). One section
// per template section with an enabled question, in template order, keyed by SectionKey; content is "Label: answer"
// lines. The note is reviewed and signed like any note; NoteEditing.SignAsync calls SignAsync below in its transaction.
// Once the note exists the answers are locked (they are what the note says); "Editar respostas" (UnlockAsync) deletes the
// unsigned note and unlocks them, and discarding a draft intake deletes it with its note. Lock order: the patient row
// (key share, like every write that adds or removes note rows; NoteLocks), the note, then the intake — signing takes the
// note and then the intake, so the order agrees.
public static class IntakeNote
{
    public static Task<IntakeResult<IntakeNoteResponse>> GenerateAsync(
        AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct) =>
        WithLocksAsync(db, providerId, id, async intake =>
        {
            if (intake.Status != IntakeStatus.Draft)
            {
                return new IntakeResult<IntakeNoteResponse>(IntakeOutcome.NotDraft);
            }
            if (intake.NoteId is { } existing)
            {
                // Generated already: the provider may be editing it, so it is never rewritten here.
                return new(IntakeOutcome.Ok, new IntakeNoteResponse(existing));
            }
            if (await IntakeService.PatientArchivedAsync(db, intake.PatientId, ct))
            {
                return new(IntakeOutcome.PatientArchived);
            }
            var questions = await db.IntakeQuestions.AsNoTracking()
                .Where(q => q.TemplateId == intake.TemplateId && !q.Disabled)
                .ToListAsync(ct);
            var answers = await AnswersAsync(db, intake.Id, ct);
            var missing = questions.Where(q => q.Required && !answers.ContainsKey(q.Id)).OrderBy(q => q.Position).Select(q => q.Id).ToList();
            if (missing.Count > 0)
            {
                return new(IntakeOutcome.RequiredMissing, QuestionIds: missing);
            }
            var sections = Contents(questions, answers);
            var note = new Note
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                PatientId = intake.PatientId,
                Status = "ReadyForReview",
                InputMode = "Text",
                Format = NoteFormats.Anamnese,
                IntakeId = intake.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Notes.Add(note);
            await db.SaveChangesAsync(ct);
            NoteAudit.Add(db, note.Id, providerId, "note_created", null, null, $"Text,{NoteFormats.Anamnese}", now);
            db.NoteSections.AddRange(sections.Select((s, i) => new NoteSection
            {
                Id = Guid.NewGuid(),
                NoteId = note.Id,
                Key = s.Key,
                Content = s.Content,
                Position = i,
                GeneratedAt = now,
                Revision = 0,
            }));
            NoteAudit.Add(db, note.Id, providerId, "sections_created", null, null, string.Join(",", sections.Select(s => s.Key)), now);
            intake.NoteId = note.Id;
            intake.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return new(IntakeOutcome.Ok, new IntakeNoteResponse(note.Id));
        }, ct);

    // "Editar respostas": deletes the unsigned note (its edits are lost; the UI warns) so the answers can change again.
    // Without a note it only answers Ok.
    public static Task<IntakeResult<bool>> UnlockAsync(
        AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct) =>
        WithLocksAsync(db, providerId, id, async intake =>
        {
            if (intake.Status != IntakeStatus.Draft)
            {
                return new IntakeResult<bool>(IntakeOutcome.NotDraft);
            }
            if (intake.NoteId is { } noteId)
            {
                if (!await DeleteDraftNoteAsync(db, providerId, noteId, ct))
                {
                    return new(IntakeOutcome.NotDraft);
                }
                intake.NoteId = null;
                intake.UpdatedAt = now;
                IntakeRules.Audit(db, providerId, IntakeRules.UnlockedAction, $"{intake.Id} note {noteId} deleted", now);
                await db.SaveChangesAsync(ct);
            }
            return new(IntakeOutcome.Ok, true);
        }, ct);

    // Discards a draft intake with its answers and its unsigned note, so a stale draft never blocks a new intake.
    public static Task<IntakeResult<bool>> DiscardAsync(
        AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct) =>
        WithLocksAsync(db, providerId, id, async intake =>
        {
            if (intake.Status != IntakeStatus.Draft)
            {
                return new IntakeResult<bool>(IntakeOutcome.NotDraft);
            }
            if (intake.NoteId is { } noteId && !await DeleteDraftNoteAsync(db, providerId, noteId, ct))
            {
                return new(IntakeOutcome.NotDraft);
            }
            var answers = await db.IntakeResponses.CountAsync(r => r.IntakeId == intake.Id, ct);
            db.Intakes.Remove(intake);
            IntakeRules.Audit(db, providerId, IntakeRules.DiscardedAction,
                $"{intake.Id} patient {intake.PatientId} note {(object?)intake.NoteId ?? "-"} answers {answers}", now);
            await db.SaveChangesAsync(ct);
            return new(IntakeOutcome.Ok, true);
        }, ct);

    // Called by NoteEditing.SignAsync for a note with IntakeId, inside its transaction and after its note lock. False
    // (the note stays unsigned) when the intake is not a draft of this provider or the note's section keys are not the
    // template's. Otherwise marks the intake signed, creates one pending transfer per answered question with a record
    // destination whose answer the signed text still shows, and audits intake_signed; the caller saves, seals and commits.
    public static async Task<bool> SignAsync(
        AnamnysDbContext db, Guid providerId, Note note, IReadOnlyList<string> sectionKeys, DateTimeOffset now, CancellationToken ct)
    {
        if (note.IntakeId is not { } intakeId)
        {
            return false;
        }
        await IntakeService.LockAsync(db, intakeId, ct);
        var intake = await db.Intakes.SingleOrDefaultAsync(
            i => i.Id == intakeId && i.ProviderId == providerId && i.PatientId == note.PatientId && i.NoteId == note.Id, ct);
        if (intake is null || intake.Status != IntakeStatus.Draft)
        {
            return false;
        }
        var questions = await db.IntakeQuestions.AsNoTracking()
            .Where(q => q.TemplateId == intake.TemplateId && !q.Disabled)
            .ToListAsync(ct);
        if (!sectionKeys.SequenceEqual(SectionKeys(questions), StringComparer.Ordinal))
        {
            return false;
        }
        var answers = await AnswersAsync(db, intake.Id, ct);
        var texts = await db.NoteSections.AsNoTracking()
            .Where(s => s.NoteId == note.Id)
            .ToDictionaryAsync(s => s.Key, s => s.Content, ct);
        // The answers are locked once the note exists, so they match the generated text; an answer whose whole
        // "Label: answer" line the provider then changed or removed in the editor is not offered for the record (it was
        // not signed as generated).
        var transfers = questions
            .Where(q => q.TargetField is not null && answers.ContainsKey(q.Id)
                && texts.TryGetValue(q.SectionKey, out var text)
                && ContainsLine(text, Line(q.Label, q.AnswerType, answers[q.Id])))
            .OrderBy(q => q.Position)
            .Select(q => new IntakeTransfer
            {
                Id = Guid.NewGuid(),
                IntakeId = intake.Id,
                QuestionId = q.Id,
                TargetField = q.TargetField!,
                Value = answers[q.Id],
                Status = IntakeTransferStatus.Pending,
                CreatedAt = now,
            })
            .ToList();
        db.IntakeTransfers.AddRange(transfers);
        intake.Status = IntakeStatus.Signed;
        intake.SignedAt = now;
        intake.UpdatedAt = now;
        IntakeRules.Audit(db, providerId, IntakeRules.SignedAction, $"{intake.Id} note {note.Id} transfers {transfers.Count}", now);
        return true;
    }

    // The section titles of an intake note's template, by section key; null when the note has no intake.
    public static async Task<IReadOnlyDictionary<string, string>?> SectionTitlesAsync(
        AnamnysDbContext db, Guid providerId, Guid? intakeId, CancellationToken ct)
    {
        if (intakeId is not { } id)
        {
            return null;
        }
        var rows = await (
            from i in db.Intakes.AsNoTracking()
            join q in db.IntakeQuestions.AsNoTracking() on i.TemplateId equals q.TemplateId
            where i.Id == id && i.ProviderId == providerId
            orderby q.Position
            select new { q.SectionKey, q.SectionTitle }).ToListAsync(ct);
        return rows.GroupBy(r => r.SectionKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().SectionTitle, StringComparer.Ordinal);
    }

    // The same titles for many notes at once (dossier, account export): note id -> section key -> title, only for the
    // given notes of this provider that came from an intake. Other notes are absent and keep the fixed titles.
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> SectionTitlesByNoteAsync(
        AnamnysDbContext db, Guid providerId, IReadOnlyCollection<Guid> noteIds, CancellationToken ct)
    {
        if (noteIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        }
        var ids = noteIds.Distinct().ToList();
        var rows = await (
            from n in db.Notes.AsNoTracking()
            join i in db.Intakes.AsNoTracking() on n.IntakeId equals (Guid?)i.Id
            join q in db.IntakeQuestions.AsNoTracking() on i.TemplateId equals q.TemplateId
            where n.ProviderId == providerId && i.ProviderId == providerId && ids.Contains(n.Id)
            select new { NoteId = n.Id, q.Position, q.SectionKey, q.SectionTitle }).ToListAsync(ct);
        return rows.GroupBy(r => r.NoteId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, string>)g.OrderBy(r => r.Position)
                .GroupBy(r => r.SectionKey, StringComparer.Ordinal)
                .ToDictionary(s => s.Key, s => s.First().SectionTitle, StringComparer.Ordinal));
    }

    // True when the whole line (as GenerateAsync wrote it) is still one of the lines of the section text; a multi-line
    // answer must still appear as one block starting and ending at line breaks.
    internal static bool ContainsLine(string text, string line)
    {
        var normalized = text.Replace("\r\n", "\n");
        for (var start = normalized.IndexOf(line, StringComparison.Ordinal); start >= 0;
             start = normalized.IndexOf(line, start + 1, StringComparison.Ordinal))
        {
            var end = start + line.Length;
            if ((start == 0 || normalized[start - 1] == '\n') && (end == normalized.Length || normalized[end] == '\n'))
            {
                return true;
            }
        }
        return false;
    }

    // One generated line of the note: "Label: answer" with the answer as displayed.
    internal static string Line(string label, string answerType, string answer) =>
        $"{label}: {IntakeService.Display(answerType, answer)}";

    internal static List<string> SectionKeys(IEnumerable<IntakeQuestion> enabledQuestions) =>
        [.. IntakeRules.Sections(enabledQuestions, enabledOnly: true).Select(s => s.Key)];

    // Runs body inside one transaction holding the patient (key share), note and intake locks, with the intake tracked.
    // Ok commits; anything else rolls back. The note id is read before locking; if another request changed it in between,
    // the attempt starts over once.
    private static async Task<IntakeResult<T>> WithLocksAsync<T>(
        AnamnysDbContext db, Guid providerId, Guid id, Func<Intake, Task<IntakeResult<T>>> body, CancellationToken ct)
    {
        var result = new IntakeResult<T>(IntakeOutcome.NotFound);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                result = new(IntakeOutcome.NotFound);
                db.ChangeTracker.Clear();
                var seen = await IntakeService.Owned(db, providerId).AsNoTracking()
                    .Where(i => i.Id == id).Select(i => new { i.NoteId }).SingleOrDefaultAsync(ct);
                if (seen is null)
                {
                    return;
                }
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlAsync($"""
                    SELECT 1 FROM "Patients"
                     WHERE "ProviderId" = {providerId}
                       AND "Id" = (SELECT "PatientId" FROM "Intakes" WHERE "Id" = {id} AND "ProviderId" = {providerId})
                       FOR KEY SHARE
                    """, ct);
                if (seen.NoteId is { } noteId)
                {
                    await NoteLocks.LockAsync(db, noteId, ct);
                }
                await IntakeService.LockAsync(db, id, ct);
                var intake = await IntakeService.Owned(db, providerId).SingleOrDefaultAsync(i => i.Id == id, ct);
                if (intake is null)
                {
                    return;
                }
                if (intake.NoteId != seen.NoteId)
                {
                    await tx.RollbackAsync(ct);
                    continue;
                }
                result = await body(intake);
                if (result.Outcome == IntakeOutcome.Ok)
                {
                    await tx.CommitAsync(ct);
                }
                return;
            }
            result = new(IntakeOutcome.NotDraft);
        });
        return result;
    }

    // Deletes an unsigned intake note with what hangs on it: sections (cascade), points "para retomar" marked on it,
    // excerpt references, notifications and its audit trail (which holds the generated and edited text, like the discard
    // of a record; the intake's own audit entry, ids only, records the deletion). A point resolved in this note goes back
    // to open (its resolution pointed at a note that no longer exists). False when the note is signed (it then stays).
    private static async Task<bool> DeleteDraftNoteAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct)
    {
        if (!await db.Notes.AnyAsync(n => n.Id == noteId && n.ProviderId == providerId && n.SignedAt == null, ct))
        {
            return false;
        }
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "FollowUpItems" WHERE "ProviderId" = {providerId} AND "NoteId" = {noteId}""", ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE "FollowUpItems" SET "ResolvedAt" = NULL, "ResolvedNoteId" = NULL, "ResolvedById" = NULL
             WHERE "ProviderId" = {providerId} AND "ResolvedNoteId" = {noteId}
            """, ct);
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "ExcerptRefs" WHERE "ProviderId" = {providerId} AND "SourceType" = 'note' AND "SourceId" = {noteId}""", ct);
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "Notifications" WHERE "ProviderId" = {providerId} AND "SubjectType" = 'note' AND "SubjectId" = {noteId}""", ct);
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "AuditEntries" WHERE "NoteId" = {noteId}""", ct);
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "Notes" WHERE "Id" = {noteId} AND "ProviderId" = {providerId} AND "SignedAt" IS NULL""", ct);
        return true;
    }

    private static List<(string Key, string Content)> Contents(List<IntakeQuestion> questions, Dictionary<Guid, string> answers) =>
        [.. IntakeRules.Sections(questions, enabledOnly: true).Select(s => (s.Key, string.Join("\n", s.Questions
            .Where(q => answers.ContainsKey(q.Id))
            .Select(q => Line(q.Label, q.AnswerType, answers[q.Id])))))];

    private static Task<Dictionary<Guid, string>> AnswersAsync(AnamnysDbContext db, Guid intakeId, CancellationToken ct) =>
        db.IntakeResponses.AsNoTracking()
            .Where(r => r.IntakeId == intakeId && r.Answer != null && r.Answer != "")
            .ToDictionaryAsync(r => r.QuestionId, r => r.Answer!, ct);
}
