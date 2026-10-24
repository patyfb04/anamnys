using Anamnys.Server.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteListTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pending", new[] { "Draft", "ReadyForReview" })]
    [InlineData("signed", new[] { "Signed" })]
    [InlineData("failed", new[] { "Failed", "StructureFailed" })]
    [InlineData(null, new[] { "Draft", "ReadyForReview", "Signed", "Failed", "StructureFailed" })]
    public async Task SearchAsync_StatusFilter_ReturnsMatchingNotes(string? status, string[] expected)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var labels = await AddLabelledNotesAsync(seed, patient);
        await using var db = seed.CreateDbContext();

        // Act
        var response = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(status, null, null, null), Ct);

        // Assert
        response.Items.Select(i => labels[i.Id]).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task SearchAsync_PatientAndName_Filter()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var ana = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var bia = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        await seed.AddNoteAsync(ana, "ReadyForReview", Now, cancellationToken: Ct);
        await seed.AddNoteAsync(bia, "ReadyForReview", Now, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var byPatient = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, bia, null, null), Ct);
        var byName = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, null, "na sil", null), Ct);

        // Assert
        byPatient.Items.Should().ContainSingle().Which.PatientName.Should().Be("Bia Souza");
        byName.Items.Should().ContainSingle().Which.PatientId.Should().Be(ana);
    }

    [Fact]
    public async Task SearchAsync_MoreThanAPage_PagesNewestFirst()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        for (var i = 0; i < NoteList.PageSize + 2; i++)
        {
            await seed.AddNoteAsync(patient, "ReadyForReview", Now.AddMinutes(-i), cancellationToken: Ct);
        }
        await using var db = seed.CreateDbContext();

        // Act
        var first = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, null, null, 1), Ct);
        var second = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, null, null, 2), Ct);

        // Assert
        first.Items.Should().HaveCount(NoteList.PageSize);
        first.HasMore.Should().BeTrue();
        first.Items.Select(i => i.CreatedAt).Should().BeInDescendingOrder();
        first.Items[0].CreatedAt.Should().Be(Now);
        second.Items.Should().HaveCount(2);
        second.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_OtherProvidersNotes_NeverReturned()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var stranger = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await seed.AddNoteAsync(stranger, "ReadyForReview", Now, providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var response = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, stranger, null, null), Ct);

        // Assert
        response.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_NoteOnAppointmentSession_ReturnsAppointmentStart()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), cancellationToken: Ct);
        await using (var setup = seed.CreateDbContext())
        {
            await NoteCreation.CreateAsync(setup, new Anamnys.Server.LiveSession.TranscriptionQueue(), seed.ProviderId,
                new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);
        }
        await using var db = seed.CreateDbContext();

        // Act
        var response = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, patient, null, null), Ct);

        // Assert
        response.Items.Should().ContainSingle().Which.AppointmentStartsAt.Should().Be(Now.AddHours(-2));
    }

    [Theory]
    [InlineData("open", null, "status")]
    [InlineData(null, 0, "page")]
    [InlineData(null, NoteSearchRequest.MaxPage + 1, "page")]
    public void Validate_Invalid_ReportsField(string? status, int? page, string field)
    {
        // Act
        var errors = new NoteSearchRequest(status, null, null, page).Validate();

        // Assert
        errors.Should().ContainKey(field);
    }

    [Fact]
    public void Validate_NameOverLimit_ReportsName()
    {
        // Act
        var errors = new NoteSearchRequest(null, null, new string('a', NoteSearchRequest.MaxName + 1), null).Validate();

        // Assert
        errors.Should().ContainKey("name");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(NoteList.PageSize + 1)]
    public void Validate_PageSizeOutOfRange_ReportsPageSize(int pageSize)
    {
        // Act
        var errors = new NoteSearchRequest(null, null, null, null, pageSize).Validate();

        // Assert
        errors.Should().ContainKey("pageSize");
    }

    [Fact]
    public async Task SearchAsync_PageSizeFour_PagesByFour()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        for (var i = 0; i < 6; i++)
        {
            await seed.AddNoteAsync(patient, "ReadyForReview", Now.AddMinutes(-i), cancellationToken: Ct);
        }
        await using var db = seed.CreateDbContext();

        // Act
        var first = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, patient, null, 1, 4), Ct);
        var second = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, patient, null, 2, 4), Ct);

        // Assert
        first.Items.Should().HaveCount(4);
        first.HasMore.Should().BeTrue();
        first.Items[0].CreatedAt.Should().Be(Now);
        second.Items.Should().HaveCount(2);
        second.HasMore.Should().BeFalse();
        second.Items[0].CreatedAt.Should().Be(Now.AddMinutes(-4));
    }

    [Fact]
    public async Task SearchAsync_TermFilter_ReturnsSignedNotesTaggedWithTermOrChild()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        // Own vocabulary: the shared database is not guaranteed to carry the seeded mh-1 terms.
        var vocabulary = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "ThemeVocabularies" ("Id","Version") VALUES (@v, 'test-' || @v);
            INSERT INTO "ThemeTerms" ("Id","VocabularyId","Code","Label") VALUES (@p, @v, 'pai', 'Pai'), (@u, @v, 'outro', 'Outro');
            INSERT INTO "ThemeTerms" ("Id","VocabularyId","Code","Label","ParentTermId") VALUES (@c, @v, 'filho', 'Filho', @p);
            """,
            Ct, ("v", vocabulary), ("p", parent), ("c", childId), ("u", unrelated));
        try
        {
            var child = new { Id = childId };
            var other = await seed.AddProviderAsync(Ct);
            var mine = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
            var theirs = await seed.AddPatientAsync("Bia", "Souza", providerId: other, cancellationToken: Ct);

            var direct = await AddTaggedNoteAsync(seed, mine, "Signed", parent, null, Now);
            var viaChild = await AddTaggedNoteAsync(seed, mine, "Exported", child.Id, null, Now.AddMinutes(-1));
            var otherTerm = await AddTaggedNoteAsync(seed, mine, "Signed", unrelated, null, Now.AddMinutes(-2));
            var draft = await AddTaggedNoteAsync(seed, mine, "Draft", parent, null, Now.AddMinutes(-3));
            var removed = await AddTaggedNoteAsync(seed, mine, "Signed", parent, Now, Now.AddMinutes(-4));
            var foreign = await AddTaggedNoteAsync(seed, theirs, "Signed", parent, null, Now.AddMinutes(-5), other);

            // Act
            var response = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, null, null, null, null, parent), Ct);
            var childOnly = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, null, null, null, null, child.Id), Ct);
            var combined = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest("pending", null, null, null, null, parent), Ct);

            // Assert
            response.Items.Select(i => i.Id).Should().BeEquivalentTo([direct, viaChild]);
            childOnly.Items.Select(i => i.Id).Should().BeEquivalentTo([viaChild]);
            combined.Items.Should().BeEmpty();
            new[] { otherTerm, draft, removed, foreign }.Should().NotIntersectWith(response.Items.Select(i => i.Id));
        }
        finally
        {
            await seed.ExecuteSqlAsync(
                """
                DELETE FROM "ThemeTags" WHERE "TermId" IN (SELECT "Id" FROM "ThemeTerms" WHERE "VocabularyId" = @v);
                DELETE FROM "ThemeTerms" WHERE "VocabularyId" = @v;
                DELETE FROM "ThemeVocabularies" WHERE "Id" = @v;
                """,
                CancellationToken.None, ("v", vocabulary));
        }
    }

    private async Task<Guid> AddTaggedNoteAsync(
        PatientSearchSeed seed, Guid patient, string status, Guid termId, DateTimeOffset? removedAt, DateTimeOffset createdAt, Guid? providerId = null)
    {
        await seed.AddNoteAsync(patient, status, createdAt, providerId, Ct);
        await using var db = seed.CreateDbContext();
        var noteId = await db.Notes.AsNoTracking().Where(n => n.PatientId == patient && n.CreatedAt == createdAt)
            .Select(n => n.Id).SingleAsync(Ct);
        await seed.ExecuteSqlAsync(
            """INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","RemovedAt","RemovedById") VALUES (@n, @t, 'ai', @r, @rb)""",
            Ct, ("n", noteId), ("t", termId), ("r", (object?)removedAt ?? DBNull.Value), ("rb", removedAt is null ? DBNull.Value : (object)(providerId ?? seed.ProviderId)));
        return noteId;
    }

    // Labels: Draft, ReadyForReview, Signed, Failed, StructureFailed (ReadyForReview + StructureStatus failed).
    private async Task<Dictionary<Guid, string>> AddLabelledNotesAsync(PatientSearchSeed seed, Guid patient)
    {
        foreach (var status in new[] { "Draft", "ReadyForReview", "Signed" })
        {
            await seed.AddNoteAsync(patient, status, Now, cancellationToken: Ct);
        }
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "Notes" ("ProviderId", "PatientId", "Status", "InputMode", "FailureReason") VALUES (@provider, @patient, 'Failed', 'VoiceLive', 'no_speech');
            INSERT INTO "Notes" ("ProviderId", "PatientId", "Status", "InputMode", "StructureStatus", "StructureFailure", "RawTranscript")
            VALUES (@provider, @patient, 'ReadyForReview', 'Text', 'failed', 'model_error', 'x');
            """,
            Ct, ("provider", seed.ProviderId), ("patient", patient));
        await using var db = seed.CreateDbContext();
        var rows = await db.Notes.AsNoTracking().Where(n => n.PatientId == patient)
            .Select(n => new { n.Id, n.Status, n.StructureStatus }).ToListAsync(Ct);
        return rows.ToDictionary(r => r.Id, r => r.StructureStatus == "failed" ? "StructureFailed" : r.Status);
    }
}
