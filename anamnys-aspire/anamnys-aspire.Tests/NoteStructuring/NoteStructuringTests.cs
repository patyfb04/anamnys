using Anamnys.Server.NoteStructuring;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.NoteStructuring;

[Collection(SharedAppHostCollection.Name)]
public class NoteStructuringTests(SharedAppHostFixture fixture)
{
    private const string Transcript = "Paciente relata insônia. Plano: retorno em uma semana.";
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyList<StructuredSection> DapSections =
    [
        new("data", " Relata insônia. "),
        new("assessment", ""),
        new("plan", "Retorno em uma semana."),
    ];

    private static readonly IReadOnlyList<StructuredSection> SoapSections =
    [
        new("subjective", "Relata insônia."),
        new("objective", ""),
        new("assessment", ""),
        new("plan", "Retorno em uma semana."),
    ];

    [Fact]
    public async Task Process_Success_WritesSectionsInOrderAndDone()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        await using var db = seed.CreateDbContext();
        var structurer = new FakeNoteStructurer((_, _) => DapSections);

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, structurer, noteId, seed.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Done);
        var sections = await db.NoteSections.AsNoTracking().Where(s => s.NoteId == noteId).OrderBy(s => s.Position).ToListAsync(Ct);
        sections.Select(s => s.Key).Should().Equal("data", "assessment", "plan");
        sections.Select(s => s.Content).Should().Equal("Relata insônia.", "", "Retorno em uma semana.");
        sections.Select(s => s.Position).Should().Equal(0, 1, 2);
        sections.Should().OnlyContain(s => s.GeneratedAt == Now && s.EditedAt == null);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.StructureStatus.Should().Be("done");
        note.StructureFailure.Should().BeNull();
    }

    [Fact]
    public async Task Process_Restructure_ReplacesPreviousSections()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        await using var db = seed.CreateDbContext();
        var clock = new FakeTimeProvider(Now);
        await NoteStructuringJob.ProcessAsync(db, new FakeNoteStructurer((_, _) => DapSections), noteId, seed.ProviderId, clock, NullLogger.Instance, Ct);
        await seed.ExecuteSqlAsync(
            """UPDATE "Notes" SET "Format" = 'SOAP', "StructureStatus" = 'pending' WHERE "Id" = @id""", Ct, ("id", noteId));

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, new FakeNoteStructurer((_, _) => SoapSections), noteId, seed.ProviderId, clock, NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Done);
        var keys = await db.NoteSections.AsNoTracking().Where(s => s.NoteId == noteId).OrderBy(s => s.Position).Select(s => s.Key).ToListAsync(Ct);
        keys.Should().Equal("subjective", "objective", "assessment", "plan");
    }

    [Fact]
    public async Task Process_SectionEdited_DiscardsResult()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        await seed.ExecuteSqlAsync(
            """INSERT INTO "NoteSections" ("NoteId", "Key", "Content", "Position", "EditedAt") VALUES (@id, 'data', 'editado', 0, now())""",
            Ct, ("id", noteId));
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, new FakeNoteStructurer((_, _) => DapSections), noteId, seed.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Discarded);
        var sections = await db.NoteSections.AsNoTracking().Where(s => s.NoteId == noteId).ToListAsync(Ct);
        sections.Should().ContainSingle().Which.Content.Should().Be("editado");
        sections[0].EditedAt.Should().NotBeNull();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct)).StructureStatus.Should().Be("done");
    }

    [Fact]
    public async Task Process_StructurerThrows_FailsModelErrorKeepsTranscript()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        await using var db = seed.CreateDbContext();
        var structurer = new FakeNoteStructurer((_, _) => throw new InvalidOperationException("boom"));

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, structurer, noteId, seed.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Failed);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.StructureStatus.Should().Be("failed");
        note.StructureFailure.Should().Be("model_error");
        note.Status.Should().Be("ReadyForReview");
        note.RawTranscript.Should().Be(Transcript);
        (await db.NoteSections.AnyAsync(s => s.NoteId == noteId, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Process_TooLong_FailsTooLong()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        await using var db = seed.CreateDbContext();
        var structurer = new FakeNoteStructurer((_, _) => throw new TranscriptTooLongException());

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, structurer, noteId, seed.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Failed);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.StructureStatus.Should().Be("failed");
        note.StructureFailure.Should().Be("too_long");
    }

    [Fact]
    public async Task Process_WrongKeys_FailsModelError()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        await using var db = seed.CreateDbContext();
        var structurer = new FakeNoteStructurer((_, _) => [new("data", "x"), new("plan", "y")]);

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, structurer, noteId, seed.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Failed);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.StructureStatus.Should().Be("failed");
        note.StructureFailure.Should().Be("model_error");
        (await db.NoteSections.AnyAsync(s => s.NoteId == noteId, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Process_NotPending_Skips()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "done");
        await using var db = seed.CreateDbContext();
        var structurer = new FakeNoteStructurer((_, _) => DapSections);

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, structurer, noteId, seed.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Skipped);
        structurer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Process_OtherProvider_Skips()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        var other = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();
        var structurer = new FakeNoteStructurer((_, _) => DapSections);

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, structurer, noteId, other, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Skipped);
        structurer.Calls.Should().Be(0);
        (await db.NoteSections.AnyAsync(s => s.NoteId == noteId, Ct)).Should().BeFalse();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct)).StructureStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Process_PendingWithEmptyTranscript_ClearsStatusAndSkips()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        await seed.ExecuteSqlAsync("""UPDATE "Notes" SET "RawTranscript" = '  ' WHERE "Id" = @id""", Ct, ("id", noteId));
        await using var db = seed.CreateDbContext();
        var structurer = new FakeNoteStructurer((_, _) => DapSections);

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, structurer, noteId, seed.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Skipped);
        structurer.Calls.Should().Be(0);
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct)).StructureStatus.Should().BeNull();
    }

    private async Task<Guid> AddNoteAsync(PatientSearchSeed seed, string format, string structureStatus)
    {
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var id = Guid.NewGuid();
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "Notes" ("Id", "ProviderId", "PatientId", "Status", "InputMode", "Format", "RawTranscript", "StructureStatus")
            VALUES (@id, @provider, @patient, 'ReadyForReview', 'VoiceLive', @format, @transcript, @structure)
            """,
            Ct, ("id", id), ("provider", seed.ProviderId), ("patient", patient), ("format", format),
            ("transcript", Transcript), ("structure", structureStatus));
        return id;
    }
}
