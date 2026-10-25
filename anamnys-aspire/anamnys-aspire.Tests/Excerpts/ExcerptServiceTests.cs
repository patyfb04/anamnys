using Anamnys.Server.Data;
using Anamnys.Server.Excerpts;
using Anamnys.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Tests.Excerpts;

[Collection(SharedAppHostCollection.Name)]
public class ExcerptServiceTests(SharedAppHostFixture fixture)
{
    private const string Subjective = "Paciente relata insônia.\nDormiu mal a semana toda.";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(ScratchDatabase Scratch, Guid ProviderId, Guid PatientId, Guid NoteId, DateTimeOffset SessionStart);

    private async Task<Seeded> SeedAsync()
    {
        var scratch = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await scratch.OpenAsync(Ct);
        var (provider, patient, note, session) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var start = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @e, gen_random_uuid(), 'P')""",
            ("p", provider), ("e", $"{provider:N}@test.local"));
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'A', 'B')""",
            ("pa", patient), ("p", provider));
        await ExecuteAsync(db, """INSERT INTO "Sessions" ("Id","PatientId","ProviderId","ScheduledStart") VALUES (@s, @pa, @p, @start)""",
            ("s", session), ("pa", patient), ("p", provider), ("start", start));
        await ExecuteAsync(db, """
            INSERT INTO "Notes" ("Id","PatientId","ProviderId","SessionId","Status","InputMode")
            VALUES (@n, @pa, @p, @s, 'Draft', 'Text')
            """, ("n", note), ("pa", patient), ("p", provider), ("s", session));
        await ExecuteAsync(db, """
            INSERT INTO "NoteSections" ("NoteId","Key","Content","Position") VALUES (@n, 'data', '', 0), (@n, 'subjective', @c, 1)
            """, ("n", note), ("c", Subjective));
        return new Seeded(scratch, provider, patient, note, start);
    }

    private static async Task ExecuteAsync(NpgsqlConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<Guid> SaveAsync(AnamnysDbContext db, ExcerptCreation creation)
    {
        await db.SaveChangesAsync(Ct);
        return creation.Ref!.Id;
    }

    [Fact]
    public async Task AddAsync_LocatableQuote_IsExactWithOffsetsAndSessionDate()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var created = await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, null, "dormiu mal", null, null, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        created.Outcome.Should().Be(ExcerptOutcome.Ok);
        created.Ref!.SectionKey.Should().Be("subjective");
        created.Ref.Kind.Should().Be("exact");
        created.Ref.Quote.Should().Be("Dormiu mal");
        Subjective[created.Ref.OffsetStart!.Value..created.Ref.OffsetEnd!.Value].Should().Be("Dormiu mal");
        created.Ref.DatedAt.Should().Be(s.SessionStart);
    }

    [Fact]
    public async Task AddAsync_SessionThatHappened_IsDatedByItsActualStart()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var actual = s.SessionStart.AddMinutes(12);
        await db.Sessions.ExecuteUpdateAsync(u => u.SetProperty(x => x.ActualStart, actual), Ct);

        // Act
        var id = await SaveAsync(db, await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, null, "dormiu mal", null, null, Ct));
        var passage = await ExcerptService.GetPassageAsync(db, s.ProviderId, id, Ct);

        // Assert
        passage!.DatedAt.Should().Be(actual);
        passage.SessionStartsAt.Should().Be(actual);
    }

    [Fact]
    public async Task AddAsync_UnlocatableQuote_IsSectionReferenceToTheFirstNonEmptySection()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var created = await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, null, "texto inexistente", null, null, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        created.Ref!.Kind.Should().Be("section");
        created.Ref.SectionKey.Should().Be("subjective");
        created.Ref.OffsetStart.Should().BeNull();
        created.Ref.Quote.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_Selection_IsExactWithTheSelectedTextAsQuote()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var created = await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, "subjective", null, 9, 15, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        created.Ref!.Kind.Should().Be("exact");
        created.Ref.Quote.Should().Be("relata");
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(5, 5)]
    [InlineData(10, 5)]
    [InlineData(0, 9999)]
    public async Task AddAsync_SelectionOutOfBounds_IsInvalid(int start, int end)
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var created = await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, "subjective", null, start, end, Ct);

        // Assert
        created.Outcome.Should().Be(ExcerptOutcome.InvalidSelection);
        (await db.ExcerptRefs.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task AddAsync_OtherProvider_AndUnknownSection_AreNotFound()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var other = await ExcerptService.AddAsync(db, Guid.NewGuid(), s.NoteId, null, null, null, null, Ct);
        var unknown = await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, "plan", null, null, null, Ct);

        // Assert
        other.Outcome.Should().Be(ExcerptOutcome.NotFound);
        unknown.Outcome.Should().Be(ExcerptOutcome.NotFound);
    }

    [Fact]
    public async Task RelocateSectionAsync_QuoteMovesOrDisappears_ReferenceFollowsAndDegrades()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var id = await SaveAsync(db, await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, null, "dormiu mal", null, null, Ct));

        // Act
        const string moved = "Nova linha antes. Dormiu mal a semana toda.";
        await ExcerptService.RelocateSectionAsync(db, s.NoteId, "subjective", moved, Ct);
        await db.SaveChangesAsync(Ct);
        var afterMove = await db.ExcerptRefs.AsNoTracking().SingleAsync(r => r.Id == id, Ct);
        await ExcerptService.RelocateSectionAsync(db, s.NoteId, "subjective", "Outro texto.", Ct);
        await db.SaveChangesAsync(Ct);
        var afterGone = await db.ExcerptRefs.AsNoTracking().SingleAsync(r => r.Id == id, Ct);

        // Assert
        afterMove.Kind.Should().Be("exact");
        moved[afterMove.OffsetStart!.Value..afterMove.OffsetEnd!.Value].Should().Be("Dormiu mal");
        afterGone.Kind.Should().Be("section");
        afterGone.OffsetStart.Should().BeNull();
        afterGone.OffsetEnd.Should().BeNull();
    }

    [Fact]
    public async Task RelocateNoteAsync_NewSections_QuoteFollowsOrFallsBackToFirstNonEmptySection()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var moved = await SaveAsync(db, await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, null, "dormiu mal", null, null, Ct));
        var gone = await SaveAsync(db, await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, null, "insônia", null, null, Ct));
        var sections = new List<Anamnys.Server.Data.Entities.NoteSection>
        {
            new() { Id = Guid.NewGuid(), NoteId = s.NoteId, Key = "assessment", Content = "", Position = 0 },
            new() { Id = Guid.NewGuid(), NoteId = s.NoteId, Key = "plan", Content = "Retorno. Dormiu mal.", Position = 1 },
        };

        // Act
        var count = await ExcerptService.RelocateNoteAsync(db, s.NoteId, sections, Ct);
        await db.SaveChangesAsync(Ct);
        var afterMoved = await db.ExcerptRefs.AsNoTracking().SingleAsync(r => r.Id == moved, Ct);
        var afterGone = await db.ExcerptRefs.AsNoTracking().SingleAsync(r => r.Id == gone, Ct);

        // Assert
        count.Should().Be(2);
        afterMoved.Kind.Should().Be("exact");
        afterMoved.SectionKey.Should().Be("plan");
        "Retorno. Dormiu mal."[afterMoved.OffsetStart!.Value..afterMoved.OffsetEnd!.Value].Should().Be("Dormiu mal");
        afterGone.Kind.Should().Be("section");
        afterGone.SectionKey.Should().Be("plan");
        afterGone.Quote.Should().Be("insônia");
        afterGone.OffsetStart.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_SelectionSplittingASurrogatePair_IsInvalid()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await using (var conn = await scratch.OpenAsync(Ct)) await ExecuteAsync(conn, """UPDATE "NoteSections" SET "Content" = @c WHERE "NoteId" = @n AND "Key" = 'subjective'""",
            ("c", "Ok 😀 fim"), ("n", s.NoteId));

        // Act
        var splitEnd = await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, "subjective", null, 0, 4, Ct);
        var splitStart = await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, "subjective", null, 4, 8, Ct);
        var whole = await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, "subjective", null, 0, 5, Ct);

        // Assert
        splitEnd.Outcome.Should().Be(ExcerptOutcome.InvalidSelection);
        splitStart.Outcome.Should().Be(ExcerptOutcome.InvalidSelection);
        whole.Outcome.Should().Be(ExcerptOutcome.Ok);
    }

    [Fact]
    public async Task GetPassageAsync_OffsetsNoLongerMatchTheQuote_ReturnsTheSection()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var id = await SaveAsync(db, await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, null, "dormiu mal", null, null, Ct));
        await using (var conn = await scratch.OpenAsync(Ct)) await ExecuteAsync(conn, """UPDATE "NoteSections" SET "Content" = @c WHERE "NoteId" = @n AND "Key" = 'subjective'""",
            ("c", "Texto totalmente diferente aqui, sem a citação."), ("n", s.NoteId));

        // Act
        var passage = await ExcerptService.GetPassageAsync(db, s.ProviderId, id, Ct);

        // Assert
        passage!.Kind.Should().Be("section");
        passage.Start.Should().BeNull();
        passage.End.Should().BeNull();
    }

    [Fact]
    public async Task GetPassageAsync_OwnReference_ReturnsSectionTextOffsetsAndDates()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var id = await SaveAsync(db, await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, null, "dormiu mal", null, null, Ct));

        // Act
        var passage = await ExcerptService.GetPassageAsync(db, s.ProviderId, id, Ct);

        // Assert
        passage.Should().NotBeNull();
        passage!.Kind.Should().Be("exact");
        passage.NoteId.Should().Be(s.NoteId);
        passage.PatientId.Should().Be(s.PatientId);
        passage.SectionKey.Should().Be("subjective");
        passage.SectionTitle.Should().Be("Subjetivo");
        passage.SectionText.Should().Be(Subjective);
        passage.SessionStartsAt.Should().Be(s.SessionStart);
        passage.DatedAt.Should().Be(s.SessionStart);
        Subjective[passage.Start!.Value..passage.End!.Value].Should().Be("Dormiu mal");
    }

    [Fact]
    public async Task GetPassageAsync_OtherProviderOrUnknownId_ReturnsNull()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var id = await SaveAsync(db, await ExcerptService.AddAsync(db, s.ProviderId, s.NoteId, null, null, null, null, Ct));

        // Act
        var other = await ExcerptService.GetPassageAsync(db, Guid.NewGuid(), id, Ct);
        var unknown = await ExcerptService.GetPassageAsync(db, s.ProviderId, Guid.NewGuid(), Ct);

        // Assert
        other.Should().BeNull();
        unknown.Should().BeNull();
    }
}
