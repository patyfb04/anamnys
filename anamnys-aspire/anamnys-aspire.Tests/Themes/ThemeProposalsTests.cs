using Anamnys.Server.Data;
using Anamnys.Server.NoteStructuring;
using Anamnys.Server.Themes;
using Anamnys.Tests.Data;
using Anamnys.Tests.NoteStructuring;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Anamnys.Tests.Themes;

[Collection(SharedAppHostCollection.Name)]
public class ThemeProposalsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyList<StructuredSection> DapSections =
    [
        new("data", "Relata insônia há duas semanas."),
        new("assessment", ""),
        new("plan", "Retorno em uma semana."),
    ];

    private sealed record Seeded(ScratchDatabase Scratch, Guid ProviderId, Guid NoteId);

    private async Task<Seeded> SeedAsync(string status = "ReadyForReview")
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
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'A', 'B')""",
            ("pa", patient), ("p", provider));
        await ExecuteAsync(db, """
            INSERT INTO "Notes" ("Id","PatientId","ProviderId","Status","InputMode","Format","RawTranscript","StructureStatus","SignedAt","SignedByCrp")
            VALUES (@n, @pa, @p, @s, 'VoiceLive', 'DAP', 'Paciente relata insônia. Retorno em uma semana.', 'pending',
                    CASE WHEN @s = 'Signed' THEN now() END, CASE WHEN @s = 'Signed' THEN '000000/00' END)
            """, ("n", note), ("pa", patient), ("p", provider), ("s", status));
        return new Seeded(scratch, provider, note);
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

    private static async Task<List<string>> ActiveCodesAsync(AnamnysDbContext db, Guid noteId) =>
        (await (
            from tag in db.ThemeTags.AsNoTracking()
            join term in db.ThemeTerms.AsNoTracking() on tag.TermId equals term.Id
            where tag.NoteId == noteId && tag.RemovedAt == null
            select term.Code)
            .ToListAsync(Ct))
        .Order(StringComparer.Ordinal)
        .ToList();

    private static Task ProposeAsync(AnamnysDbContext db, Seeded s, FakeThemeSuggester suggester) =>
        ThemeProposals.ProposeAsync(db, suggester, s.NoteId, s.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

    [Fact]
    public async Task ProcessAsync_DraftStructured_WritesProposalsAndAudits()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var suggester = new FakeThemeSuggester((_, _) => ["sono", "sobrecarga_jornada", "nao_existe"]);

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, new FakeNoteStructurer((_, _) => DapSections), suggester,
            s.NoteId, s.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Done);
        suggester.LastText.Should().Be("Dados: Relata insônia há duas semanas.\nPlano: Retorno em uma semana.");
        suggester.LastTerms.Should().HaveCount(40);
        (await ActiveCodesAsync(db, s.NoteId)).Should().Equal("sobrecarga_jornada", "sono");
        var tags = await db.ThemeTags.AsNoTracking().Where(t => t.NoteId == s.NoteId).ToListAsync(Ct);
        tags.Should().OnlyContain(t => t.CreatedBy == "ai" && t.AddedById == null && t.CreatedAt == Now && t.Confidence == null);
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.NoteId == s.NoteId && a.FieldChanged == "theme").ToListAsync(Ct);
        audit.Should().HaveCount(2).And.OnlyContain(a => a.Action == "theme_proposed" && a.ActorId == null && a.PreviousValue == null);
        audit.Select(a => a.NewValue).Should().BeEquivalentTo("sono", "sobrecarga_jornada");
    }

    [Fact]
    public async Task ProcessAsync_SuggesterThrows_StructuringStaysDone()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var suggester = new FakeThemeSuggester((_, _) => throw new InvalidOperationException("boom"));

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, new FakeNoteStructurer((_, _) => DapSections), suggester,
            s.NoteId, s.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Done);
        suggester.Calls.Should().Be(1);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == s.NoteId, Ct);
        note.StructureStatus.Should().Be("done");
        (await db.NoteSections.AsNoTracking().CountAsync(x => x.NoteId == s.NoteId, Ct)).Should().Be(3);
        (await db.ThemeTags.AsNoTracking().AnyAsync(t => t.NoteId == s.NoteId, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessAsync_StructuringFails_DoesNotSuggest()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var suggester = new FakeThemeSuggester((_, _) => ["sono"]);

        // Act
        var outcome = await NoteStructuringJob.ProcessAsync(db, new FakeNoteStructurer((_, _) => throw new InvalidOperationException("boom")), suggester,
            s.NoteId, s.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(StructuringOutcome.Failed);
        suggester.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ProposeAsync_Rerun_KeepsProviderTagsAndReplacesOnlyUntouchedProposals()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await ProposeAsync(db, s, new FakeThemeSuggester((_, _) => ["sono", "luto"]));
        var lutoTerm = await db.ThemeTerms.Where(t => t.Code == "luto").Select(t => t.Id).SingleAsync(Ct);
        var lutoTag = await db.ThemeTags.AsNoTracking().Where(t => t.NoteId == s.NoteId && t.TermId == lutoTerm).Select(t => t.Id).SingleAsync(Ct);
        var ansiedade = await db.ThemeTerms.Where(t => t.Code == "ansiedade").Select(t => t.Id).SingleAsync(Ct);
        var familia = await db.ThemeTerms.Where(t => t.Code == "familia").Select(t => t.Id).SingleAsync(Ct);
        (await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, ansiedade, false, Now, Ct)).Outcome.Should().Be(ThemeOutcome.Ok);
        (await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, familia, true, Now, Ct)).Outcome.Should().Be(ThemeOutcome.Ok);

        // Act
        await ProposeAsync(db, s, new FakeThemeSuggester((_, _) => ["luto", "trabalho", "ansiedade"]));

        // Assert
        (await ActiveCodesAsync(db, s.NoteId)).Should().Equal("ansiedade", "familia", "luto", "trabalho");
        var tags = await db.ThemeTags.AsNoTracking().Where(t => t.NoteId == s.NoteId).ToListAsync(Ct);
        tags.Should().ContainSingle(t => t.TermId == ansiedade).Which.CreatedBy.Should().Be("human");
        tags.Should().ContainSingle(t => t.TermId == familia).Which.AddedById.Should().Be(s.ProviderId);
        tags.Should().ContainSingle(t => t.TermId == lutoTerm).Which.Id.Should().Be(lutoTag);
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.NoteId == s.NoteId && a.ActorId == null && a.FieldChanged == "theme").ToListAsync(Ct);
        audit.Should().ContainSingle(a => a.Action == "theme_proposal_withdrawn" && a.PreviousValue == "sono" && a.NewValue == null);
        audit.Count(a => a.Action == "theme_proposed").Should().Be(3);
    }

    [Fact]
    public async Task ProposeAsync_TermRemovedByProvider_IsNotProposedAgain()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await ProposeAsync(db, s, new FakeThemeSuggester((_, _) => ["sono"]));
        var tag = await db.ThemeTags.AsNoTracking().SingleAsync(t => t.NoteId == s.NoteId, Ct);
        await NoteThemeService.RemoveAsync(db, s.ProviderId, s.NoteId, tag.Id, Now, Ct);

        // Act
        await ProposeAsync(db, s, new FakeThemeSuggester((_, _) => ["sono", "luto"]));

        // Assert
        (await db.ThemeTags.AsNoTracking().CountAsync(t => t.NoteId == s.NoteId && t.TermId == tag.TermId, Ct)).Should().Be(1);
        var removed = await db.ThemeTags.AsNoTracking().SingleAsync(t => t.Id == tag.Id, Ct);
        removed.RemovedAt.Should().Be(Now);
        removed.RemovedById.Should().Be(s.ProviderId);
        (await ActiveCodesAsync(db, s.NoteId)).Should().Equal("luto");
    }

    [Fact]
    public async Task ProposeAsync_SignedNote_WritesNothing()
    {
        // Arrange
        var s = await SeedAsync("Signed");
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var suggester = new FakeThemeSuggester((_, _) => ["sono"]);

        // Act
        await ProposeAsync(db, s, suggester);

        // Assert
        suggester.Calls.Should().Be(0);
        (await db.ThemeTags.AsNoTracking().AnyAsync(t => t.NoteId == s.NoteId, Ct)).Should().BeFalse();
        (await db.AuditEntries.AsNoTracking().AnyAsync(a => a.NoteId == s.NoteId, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task ProposeAsync_PatientArchivedWhileModelRuns_WritesNothing()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var suggester = new FakeThemeSuggester((_, _) =>
        {
            using var other = scratch.OpenAsync(Ct).GetAwaiter().GetResult();
            ExecuteAsync(other, """UPDATE "Patients" SET "ArchivedAt" = now() WHERE "ProviderId" = @p""", ("p", s.ProviderId)).GetAwaiter().GetResult();
            return ["sono"];
        });

        // Act
        await ProposeAsync(db, s, suggester);

        // Assert
        suggester.Calls.Should().Be(1);
        (await db.ThemeTags.AsNoTracking().AnyAsync(t => t.NoteId == s.NoteId, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task ProposeAsync_OtherProvider_WritesNothing()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var suggester = new FakeThemeSuggester((_, _) => ["sono"]);

        // Act
        await ThemeProposals.ProposeAsync(db, suggester, s.NoteId, Guid.NewGuid(), new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        suggester.Calls.Should().Be(0);
        (await db.ThemeTags.AsNoTracking().AnyAsync(t => t.NoteId == s.NoteId, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task SuggestAsync_TaggedTerms_AreExcludedAndNothingIsWritten()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var sono = await db.ThemeTerms.Where(t => t.Code == "sono").Select(t => t.Id).SingleAsync(Ct);
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, sono, false, Now, Ct);
        var suggester = new FakeThemeSuggester((_, _) => ["sono", "luto"]);

        // Act
        var result = await NoteThemeService.SuggestAsync(db, suggester, s.ProviderId, s.NoteId, NullLogger.Instance, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeSuggestOutcome.Ok);
        result.Terms.Select(t => t.Code).Should().Equal("luto");
        suggester.LastText.Should().Be("Paciente relata insônia. Retorno em uma semana.");
        suggester.LastTerms.Should().HaveCount(40);
        suggester.LastExcluded.Should().Equal("sono");
        (await ActiveCodesAsync(db, s.NoteId)).Should().Equal("sono");
    }

    [Fact]
    public async Task SuggestAsync_SuggesterThrows_ReturnsFailed()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var suggester = new FakeThemeSuggester((_, _) => throw new InvalidOperationException("boom"));

        // Act
        var result = await NoteThemeService.SuggestAsync(db, suggester, s.ProviderId, s.NoteId, NullLogger.Instance, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeSuggestOutcome.Failed);
    }

    [Fact]
    public async Task SuggestAsync_NoteWithoutTextOrOtherProvider_ReturnsEmptyOrNotFound()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await db.Notes.Where(n => n.Id == s.NoteId).ExecuteUpdateAsync(x => x.SetProperty(n => n.RawTranscript, (string?)null), Ct);
        var suggester = new FakeThemeSuggester((_, _) => ["sono"]);

        // Act
        var empty = await NoteThemeService.SuggestAsync(db, suggester, s.ProviderId, s.NoteId, NullLogger.Instance, Ct);
        var other = await NoteThemeService.SuggestAsync(db, suggester, Guid.NewGuid(), s.NoteId, NullLogger.Instance, Ct);

        // Assert
        empty.Outcome.Should().Be(ThemeSuggestOutcome.Ok);
        empty.Terms.Should().BeEmpty();
        other.Outcome.Should().Be(ThemeSuggestOutcome.NotFound);
        suggester.Calls.Should().Be(0);
    }
}
