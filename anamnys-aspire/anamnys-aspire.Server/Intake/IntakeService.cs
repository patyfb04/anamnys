using System.Globalization;
using Anamnys.Server.Availability;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Intakes;

// Intakes of a patient (design/specs/2026-11-12-structured-intake-design.md §3 Starting, Answers). Every call resolves
// the intake (or patient) with ProviderId == providerId and the patient not discarded, so a foreign id answers like a
// missing one. Audit values carry ids only.
public static class IntakeService
{
    public const int MaxAnswerLength = 4000;
    public const int MaxBatch = 200;
    public const int MaxPhoneLength = 40;
    private const string OneDraftConstraint = "Intakes_OneDraft_key";

    public static async Task<IReadOnlyList<IntakeListItem>?> ListForPatientAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct))
        {
            return null;
        }
        return await db.Intakes.AsNoTracking()
            .Where(i => i.PatientId == patientId && i.ProviderId == providerId)
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Select(i => new IntakeListItem(i.Id, i.Status, i.CreatedAt, i.SignedAt, i.NoteId,
                db.IntakeTransfers.Count(t => t.IntakeId == i.Id && t.Status == IntakeTransferStatus.Pending)))
            .ToListAsync(ct);
    }

    public static async Task<IntakeResult<IntakeCreated>> StartAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null)
            .Select(p => new { p.ArchivedAt })
            .SingleOrDefaultAsync(ct);
        if (patient is null)
        {
            return new(IntakeOutcome.NotFound);
        }
        if (patient.ArchivedAt is not null)
        {
            return new(IntakeOutcome.PatientArchived);
        }
        if (await db.Intakes.AnyAsync(i => i.PatientId == patientId && i.Status == IntakeStatus.Draft, ct))
        {
            return new(IntakeOutcome.InProgress);
        }
        var templateId = await IntakeTemplates.DefaultTemplateIdAsync(db, providerId, ct);
        if (!await IntakeTemplates.Visible(db, providerId).AnyAsync(t => t.Id == templateId, ct))
        {
            // No seeded default: the migration was not applied.
            throw new InvalidOperationException("The default intake template is missing.");
        }
        var intake = new Intake
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            PatientId = patientId,
            TemplateId = templateId,
            Status = IntakeStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Intakes.Add(intake);
        IntakeRules.Audit(db, providerId, IntakeRules.StartedAction, $"{intake.Id} patient {patientId} template {templateId}", now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { ConstraintName: OneDraftConstraint })
        {
            db.ChangeTracker.Clear();
            return new(IntakeOutcome.InProgress);
        }
        return new(IntakeOutcome.Ok, new IntakeCreated(intake.Id));
    }

    public static async Task<IntakeDetail?> GetAsync(AnamnysDbContext db, Guid providerId, Guid id, CancellationToken ct)
    {
        var row = await (
            from i in Owned(db, providerId).AsNoTracking()
            join t in db.IntakeTemplates.AsNoTracking() on i.TemplateId equals t.Id
            where i.Id == id
            select new
            {
                Intake = i,
                Template = new IntakeTemplateRef(t.Id, t.Name, t.Version),
                NoteStatus = db.Notes.Where(n => n.Id == i.NoteId).Select(n => n.Status).FirstOrDefault(),
                Pending = db.IntakeTransfers.Count(x => x.IntakeId == i.Id && x.Status == IntakeTransferStatus.Pending),
            }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }
        var questions = await db.IntakeQuestions.AsNoTracking().Where(q => q.TemplateId == row.Template.Id).ToListAsync(ct);
        var intake = row.Intake;
        return new IntakeDetail(intake.Id, intake.PatientId, intake.Status, intake.CreatedAt, intake.UpdatedAt, intake.SignedAt, intake.NoteId,
            row.NoteStatus, row.Pending, row.Template, IntakeRules.Sections(questions, enabledOnly: true), await AnswersAsync(db, intake.Id, ct));
    }

    // Upserts a batch of answers; an empty answer removes it. All or nothing: one invalid answer refuses the batch.
    public static async Task<IntakeResult<IReadOnlyList<IntakeAnswerView>>> SaveAnswersAsync(
        AnamnysDbContext db, Guid providerId, Guid id, IReadOnlyList<IntakeAnswerInput?> answers, DateTimeOffset now, CancellationToken ct)
    {
        var result = new IntakeResult<IReadOnlyList<IntakeAnswerView>>(IntakeOutcome.NotFound);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            result = new(IntakeOutcome.NotFound);
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(db, id, ct);
            var intake = await Owned(db, providerId).SingleOrDefaultAsync(i => i.Id == id, ct);
            if (intake is null)
            {
                return;
            }
            if (intake.Status != IntakeStatus.Draft)
            {
                result = new(IntakeOutcome.NotDraft);
                return;
            }
            if (intake.NoteId is not null)
            {
                // The note says what the answers say; "Editar respostas" (IntakeNote.UnlockAsync) deletes it first.
                result = new(IntakeOutcome.AnswersLocked);
                return;
            }
            if (await PatientArchivedAsync(db, intake.PatientId, ct))
            {
                result = new(IntakeOutcome.PatientArchived);
                return;
            }
            var questions = await db.IntakeQuestions.AsNoTracking()
                .Where(q => q.TemplateId == intake.TemplateId && !q.Disabled)
                .ToDictionaryAsync(q => q.Id, ct);
            var today = AvailabilityTime.LocalDate(now, AvailabilityTime.Provider);
            var invalid = new List<Guid>();
            var accepted = new Dictionary<Guid, string?>();
            foreach (var input in answers)
            {
                if (input?.QuestionId is not { } questionId)
                {
                    invalid.Add(Guid.Empty);
                    continue;
                }
                if (!questions.TryGetValue(questionId, out var question) || accepted.ContainsKey(questionId))
                {
                    invalid.Add(questionId);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(input.Answer))
                {
                    accepted[questionId] = null;
                }
                else if (Normalize(question, input.Answer, today) is { } value)
                {
                    accepted[questionId] = value;
                }
                else
                {
                    invalid.Add(questionId);
                }
            }
            if (invalid.Count > 0)
            {
                result = new(IntakeOutcome.InvalidAnswer, QuestionIds: invalid.Distinct().ToList());
                return;
            }
            var ids = accepted.Keys.ToList();
            var existing = await db.IntakeResponses.Where(r => r.IntakeId == id && ids.Contains(r.QuestionId)).ToDictionaryAsync(r => r.QuestionId, ct);
            var changed = false;
            foreach (var (questionId, value) in accepted)
            {
                existing.TryGetValue(questionId, out var response);
                if (value is null)
                {
                    if (response is not null)
                    {
                        db.IntakeResponses.Remove(response);
                        changed = true;
                    }
                }
                else if (response is null)
                {
                    db.IntakeResponses.Add(new IntakeResponse
                    {
                        Id = Guid.NewGuid(),
                        IntakeId = id,
                        PatientId = intake.PatientId,
                        QuestionId = questionId,
                        Answer = value,
                        RecordedAt = now,
                        UpdatedAt = now,
                    });
                    changed = true;
                }
                else if (response.Answer != value)
                {
                    response.Answer = value;
                    response.UpdatedAt = now;
                    changed = true;
                }
            }
            if (changed)
            {
                intake.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            result = new(IntakeOutcome.Ok, await AnswersAsync(db, id, ct));
        });
        return result;
    }

    // The stored form of an answer, or null when it is not valid for the question: numbers in invariant form, dates
    // "yyyy-MM-dd", booleans "true"/"false", choices exactly one of the options. Answers with a record destination
    // must also be acceptable there (length, e-mail shape, a plausible birth date).
    internal static string? Normalize(IntakeQuestion question, string answer, DateOnly today)
    {
        var text = answer.Trim();
        switch (question.AnswerType)
        {
            case IntakeRules.Number:
                return decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : null;
            case IntakeRules.Date:
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return null;
                }
                if (question.TargetField == IntakeRules.DateOfBirth && (date > today || date < new DateOnly(1900, 1, 1)))
                {
                    return null;
                }
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case IntakeRules.Boolean:
                return text.ToLowerInvariant() is "true" or "false" ? text.ToLowerInvariant() : null;
            case IntakeRules.Choice:
                return question.Choices?.Contains(text, StringComparer.Ordinal) == true ? text : null;
        }
        if (text.Length > MaxAnswerLength)
        {
            return null;
        }
        switch (question.TargetField)
        {
            case IntakeRules.Medication or IntakeRules.Diagnosis or IntakeRules.Objective when text.Length > PatientRules.MaxItemText:
            case IntakeRules.Phone when text.Length > MaxPhoneLength:
                return null;
            case IntakeRules.ContactEmail:
                var errors = new Dictionary<string, string[]>();
                PatientRules.RequireEmail(errors, "email", text);
                return errors.Count == 0 ? text : null;
        }
        return text;
    }

    // How an answer reads in the note: dates dd/MM/yyyy, booleans Sim/Não, numbers with the pt-BR decimal comma (stored
    // invariant, "7.5" reads "7,5"; no thousands separator).
    internal static string Display(string answerType, string answer) => answerType switch
    {
        IntakeRules.Number when decimal.TryParse(answer, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var n)
            => n.ToString(CultureInfo.InvariantCulture).Replace('.', ','),
        IntakeRules.Date when DateOnly.TryParseExact(answer, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        IntakeRules.Boolean => answer == "true" ? "Sim" : "Não",
        _ => answer,
    };

    internal static IQueryable<Intake> Owned(AnamnysDbContext db, Guid providerId) =>
        db.Intakes.Where(i => i.ProviderId == providerId
            && db.Patients.Any(p => p.Id == i.PatientId && p.ProviderId == providerId && p.DiscardedAt == null));

    internal static Task LockAsync(AnamnysDbContext db, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Intakes" WHERE "Id" = {id} FOR UPDATE""", ct);

    internal static Task<bool> PatientArchivedAsync(AnamnysDbContext db, Guid patientId, CancellationToken ct) =>
        db.Patients.AnyAsync(p => p.Id == patientId && p.ArchivedAt != null, ct);

    private static async Task<IReadOnlyList<IntakeAnswerView>> AnswersAsync(AnamnysDbContext db, Guid intakeId, CancellationToken ct) =>
        await db.IntakeResponses.AsNoTracking()
            .Where(r => r.IntakeId == intakeId && r.Answer != null)
            .OrderBy(r => r.QuestionId)
            .Select(r => new IntakeAnswerView(r.QuestionId, r.Answer!, r.UpdatedAt))
            .ToListAsync(ct);
}
