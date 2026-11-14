using System.Globalization;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Intakes;

// The folder is Intake/; the namespace is Intakes so it does not shadow the Data.Entities.Intake entity.

public enum IntakeOutcome
{
    Ok, NotFound, Forbidden, NameTaken, TemplateArchived, InProgress, PatientArchived, NotDraft, InvalidAnswer, RequiredMissing,
    NotSigned, NotPending, AnswersLocked,
}

public sealed record IntakeResult<T>(IntakeOutcome Outcome, T? Value = default, IReadOnlyList<Guid>? QuestionIds = null);

public static class IntakeRules
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Boolean = "boolean";
    public const string Choice = "choice";
    public static readonly string[] AnswerTypes = [Text, Number, Date, Boolean, Choice];

    public const string DateOfBirth = "patient.dateOfBirth";
    public const string Phone = "patient.phone";
    public const string ContactEmail = "patient.contactEmail";
    public const string Medication = "medication";
    public const string Diagnosis = "diagnosis";
    public const string Objective = "plan.objective";
    public static readonly string[] TargetFields = [DateOfBirth, Phone, ContactEmail, Medication, Diagnosis, Objective];

    public const string StartedAction = "intake_started";
    public const string SignedAction = "intake_signed";
    public const string TransferConfirmedAction = "intake_transfer_confirmed";
    public const string TransferIgnoredAction = "intake_transfer_ignored";
    public const string TemplateAdaptedAction = "intake_template_adapted";
    public const string DiscardedAction = "intake_discarded";
    public const string UnlockedAction = "intake_answers_unlocked";
    public const string AuditField = "intake";

    // The seeded "Anamnese padrão" (design/migrations/2026-11-12-structured-intake.sql).
    public static readonly Guid SystemDefaultTemplateId = Guid.Parse("0f310000-0000-4000-8000-000000000001");

    public static string AnswerTypeFor(string targetField) => targetField == DateOfBirth ? Date : Text;

    // Ids, actions and counts only: never an answer, a label or a name.
    internal static void Audit(AnamnysDbContext db, Guid providerId, string action, string value, DateTimeOffset now) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorId = providerId,
            Action = action,
            FieldChanged = AuditField,
            NewValue = value,
            At = now,
        });

    internal static IntakeQuestionView View(IntakeQuestion q) =>
        new(q.Id, q.Key, q.Label, q.HelpText, q.AnswerType, q.AnswerType == Choice ? q.Choices : null, q.Required, q.TargetField,
            q.Disabled, q.Position);

    // Sections in the order of their first question; with enabledOnly, disabled questions (and sections left empty) go.
    internal static List<IntakeSectionView> Sections(IEnumerable<IntakeQuestion> questions, bool enabledOnly)
    {
        var sections = new List<(string Key, string Title, List<IntakeQuestionView> Questions)>();
        foreach (var q in questions.OrderBy(q => q.Position).ThenBy(q => q.Key, StringComparer.Ordinal))
        {
            if (enabledOnly && q.Disabled)
            {
                continue;
            }
            var index = sections.FindIndex(s => s.Key == q.SectionKey);
            if (index < 0)
            {
                sections.Add((q.SectionKey, q.SectionTitle, []));
                index = sections.Count - 1;
            }
            sections[index].Questions.Add(View(q));
        }
        return [.. sections.Select(s => new IntakeSectionView(s.Key, s.Title, s.Questions))];
    }

    internal static int VersionNumber(string version) =>
        int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

