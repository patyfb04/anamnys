using Anamnys.Server.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteEditingTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Edit_Valid_SavesContentRevisionEditedAtAndAudit()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteEditing.EditSectionAsync(db, seed.ProviderId, noteId, "data", "  novo  ", 0, Now, Ct);

        // Assert
        result.Should().Be(new EditResult(EditOutcome.Saved, 1));
        var section = await db.NoteSections.AsNoTracking().SingleAsync(s => s.NoteId == noteId && s.Key == "data", Ct);
        section.Content.Should().Be("novo");
        section.Revision.Should().Be(1);
        section.EditedAt.Should().Be(Now);
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.NoteId == noteId).ToListAsync(Ct);
        var entry = audit.Should().ContainSingle().Subject;
        entry.Action.Should().Be("section_edited");
        entry.FieldChanged.Should().Be("data");
        entry.PreviousValue.Should().Be("a");
        entry.NewValue.Should().Be("novo");
        entry.ActorId.Should().Be(seed.ProviderId);
        entry.At.Should().Be(Now);
    }

    [Fact]
    public async Task Edit_SameContent_NoAuditNoRevisionChange()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteEditing.EditSectionAsync(db, seed.ProviderId, noteId, "data", " a ", 0, Now, Ct);

        // Assert
        result.Should().Be(new EditResult(EditOutcome.Saved, 0));
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == noteId, Ct)).Should().Be(0);
        var section = await db.NoteSections.AsNoTracking().SingleAsync(s => s.NoteId == noteId && s.Key == "data", Ct);
        section.Revision.Should().Be(0);
        section.EditedAt.Should().BeNull();
    }

    [Fact]
    public async Task Edit_StaleRevision_Stale()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteEditing.EditSectionAsync(db, seed.ProviderId, noteId, "data", "novo", 5, Now, Ct);

        // Assert
        result.Should().Be(new EditResult(EditOutcome.Stale, 0));
        var section = await db.NoteSections.AsNoTracking().SingleAsync(s => s.NoteId == noteId && s.Key == "data", Ct);
        section.Content.Should().Be("a");
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == noteId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Edit_PendingStructure_Locked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "pending", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteEditing.EditSectionAsync(db, seed.ProviderId, noteId, "data", "novo", 0, Now, Ct);

        // Assert
        result.Outcome.Should().Be(EditOutcome.Locked);
    }

    [Fact]
    public async Task Edit_SignedNote_Locked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await seed.ExecuteSqlAsync(
            """UPDATE "Notes" SET "Status" = 'Signed', "SignedAt" = now(), "SignedByCrp" = '123/06' WHERE "Id" = @id""", Ct, ("id", noteId));
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteEditing.EditSectionAsync(db, seed.ProviderId, noteId, "data", "novo", 0, Now, Ct);

        // Assert
        result.Outcome.Should().Be(EditOutcome.Locked);
    }

    [Fact]
    public async Task Edit_OtherProvider_NotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        var other = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteEditing.EditSectionAsync(db, other, noteId, "data", "novo", 0, Now, Ct);

        // Assert
        result.Outcome.Should().Be(EditOutcome.NotFound);
    }

    [Fact]
    public async Task Edit_UnknownKey_NotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteEditing.EditSectionAsync(db, seed.ProviderId, noteId, "nope", "novo", 0, Now, Ct);

        // Assert
        result.Outcome.Should().Be(EditOutcome.NotFound);
    }

    [Fact]
    public async Task Edit_TooLong_Invalid()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteEditing.EditSectionAsync(
            db, seed.ProviderId, noteId, "data", new string('x', NoteEditing.MaxContentLength + 1), 0, Now, Ct);

        // Assert
        result.Outcome.Should().Be(EditOutcome.Invalid);
    }

    [Fact]
    public async Task Blank_NoSections_CreatesFormatKeysInOrderAndAudit()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "failed", withSections: false);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteEditing.CreateBlankSectionsAsync(db, seed.ProviderId, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(BlankOutcome.Created);
        var sections = await db.NoteSections.AsNoTracking().Where(s => s.NoteId == noteId).OrderBy(s => s.Position).ToListAsync(Ct);
        sections.Select(s => s.Key).Should().Equal("data", "assessment", "plan");
        sections.Should().OnlyContain(s => s.Content == "" && s.Revision == 0 && s.GeneratedAt == Now);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.StructureStatus.Should().Be("done");
        note.StructureFailure.Should().BeNull();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.NoteId == noteId).ToListAsync(Ct);
        var entry = audit.Should().ContainSingle().Subject;
        entry.Action.Should().Be("sections_created");
        entry.NewValue.Should().Be("data,assessment,plan");
        entry.ActorId.Should().Be(seed.ProviderId);
    }

    [Fact]
    public async Task Blank_HasSections_AlreadyHasSections()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "done", withSections: true);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteEditing.CreateBlankSectionsAsync(db, seed.ProviderId, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(BlankOutcome.AlreadyHasSections);
    }

    [Fact]
    public async Task Blank_Pending_Locked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, structureStatus: "pending", withSections: false);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await NoteEditing.CreateBlankSectionsAsync(db, seed.ProviderId, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(BlankOutcome.Locked);
    }

    private async Task<Guid> AddNoteAsync(PatientSearchSeed seed, string structureStatus, bool withSections)
    {
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var id = Guid.NewGuid();
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "Notes" ("Id", "ProviderId", "PatientId", "Status", "InputMode", "Format", "StructureStatus", "StructureFailure")
            VALUES (@id, @provider, @patient, 'ReadyForReview', 'VoiceLive', 'DAP', @structure, CASE WHEN @structure = 'failed' THEN 'model_error' END)
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
