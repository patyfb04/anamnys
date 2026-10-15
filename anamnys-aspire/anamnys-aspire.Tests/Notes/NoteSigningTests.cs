using Anamnys.Server.LiveSession;
using Anamnys.Server.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteSigningTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sign_Valid_SignsAndAudits()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await SetCrpAsync(seed, "12345", "06");
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteEditing.SignAsync(db, seed.ProviderId, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(SignOutcome.Signed);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.Status.Should().Be("Signed");
        note.SignedAt.Should().Be(Now);
        note.SignedByCrp.Should().Be("12345/06");
        note.UpdatedAt.Should().Be(Now);
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.NoteId == noteId).ToListAsync(Ct);
        var entry = audit.Should().ContainSingle().Subject;
        entry.Action.Should().Be("signed");
        entry.NewValue.Should().Be("12345/06");
        entry.ActorId.Should().Be(seed.ProviderId);
        entry.At.Should().Be(Now);
    }

    [Fact]
    public async Task Sign_MissingCrp()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteEditing.SignAsync(db, seed.ProviderId, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(SignOutcome.MissingCrp);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.Status.Should().Be("ReadyForReview");
        note.SignedAt.Should().BeNull();
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == noteId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Sign_NoSections()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await SetCrpAsync(seed, "12345", "06");
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: false);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteEditing.SignAsync(db, seed.ProviderId, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(SignOutcome.NoSections);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.SignedAt.Should().BeNull();
        note.Status.Should().Be("ReadyForReview");
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == noteId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Sign_Pending_Locked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await SetCrpAsync(seed, "12345", "06");
        var noteId = await AddNoteAsync(seed, structureStatus: "pending", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteEditing.SignAsync(db, seed.ProviderId, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(SignOutcome.Locked);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.SignedAt.Should().BeNull();
        note.Status.Should().Be("ReadyForReview");
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == noteId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Sign_SectionKeysDifferFromFormat_Locked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await SetCrpAsync(seed, "12345", "06");
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await seed.ExecuteSqlAsync("""UPDATE "Notes" SET "Format" = 'SOAP' WHERE "Id" = @id""", Ct, ("id", noteId));
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteEditing.SignAsync(db, seed.ProviderId, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(SignOutcome.Locked);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.SignedAt.Should().BeNull();
        note.Status.Should().Be("ReadyForReview");
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == noteId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Sign_Twice_SecondLocked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await SetCrpAsync(seed, "12345", "06");
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var first = await NoteEditing.SignAsync(db, seed.ProviderId, noteId, Now, Ct);
        var second = await NoteEditing.SignAsync(db, seed.ProviderId, noteId, Now.AddMinutes(1), Ct);

        // Assert
        first.Should().Be(SignOutcome.Signed);
        second.Should().Be(SignOutcome.Locked);
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == noteId, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Sign_OtherProvider_NotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        var other = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteEditing.SignAsync(db, other, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(SignOutcome.NotFound);
    }

    [Fact]
    public async Task Restructure_UnsignedNoteWithTranscript_IsAccepted()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var restructure = await NoteRestructure.RequestAsync(db, new TranscriptionQueue(), seed.ProviderId, noteId, "DAP", Now, Ct);

        // Assert
        restructure.Should().Be(RestructureOutcome.Accepted);
    }

    [Fact]
    public async Task AfterSign_EditBlankRestructure_AllRefused()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await SetCrpAsync(seed, "12345", "06");
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();
        (await NoteEditing.SignAsync(db, seed.ProviderId, noteId, Now, Ct)).Should().Be(SignOutcome.Signed);

        // Act
        var edit = await NoteEditing.EditSectionAsync(db, seed.ProviderId, noteId, "data", "novo", 0, Now, Ct);
        var blank = await NoteEditing.CreateBlankSectionsAsync(db, seed.ProviderId, noteId, Now, Ct);
        var restructure = await NoteRestructure.RequestAsync(db, new TranscriptionQueue(), seed.ProviderId, noteId, "DAP", Now, Ct);

        // Assert
        edit.Outcome.Should().Be(EditOutcome.Locked);
        blank.Should().Be(BlankOutcome.Locked);
        restructure.Should().Be(RestructureOutcome.Conflict);
    }

    private Task SetCrpAsync(PatientSearchSeed seed, string number, string region) =>
        seed.ExecuteSqlAsync(
            """UPDATE "Providers" SET "CrpNumber" = @number, "CrpRegion" = @region WHERE "Id" = @id""",
            Ct, ("number", number), ("region", region), ("id", seed.ProviderId));

    private async Task<Guid> AddNoteAsync(PatientSearchSeed seed, string structureStatus, bool withSections)
    {
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var id = Guid.NewGuid();
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "Notes" ("Id", "ProviderId", "PatientId", "Status", "InputMode", "Format", "StructureStatus", "RawTranscript")
            VALUES (@id, @provider, @patient, 'ReadyForReview', 'VoiceLive', 'DAP', @structure, 'transcricao da sessao')
            """,
            Ct, ("id", id), ("provider", seed.ProviderId), ("patient", patient), ("structure", structureStatus));
        if (withSections)
        {
            await seed.ExecuteSqlAsync(
                """
                INSERT INTO "NoteSections" ("Id", "NoteId", "Key", "Content", "Position", "GeneratedAt", "Revision") VALUES
                (gen_random_uuid(), @id, 'data', 'a', 0, now(), 0),
                (gen_random_uuid(), @id, 'assessment', '', 1, now(), 0),
                (gen_random_uuid(), @id, 'plan', 'c', 2, now(), 0)
                """,
                Ct, ("id", id));
        }
        return id;
    }
}
