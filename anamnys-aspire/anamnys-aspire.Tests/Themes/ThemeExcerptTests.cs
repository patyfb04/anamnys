using Anamnys.Server.Data;
using Anamnys.Server.Notes;
using Anamnys.Server.Themes;
using Anamnys.Tests.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Anamnys.Tests.Themes;

// Themes point at their passage (design/specs/2026-10-24-excerpt-references-design.md §3, §6).
[Collection(SharedAppHostCollection.Name)]
public class ThemeExcerptTests(SharedAppHostFixture fixture)
{
    private const string Data = "O paciente não dorme há 3 dias.\nTem tido estresse no trabalho.";
    private const string Plan = "Retorno em uma semana.";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            INSERT INTO "Notes" ("Id","PatientId","ProviderId","Status","InputMode","Format","SignedAt","SignedByCrp")
            VALUES (@n, @pa, @p, @s, 'Text', 'DAP', CASE WHEN @s = 'Signed' THEN now() END, CASE WHEN @s = 'Signed' THEN '000000/00' END)
            """, ("n", note), ("pa", patient), ("p", provider), ("s", status));
        await ExecuteAsync(db, """
            INSERT INTO "NoteSections" ("NoteId","Key","Content","Position")
            VALUES (@n, 'data', @d, 0), (@n, 'assessment', '', 1), (@n, 'plan', @pl, 2)
            """, ("n", note), ("d", Data), ("pl", Plan));
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

    private static Task<Guid> TermAsync(AnamnysDbContext db, string code) =>
        db.ThemeTerms.Where(t => t.Code == code).Select(t => t.Id).SingleAsync(Ct);

    private sealed record TagRef(string Code, string? Kind, string? SectionKey, string? Quote, int? Start, int? End, Guid? RefId);

    private static async Task<List<TagRef>> TagRefsAsync(AnamnysDbContext db, Guid noteId) =>
        (await (
            from tag in db.ThemeTags.AsNoTracking()
            join term in db.ThemeTerms.AsNoTracking() on tag.TermId equals term.Id
            join r in db.ExcerptRefs.AsNoTracking() on tag.ExcerptRefId equals r.Id into refs
            from r in refs.DefaultIfEmpty()
            where tag.NoteId == noteId && tag.RemovedAt == null
            select new TagRef(term.Code, r.Kind, r.SectionKey, r.Quote, r.OffsetStart, r.OffsetEnd, (Guid?)r.Id))
            .ToListAsync(Ct))
        .OrderBy(t => t.Code, StringComparer.Ordinal)
        .ToList();

    private static Task ProposeAsync(AnamnysDbContext db, Seeded s, FakeThemeSuggester suggester) =>
        ThemeProposals.ProposeAsync(db, suggester, s.NoteId, s.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

    [Fact]
    public async Task Propose_LocatableQuote_IsExact_UnlocatableIsSection()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var suggester = new FakeThemeSuggester((_, _) => ["sono", "trabalho"],
            new Dictionary<string, string> { ["sono"] = "não  DORME há 3 dias", ["trabalho"] = "frase que não está na nota" });

        // Act
        await ProposeAsync(db, s, suggester);

        // Assert
        var refs = await TagRefsAsync(db, s.NoteId);
        refs.Should().HaveCount(2);
        var sono = refs.Single(r => r.Code == "sono");
        sono.Kind.Should().Be("exact");
        sono.SectionKey.Should().Be("data");
        Data[sono.Start!.Value..sono.End!.Value].Should().Be("não dorme há 3 dias");
        sono.Quote.Should().Be("não dorme há 3 dias");
        var trabalho = refs.Single(r => r.Code == "trabalho");
        trabalho.Kind.Should().Be("section");
        trabalho.SectionKey.Should().Be("data");
        trabalho.Start.Should().BeNull();
    }

    [Fact]
    public async Task Propose_QuoteWithSectionTitle_IsStillLocated()
    {
        // Arrange: the prompt shows "Plano: …", and the model may copy the title.
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var suggester = new FakeThemeSuggester((_, _) => ["sono"], new Dictionary<string, string> { ["sono"] = "Plano: Retorno em uma semana" });

        // Act
        await ProposeAsync(db, s, suggester);

        // Assert
        var sono = (await TagRefsAsync(db, s.NoteId)).Single();
        sono.Kind.Should().Be("exact");
        sono.SectionKey.Should().Be("plan");
        sono.Quote.Should().Be("Retorno em uma semana");
    }

    [Fact]
    public async Task Propose_WithdrawnProposal_RemovesItsReference()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await ProposeAsync(db, s, new FakeThemeSuggester((_, _) => ["sono"], new Dictionary<string, string> { ["sono"] = "não dorme" }));
        var first = (await TagRefsAsync(db, s.NoteId)).Single().RefId;

        // Act
        await ProposeAsync(db, s, new FakeThemeSuggester((_, _) => ["luto"]));

        // Assert
        (await TagRefsAsync(db, s.NoteId)).Select(r => r.Code).Should().Equal("luto");
        (await db.ExcerptRefs.AsNoTracking().AnyAsync(r => r.Id == first, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Add_Selection_IsExactWithSelectedText()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var start = Data.IndexOf("estresse", StringComparison.Ordinal);

        // Act
        var result = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "trabalho"), false,
            new ThemeAnchor("data", start, start + "estresse no trabalho".Length), Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeOutcome.Ok);
        var tag = (await TagRefsAsync(db, s.NoteId)).Single();
        tag.Kind.Should().Be("exact");
        tag.Quote.Should().Be("estresse no trabalho");
        tag.Start.Should().Be(start);
    }

    [Fact]
    public async Task Add_SectionOrNothing_IsSectionReference()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), false, new ThemeAnchor("plan"), Now, Ct);
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "luto"), false, Now, Ct);

        // Assert
        var refs = await TagRefsAsync(db, s.NoteId);
        refs.Single(r => r.Code == "sono").Should().Match<TagRef>(r => r.Kind == "section" && r.SectionKey == "plan" && r.Start == null);
        refs.Single(r => r.Code == "luto").Should().Match<TagRef>(r => r.Kind == "section" && r.SectionKey == "data");
    }

    [Fact]
    public async Task Add_FromSuggestionWithQuote_IsExact()
    {
        // Arrange
        var s = await SeedAsync("Signed");
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), true,
            new ThemeAnchor(Quote: "não dorme há 3 dias"), Now, Ct);

        // Assert
        (await TagRefsAsync(db, s.NoteId)).Single().Should().Match<TagRef>(r => r.Kind == "exact" && r.Quote == "não dorme há 3 dias");
    }

    [Theory]
    [InlineData("data", -1, 4)]
    [InlineData("data", 4, 4)]
    [InlineData("data", 0, 999)]
    [InlineData("assessment", 0, 1)]
    [InlineData("nao_existe", null, null)]
    public async Task Add_InvalidSelectionOrSection_RefusesAndWritesNothing(string key, int? start, int? end)
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var result = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), false,
            new ThemeAnchor(key, start, end), Now, Ct);

        // Assert
        result.Outcome.Should().Be(ThemeOutcome.InvalidSelection);
        (await db.ThemeTags.AsNoTracking().CountAsync(t => t.NoteId == s.NoteId, Ct)).Should().Be(0);
        (await db.ExcerptRefs.AsNoTracking().CountAsync(r => r.SourceId == s.NoteId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task DraftEdit_KeepsOrDegradesTheThemeReference()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), true, new ThemeAnchor(Quote: "não dorme"), Now, Ct);
        int Revision() => db.NoteSections.AsNoTracking().Single(x => x.NoteId == s.NoteId && x.Key == "data").Revision;

        // Act
        const string moved = "Início novo. O paciente não dorme bem.";
        await NoteEditing.EditSectionAsync(db, s.ProviderId, s.NoteId, "data", moved, Revision(), Now, Ct);
        var kept = (await TagRefsAsync(db, s.NoteId)).Single();
        await NoteEditing.EditSectionAsync(db, s.ProviderId, s.NoteId, "data", "Outro assunto.", Revision(), Now, Ct);
        var degraded = (await TagRefsAsync(db, s.NoteId)).Single();

        // Assert
        kept.Kind.Should().Be("exact");
        moved[kept.Start!.Value..kept.End!.Value].Should().Be("não dorme");
        degraded.Kind.Should().Be("section");
        degraded.Start.Should().BeNull();
    }

    [Fact]
    public async Task SignedNote_EditIsLocked_ReferenceUnchanged()
    {
        // Arrange
        var s = await SeedAsync("Signed");
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), true, new ThemeAnchor(Quote: "não dorme"), Now, Ct);
        var before = (await TagRefsAsync(db, s.NoteId)).Single();

        // Act
        var result = await NoteEditing.EditSectionAsync(db, s.ProviderId, s.NoteId, "data", "Outro assunto.", 0, Now, Ct);

        // Assert
        result.Outcome.Should().Be(EditOutcome.Locked);
        (await TagRefsAsync(db, s.NoteId)).Single().Should().Be(before);
    }

    [Fact]
    public async Task List_ReturnsExcerptIdAndKind()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), true, new ThemeAnchor(Quote: "não dorme"), Now, Ct);
        await db.ThemeTags.AddAsync(new Anamnys.Server.Data.Entities.ThemeTag
        {
            Id = Guid.NewGuid(), NoteId = s.NoteId, TermId = await TermAsync(db, "luto"), CreatedBy = "human", CreatedAt = Now, AddedById = s.ProviderId,
        }, Ct);
        await db.SaveChangesAsync(Ct);

        // Act
        var list = await NoteThemeService.ListAsync(db, s.ProviderId, s.NoteId, Ct);

        // Assert
        var sono = list!.Single(i => i.Code == "sono");
        sono.ExcerptKind.Should().Be("exact");
        sono.ExcerptId.Should().Be((await TagRefsAsync(db, s.NoteId)).Single(r => r.Code == "sono").RefId);
        var luto = list.Single(i => i.Code == "luto");
        luto.ExcerptId.Should().BeNull();
        luto.ExcerptKind.Should().BeNull();
    }

    [Fact]
    public async Task Suggest_IsCodesOnly_ProposalsAskForQuotes()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var dialog = new FakeThemeSuggester((_, _) => ["sono", "luto"], new Dictionary<string, string> { ["sono"] = "não dorme" });
        var structuring = new FakeThemeSuggester((_, _) => ["sono"], new Dictionary<string, string> { ["sono"] = "não dorme" });

        // Act
        var result = await NoteThemeService.SuggestAsync(db, dialog, s.ProviderId, s.NoteId, NullLogger.Instance, Ct);
        await ProposeAsync(db, s, structuring);

        // Assert
        result.Terms.Select(t => t.Code).Should().Equal("sono", "luto");
        dialog.LastWithQuotes.Should().BeFalse();
        structuring.LastWithQuotes.Should().BeTrue();
    }

    [Fact]
    public async Task AddResult_ReportsTheReferenceKind()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();

        // Act
        var plain = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), true, Now, Ct);
        var selected = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "luto"), false,
            new ThemeAnchor("plan", 0, 7), Now, Ct);

        // Assert
        plain.ExcerptKind.Should().Be("section");
        selected.ExcerptKind.Should().Be("exact");
    }

    [Fact]
    public async Task Locate_OneTag_LeavesTheOthersAndSkipsRemovedTags()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var sono = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), true, Now, Ct);
        var trabalho = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "trabalho"), true, Now, Ct);
        await NoteThemeService.RemoveAsync(db, s.ProviderId, s.NoteId, trabalho.Id!.Value, Now, Ct);
        var suggester = new FakeThemeSuggester(quotes: new Dictionary<string, string>
        {
            ["sono"] = "não dorme há 3 dias",
            ["trabalho"] = "estresse no trabalho",
        });

        // Act
        var removed = await NoteThemeService.LocateAsync(db, suggester, s.ProviderId, s.NoteId, Now, NullLogger.Instance, Ct, trabalho.Id);
        var one = await NoteThemeService.LocateAsync(db, suggester, s.ProviderId, s.NoteId, Now, NullLogger.Instance, Ct, sono.Id);
        var again = await NoteThemeService.LocateAsync(db, suggester, s.ProviderId, s.NoteId, Now, NullLogger.Instance, Ct, sono.Id);

        // Assert
        removed.Should().Be(new ThemeLocateResult(ThemeLocateOutcome.Ok, 0, 0));
        one.Should().Be(new ThemeLocateResult(ThemeLocateOutcome.Ok, 1, 0));
        again.Should().Be(new ThemeLocateResult(ThemeLocateOutcome.Ok, 0, 0));
        suggester.QuoteCalls.Should().Be(1);
        suggester.LastQuoteCodes.Should().Equal("sono");
        (await TagRefsAsync(db, s.NoteId)).Single().Kind.Should().Be("exact");
    }

    [Fact]
    public async Task LocateWorker_Job_UpgradesTheTag_AndAFailureIsSwallowed()
    {
        // Arrange
        var s = await SeedAsync("Signed");
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var tag = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), true, Now, Ct);
        var services = new ServiceCollection()
            .AddScoped(_ => scratch.CreateDbContext())
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var job = new ThemeLocateJob(s.ProviderId, s.NoteId, tag.Id);
        var failing = new FakeThemeSuggester { QuoteFailure = new InvalidOperationException("boom") };
        var working = new FakeThemeSuggester(quotes: new Dictionary<string, string> { ["sono"] = "não dorme" });

        // Act
        await ThemeLocateWorker.RunAsync(job, scopes, failing, new FakeTimeProvider(Now), NullLogger.Instance, Ct);
        var afterFailure = (await TagRefsAsync(db, s.NoteId)).Single().Kind;
        await ThemeLocateWorker.RunAsync(job, scopes, working, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        afterFailure.Should().Be("section");
        (await TagRefsAsync(db, s.NoteId)).Single().Should().Match<TagRef>(r => r.Kind == "exact" && r.Quote == "não dorme");
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == s.NoteId && a.Action == "theme_excerpt_located", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Locate_SectionReferences_UpgradedWhenFoundAndAudited()
    {
        // Arrange: sono and trabalho point at a section; luto is already exact.
        var s = await SeedAsync("Signed");
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), false, Now, Ct);
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "trabalho"), false, new ThemeAnchor("plan"), Now, Ct);
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "luto"), true, new ThemeAnchor(Quote: "Retorno"), Now, Ct);
        var suggester = new FakeThemeSuggester(quotes: new Dictionary<string, string>
        {
            ["sono"] = "não dorme há 3 dias",
            ["trabalho"] = "estresse no trabalho",
            ["luto"] = "Retorno em uma semana",
        });

        // Act
        var result = await NoteThemeService.LocateAsync(db, suggester, s.ProviderId, s.NoteId, Now, NullLogger.Instance, Ct);

        // Assert
        result.Should().Be(new ThemeLocateResult(ThemeLocateOutcome.Ok, 2, 0));
        suggester.LastQuoteCodes.Should().BeEquivalentTo(["sono", "trabalho"]);
        var refs = await TagRefsAsync(db, s.NoteId);
        refs.Single(r => r.Code == "sono").Should().Match<TagRef>(r => r.Kind == "exact" && r.SectionKey == "data" && r.Quote == "não dorme há 3 dias");
        refs.Single(r => r.Code == "trabalho").Should().Match<TagRef>(r => r.Kind == "exact" && r.SectionKey == "data");
        refs.Single(r => r.Code == "luto").Quote.Should().Be("Retorno");
        var audit = await db.AuditEntries.AsNoTracking()
            .Where(a => a.NoteId == s.NoteId && a.Action == "theme_excerpt_located").Select(a => a.NewValue).ToListAsync(Ct);
        audit.Should().BeEquivalentTo(["sono", "trabalho"]);
    }

    [Fact]
    public async Task Locate_QuoteNotFound_StaysSectionWithoutAudit()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), false, Now, Ct);
        var suggester = new FakeThemeSuggester(quotes: new Dictionary<string, string> { ["sono"] = "texto inventado" });

        // Act
        var result = await NoteThemeService.LocateAsync(db, suggester, s.ProviderId, s.NoteId, Now, NullLogger.Instance, Ct);

        // Assert
        result.Should().Be(new ThemeLocateResult(ThemeLocateOutcome.Ok, 0, 1));
        (await TagRefsAsync(db, s.NoteId)).Single().Kind.Should().Be("section");
        (await db.AuditEntries.AsNoTracking().AnyAsync(a => a.NoteId == s.NoteId && a.Action == "theme_excerpt_located", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Locate_OtherProviderFailureOrNothingPending()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var idle = new FakeThemeSuggester();
        var failing = new FakeThemeSuggester { QuoteFailure = new InvalidOperationException("boom") };

        // Act
        var nothing = await NoteThemeService.LocateAsync(db, idle, s.ProviderId, s.NoteId, Now, NullLogger.Instance, Ct);
        var other = await NoteThemeService.LocateAsync(db, idle, Guid.NewGuid(), s.NoteId, Now, NullLogger.Instance, Ct);
        await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "sono"), false, Now, Ct);
        var failed = await NoteThemeService.LocateAsync(db, failing, s.ProviderId, s.NoteId, Now, NullLogger.Instance, Ct);

        // Assert
        nothing.Should().Be(new ThemeLocateResult(ThemeLocateOutcome.Ok, 0, 0));
        idle.QuoteCalls.Should().Be(0);
        other.Outcome.Should().Be(ThemeLocateOutcome.NotFound);
        failed.Outcome.Should().Be(ThemeLocateOutcome.Failed);
    }

    [Fact]
    public async Task Locate_SectionOnly_SearchesOnlyTheChosenSection()
    {
        // Arrange: the provider put "trabalho" on the plan; the quote is only in the data section.
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var tag = await NoteThemeService.AddAsync(db, s.ProviderId, s.NoteId, await TermAsync(db, "trabalho"), false, new ThemeAnchor("plan"), Now, Ct);
        var suggester = new FakeThemeSuggester(quotes: new Dictionary<string, string> { ["trabalho"] = "estresse no trabalho" });

        // Act
        var only = await NoteThemeService.LocateAsync(db, suggester, s.ProviderId, s.NoteId, Now, NullLogger.Instance, Ct, tag.Id, sectionOnly: true);
        var anywhere = await NoteThemeService.LocateAsync(db, suggester, s.ProviderId, s.NoteId, Now, NullLogger.Instance, Ct, tag.Id);

        // Assert
        only.Located.Should().Be(0);
        anywhere.Located.Should().Be(1);
        (await TagRefsAsync(db, s.NoteId)).Single().Should().Match<TagRef>(r => r.Kind == "exact" && r.SectionKey == "data");
    }

    private static (int? Status, System.Text.Json.JsonElement Body) Read(IResult result)
    {
        var status = (result as IStatusCodeHttpResult)?.StatusCode;
        var value = (result as IValueHttpResult)?.Value;
        return (status, System.Text.Json.JsonSerializer.SerializeToElement(value));
    }

    private static List<ThemeLocateJob> Drain(ThemeLocateQueue queue)
    {
        var jobs = new List<ThemeLocateJob>();
        while (queue.Reader.TryRead(out var job))
        {
            jobs.Add(job);
        }
        return jobs;
    }

    [Fact]
    public async Task AddEndpoint_Created_ReturnsKindAndQueuesALocateUnlessSelected()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var queue = new ThemeLocateQueue();

        // Act
        var accepted = await ThemeEndpoints.AddAsync(db, queue, s.ProviderId, s.NoteId,
            new AddThemeRequest(await TermAsync(db, "sono"), true), Now, Ct);
        var acceptedJobs = Drain(queue);
        var chosen = await ThemeEndpoints.AddAsync(db, queue, s.ProviderId, s.NoteId,
            new AddThemeRequest(await TermAsync(db, "luto"), false, SectionKey: "plan"), Now, Ct);
        var chosenJobs = Drain(queue);
        var selected = await ThemeEndpoints.AddAsync(db, queue, s.ProviderId, s.NoteId,
            new AddThemeRequest(await TermAsync(db, "trabalho"), false, "data", 0, 10), Now, Ct);
        var selectedJobs = Drain(queue);

        // Assert
        var (status, body) = Read(accepted);
        status.Should().Be(201);
        body.GetProperty("excerptKind").GetString().Should().Be("section");
        var id = body.GetProperty("id").GetGuid();
        acceptedJobs.Should().Equal(new ThemeLocateJob(s.ProviderId, s.NoteId, id));
        chosenJobs.Should().ContainSingle().Which.SectionOnly.Should().BeTrue();
        var (selectedStatus, selectedBody) = Read(selected);
        selectedStatus.Should().Be(201);
        selectedBody.GetProperty("excerptKind").GetString().Should().Be("exact");
        selectedJobs.Should().BeEmpty();
        Read(chosen).Status.Should().Be(201);
    }

    [Fact]
    public async Task LocateEndpoint_QueuesANoteWideJob_404ForOthers()
    {
        // Arrange
        var s = await SeedAsync();
        await using var scratch = s.Scratch;
        await using var db = scratch.CreateDbContext();
        var queue = new ThemeLocateQueue();

        // Act
        var ok = await ThemeEndpoints.LocateAsync(db, queue, s.ProviderId, s.NoteId, Ct);
        var okJobs = Drain(queue);
        var other = await ThemeEndpoints.LocateAsync(db, queue, Guid.NewGuid(), s.NoteId, Ct);
        var otherJobs = Drain(queue);

        // Assert
        var (status, body) = Read(ok);
        status.Should().Be(202);
        body.GetProperty("queued").GetBoolean().Should().BeTrue();
        okJobs.Should().Equal(new ThemeLocateJob(s.ProviderId, s.NoteId));
        Read(other).Status.Should().Be(404);
        otherJobs.Should().BeEmpty();
    }
}