// Intake templates (design/specs/2026-11-12-structured-intake-design.md §3 Templates, §4). A provider sees the system
// default and their own templates; another provider's template is indistinguishable from a missing one. The default is
// read-only; editing an own template already used by an intake archives it and creates the next version.
public static class IntakeTemplates
{
    private const string NameVersionConstraint = "IntakeTemplates_Provider_Name_Version_key";
    private static readonly StringComparer PtBr = StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), CompareOptions.IgnoreCase);

    public static async Task<IntakeTemplateList> ListAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var rows = await db.IntakeTemplates.AsNoTracking()
            .Where(t => t.IsDefault || t.ProviderId == providerId)
            .Select(t => new
            {
                t.Id, t.Name, t.Version, t.IsDefault, t.BasedOnTemplateId, t.ArchivedAt, t.CreatedAt,
                InUse = db.Intakes.Any(i => i.TemplateId == t.Id && i.ProviderId == providerId),
            })
            .ToListAsync(ct);
        var templates = rows
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.ArchivedAt is not null)
            .ThenBy(t => t.Name, PtBr).ThenBy(t => IntakeRules.VersionNumber(t.Version)).ThenBy(t => t.CreatedAt)
            .Select(t => new IntakeTemplateSummary(t.Id, t.Name, t.Version, t.IsDefault, t.BasedOnTemplateId, t.ArchivedAt is not null, t.InUse))
            .ToList();
        return new IntakeTemplateList(await DefaultTemplateIdAsync(db, providerId, ct), templates);
    }

    // The provider's preferred template when it is still active, else the system default.
    public static async Task<Guid> DefaultTemplateIdAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct) =>
        await (
            from s in db.ProviderIntakeSettings.AsNoTracking()
            join t in db.IntakeTemplates.AsNoTracking() on s.DefaultTemplateId equals t.Id
            where s.ProviderId == providerId && t.ArchivedAt == null && (t.IsDefault || t.ProviderId == providerId)
            select (Guid?)t.Id).SingleOrDefaultAsync(ct)
        ?? IntakeRules.SystemDefaultTemplateId;

    public static async Task<IntakeTemplateDetail?> GetAsync(AnamnysDbContext db, Guid providerId, Guid id, CancellationToken ct)
    {
        var template = await Visible(db, providerId).AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
        return template is null ? null : await DetailAsync(db, providerId, template, ct);
    }

    public static async Task<IntakeResult<IntakeTemplateDetail>> AdaptAsync(
        AnamnysDbContext db, Guid providerId, Guid id, AdaptIntakeTemplateRequest? request, DateTimeOffset now, CancellationToken ct)
    {
        var result = new IntakeResult<IntakeTemplateDetail>(IntakeOutcome.NotFound);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            result = new(IntakeOutcome.NotFound);
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var source = await Visible(db, providerId).AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
            if (source is null)
            {
                return;
            }
            string name;
            if (request?.Name is { } requested)
            {
                name = requested.Trim();
                if (await NameTakenAsync(db, providerId, name, "1", null, ct))
                {
                    result = new(IntakeOutcome.NameTaken);
                    return;
                }
            }
            else
            {
                name = await FreeNameAsync(db, providerId, source.IsDefault ? "Minha anamnese" : $"{source.Name} (cópia)", ct);
            }
            var questions = await db.IntakeQuestions.AsNoTracking().Where(q => q.TemplateId == source.Id).ToListAsync(ct);
            var copy = new IntakeTemplate
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                Name = name,
                Version = "1",
                Active = true,
                BasedOnTemplateId = source.Id,
                CreatedAt = now,
            };
            db.IntakeTemplates.Add(copy);
            db.IntakeQuestions.AddRange(questions.Select(q => Copy(q, copy.Id)));
            IntakeRules.Audit(db, providerId, IntakeRules.TemplateAdaptedAction, $"{copy.Id} adapted from {source.Id}", now);
            if (!await TrySaveAsync(db, ct))
            {
                result = new(IntakeOutcome.NameTaken);
                return;
            }
            await tx.CommitAsync(ct);
            result = new(IntakeOutcome.Ok, await DetailAsync(db, providerId, copy, ct));
        });
        return result;
    }

    // Call request.Validate() first. Forbidden for the system default; TemplateArchived for an archived own template.
    public static async Task<IntakeResult<IntakeTemplateDetail>> UpdateAsync(
        AnamnysDbContext db, Guid providerId, Guid id, UpdateIntakeTemplateRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var result = new IntakeResult<IntakeTemplateDetail>(IntakeOutcome.NotFound);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            result = new(IntakeOutcome.NotFound);
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Serialises with intakes being started on this template (their foreign key takes a key-share lock).
            await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "IntakeTemplates" WHERE "Id" = {id} FOR UPDATE""", ct);
            var template = await Visible(db, providerId).SingleOrDefaultAsync(t => t.Id == id, ct);
            if (template is null)
            {
                return;
            }
            if (template.IsDefault)
            {
                result = new(IntakeOutcome.Forbidden);
                return;
            }
            if (template.ArchivedAt is not null)
            {
                result = new(IntakeOutcome.TemplateArchived);
                return;
            }
            var name = request.Name?.Trim() ?? template.Name;
            var inputs = Ordered(request.Questions!);
            var inUse = await db.Intakes.AnyAsync(i => i.TemplateId == template.Id, ct);
            IntakeTemplate saved;
            if (!inUse)
            {
                if (await NameTakenAsync(db, providerId, name, template.Version, template.Id, ct))
                {
                    result = new(IntakeOutcome.NameTaken);
                    return;
                }
                template.Name = name;
                var existing = await db.IntakeQuestions.Where(q => q.TemplateId == template.Id).ToListAsync(ct);
                var byKey = existing.ToDictionary(q => q.Key, StringComparer.Ordinal);
                for (var i = 0; i < inputs.Count; i++)
                {
                    if (byKey.Remove(inputs[i].Input.Key!, out var question))
                    {
                        Apply(question, inputs[i].Input, inputs[i].SectionTitle, i);
                    }
                    else
                    {
                        var added = new IntakeQuestion { Id = Guid.NewGuid(), TemplateId = template.Id, Key = inputs[i].Input.Key! };
                        Apply(added, inputs[i].Input, inputs[i].SectionTitle, i);
                        db.IntakeQuestions.Add(added);
                    }
                }
                db.IntakeQuestions.RemoveRange(byKey.Values);
                IntakeRules.Audit(db, providerId, IntakeRules.TemplateAdaptedAction, $"{template.Id} edited", now);
                saved = template;
            }
            else
            {
                // A rename onto another template's name would continue that template's version line.
                if (name != template.Name && await db.IntakeTemplates.AnyAsync(t => t.ProviderId == providerId && t.Name == name, ct))
                {
                    result = new(IntakeOutcome.NameTaken);
                    return;
                }
                var versions = await db.IntakeTemplates.AsNoTracking()
                    .Where(t => t.ProviderId == providerId && t.Name == name)
                    .Select(t => t.Version)
                    .ToListAsync(ct);
                var next = Math.Max(IntakeRules.VersionNumber(template.Version), versions.Select(IntakeRules.VersionNumber).DefaultIfEmpty(0).Max()) + 1;
                saved = new IntakeTemplate
                {
                    Id = Guid.NewGuid(),
                    ProviderId = providerId,
                    Name = name,
                    Version = next.ToString(CultureInfo.InvariantCulture),
                    Active = true,
                    BasedOnTemplateId = template.Id,
                    CreatedAt = now,
                };
                db.IntakeTemplates.Add(saved);
                for (var i = 0; i < inputs.Count; i++)
                {
                    var added = new IntakeQuestion { Id = Guid.NewGuid(), TemplateId = saved.Id, Key = inputs[i].Input.Key! };
                    Apply(added, inputs[i].Input, inputs[i].SectionTitle, i);
                    db.IntakeQuestions.Add(added);
                }
                template.ArchivedAt = now;
                template.Active = false;
                var setting = await db.ProviderIntakeSettings.SingleOrDefaultAsync(s => s.ProviderId == providerId && s.DefaultTemplateId == template.Id, ct);
                if (setting is not null)
                {
                    setting.DefaultTemplateId = saved.Id;
                }
                IntakeRules.Audit(db, providerId, IntakeRules.TemplateAdaptedAction, $"{saved.Id} version {saved.Version} of {template.Id}", now);
            }
            if (!await TrySaveAsync(db, ct))
            {
                result = new(IntakeOutcome.NameTaken);
                return;
            }
            await tx.CommitAsync(ct);
            result = new(IntakeOutcome.Ok, await DetailAsync(db, providerId, saved, ct));
        });
        return result;
    }

    // Not in spec §4's table but needed by §5 ("archive"): an archived template is no longer offered for new intakes.
    public static async Task<IntakeResult<IntakeTemplateSummary>> ArchiveAsync(
        AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var result = new IntakeResult<IntakeTemplateSummary>(IntakeOutcome.NotFound);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            result = new(IntakeOutcome.NotFound);
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var template = await Visible(db, providerId).SingleOrDefaultAsync(t => t.Id == id, ct);
            if (template is null)
            {
                return;
            }
            if (template.IsDefault)
            {
                result = new(IntakeOutcome.Forbidden);
                return;
            }
            if (template.ArchivedAt is null)
            {
                template.ArchivedAt = now;
                template.Active = false;
                await db.ProviderIntakeSettings.Where(s => s.ProviderId == providerId && s.DefaultTemplateId == template.Id).ExecuteDeleteAsync(ct);
                IntakeRules.Audit(db, providerId, IntakeRules.TemplateAdaptedAction, $"{template.Id} archived", now);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            var inUse = await db.Intakes.AnyAsync(i => i.TemplateId == template.Id && i.ProviderId == providerId, ct);
            result = new(IntakeOutcome.Ok, new IntakeTemplateSummary(
                template.Id, template.Name, template.Version, false, template.BasedOnTemplateId, true, inUse));
        });
        return result;
    }

    // Null chooses the system default. NotFound for a template the provider cannot see; TemplateArchived for an archived one.
    public static async Task<IntakeResult<IntakeSettingsResponse>> SetDefaultAsync(
        AnamnysDbContext db, Guid providerId, Guid? templateId, CancellationToken ct)
    {
        var target = templateId ?? IntakeRules.SystemDefaultTemplateId;
        var template = await Visible(db, providerId).AsNoTracking().SingleOrDefaultAsync(t => t.Id == target, ct);
        if (template is null)
        {
            return new(IntakeOutcome.NotFound);
        }
        if (template.ArchivedAt is not null)
        {
            return new(IntakeOutcome.TemplateArchived);
        }
        // One statement each, so two concurrent choices never collide on the primary key.
        if (template.IsDefault)
        {
            await db.ProviderIntakeSettings.Where(s => s.ProviderId == providerId).ExecuteDeleteAsync(ct);
        }
        else
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ProviderIntakeSettings" ("ProviderId","DefaultTemplateId") VALUES ({providerId}, {template.Id})
                ON CONFLICT ("ProviderId") DO UPDATE SET "DefaultTemplateId" = EXCLUDED."DefaultTemplateId"
                """, ct);
        }
        return new(IntakeOutcome.Ok, new IntakeSettingsResponse(template.Id));
    }

    // A concurrent adapt or edit took the same (provider, name, version): answered as name_taken, nothing saved.
    private static async Task<bool> TrySaveAsync(AnamnysDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: NameVersionConstraint })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    internal static IQueryable<IntakeTemplate> Visible(AnamnysDbContext db, Guid providerId) =>
        db.IntakeTemplates.Where(t => t.IsDefault || t.ProviderId == providerId);

    private static async Task<IntakeTemplateDetail> DetailAsync(AnamnysDbContext db, Guid providerId, IntakeTemplate template, CancellationToken ct)
    {
        var questions = await db.IntakeQuestions.AsNoTracking().Where(q => q.TemplateId == template.Id).ToListAsync(ct);
        var inUse = await db.Intakes.AnyAsync(i => i.TemplateId == template.Id && i.ProviderId == providerId, ct);
        return new IntakeTemplateDetail(template.Id, template.Name, template.Version, template.IsDefault, template.BasedOnTemplateId,
            template.ArchivedAt is not null, inUse, IntakeRules.Sections(questions, enabledOnly: false));
    }

    private static Task<bool> NameTakenAsync(
        AnamnysDbContext db, Guid providerId, string name, string version, Guid? except, CancellationToken ct) =>
        db.IntakeTemplates.AnyAsync(t => t.ProviderId == providerId && t.Name == name && t.Version == version && t.Id != except, ct);

    private static async Task<string> FreeNameAsync(AnamnysDbContext db, Guid providerId, string baseName, CancellationToken ct)
    {
        var name = baseName.Length > UpdateIntakeTemplateRequest.MaxName - 4 ? baseName[..(UpdateIntakeTemplateRequest.MaxName - 4)] : baseName;
        var taken = await db.IntakeTemplates.AsNoTracking()
            .Where(t => t.ProviderId == providerId && t.Name.StartsWith(name))
            .Select(t => t.Name)
            .ToListAsync(ct);
        if (!taken.Contains(name))
        {
            return name;
        }
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} {n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    // Questions grouped by section in the order the sections first appear; a section's title is its first question's.
    private static List<(IntakeTemplateQuestionInput Input, string SectionTitle)> Ordered(IReadOnlyList<IntakeTemplateQuestionInput?> questions) =>
        [.. questions.Select(q => q!)
            .GroupBy(q => q.SectionKey!, StringComparer.Ordinal)
            .SelectMany(g => g.Select(q => (q, g.First().SectionTitle!.Trim())))];

    private static void Apply(IntakeQuestion question, IntakeTemplateQuestionInput input, string sectionTitle, int position)
    {
        question.SectionKey = input.SectionKey!;
        question.SectionTitle = sectionTitle;
        question.Label = input.Label!.Trim();
        question.HelpText = string.IsNullOrWhiteSpace(input.HelpText) ? null : input.HelpText.Trim();
        question.AnswerType = input.AnswerType!;
        question.Choices = input.AnswerType == IntakeRules.Choice ? [.. input.Choices!.Select(c => c!.Trim())] : null;
        question.Position = position;
        question.TargetField = input.TargetField;
        question.Required = input.Required;
        question.Disabled = input.Disabled;
    }

    private static IntakeQuestion Copy(IntakeQuestion q, Guid templateId) => new()
    {
        Id = Guid.NewGuid(),
        TemplateId = templateId,
        SectionKey = q.SectionKey,
        SectionTitle = q.SectionTitle,
        Key = q.Key,
        Label = q.Label,
        HelpText = q.HelpText,
        AnswerType = q.AnswerType,
        Choices = q.Choices,
        Position = q.Position,
        TargetField = q.TargetField,
        Required = q.Required,
        Disabled = q.Disabled,
    };
}
