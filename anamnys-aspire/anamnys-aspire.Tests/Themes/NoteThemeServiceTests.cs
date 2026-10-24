using Anamnys.Server.Data;
using Anamnys.Server.Themes;
using Anamnys.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Tests.Themes;

[Collection(SharedAppHostCollection.Name)]
public class NoteThemeServiceTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(ScratchDatabase Scratch, Guid ProviderId, Guid PatientId, Guid NoteId);

    private async Task<Seeded> SeedAsync(string status = "Draft", bool archived = false)
    {
        var scratch = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await scratch.OpenAsync(Ct);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "2026-10-21-theme-vocabulary.sql"), Ct);
        await using (var migrate = new NpgsqlCommand(sql, db))
        {
            await migrate.ExecuteNonQueryAsync(Ct);
        }
        var (provider, patient, note) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @e, gen_random_uuid(), 'P')""",
            ("p", provider), ("e", $"{provider:N}@test.local"));
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName","ArchivedAt") VALUES (@pa, @p, 'A', 'B', CASE WHEN @arch THEN now() END)""",
            ("pa", patient), ("p", provider), ("arch", archived));
        await ExecuteAsync(db, """
            INSERT INTO "Notes" ("Id","PatientId","ProviderId","Status","InputMode","SignedAt","SignedByCrp")
            VALUES (@n, @pa, @p, @s, 'Text', CASE WHEN @s = 'Signed' THEN now() END, CASE WHEN @s = 'Signed' THEN '000000/00' END)
            """, ("n", note), ("pa", patient), ("p", provider), ("s", status));
        return new Seeded(scratch, provider, patient, note);
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

    private static Task<Guid> TermAsync(AnamnysDbContext db, string code) =>
        db.ThemeTerms.Where(t => t.Code == code).Select(t => t.Id).SingleAsync(Ct);

    [Fact]
    public async Task AddAsync_ActiveTerm_TagsNoteAndAudits()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");

        // Act
        var result = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct);
        var list = await NoteThemeService.ListAsync(db, s.ProviderId, s.NoteId, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeOutcome.Ok);
        list.Should().ContainSingle();
        list![0].Code.Should().Be("sono");
        list[0].Origin.Should().Be("human");
        list[0].Proposed.Should().BeFalse();
        var audit = await db.AuditEntries.Where(a => a.NoteId == s.NoteId).ToListAsync(Ct);
        audit.Should().ContainSingle(a => a.Action == "theme_added" && a.FieldChanged == "theme"
            && a.PreviousValue == null && a.NewValue == "sono" && a.ActorId == s.ProviderId);
    }

    [Fact]
    public async Task AddAsync_FromSuggestion_RecordsAiOrigin()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");

        // Act
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, true, Now, Ct);
        var list = await NoteThemeService.ListAsync(db, s.ProviderId, s.NoteId, Ct);

        // Assert
        list![0].Origin.Should().Be("ai");
        list[0].Proposed.Should().BeFalse();
    }

    [Fact]
    public async Task AddAsync_SignedNote_IsAllowed()
    {
        // Arrange
        var s = await SeedAsync("Signed");
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");

        // Act
        var result = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeOutcome.Ok);
    }

    [Fact]
    public async Task AddAsync_InactiveOrUnknownTerm_ReturnsTermInactiveAndWritesNothing()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");
        await db.ThemeTerms.Where(t => t.Id == term).ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, "retired"), Ct);

        // Act
        var inactive = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct);
        var unknown = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, Guid.NewGuid(), false, Now, Ct);

        // Assert
        inactive.Outcome.Should().Be(ThemeOutcome.TermInactive);
        unknown.Outcome.Should().Be(ThemeOutcome.TermInactive);
        (await db.ThemeTags.CountAsync(Ct)).Should().Be(0);
        (await db.AuditEntries.CountAsync(a => a.NoteId == s.NoteId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task AddAsync_SameTermTwice_ReturnsAlreadyTaggedAndWritesNothing()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct);

        // Act
        var again = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct);

        // Assert
        again.Outcome.Should().Be(ThemeOutcome.AlreadyTagged);
        (await db.ThemeTags.CountAsync(Ct)).Should().Be(1);
        (await db.AuditEntries.CountAsync(a => a.NoteId == s.NoteId, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task AddAsync_ArchivedPatient_ReturnsArchived()
    {
        // Arrange
        var s = await SeedAsync(archived: true);
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");

        // Act
        var result = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeOutcome.Archived);
        (await db.ThemeTags.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task AddAsync_OtherProvidersNote_ReturnsNotFound()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");

        // Act
        var add = await NoteThemeService.AddAsync(db, Guid.NewGuid(), s.NoteId, term, false, Now, Ct);
        var list = await NoteThemeService.ListAsync(db, Guid.NewGuid(), s.NoteId, Ct);

        // Assert
        add.Outcome.Should().Be(ThemeOutcome.NotFound);
        list.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_ActiveTag_SoftRemovesAndAudits()
    {
        // Arrange
        var s = await SeedAsync("Signed");
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");
        var tagId = (await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct)).Id!.Value;

        // Act
        var result = await NoteThemeService.RemoveAsync(db, s.ProviderId, s.NoteId, tagId, Now.AddMinutes(1), Ct);
        var list = await NoteThemeService.ListAsync(db, s.ProviderId, s.NoteId, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeOutcome.Ok);
        list.Should().BeEmpty();
        var tag = await db.ThemeTags.AsNoTracking().SingleAsync(Ct);
        tag.RemovedAt.Should().Be(Now.AddMinutes(1));
        tag.RemovedById.Should().Be(s.ProviderId);
        (await db.AuditEntries.AnyAsync(a => a.Action == "theme_removed" && a.PreviousValue == "sono" && a.NewValue == null, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAsync_AlreadyRemovedUnknownOrArchived_Refuses()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");
        var tagId = (await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct)).Id!.Value;
        await NoteThemeService.RemoveAsync(db, s.ProviderId, s.NoteId, tagId, Now, Ct);
        var other = (await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct)).Id!.Value;

        // Act
        var again = await NoteThemeService.RemoveAsync(db, s.ProviderId, s.NoteId, tagId, Now, Ct);
        var unknown = await NoteThemeService.RemoveAsync(db, s.ProviderId, s.NoteId, Guid.NewGuid(), Now, Ct);
        await ExecuteAsync(await scratch.OpenAsync(Ct), """UPDATE "Patients" SET "ArchivedAt" = now()""");
        var archived = await NoteThemeService.RemoveAsync(db, s.ProviderId, s.NoteId, other, Now, Ct);

        // Assert
        again.Outcome.Should().Be(ThemeOutcome.NotFound);
        unknown.Outcome.Should().Be(ThemeOutcome.NotFound);
        archived.Outcome.Should().Be(ThemeOutcome.Archived);
        (await db.AuditEntries.CountAsync(a => a.Action == "theme_removed", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task RemoveAsync_ArchivedPatientAndAlreadyRemovedTag_ReturnsArchived()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var term = await TermAsync(db, "sono");
        var tagId = (await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, term, false, Now, Ct)).Id!.Value;
        await NoteThemeService.RemoveAsync(db, s.ProviderId, s.NoteId, tagId, Now, Ct);
        await ExecuteAsync(await scratch.OpenAsync(Ct), """UPDATE "Patients" SET "ArchivedAt" = now()""");

        // Act
        var result = await NoteThemeService.RemoveAsync(db, s.ProviderId, s.NoteId, tagId, Now, Ct);

        // Assert: the archived check runs before the tag lookup, so 409 archived wins over 404.
        result.Outcome.Should().Be(ThemeOutcome.Archived);
    }

    [Fact]
    public async Task ListAsync_AiProposalAndChildTerm_ReportsProposedAndParentLabel()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var child = await db.ThemeTerms.Where(t => t.ParentTermId != null).Select(t => t.Id).FirstAsync(Ct);
        var top = await TermAsync(db, "sono");
        await ExecuteAsync(await scratch.OpenAsync(Ct),
            """INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy") VALUES (@n, @t, 'ai')""", ("n", s.NoteId), ("t", top));
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, child, false, Now, Ct);

        // Act
        var list = await NoteThemeService.ListAsync(db, s.ProviderId, s.NoteId, Ct);

        // Assert
        list.Should().HaveCount(2);
        list!.Single(i => i.Code == "sono").Proposed.Should().BeTrue();
        list.Single(i => i.TermId == child).ParentLabel.Should().NotBeNullOrEmpty();
        list.Single(i => i.TermId == child).Proposed.Should().BeFalse();
    }

    [Fact]
    public async Task ActiveVocabularyAsync_WithSuccessions_ReturnsSuccessorIds()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var from = await TermAsync(db, "sono");
        var to = await TermAsync(db, "ansiedade");
        var staff = Guid.NewGuid();
        await ExecuteAsync(await scratch.OpenAsync(Ct),
            """INSERT INTO "Staff" ("Id","Email","ExternalSubject","Name") VALUES (@s, @e, gen_random_uuid(), 'S')""",
            ("s", staff), ("e", $"{staff:N}@test.local"));
        await ExecuteAsync(await scratch.OpenAsync(Ct),
            """INSERT INTO "ThemeTermSuccessions" ("FromTermId","ToTermId","Kind","StaffId") VALUES (@f, @t, 'split', @s)""",
            ("f", from), ("t", to), ("s", staff));

        // Act
        var terms = await ThemeVocabularyQuery.ActiveVocabularyAsync(db, Ct);

        // Assert
        terms.Should().HaveCount(40);
        terms.Single(t => t.Id == from).SuccessorIds.Should().Equal(to);
        terms.Where(t => t.Id != from).Should().OnlyContain(t => t.SuccessorIds.Count == 0);
    }
}
