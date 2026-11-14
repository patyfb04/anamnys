using Anamnys.Server.Intakes;
using Anamnys.Server.Notes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static Anamnys.Tests.Intakes.IntakeScene;

namespace Anamnys.Tests.Intakes;

// Rulings of 2026-11-12 on design/specs/2026-11-12-structured-intake-design.md §3: answers are locked once the note is
// generated ("Editar respostas" deletes the unsigned note), a draft intake can be discarded, and the record is only
// offered what the signed note says.
[Collection(SharedAppHostCollection.Name)]
public class IntakeDraftTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unlock_DeletesTheUnsignedNote_AllowsAnswers_AndTheNewNoteAndTransfersFollowThem()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var intake = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        var firstNote = await s.GenerateNoteAsync(intake, Ct);
        await using (var db = s.CreateDbContext())
        {
            (await NoteEditing.EditSectionAsync(db, s.Provider, firstNote, "saude", "Editado pela profissional.", 0, Now, Ct)).Outcome
                .Should().Be(EditOutcome.Saved);
        }

        // Act
        var locked = await s.AnswerAsync(intake, Ct, ("medicacoes", "Escitalopram 10 mg"));
        IntakeResult<bool> unlocked;
        await using (var db = s.CreateDbContext())
        {
            unlocked = await IntakeNote.UnlockAsync(db, s.Provider, intake, Now, Ct);
        }
        var afterUnlock = await s.AnswerAsync(intake, Ct, ("medicacoes", "Escitalopram 10 mg"));
        var secondNote = await s.GenerateNoteAsync(intake, Ct);
        var signed = await s.SignAsync(secondNote, Ct);

        // Assert
        locked.Outcome.Should().Be(IntakeOutcome.AnswersLocked);
        unlocked.Outcome.Should().Be(IntakeOutcome.Ok);
        afterUnlock.Outcome.Should().Be(IntakeOutcome.Ok);
        secondNote.Should().NotBe(firstNote);
        signed.Should().Be(SignOutcome.Signed);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Notes" WHERE "Id" = @n""", Ct, ("n", firstNote))).Should().Be(0);
        (await s.Db.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "NoteId" = @n""", Ct, ("n", firstNote))).Should().Be(0);
        (await s.TransfersAsync(intake, Ct)).Single(t => t.TargetField == "medication").Value.Should().Be("Escitalopram 10 mg");
        (await s.AuditAsync(Ct)).Should().Contain($"intake_answers_unlocked {intake} note {firstNote} deleted");
    }

    [Fact]
    public async Task Discard_DraftIntake_RemovesItsAnswersAndNote_AndANewOneCanStart()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var intake = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        var noteId = await s.GenerateNoteAsync(intake, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var foreign = await IntakeNote.DiscardAsync(db, s.OtherProvider, intake, Now, Ct);
        var discarded = await IntakeNote.DiscardAsync(db, s.Provider, intake, Now, Ct);
        var again = await IntakeNote.DiscardAsync(db, s.Provider, intake, Now, Ct);
        var next = await IntakeService.StartAsync(db, s.Provider, s.Patient, Now, Ct);

        // Assert
        foreign.Outcome.Should().Be(IntakeOutcome.NotFound);
        discarded.Outcome.Should().Be(IntakeOutcome.Ok);
        again.Outcome.Should().Be(IntakeOutcome.NotFound);
        next.Outcome.Should().Be(IntakeOutcome.Ok);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Intakes" WHERE "Id" = @i""", Ct, ("i", intake))).Should().Be(0);
        (await s.Db.CountAsync("""SELECT count(*) FROM "IntakeResponses" WHERE "IntakeId" = @i""", Ct, ("i", intake))).Should().Be(0);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Notes" WHERE "Id" = @n""", Ct, ("n", noteId))).Should().Be(0);
        (await s.AuditAsync(Ct)).Should().Contain($"intake_discarded {intake} patient {s.Patient} note {noteId} answers 8");
    }

    [Fact]
    public async Task Discard_SignedIntake_IsRefused()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var (intake, noteId) = await s.SignedIntakeAsync(Ct);
        await using var db = s.CreateDbContext();

        // Act
        var discard = await IntakeNote.DiscardAsync(db, s.Provider, intake, Now, Ct);
        var unlock = await IntakeNote.UnlockAsync(db, s.Provider, intake, Now, Ct);

        // Assert
        discard.Outcome.Should().Be(IntakeOutcome.NotDraft);
        unlock.Outcome.Should().Be(IntakeOutcome.NotDraft);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Notes" WHERE "Id" = @n""", Ct, ("n", noteId))).Should().Be(1);
    }

    [Fact]
    public async Task Sign_AnswerEditedOutOfTheNote_IsNotOfferedForTheRecord()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var intake = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        var noteId = await s.GenerateNoteAsync(intake, Ct);
        await using (var db = s.CreateDbContext())
        {
            var saude = (await NoteQueries.GetAsync(db, s.Provider, noteId, Ct))!.Sections.Single(x => x.Key == "saude");
            await NoteEditing.EditSectionAsync(db, s.Provider, noteId, "saude",
                saude.Content.Replace("Sertralina 50 mg", "Sertralina (dose a confirmar)"), saude.Revision, Now, Ct);
        }

        // Act
        var signed = await s.SignAsync(noteId, Ct);

        // Assert
        signed.Should().Be(SignOutcome.Signed);
        var transfers = await s.TransfersAsync(intake, Ct);
        transfers.Select(t => t.TargetField).Should().NotContain("medication");
        transfers.Should().HaveCount(5);
    }

    [Fact]
    public async Task Sign_AnswerStillInTheSectionButItsLineChanged_IsNotOfferedForTheRecord()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var intake = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        var noteId = await s.GenerateNoteAsync(intake, Ct);
        await using (var db = s.CreateDbContext())
        {
            var saude = (await NoteQueries.GetAsync(db, s.Provider, noteId, Ct))!.Sections.Single(x => x.Key == "saude");
            await NoteEditing.EditSectionAsync(db, s.Provider, noteId, "saude",
                saude.Content.Replace(": Transtorno de ansiedade generalizada", ": nega Transtorno de ansiedade generalizada")
                    + "\nObservação adicional da profissional.",
                saude.Revision, Now, Ct);
        }

        // Act
        var signed = await s.SignAsync(noteId, Ct);

        // Assert
        signed.Should().Be(SignOutcome.Signed);
        var transfers = await s.TransfersAsync(intake, Ct);
        transfers.Select(t => t.TargetField).Should().NotContain("diagnosis");
        transfers.Select(t => t.TargetField).Should().Contain("medication");
        transfers.Should().HaveCount(5);
    }

    [Fact]
    public void ContainsLine_OnlyWholeLinesMatch()
    {
        // Arrange
        const string text = "Medicações em uso: Sertralina 50 mg\nDiagnósticos prévios: nega TAG\r\nAlergias: Nenhuma";

        // Act
        var results = new[]
        {
            IntakeNote.ContainsLine(text, "Medicações em uso: Sertralina 50 mg"),
            IntakeNote.ContainsLine(text, "Diagnósticos prévios: nega TAG"),
            IntakeNote.ContainsLine(text, "Alergias: Nenhuma"),
            IntakeNote.ContainsLine(text, "Diagnósticos prévios: TAG"),
            IntakeNote.ContainsLine(text, "Medicações em uso: Sertralina 50"),
            IntakeNote.ContainsLine(text, "TAG"),
        };

        // Assert
        results.Should().Equal(true, true, true, false, false, false);
    }

    [Fact]
    public void Display_Number_UsesThePtBrDecimalComma()
    {
        // Act
        var values = new[]
        {
            IntakeService.Display(IntakeRules.Number, "7.5"),
            IntakeService.Display(IntakeRules.Number, "-1200.25"),
            IntakeService.Display(IntakeRules.Number, "3"),
        };

        // Assert
        values.Should().Equal("7,5", "-1200,25", "3");
    }

    [Fact]
    public async Task Unlock_DeletesFollowUpsMarkedOnTheDraft_ReopensThoseResolvedInIt_AndLeavesOtherNotesAlone()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var intake = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        var draft = await s.GenerateNoteAsync(intake, Ct);
        var other = Guid.NewGuid();
        await s.Db.ExecuteAsync(
            """INSERT INTO "Notes" ("Id","ProviderId","PatientId","Status","InputMode","Format") VALUES (@id, @p, @pa, 'ReadyForReview', 'Text', 'SOAP')""",
            Ct, ("id", other), ("p", s.Provider), ("pa", s.Patient));
        var onDraft = Guid.NewGuid();
        var onOther = Guid.NewGuid();
        var resolvedInDraft = Guid.NewGuid();
        await s.Db.ExecuteAsync(
            """
            INSERT INTO "FollowUpItems" ("Id","ProviderId","PatientId","NoteId","Description","CreatedAt") VALUES
                (@d, @p, @pa, @draft, 'Retomar sono', @now),
                (@o, @p, @pa, @other, 'Retomar trabalho', @now);
            INSERT INTO "FollowUpItems" ("Id","ProviderId","PatientId","NoteId","Description","CreatedAt","ResolvedAt","ResolvedNoteId","ResolvedById")
                VALUES (@r, @p, @pa, @other, 'Retomar família', @now, @now, @draft, @p);
            """,
            Ct, ("d", onDraft), ("o", onOther), ("r", resolvedInDraft), ("p", s.Provider), ("pa", s.Patient),
            ("draft", draft), ("other", other), ("now", Now));

        // Act
        IntakeResult<bool> unlocked;
        await using (var db = s.CreateDbContext())
        {
            unlocked = await IntakeNote.UnlockAsync(db, s.Provider, intake, Now, Ct);
        }

        // Assert
        unlocked.Outcome.Should().Be(IntakeOutcome.Ok);
        await using var check = s.CreateDbContext();
        var items = await check.FollowUpItems.AsNoTracking().Where(f => f.PatientId == s.Patient).ToListAsync(Ct);
        items.Select(f => f.Id).Should().BeEquivalentTo([onOther, resolvedInDraft]);
        items.Should().OnlyContain(f => f.NoteId == other && f.ResolvedAt == null && f.ResolvedNoteId == null && f.ResolvedById == null);
        (await check.Notes.AnyAsync(n => n.Id == other, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Sign_AnamneseFormatAndIntakeLinkMustAgree()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var orphan = Guid.NewGuid();
        await s.Db.ExecuteAsync(
            """INSERT INTO "Notes" ("Id","ProviderId","PatientId","Status","InputMode","Format") VALUES (@id, @p, @pa, 'ReadyForReview', 'Text', 'Anamnese')""",
            Ct, ("id", orphan), ("p", s.Provider), ("pa", s.Patient));
        await s.Db.ExecuteAsync("""INSERT INTO "NoteSections" ("NoteId","Key","Content") VALUES (@n, 'queixa', 'Texto.')""", Ct, ("n", orphan));
        var intake = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        var noteId = await s.GenerateNoteAsync(intake, Ct);
        await s.Db.ExecuteAsync("""UPDATE "Notes" SET "Format" = 'PtFunctional' WHERE "Id" = @n""", Ct, ("n", noteId));

        // Act
        var orphanSigned = await s.SignAsync(orphan, Ct);
        var relabelled = await s.SignAsync(noteId, Ct);

        // Assert
        orphanSigned.Should().Be(SignOutcome.Locked);
        relabelled.Should().Be(SignOutcome.Locked);
        (await s.IntakeAsync(intake, Ct)).Status.Should().Be("draft");
        await using var db = s.CreateDbContext();
        (await db.Notes.CountAsync(n => n.ProviderId == s.Provider && n.SignedAt != null, Ct)).Should().Be(0);
    }
}
