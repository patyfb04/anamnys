using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Intakes;
using Anamnys.Server.Notes;
using Anamnys.Tests.Retention;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Intakes;

// A scratch database from the bootstrap script (which carries design/migrations/2026-11-12-structured-intake.sql and its
// default template), so these tests do not depend on the dev database having the migration applied. The provider has a
// CRP (signing needs one); a second provider owns the stranger patient. Now is Thursday 2026-11-12, 12:00 in São Paulo.
internal sealed class IntakeScene : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    private IntakeScene(RetentionScratch db, Guid provider, Guid patient, Guid otherProvider, Guid stranger)
    {
        Db = db;
        Provider = provider;
        Patient = patient;
        OtherProvider = otherProvider;
        Stranger = stranger;
    }

    public RetentionScratch Db { get; }
    public Guid Provider { get; }
    public Guid Patient { get; }
    public Guid OtherProvider { get; }
    public Guid Stranger { get; }

    public static async Task<IntakeScene> CreateAsync(SharedAppHostFixture fixture, CancellationToken ct)
    {
        var db = await RetentionScratch.CreateAsync(fixture, ct);
        var provider = await db.AddProviderAsync(ct, withCrp: true);
        var patient = await db.AddPatientAsync(provider, "Benedita", ct);
        var other = await db.AddProviderAsync(ct, withCrp: true);
        var stranger = await db.AddPatientAsync(other, "Estranha", ct);
        return new IntakeScene(db, provider, patient, other, stranger);
    }

    public AnamnysDbContext CreateDbContext() => Db.CreateDbContext();

    public async Task<Guid> StartAsync(CancellationToken ct, Guid? patient = null)
    {
        await using var db = CreateDbContext();
        var result = await IntakeService.StartAsync(db, Provider, patient ?? Patient, Now, ct);
        return result.Outcome == IntakeOutcome.Ok ? result.Value!.Id : throw new InvalidOperationException(result.Outcome.ToString());
    }

    // The question ids of a template by key (the system default when no template is given).
    public async Task<Dictionary<string, Guid>> QuestionsAsync(CancellationToken ct, Guid? templateId = null)
    {
        await using var db = CreateDbContext();
        var id = templateId ?? IntakeRules.SystemDefaultTemplateId;
        return await db.IntakeQuestions.AsNoTracking().Where(q => q.TemplateId == id).ToDictionaryAsync(q => q.Key, q => q.Id, ct);
    }

    public async Task<IntakeResult<IReadOnlyList<IntakeAnswerView>>> AnswerAsync(
        Guid intakeId, CancellationToken ct, params (string Key, string? Answer)[] answers)
    {
        var intake = await IntakeAsync(intakeId, ct);
        var questions = await QuestionsAsync(ct, intake.TemplateId);
        await using var db = CreateDbContext();
        return await IntakeService.SaveAnswersAsync(db, Provider, intakeId,
            [.. answers.Select(a => (IntakeAnswerInput?)new IntakeAnswerInput(questions[a.Key], a.Answer))], Now, ct);
    }

    // The two required questions of the default template, plus every question with a record destination.
    public Task AnswerRequiredAndDestinationsAsync(Guid intakeId, CancellationToken ct) =>
        AnswerAsync(intakeId, ct,
            ("queixa_principal", "Ansiedade antes de reuniões"),
            ("risco", "Nunca"),
            ("data_nascimento", "1990-05-04"),
            ("telefone", "11 98888-7777"),
            ("email", "benedita@example.test"),
            ("medicacoes", "Sertralina 50 mg"),
            ("diagnosticos_previos", "Transtorno de ansiedade generalizada"),
            ("objetivos_terapia", "Falar em público sem crises"));

    public async Task<Guid> GenerateNoteAsync(Guid intakeId, CancellationToken ct)
    {
        await using var db = CreateDbContext();
        var result = await IntakeNote.GenerateAsync(db, Provider, intakeId, Now, ct);
        return result.Outcome == IntakeOutcome.Ok ? result.Value!.NoteId : throw new InvalidOperationException(result.Outcome.ToString());
    }

    public async Task<SignOutcome> SignAsync(Guid noteId, CancellationToken ct)
    {
        await using var db = CreateDbContext();
        return await NoteEditing.SignAsync(db, Provider, noteId, Now, ct);
    }

    // A signed intake with every destination answered; returns the intake and its note.
    public async Task<(Guid Intake, Guid Note)> SignedIntakeAsync(CancellationToken ct)
    {
        var intake = await StartAsync(ct);
        await AnswerRequiredAndDestinationsAsync(intake, ct);
        var note = await GenerateNoteAsync(intake, ct);
        var outcome = await SignAsync(note, ct);
        return outcome == SignOutcome.Signed ? (intake, note) : throw new InvalidOperationException(outcome.ToString());
    }

    public async Task<Anamnys.Server.Data.Entities.Intake> IntakeAsync(Guid id, CancellationToken ct)
    {
        await using var db = CreateDbContext();
        return await db.Intakes.AsNoTracking().SingleAsync(i => i.Id == id, ct);
    }

    public async Task<List<IntakeTransfer>> TransfersAsync(Guid intakeId, CancellationToken ct)
    {
        await using var db = CreateDbContext();
        return await db.IntakeTransfers.AsNoTracking().Where(t => t.IntakeId == intakeId).ToListAsync(ct);
    }

    public async Task<List<string>> AuditAsync(CancellationToken ct)
    {
        await using var db = CreateDbContext();
        return await db.AuditEntries.AsNoTracking()
            .Where(a => a.ActorId == Provider && a.NoteId == null)
            .OrderBy(a => a.At).ThenBy(a => a.Id)
            .Select(a => a.Action + " " + a.NewValue)
            .ToListAsync(ct);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
