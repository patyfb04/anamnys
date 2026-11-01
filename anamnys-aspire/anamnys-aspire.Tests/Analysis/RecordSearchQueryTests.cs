using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Server.Analysis;
using Anamnys.Server.Data.Entities;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Patients;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Analysis;

// Real database (design/specs/2026-10-31-record-search-design.md §6): a scratch database from the bootstrap script,
// which carries anamnys_pt, so these tests do not depend on the dev database having had the migration applied.
[Collection(SharedAppHostCollection.Name)]
public class RecordSearchQueryTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Day0 = new(2026, 1, 5, 15, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Scenario(RetentionScratch scratch, Guid providerId, Guid patientId, Guid vocabularyId) : IAsyncDisposable
    {
        public RetentionScratch Scratch { get; } = scratch;
        public Guid ProviderId { get; } = providerId;
        public Guid PatientId { get; } = patientId;
        public Guid VocabularyId { get; } = vocabularyId;

        public ValueTask DisposeAsync() => Scratch.DisposeAsync();
    }

    private async Task<Scenario> StartAsync()
    {
        var scratch = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await scratch.AddProviderAsync(Ct, withCrp: true);
        var patient = await scratch.AddPatientAsync(provider, "Ana", Ct);
        await using var db = scratch.CreateDbContext();
        var vocabulary = new ThemeVocabulary { Id = Guid.NewGuid(), Version = $"test-{Guid.NewGuid():N}", Active = false };
        db.ThemeVocabularies.Add(vocabulary);
        await db.SaveChangesAsync(Ct);
        return new Scenario(scratch, provider, patient, vocabulary.Id);
    }

    private static async Task<ThemeTerm> TermAsync(Scenario s, string label, Guid? parent = null)
    {
        await using var db = s.Scratch.CreateDbContext();
        var term = new ThemeTerm
        {
            Id = Guid.NewGuid(), VocabularyId = s.VocabularyId, Code = $"t{Guid.NewGuid():N}", Label = label,
            ParentTermId = parent, IntroducedOn = new DateOnly(2026, 1, 1),
        };
        db.ThemeTerms.Add(term);
        await db.SaveChangesAsync(Ct);
        return term;
    }

    private static async Task<Guid> NoteAsync(
        Scenario s, DateTimeOffset at, string text, string key = "evolucao", bool signed = true, Guid? patientId = null, string? status = null, Guid? providerId = null)
    {
        var note = await s.Scratch.AddNoteAsync(providerId ?? s.ProviderId, patientId ?? s.PatientId, at, Ct, signedAt: signed ? at : null, status: status);
        await SectionAsync(s, note, key, text);
        return note;
    }

    private static Task SectionAsync(Scenario s, Guid note, string key, string text, int position = 0) =>
        s.Scratch.ExecuteAsync(
            """INSERT INTO "NoteSections" ("NoteId","Key","Content","Position") VALUES (@n, @k, @t, @p)""", Ct, ("n", note), ("k", key), ("t", text), ("p", position));

    private static async Task<Guid?> TagAsync(
        Scenario s, Guid note, Guid term, string quote, string kind = "exact", string createdBy = "human", bool confirmed = true, bool removed = false,
        bool withExcerpt = true, string section = "evolucao")
    {
        await using var db = s.Scratch.CreateDbContext();
        var reference = new ExcerptRef
        {
            Id = Guid.NewGuid(), ProviderId = s.ProviderId, SourceType = "note", SourceId = note, SectionKey = section, Kind = kind,
            OffsetStart = kind == "exact" ? 0 : null, OffsetEnd = kind == "exact" ? quote.Length : null, Quote = quote, DatedAt = Day0,
        };
        if (withExcerpt)
        {
            db.ExcerptRefs.Add(reference);
        }
        db.ThemeTags.Add(new ThemeTag
        {
            Id = Guid.NewGuid(), NoteId = note, TermId = term, ExcerptRefId = withExcerpt ? reference.Id : null, CreatedBy = createdBy, CreatedAt = Day0,
            AddedById = confirmed ? s.ProviderId : null, RemovedAt = removed ? Day0 : null, RemovedById = removed ? s.ProviderId : null,
        });
        await db.SaveChangesAsync(Ct);
        return withExcerpt ? reference.Id : null;
    }

    private static async Task<RecordSearchResult?> SearchAsync(
        Scenario s, string question, DateOnly? from = null, DateOnly? to = null, Guid? providerId = null, Guid? patientId = null, int page = 1)
    {
        await using var db = s.Scratch.CreateDbContext();
        return await RecordSearchQuery.ExecuteAsync(db, providerId ?? s.ProviderId, patientId ?? s.PatientId, question, from, to, Ct, page);
    }

    [Theory]
    [InlineData("A relação com a mãe piorou.", "relacao com a mae")]
    [InlineData("A relacao com a mae piorou.", "relação com a mãe")]
    public async Task Text_IsAccentInsensitive(string text, string question)
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0, text);

        // Act
        var result = await SearchAsync(s, question);

        // Assert
        var passage = result!.Passages.Should().ContainSingle().Subject;
        (passage.NoteId, passage.On, passage.Section, passage.Snippet).Should().Be((note, new DateOnly(2026, 1, 5), "evolucao", text));
        passage.MatchedBy.Should().Be(RecordSearchQuery.ByText);
        passage.ExcerptId.Should().BeNull();
        passage.Term.Should().BeNull();
        passage.Highlights.Should().HaveCount(2);
    }

    [Fact]
    public async Task Text_IsStemmed_AndHighlightsTheWordOfTheNote()
    {
        // Arrange
        await using var s = await StartAsync();
        await NoteAsync(s, Day0, "Chegou calma. Teve discussões com o irmão. Saiu melhor.");

        // Act
        var result = await SearchAsync(s, "quando houve uma discussão?");

        // Assert
        var passage = result!.Passages.Should().ContainSingle().Subject;
        passage.Snippet.Should().Be("Teve discussões com o irmão.");
        passage.Highlights.Select(h => passage.Snippet[h.Start..h.End]).Should().Equal("discussões");
    }

    [Theory]
    [InlineData("Escreveu <b>luto</b> & <script>alert(1)</script> no diário.")]
    [InlineData("Falou da auto-estima e do luto, com a<b e c>d.")]
    public async Task Snippet_KeepsTheTextOfTheNoteVerbatim_WithoutInjectingMarkup(string text)
    {
        // Arrange
        await using var s = await StartAsync();
        await NoteAsync(s, Day0, text);

        // Act
        var result = await SearchAsync(s, "luto");

        // Assert
        var passage = result!.Passages.Should().ContainSingle().Subject;
        passage.Snippet.Should().Be(text);
        passage.Highlights.Select(h => passage.Snippet[h.Start..h.End]).Should().Equal("luto");
    }

    [Fact]
    public async Task Text_AHyphenatedWord_IsFound_AndTheTextStaysVerbatim()
    {
        // Arrange
        await using var s = await StartAsync();
        await NoteAsync(s, Day0, "Melhorou a auto-estima no período.");

        // Act
        var result = await SearchAsync(s, "estima");

        // Assert
        var passage = result!.Passages.Should().ContainSingle().Subject;
        passage.Snippet.Should().Be("Melhorou a auto-estima no período.");
    }

    [Theory]
    [InlineData("luto'; DROP TABLE \"Notes\"; --")]
    [InlineData("luto & | ! ( ) <-> : * \\ \"")]
    public async Task Text_QueryOperatorsInTheQuestion_AreJustIgnoredPunctuation(string question)
    {
        // Arrange
        await using var s = await StartAsync();
        await NoteAsync(s, Day0, "Falou do luto do pai e do drop table notes.");

        // Act
        var result = await SearchAsync(s, question);

        // Assert
        result!.Passages.Should().ContainSingle();
    }

    [Fact]
    public async Task Theme_AParentTerm_BringsItsChildrensTags_WithExcerptAndTheChildTerm()
    {
        // Arrange: the text does not contain the word of the question.
        await using var s = await StartAsync();
        var parent = await TermAsync(s, "Bruxismo");
        var child = await TermAsync(s, "Ranger", parent.Id);
        var grandchild = await TermAsync(s, "Ranger noturno", child.Id);
        var note = await NoteAsync(s, Day0, "Acorda com dor na mandíbula.");
        var excerpt = await TagAsync(s, note, grandchild.Id, "dor na mandíbula");

        // Act
        var result = await SearchAsync(s, "quando falamos de bruxismo?");

        // Assert
        var passage = result!.Passages.Should().ContainSingle().Subject;
        passage.Should().BeEquivalentTo(new
        {
            NoteId = note, On = new DateOnly(2026, 1, 5), Section = "evolucao", Snippet = "dor na mandíbula", ExcerptId = excerpt,
            MatchedBy = RecordSearchQuery.ByTheme, Term = new RecordSearchTerm(grandchild.Id, "Ranger noturno"),
        });
        passage.Highlights.Should().BeEmpty();
        result.Total.Should().Be(1);
    }

    [Fact]
    public async Task Theme_AtSeededTerm_MatchesWithoutAccent()
    {
        // Arrange
        await using var s = await StartAsync();
        await using var db = s.Scratch.CreateDbContext();
        var familia = await db.ThemeTerms.AsNoTracking().FirstAsync(t => t.Code == "familia", Ct);
        var note = await NoteAsync(s, Day0, "Discutiu com o irmão.");
        await TagAsync(s, note, familia.Id, "Discutiu com o irmão");

        // Act
        var result = await SearchAsync(s, "familia");

        // Assert
        result!.Passages.Should().ContainSingle().Which.Term.Should().Be(new RecordSearchTerm(familia.Id, familia.Label));
    }

    [Fact]
    public async Task Theme_OnlyCountedTags_Are_Returned()
    {
        // Arrange
        await using var s = await StartAsync();
        var term = await TermAsync(s, "Bruxismo");
        await TagAsync(s, await NoteAsync(s, Day0, "Acorda com dor."), term.Id, "removida", removed: true);
        await TagAsync(s, await NoteAsync(s, Day0, "Acorda com dor."), term.Id, "proposta intocada", createdBy: "ai", confirmed: false);
        await TagAsync(s, await NoteAsync(s, Day0, "Acorda com dor."), term.Id, "sem referência", withExcerpt: false);
        await TagAsync(s, await NoteAsync(s, Day0, "Acorda confirmada com dor."), term.Id, "confirmada", createdBy: "ai");

        // Act
        var result = await SearchAsync(s, "bruxismo");

        // Assert
        result!.Passages.Select(p => p.Snippet).Should().Equal("confirmada");
    }

    [Theory]
    [InlineData("section")]
    [InlineData("exact")]
    public async Task Theme_AReferenceThatLostItsPassage_ShowsTheSection_NotTheOldQuote(string kind)
    {
        // Arrange: the quote was edited away; the reference keeps it, whatever its kind.
        await using var s = await StartAsync();
        var term = await TermAsync(s, "Bruxismo");
        var note = await NoteAsync(s, Day0, "Texto atual da seção. Outra frase.");
        await TagAsync(s, note, term.Id, "citação que saiu do texto", kind: kind);

        // Act
        var result = await SearchAsync(s, "bruxismo");

        // Assert
        result!.Passages.Should().ContainSingle().Which.Snippet.Should().Be("Texto atual da seção.");
    }

    [Fact]
    public async Task Theme_AndText_OfTheSameSection_AreOnePassage_TheThemeOne()
    {
        // Arrange
        await using var s = await StartAsync();
        var term = await TermAsync(s, "Bruxismo");
        var note = await NoteAsync(s, Day0, "Relata bruxismo intenso.");
        await SectionAsync(s, note, "plano", "Avaliar bruxismo com dentista.", position: 1);
        await TagAsync(s, note, term.Id, "bruxismo intenso");

        // Act
        var result = await SearchAsync(s, "bruxismo");

        // Assert
        result!.Total.Should().Be(2);
        result.Passages.Select(p => (p.Section, p.MatchedBy)).Should().Equal(("evolucao", RecordSearchQuery.ByTheme), ("plano", RecordSearchQuery.ByText));
    }

    [Fact]
    public async Task Theme_WordsBeyondTheTerm_RequireTheText_NotTheClosestTag()
    {
        // Arrange
        await using var s = await StartAsync();
        var term = await TermAsync(s, "Bruxismo");
        var note = await NoteAsync(s, Day0, "Relata bruxismo intenso.");
        await TagAsync(s, note, term.Id, "bruxismo intenso");

        // Act
        var result = await SearchAsync(s, "bruxismo na escola");

        // Assert
        result!.Passages.Should().BeEmpty();
        result.Total.Should().Be(0);
    }

    [Fact]
    public async Task AnEvaluativeQuestion_ReturnsThePassagesOfTheTheme_AndNoStatement()
    {
        // Arrange
        await using var s = await StartAsync();
        await NoteAsync(s, Day0, "Dorme pouco e tem sono agitado.");

        // Act
        var result = await SearchAsync(s, "o sono melhorou?");

        // Assert
        result!.Passages.Select(p => p.Snippet).Should().Equal("Dorme pouco e tem sono agitado.");
    }

    [Fact]
    public async Task OnlySignedNotes_AreSearched()
    {
        // Arrange
        await using var s = await StartAsync();
        var term = await TermAsync(s, "Luto");
        var signed = await NoteAsync(s, Day0, "Falou do luto.");
        var exported = await NoteAsync(s, Day0.AddDays(7), "Falou do luto.");
        await s.Scratch.ExecuteAsync(
            """UPDATE "Notes" SET "Status" = 'Exported', "PdfObjectKey" = 'k', "PdfSha256" = repeat('a', 64), "ExportedAt" = now() WHERE "Id" = @n""", Ct, ("n", exported));
        var review = await NoteAsync(s, Day0.AddDays(14), "Falou do luto.", signed: false);
        await TagAsync(s, review, term.Id, "Falou do luto");

        // Act
        var result = await SearchAsync(s, "luto");

        // Assert
        result!.Passages.Select(p => p.NoteId).Should().Equal(signed, exported);
    }

    [Fact]
    public async Task ThePeriod_AndTheDate_FollowTheSessionDateRule()
    {
        // Arrange: signed in March for a session held in January.
        await using var s = await StartAsync();
        var session = await s.Scratch.AddSessionAsync(s.ProviderId, s.PatientId, Day0, Ct, actualStart: Day0);
        var january = await NoteAsync(s, Day0.AddDays(60), "Falou do luto.");
        await s.Scratch.ExecuteAsync("""UPDATE "Notes" SET "SessionId" = @s WHERE "Id" = @n""", Ct, ("s", session), ("n", january));
        var march = await NoteAsync(s, Day0.AddDays(59), "Falou do luto.");

        // Act
        var all = await SearchAsync(s, "luto");
        var onlyJanuary = await SearchAsync(s, "luto", to: new DateOnly(2026, 1, 31));
        var onlyMarch = await SearchAsync(s, "luto", from: new DateOnly(2026, 2, 1), to: new DateOnly(2026, 12, 31));

        // Assert
        all!.Passages.Select(p => (p.NoteId, p.On)).Should().Equal((january, new DateOnly(2026, 1, 5)), (march, new DateOnly(2026, 3, 5)));
        all.Order.Should().Be(RecordSearch.Chronological);
        onlyJanuary!.Passages.Select(p => p.NoteId).Should().Equal(january);
        onlyMarch!.Passages.Select(p => p.NoteId).Should().Equal(march);
    }

    [Fact]
    public async Task TheOrderHint_OrdersThePassages_TextAndThemeTogether()
    {
        // Arrange
        await using var s = await StartAsync();
        var term = await TermAsync(s, "Luto");
        var first = await NoteAsync(s, Day0, "Falou do luto.");
        var second = await NoteAsync(s, Day0.AddDays(7), "Perdeu o avô.");
        await TagAsync(s, second, term.Id, "Perdeu o avô");
        var third = await NoteAsync(s, Day0.AddDays(14), "Falou do luto.");

        // Act
        var earliest = await SearchAsync(s, "primeira vez que falamos de luto");
        var latest = await SearchAsync(s, "última vez que falamos de luto");

        // Assert
        earliest!.Order.Should().Be(RecordSearch.Earliest);
        earliest.Passages.Select(p => p.NoteId).Should().Equal(first, second, third);
        latest!.Order.Should().Be(RecordSearch.Latest);
        latest.Passages.Select(p => p.NoteId).Should().Equal(third, second, first);
    }

    [Fact]
    public async Task TheCap_Is20Passages_TotalCountsThemAll_AndTheOrderChoosesWhichStay()
    {
        // Arrange
        await using var s = await StartAsync();
        var notes = new List<Guid>();
        for (var i = 0; i < 23; i++)
        {
            notes.Add(await NoteAsync(s, Day0.AddDays(i), "Falou do luto."));
        }

        // Act
        var chronological = await SearchAsync(s, "luto");
        var latest = await SearchAsync(s, "última vez luto");

        // Assert
        chronological!.Passages.Should().HaveCount(RecordSearchQuery.PageSize);
        chronological.Total.Should().Be(23);
        chronological.Passages[0].NoteId.Should().Be(notes[0]);
        latest!.Passages.Should().HaveCount(RecordSearchQuery.PageSize);
        latest.Total.Should().Be(23);
        latest.Passages[0].NoteId.Should().Be(notes[22]);

        var last = await SearchAsync(s, "luto", page: 5);
        last!.Page.Should().Be(5);
        last.Total.Should().Be(23);
        last.Passages.Should().HaveCount(3);
        last.Passages[0].NoteId.Should().Be(notes[20]);
    }

    [Fact]
    public async Task AnotherProvidersPatient_OrADiscardedOne_IsNull_AnArchivedOneCanSearch()
    {
        // Arrange
        await using var s = await StartAsync();
        await NoteAsync(s, Day0, "Falou do luto.");
        var other = await s.Scratch.AddProviderAsync(Ct);
        var discarded = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct);
        await NoteAsync(s, Day0, "Falou do luto.", patientId: discarded);
        await s.Scratch.MarkDiscardedAsync(discarded, Day0, Ct);
        var archived = await s.Scratch.AddPatientAsync(s.ProviderId, "Cida", Ct);
        await NoteAsync(s, Day0, "Falou do luto.", patientId: archived);
        await s.Scratch.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = @at WHERE "Id" = @p""", Ct, ("at", Day0), ("p", archived));

        // Act
        var foreign = await SearchAsync(s, "luto", providerId: other);
        var gone = await SearchAsync(s, "luto", patientId: discarded);
        var readOnly = await SearchAsync(s, "luto", patientId: archived);

        // Assert
        foreign.Should().BeNull();
        gone.Should().BeNull();
        readOnly!.Passages.Should().ContainSingle();
    }

    [Fact]
    public async Task OnlyTheChosenPatient_IsSearched()
    {
        // Arrange
        await using var s = await StartAsync();
        var sibling = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct);
        await NoteAsync(s, Day0, "Falou do luto.", patientId: sibling);
        await NoteAsync(s, Day0, "Falou do sono.");

        // Act
        var result = await SearchAsync(s, "luto");

        // Assert
        result!.Passages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("quimioterapia")] // nothing about it
    [InlineData("luto na escola")] // only part of it: the closest passage is not an answer
    [InlineData("quando foi?")] // nothing to look for
    public async Task NothingFound_IsAnEmptyList_NeverTheClosestPassage(string question)
    {
        // Arrange
        await using var s = await StartAsync();
        await NoteAsync(s, Day0, "Falou do luto.");

        // Act
        var result = await SearchAsync(s, question);

        // Assert
        result!.Passages.Should().BeEmpty();
        result.Total.Should().Be(0);
        result.Order.Should().Be(RecordSearch.Chronological);
    }

    [Fact]
    public async Task TwoHundredNotes_StayWithinABound()
    {
        // Arrange
        await using var s = await StartAsync();
        var term = await TermAsync(s, "Luto");
        var filler = string.Join(' ', Enumerable.Repeat("Relata a rotina da semana e fala do luto com calma.", 30));
        await s.Scratch.ExecuteAsync(
            """
            WITH n AS (
              INSERT INTO "Notes" ("ProviderId","PatientId","Status","InputMode","Format","SignedAt","SignedByCrp","CreatedAt")
              SELECT @p, @pa, 'Signed', 'Text', 'PtFunctional', @day + (i || ' days')::interval, '123456/06', @day
                FROM generate_series(1, 200) i
              RETURNING "Id"
            )
            INSERT INTO "NoteSections" ("NoteId","Key","Content","Position")
            SELECT n."Id", k.key, @t, k.pos FROM n CROSS JOIN (VALUES ('evolucao', 0), ('plano', 1), ('observacoes', 2)) AS k(key, pos)
            """, Ct, ("p", s.ProviderId), ("pa", s.PatientId), ("day", Day0.ToUniversalTime()), ("t", filler));
        await using (var db = s.Scratch.CreateDbContext())
        {
            var notes = await db.Notes.AsNoTracking().Where(n => n.PatientId == s.PatientId).Select(n => n.Id).ToListAsync(Ct);
            foreach (var note in notes)
            {
                await TagAsync(s, note, term.Id, "luto");
            }
        }

        // Act
        var clock = Stopwatch.StartNew();
        var result = await SearchAsync(s, "quando falamos de luto?");
        clock.Stop();

        // Assert
        result!.Total.Should().Be(600);
        result.Passages.Should().HaveCount(RecordSearchQuery.PageSize);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("   ab   ")]
    [InlineData("")]
    public void Request_AShortQuestion_IsInvalid(string question) =>
        new RecordSearchRequest(question).Validate().Should().ContainKey("question");

    [Fact]
    public void Request_LengthBounds_AndThePeriod_AreChecked()
    {
        // Act + Assert
        new RecordSearchRequest(null).Validate().Should().ContainKey("question");
        new RecordSearchRequest(new string('a', 3)).Validate().Should().BeEmpty();
        new RecordSearchRequest(new string('a', 300)).Validate().Should().BeEmpty();
        new RecordSearchRequest(new string('a', 301)).Validate().Should().ContainKey("question");
        new RecordSearchRequest("luto", new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)).Validate().Should().ContainKey("from");
    }

    [Fact]
    public void Request_Errors_NeverEchoTheQuestion()
    {
        // Arrange
        var question = new string('x', 301);

        // Act
        var errors = new RecordSearchRequest(question).Validate();

        // Assert
        errors["question"].Should().NotContain(m => m.Contains("xxx"));
    }

    // Over HTTP on the dev database: the refusals reach the search neither for a bad body nor for an unknown patient.
    [Fact]
    public async Task Post_OverHttp_ValidatesTheBody_AndHidesAnotherPatient()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            new Uri("http://localhost:5273/"), "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);

        async Task<HttpStatusCode> PostAsync(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/phi/providers/me/patients/{Guid.NewGuid()}/record-search") { Content = JsonContent.Create(body) };
            using var response = await session.SendAsync(request, Ct);
            return response.StatusCode;
        }

        // Act
        var invalid = await PostAsync(new { question = "ab" });
        var unknown = await PostAsync(new { question = "quando falamos de luto?" });

        // Assert
        invalid.Should().Be(HttpStatusCode.BadRequest);
        unknown.Should().Be(HttpStatusCode.NotFound);
    }

    // Needs design/migrations/2026-10-31-record-search.sql on the dev database.
    [Fact]
    public async Task Post_OverHttp_ReturnsTheJsonShapeTheClientReads()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            new Uri("http://localhost:5273/"), "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var provider = await db.Providers.AsNoTracking().Where(p => p.Email == "dev.provider@anamnys.local").Select(p => p.Id).SingleAsync(Ct);
        var patient = await seed.AddPatientAsync("Teste", "Busca", providerId: provider, cancellationToken: Ct);
        try
        {
            await seed.AddNoteAsync(patient, "Signed", Day0, providerId: provider, cancellationToken: Ct);
            var note = await db.Notes.Where(n => n.PatientId == patient).Select(n => n.Id).SingleAsync(Ct);
            await seed.ExecuteSqlAsync(
                """INSERT INTO "NoteSections" ("NoteId","Key","Content") VALUES (@n, 'evolucao', 'Chegou calma. Falou do luto do pai.')""", Ct, ("n", note));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/phi/providers/me/patients/{patient}/record-search")
            {
                Content = JsonContent.Create(new { question = "última vez que falamos de luto" }),
            };

            // Act
            using var response = await session.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("passages", "total", "order", "page", "pageSize");
            root.GetProperty("total").GetInt32().Should().Be(1);
            root.GetProperty("page").GetInt32().Should().Be(1);
            root.GetProperty("pageSize").GetInt32().Should().Be(RecordSearchQuery.PageSize);
            root.GetProperty("order").GetString().Should().Be("latest");
            var passage = root.GetProperty("passages").EnumerateArray().Should().ContainSingle().Subject;
            passage.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
                "noteId", "on", "section", "snippet", "highlights", "excerptId", "matchedBy", "term");
            passage.GetProperty("noteId").GetGuid().Should().Be(note);
            passage.GetProperty("on").GetString().Should().Be("2026-01-05");
            passage.GetProperty("section").GetString().Should().Be("evolucao");
            passage.GetProperty("snippet").GetString().Should().Be("Falou do luto do pai.");
            var highlight = passage.GetProperty("highlights").EnumerateArray().Should().ContainSingle().Subject;
            (highlight.GetProperty("start").GetInt32(), highlight.GetProperty("end").GetInt32()).Should().Be((9, 13));
            passage.GetProperty("excerptId").ValueKind.Should().Be(JsonValueKind.Null);
            passage.GetProperty("matchedBy").GetString().Should().Be("text");
            passage.GetProperty("term").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            await seed.ExecuteSqlAsync("""DELETE FROM "NoteSections" WHERE "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = @p)""", CancellationToken.None, ("p", patient));
            await seed.ExecuteSqlAsync("""DELETE FROM "Notes" WHERE "PatientId" = @p""", CancellationToken.None, ("p", patient));
            await seed.ExecuteSqlAsync("""DELETE FROM "Patients" WHERE "Id" = @p""", CancellationToken.None, ("p", patient));
        }
    }
}
