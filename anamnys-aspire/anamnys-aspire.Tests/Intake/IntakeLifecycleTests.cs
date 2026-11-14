using Anamnys.Server.Data.Entities;
using Anamnys.Server.Intakes;
using Anamnys.Server.Notes;
using Anamnys.Server.Retention;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using static Anamnys.Tests.Intakes.IntakeScene;

namespace Anamnys.Tests.Intakes;

// design/specs/2026-11-12-structured-intake-design.md §3 Starting, Answers, Note generation, Discard; §6 "Intake lifecycle".
[Collection(SharedAppHostCollection.Name)]
public class IntakeLifecycleTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IntakeLifecycleTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Fact]
    public async Task Start_OneDraftAtATime_ArchivedAndForeignPatientsRefused()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var archived = await s.Db.AddPatientAsync(s.Provider, "Arquivada", Ct);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @pa""", Ct, ("pa", archived));
        await using var db = s.CreateDbContext();

        // Act
        var first = await IntakeService.StartAsync(db, s.Provider, s.Patient, Now, Ct);
        var second = await IntakeService.StartAsync(db, s.Provider, s.Patient, Now, Ct);
        var onArchived = await IntakeService.StartAsync(db, s.Provider, archived, Now, Ct);
        var onForeign = await IntakeService.StartAsync(db, s.Provider, s.Stranger, Now, Ct);
        var list = await IntakeService.ListForPatientAsync(db, s.Provider, s.Patient, Ct);
        var foreignList = await IntakeService.ListForPatientAsync(db, s.Provider, s.Stranger, Ct);
        var foreignGet = await IntakeService.GetAsync(db, s.OtherProvider, first.Value!.Id, Ct);

        // Assert
        first.Outcome.Should().Be(IntakeOutcome.Ok);
        second.Outcome.Should().Be(IntakeOutcome.InProgress);
        onArchived.Outcome.Should().Be(IntakeOutcome.PatientArchived);
        onForeign.Outcome.Should().Be(IntakeOutcome.NotFound);
        list.Should().ContainSingle().Which.Should().Be(new IntakeListItem(first.Value.Id, "draft", Now, null, null, 0));
        foreignList.Should().BeNull();
        foreignGet.Should().BeNull();
        var intake = await s.IntakeAsync(first.Value.Id, Ct);
        intake.TemplateId.Should().Be(IntakeRules.SystemDefaultTemplateId);
        (await s.AuditAsync(Ct)).Should().Equal($"intake_started {first.Value.Id} patient {s.Patient} template {IntakeRules.SystemDefaultTemplateId}");
    }

    [Fact]
    public async Task SaveAnswers_ValidatesPerType_NormalizesAndClears()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var intake = await s.StartAsync(Ct);
        var questions = await s.QuestionsAsync(Ct);

        // Act
        var invalid = await s.AnswerAsync(intake, Ct,
            ("intensidade", "muito"), ("data_nascimento", "2026-13-01"), ("tratamento_anterior", "talvez"), ("sono", "Ótimo"),
            ("email", "sem-arroba"), ("queixa_principal", "válida"));
        var future = await s.AnswerAsync(intake, Ct, ("data_nascimento", "2030-01-01"));
        var afterInvalid = await s.AnswerAsync(intake, Ct);
        var saved = await s.AnswerAsync(intake, Ct,
            ("intensidade", " 7,5 "), ("data_nascimento", "1990-05-04"), ("tratamento_anterior", "TRUE"), ("sono", "Dorme bem"),
            ("queixa_principal", "  Insônia  "));
        var cleared = await s.AnswerAsync(intake, Ct, ("sono", ""), ("queixa_principal", null));
        IntakeResult<IReadOnlyList<IntakeAnswerView>> unknown;
        await using (var db = s.CreateDbContext())
        {
            unknown = await IntakeService.SaveAnswersAsync(db, s.Provider, intake, [new IntakeAnswerInput(Guid.NewGuid(), "x")], Now, Ct);
        }

        // Assert
        invalid.Outcome.Should().Be(IntakeOutcome.InvalidAnswer);
        invalid.QuestionIds.Should().BeEquivalentTo(new[]
        {
            questions["intensidade"], questions["data_nascimento"], questions["tratamento_anterior"], questions["sono"], questions["email"],
        });
        future.Outcome.Should().Be(IntakeOutcome.InvalidAnswer);
        afterInvalid.Value.Should().BeEmpty();
        saved.Outcome.Should().Be(IntakeOutcome.Ok);
        saved.Value!.ToDictionary(a => a.QuestionId, a => a.Answer).Should().BeEquivalentTo(new Dictionary<Guid, string>
        {
            [questions["intensidade"]] = "7.5", [questions["data_nascimento"]] = "1990-05-04", [questions["tratamento_anterior"]] = "true",
            [questions["sono"]] = "Dorme bem", [questions["queixa_principal"]] = "Insônia",
        });
        cleared.Value!.Select(a => a.QuestionId).Should().BeEquivalentTo(new[]
        {
            questions["intensidade"], questions["data_nascimento"], questions["tratamento_anterior"],
        });
        unknown.Outcome.Should().Be(IntakeOutcome.InvalidAnswer);
    }

    [Fact]
    public async Task GenerateNote_RequiredMissingFirst_ThenCreates_AndAgainReturnsTheSameNote()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var intake = await s.StartAsync(Ct);
        var questions = await s.QuestionsAsync(Ct);

        // Act
        IntakeResult<IntakeNoteResponse> missing;
        await using (var db = s.CreateDbContext())
        {
            missing = await IntakeNote.GenerateAsync(db, s.Provider, intake, Now, Ct);
        }
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        await s.AnswerAsync(intake, Ct, ("tratamento_anterior", "false"));
        var noteId = await s.GenerateNoteAsync(intake, Ct);
        NoteDetail first;
        await using (var db = s.CreateDbContext())
        {
            first = (await NoteQueries.GetAsync(db, s.Provider, noteId, Ct))!;
        }
        var locked = await s.AnswerAsync(intake, Ct, ("queixa_principal", "Insônia persistente"));
        var again = await s.GenerateNoteAsync(intake, Ct);
        NoteDetail second;
        await using (var db = s.CreateDbContext())
        {
            second = (await NoteQueries.GetAsync(db, s.Provider, noteId, Ct))!;
        }

        // Assert
        missing.Outcome.Should().Be(IntakeOutcome.RequiredMissing);
        missing.QuestionIds.Should().Equal(questions["queixa_principal"], questions["risco"]);
        (first.Format, first.Status, first.IntakeId, first.SectionsByAi).Should().Be(("Anamnese", "ReadyForReview", intake, false));
        first.Sections.Select(x => x.Key).Should().Equal("identificacao", "queixa", "historia", "saude", "contexto", "objetivos");
        first.Sections[0].Title.Should().Be("Identificação");
        first.Sections[0].Content.Should().Be("Data de nascimento: 04/05/1990\nTelefone: 11 98888-7777\nE-mail: benedita@example.test");
        first.Sections[2].Content.Should().Be("Já fez psicoterapia ou tratamento psiquiátrico: Não");
        first.Sections[4].Content.Should().BeEmpty();
        again.Should().Be(noteId);
        locked.Outcome.Should().Be(IntakeOutcome.AnswersLocked);
        second.Sections[1].Content.Should().Be("Queixa principal: Ansiedade antes de reuniões");
        second.Sections[1].Revision.Should().Be(0);
        (await s.IntakeAsync(intake, Ct)).NoteId.Should().Be(noteId);
    }

    [Fact]
    public async Task Sign_AnamneseNote_MarksIntakeSigned_SealsTheNote_AndCreatesPendingTransfers()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var intake = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        var noteId = await s.GenerateNoteAsync(intake, Ct);

        // Act
        var outcome = await s.SignAsync(noteId, Ct);
        var answerAfter = await s.AnswerAsync(intake, Ct, ("lazer", "Leitura"));
        IntakeResult<IntakeNoteResponse> noteAfter;
        await using (var db = s.CreateDbContext())
        {
            noteAfter = await IntakeNote.GenerateAsync(db, s.Provider, intake, Now, Ct);
        }

        // Assert
        outcome.Should().Be(SignOutcome.Signed);
        var signed = await s.IntakeAsync(intake, Ct);
        (signed.Status, signed.SignedAt).Should().Be(("signed", Now));
        (await s.Db.CountAsync(
            """SELECT count(*) FROM "IntegritySeals" WHERE "SubjectType" = 'note' AND "SubjectId" = @n""", Ct, ("n", noteId))).Should().Be(1);
        var transfers = await s.TransfersAsync(intake, Ct);
        transfers.Should().OnlyContain(t => t.Status == "pending" && t.DecidedAt == null && t.ResultId == null);
        transfers.ToDictionary(t => t.TargetField, t => t.Value).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["patient.dateOfBirth"] = "1990-05-04", ["patient.phone"] = "11 98888-7777", ["patient.contactEmail"] = "benedita@example.test",
            ["medication"] = "Sertralina 50 mg", ["diagnosis"] = "Transtorno de ansiedade generalizada", ["plan.objective"] = "Falar em público sem crises",
        });
        answerAfter.Outcome.Should().Be(IntakeOutcome.NotDraft);
        noteAfter.Outcome.Should().Be(IntakeOutcome.NotDraft);
        var audit = await s.AuditAsync(Ct);
        audit.Should().Contain($"intake_signed {intake} note {noteId} transfers 6");
        audit.Should().NotContain(a => a.Contains("Sertralina") || a.Contains("Benedita") || a.Contains("ansiedade"));
        await using var db2 = s.CreateDbContext();
        (await db2.MedicationEntries.AnyAsync(m => m.PatientId == s.Patient, Ct)).Should().BeFalse();
        (await db2.Patients.SingleAsync(p => p.Id == s.Patient, Ct)).DateOfBirth.Should().BeNull();
    }

    [Fact]
    public async Task Sign_WithSectionKeysOtherThanTheTemplates_IsLocked()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var intake = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        var noteId = await s.GenerateNoteAsync(intake, Ct);
        await s.Db.ExecuteAsync("""DELETE FROM "NoteSections" WHERE "NoteId" = @n AND "Key" = 'contexto'""", Ct, ("n", noteId));

        // Act
        var outcome = await s.SignAsync(noteId, Ct);

        // Assert
        outcome.Should().Be(SignOutcome.Locked);
        (await s.IntakeAsync(intake, Ct)).Status.Should().Be("draft");
        (await s.TransfersAsync(intake, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Discard_RemovesIntakesResponsesAndTransfers()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var record = await DiscardServiceTests.SeedExpiredAsync(s, store);
        // The intake was signed back then, inside the expired period, so the record stays past its deadline.
        var then = new DateTimeOffset(2019, 6, 2, 15, 0, 0, TimeSpan.Zero);
        Guid intakeId;
        await using (var db = s.CreateDbContext())
        {
            intakeId = (await IntakeService.StartAsync(db, record.Provider, record.Patient, then, Ct)).Value!.Id;
            var questions = await db.IntakeQuestions.AsNoTracking()
                .Where(q => q.TemplateId == IntakeRules.SystemDefaultTemplateId).ToDictionaryAsync(q => q.Key, q => q.Id, Ct);
            await IntakeService.SaveAnswersAsync(db, record.Provider, intakeId,
                [new IntakeAnswerInput(questions["queixa_principal"], "Queixa"), new IntakeAnswerInput(questions["risco"], "Nunca"),
                 new IntakeAnswerInput(questions["medicacoes"], "Fluoxetina")], then, Ct);
            var note = (await IntakeNote.GenerateAsync(db, record.Provider, intakeId, then, Ct)).Value!.NoteId;
            (await NoteEditing.SignAsync(db, record.Provider, note, then, Ct)).Should().Be(SignOutcome.Signed);
        }

        // Act
        DiscardResult result;
        await using (var db = s.CreateDbContext())
        {
            result = await DiscardService.DiscardAsync(db, store, NullLogger.Instance, record.Provider, record.Patient, "Ana Silva", Now, Ct);
        }

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.Ok);
        (await s.CountAsync("""SELECT count(*) FROM "Intakes" WHERE "PatientId" = @pa""", Ct, ("pa", record.Patient))).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "IntakeResponses" WHERE "PatientId" = @pa""", Ct, ("pa", record.Patient))).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "IntakeTransfers" WHERE "IntakeId" = @i""", Ct, ("i", intakeId))).Should().Be(0);
        await using var check = s.CreateDbContext();
        var term = await check.DisposalTerms.AsNoTracking().SingleAsync(t => t.PatientId == record.Patient, Ct);
        term.Counts.Should().Contain(new Dictionary<string, int> { ["intakes"] = 1, ["intakeResponses"] = 3, ["intakeTransfers"] = 1 });
        var start = async () =>
        {
            await using var db = s.CreateDbContext();
            return await IntakeService.StartAsync(db, record.Provider, record.Patient, Now, Ct);
        };
        (await start()).Outcome.Should().Be(IntakeOutcome.NotFound);
    }
}
