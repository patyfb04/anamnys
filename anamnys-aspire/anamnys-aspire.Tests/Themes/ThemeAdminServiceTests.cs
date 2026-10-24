using Anamnys.Server.Data;
using Anamnys.Server.Themes;
using Anamnys.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Tests.Themes;

[Collection(SharedAppHostCollection.Name)]
public class ThemeAdminServiceTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(ScratchDatabase Scratch, Guid StaffId, Guid ProviderId, Guid NoteId);

    private async Task<Seeded> SeedAsync()
    {
        var scratch = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await scratch.OpenAsync(Ct);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "2026-10-21-theme-vocabulary.sql"), Ct);
        await using (var migrate = new NpgsqlCommand(sql, db))
        {
            await migrate.ExecuteNonQueryAsync(Ct);
        }
        var (staff, provider, patient, note) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, """INSERT INTO "Staff" ("Id","ExternalSubject","Email","Name","Role") VALUES (@s, gen_random_uuid(), @e, 'S', 'ops')""",
            ("s", staff), ("e", $"{staff:N}@test.local"));
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @e, gen_random_uuid(), 'P')""",
            ("p", provider), ("e", $"{provider:N}@test.local"));
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'A', 'B')""",
            ("pa", patient), ("p", provider));
        await ExecuteAsync(db, """INSERT INTO "Notes" ("Id","PatientId","ProviderId","Status","InputMode") VALUES (@n, @pa, @p, 'Draft', 'Text')""",
            ("n", note), ("pa", patient), ("p", provider));
        return new Seeded(scratch, staff, provider, note);
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

    private static async Task TagAsync(Seeded s, AnamnysDbContext db, Guid termId)
    {
        var tag = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, termId, false, Now, Ct);
        tag.Outcome.Should().Be(ThemeOutcome.Ok);
    }

    [Fact]
    public async Task ListAsync_TaggedAndSplitTerms_ReturnsCountsAndSuccessors()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var anxiety = await TermAsync(db, "ansiedade");
        await TagAsync(s, db, anxiety);
        var split = await ThemeAdminService.SplitAsync(db, s.StaffId, anxiety,
            [new("ansiedade_generalizada", "Ansiedade generalizada"), new("ansiedade_social", "Ansiedade social")], Now, Ct);

        // Act
        var list = await ThemeAdminService.ListAsync(db, Ct);

        // Assert
        list.Should().HaveCount(40 + 2);
        var old = list.Single(t => t.Id == anxiety);
        old.Status.Should().Be("superseded");
        old.ActiveTagCount.Should().Be(1);
        old.SuccessorIds.Should().BeEquivalentTo(split.Ids);
        list.Single(t => t.Code == "ansiedade_social").ActiveTagCount.Should().Be(0);
        list.Single(t => t.Code == "ansiedade_social").IntroducedOn.Should().Be(new DateOnly(2026, 10, 21));
    }

    [Fact]
    public async Task CreateTermAsync_ValidChild_AddsTermAndChange()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var parent = await TermAsync(db, "trabalho");

        // Act
        var result = await ThemeAdminService.CreateTermAsync(db, s.StaffId, new("trabalho_remoto", " Trabalho remoto "), parent, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Ok);
        await using var check = scratch.CreateDbContext();
        var term = await check.ThemeTerms.SingleAsync(t => t.Id == result.Ids![0], Ct);
        (term.Code, term.Label, term.ParentTermId, term.Status).Should().Be(("trabalho_remoto", "Trabalho remoto", parent, "active"));
        var change = await check.ThemeTermChanges.SingleAsync(c => c.TermId == term.Id, Ct);
        (change.Kind, change.NewLabel, change.StaffId).Should().Be(("add", "Trabalho remoto", s.StaffId));
    }

    [Theory]
    [InlineData("Sono", "Rótulo")]
    [InlineData("a", "Rótulo")]
    [InlineData("novo_tema", "")]
    [InlineData("novo_tema", "   ")]
    public async Task CreateTermAsync_InvalidInput_ReturnsInvalidAndWritesNothing(string code, string label)
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var result = await ThemeAdminService.CreateTermAsync(db, s.StaffId, new(code, label), null, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Invalid);
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.CountAsync(Ct)).Should().Be(40);
        (await check.ThemeTermChanges.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CreateTermAsync_LabelOver80_ReturnsInvalid()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var result = await ThemeAdminService.CreateTermAsync(db, s.StaffId, new("novo_tema", new string('x', 81)), null, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Invalid);
        result.Errors.Should().ContainKey("label");
    }

    [Fact]
    public async Task CreateTermAsync_DuplicateCode_ReturnsConflict()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var result = await ThemeAdminService.CreateTermAsync(db, s.StaffId, new("sono", "Outro sono"), null, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Conflict);
        result.Code.Should().Be("code_taken");
    }

    [Fact]
    public async Task CreateTermAsync_ChildAsParent_ReturnsInvalid()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var child = await TermAsync(db, "assedio_no_trabalho");

        // Act
        var result = await ThemeAdminService.CreateTermAsync(db, s.StaffId, new("neto", "Neto"), child, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Invalid);
        result.Errors.Should().ContainKey("parentId");
    }

    [Fact]
    public async Task RenameAsync_TaggedTerm_ChangesOnlyLabelAndKeepsTags()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var sleep = await TermAsync(db, "sono");
        await TagAsync(s, db, sleep);

        // Act
        var result = await ThemeAdminService.RenameAsync(db, s.StaffId, sleep, "Sono e descanso", Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Ok);
        await using var check = scratch.CreateDbContext();
        var term = await check.ThemeTerms.SingleAsync(t => t.Id == sleep, Ct);
        (term.Code, term.Label, term.Status).Should().Be(("sono", "Sono e descanso", "active"));
        (await check.ThemeTags.CountAsync(t => t.TermId == sleep && t.RemovedAt == null, Ct)).Should().Be(1);
        var change = await check.ThemeTermChanges.SingleAsync(c => c.TermId == sleep, Ct);
        (change.Kind, change.PreviousLabel, change.NewLabel, change.StaffId).Should().Be(("rename", "Sono", "Sono e descanso", s.StaffId));
    }

    [Fact]
    public async Task RenameAsync_SupersededTerm_ReturnsConflict()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var sleep = await TermAsync(db, "sono");
        await ThemeAdminService.RetireAsync(db, s.StaffId, sleep, Now, Ct);

        // Act
        var result = await ThemeAdminService.RenameAsync(db, s.StaffId, sleep, "Outro", Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Conflict);
        result.Code.Should().Be("term_not_active");
    }

    [Fact]
    public async Task RenameAsync_UnknownTerm_ReturnsNotFound()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var result = await ThemeAdminService.RenameAsync(db, s.StaffId, Guid.NewGuid(), "Qualquer", Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.NotFound);
    }

    [Fact]
    public async Task RetireAsync_TermWithActiveChildren_ReturnsConflictAndWritesNothing()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var work = await TermAsync(db, "trabalho");

        // Act
        var result = await ThemeAdminService.RetireAsync(db, s.StaffId, work, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Conflict);
        result.Code.Should().Be("has_active_children");
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.SingleAsync(t => t.Id == work, Ct)).Status.Should().Be("active");
        (await check.ThemeTermChanges.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task RetireAsync_LeafTerm_RetiresKeepsTagsAndRecordsChange()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var sleep = await TermAsync(db, "sono");
        await TagAsync(s, db, sleep);

        // Act
        var result = await ThemeAdminService.RetireAsync(db, s.StaffId, sleep, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Ok);
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.SingleAsync(t => t.Id == sleep, Ct)).Status.Should().Be("retired");
        (await check.ThemeTags.CountAsync(t => t.TermId == sleep && t.RemovedAt == null, Ct)).Should().Be(1);
        (await check.ThemeTermChanges.SingleAsync(c => c.TermId == sleep, Ct)).Kind.Should().Be("retire");
    }

    [Fact]
    public async Task RetireAsync_ParentAfterChildrenRetired_Succeeds()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var work = await TermAsync(db, "trabalho");
        var children = await db.ThemeTerms.Where(t => t.ParentTermId == work).Select(t => t.Id).ToListAsync(Ct);
        foreach (var child in children)
        {
            (await ThemeAdminService.RetireAsync(db, s.StaffId, child, Now, Ct)).Outcome.Should().Be(ThemeAdminOutcome.Ok);
        }

        // Act
        var result = await ThemeAdminService.RetireAsync(db, s.StaffId, work, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Ok);
    }

    [Fact]
    public async Task SplitAsync_TaggedTerm_SupersedesRecordsSuccessionsAndLeavesTags()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var anxiety = await TermAsync(db, "ansiedade");
        await TagAsync(s, db, anxiety);

        // Act
        var result = await ThemeAdminService.SplitAsync(db, s.StaffId, anxiety,
            [new("ansiedade_generalizada", "Ansiedade generalizada"), new("ansiedade_social", "Ansiedade social")], Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Ok);
        result.Ids.Should().HaveCount(2);
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.SingleAsync(t => t.Id == anxiety, Ct)).Status.Should().Be("superseded");
        (await check.ThemeTerms.Where(t => result.Ids!.Contains(t.Id)).ToListAsync(Ct))
            .Should().OnlyContain(t => t.Status == "active" && t.ParentTermId == null);
        var successions = await check.ThemeTermSuccessions.ToListAsync(Ct);
        successions.Should().HaveCount(2).And.OnlyContain(x => x.FromTermId == anxiety && x.Kind == "split" && x.StaffId == s.StaffId);
        successions.Select(x => x.ToTermId).Should().BeEquivalentTo(result.Ids);
        (await check.ThemeTags.CountAsync(t => t.TermId == anxiety && t.RemovedAt == null, Ct)).Should().Be(1);
        (await check.ThemeTermChanges.CountAsync(c => c.TermId == anxiety && c.Kind == "split", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task SplitAsync_ChildTerm_CreatesNewTermsUnderSameParent()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var child = await TermAsync(db, "assedio_no_trabalho");
        var parent = await TermAsync(db, "trabalho");

        // Act
        var result = await ThemeAdminService.SplitAsync(db, s.StaffId, child,
            [new("assedio_moral", "Assédio moral"), new("assedio_sexual", "Assédio sexual")], Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Ok);
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.Where(t => result.Ids!.Contains(t.Id)).ToListAsync(Ct))
            .Should().OnlyContain(t => t.ParentTermId == parent);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public async Task SplitAsync_WrongNumberOfTerms_ReturnsInvalid(int count)
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var anxiety = await TermAsync(db, "ansiedade");
        var terms = Enumerable.Range(0, count).Select(i => new NewTermInput($"parte_{i}", $"Parte {i}")).ToList();

        // Act
        var result = await ThemeAdminService.SplitAsync(db, s.StaffId, anxiety, terms, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Invalid);
    }

    [Fact]
    public async Task SplitAsync_CodeAlreadyInVocabulary_ReturnsConflictAndWritesNothing()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var anxiety = await TermAsync(db, "ansiedade");

        // Act
        var result = await ThemeAdminService.SplitAsync(db, s.StaffId, anxiety,
            [new("sono", "Sono"), new("ansiedade_social", "Ansiedade social")], Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Conflict);
        result.Code.Should().Be("code_taken");
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.SingleAsync(t => t.Id == anxiety, Ct)).Status.Should().Be("active");
        (await check.ThemeTerms.CountAsync(Ct)).Should().Be(40);
    }

    [Fact]
    public async Task MergeAsync_NewTarget_SupersedesSourcesAndPointsThemToTarget()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var a = await TermAsync(db, "assedio_no_trabalho");
        var b = await TermAsync(db, "conflito_no_trabalho");
        var parent = await TermAsync(db, "trabalho");
        await TagAsync(s, db, a);

        // Act
        var result = await ThemeAdminService.MergeAsync(db, s.StaffId, [a, b], new(null, "conflito_e_assedio", "Conflito e assédio"), Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Ok);
        var target = result.Ids![0];
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.SingleAsync(t => t.Id == target, Ct)).ParentTermId.Should().Be(parent);
        var sources = await check.ThemeTerms.Where(t => t.Id == a || t.Id == b).ToListAsync(Ct);
        sources.Should().OnlyContain(t => t.Status == "superseded" && t.SupersededByTermId == target);
        (await check.ThemeTermSuccessions.Where(x => x.ToTermId == target).ToListAsync(Ct))
            .Should().HaveCount(2).And.OnlyContain(x => x.Kind == "merge" && x.StaffId == s.StaffId);
        (await check.ThemeTags.CountAsync(t => t.TermId == a && t.RemovedAt == null, Ct)).Should().Be(1);
        (await check.ThemeTermChanges.CountAsync(c => c.Kind == "merge", Ct)).Should().Be(2);
    }

    [Fact]
    public async Task MergeAsync_ExistingTarget_SupersedesSourcesOnly()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var a = await TermAsync(db, "assedio_no_trabalho");
        var b = await TermAsync(db, "conflito_no_trabalho");
        var target = await TermAsync(db, "sobrecarga_jornada");

        // Act
        var result = await ThemeAdminService.MergeAsync(db, s.StaffId, [a, b], new(target, null, null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Ok);
        result.Ids.Should().Equal(target);
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.SingleAsync(t => t.Id == target, Ct)).Status.Should().Be("active");
        (await check.ThemeTerms.CountAsync(Ct)).Should().Be(40);
    }

    [Fact]
    public async Task MergeAsync_SourcesAtDifferentLevels_ReturnsConflictAndWritesNothing()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var top = await TermAsync(db, "sono");
        var child = await TermAsync(db, "assedio_no_trabalho");

        // Act
        var result = await ThemeAdminService.MergeAsync(db, s.StaffId, [top, child], new(null, "misto", "Misto"), Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Conflict);
        result.Code.Should().Be("level_mismatch");
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.CountAsync(Ct)).Should().Be(40);
        (await check.ThemeTerms.CountAsync(t => t.Status != "active", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task MergeAsync_InactiveSource_ReturnsConflict()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var a = await TermAsync(db, "sono");
        var b = await TermAsync(db, "ansiedade");
        await ThemeAdminService.RetireAsync(db, s.StaffId, a, Now, Ct);

        // Act
        var result = await ThemeAdminService.MergeAsync(db, s.StaffId, [a, b], new(null, "mistura", "Mistura"), Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Conflict);
        result.Code.Should().Be("term_not_active");
    }

    [Fact]
    public async Task SplitAsync_TermWithActiveChildren_ReturnsConflictAndWritesNothing()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var work = await TermAsync(db, "trabalho");

        // Act
        var result = await ThemeAdminService.SplitAsync(db, s.StaffId, work,
            [new("trabalho_a", "Trabalho A"), new("trabalho_b", "Trabalho B")], Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Conflict);
        result.Code.Should().Be("has_active_children");
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.CountAsync(Ct)).Should().Be(40);
        (await check.ThemeTerms.SingleAsync(t => t.Id == work, Ct)).Status.Should().Be("active");
    }

    [Fact]
    public async Task MergeAsync_SourceWithActiveChildren_ReturnsConflictAndWritesNothing()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var work = await TermAsync(db, "trabalho");
        var migration = await TermAsync(db, "imigracao");

        // Act
        var result = await ThemeAdminService.MergeAsync(db, s.StaffId, [work, migration], new(null, "mistura", "Mistura"), Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Conflict);
        result.Code.Should().Be("has_active_children");
        await using var check = scratch.CreateDbContext();
        (await check.ThemeTerms.CountAsync(Ct)).Should().Be(40);
        (await check.ThemeTerms.CountAsync(t => t.Status != "active", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task MergeAsync_TargetWithDifferentParent_ReturnsLevelMismatch()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var a = await TermAsync(db, "assedio_no_trabalho");
        var b = await TermAsync(db, "conflito_no_trabalho");
        var elsewhere = await TermAsync(db, "barreira_linguistica");

        // Act
        var result = await ThemeAdminService.MergeAsync(db, s.StaffId, [a, b], new(elsewhere, null, null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeAdminOutcome.Conflict);
        result.Code.Should().Be("level_mismatch");
    }

    [Fact]
    public async Task MergeAsync_SingleSourceOrNoTarget_ReturnsInvalid()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var a = await TermAsync(db, "sono");
        var b = await TermAsync(db, "ansiedade");

        // Act
        var single = await ThemeAdminService.MergeAsync(db, s.StaffId, [a], new(null, "mistura", "Mistura"), Now, Ct);
        var noTarget = await ThemeAdminService.MergeAsync(db, s.StaffId, [a, b], new(null, null, null), Now, Ct);

        // Assert
        single.Outcome.Should().Be(ThemeAdminOutcome.Invalid);
        noTarget.Outcome.Should().Be(ThemeAdminOutcome.Invalid);
    }
}
