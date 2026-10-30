using Anamnys.Server.Analysis;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Analysis;

// Real database (design/specs/2026-10-29-theme-absence-design.md §6). Terms live in a private inactive vocabulary
// so the shared dev database's active vocabulary is left alone.
[Collection(SharedAppHostCollection.Name)]
public class ThemeAbsenceQueryTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Day0 = new(2026, 3, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Scenario(PatientSearchSeed seed, AnamnysDbContext db, Guid patientId, Guid vocabularyId) : IAsyncDisposable
    {
        public PatientSearchSeed Seed { get; } = seed;
        public AnamnysDbContext Db { get; } = db;
        public Guid ProviderId => Seed.ProviderId;
        public Guid PatientId { get; } = patientId;
        public Guid VocabularyId { get; } = vocabularyId;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Seed.DisposeAsync();
        }
    }

    private async Task<Scenario> StartAsync()
    {
        var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var db = seed.CreateDbContext();
        var vocabulary = new ThemeVocabulary { Id = Guid.NewGuid(), Version = $"test-{Guid.NewGuid():N}", Active = false };
        db.ThemeVocabularies.Add(vocabulary);
        await db.SaveChangesAsync(Ct);
        return new Scenario(seed, db, patient, vocabulary.Id);
    }

    private static async Task<ThemeTerm> TermAsync(Scenario s, string label, Guid? parent = null, string status = "active")
    {
        var term = new ThemeTerm
        {
            Id = Guid.NewGuid(), VocabularyId = s.VocabularyId, Code = $"t-{Guid.NewGuid():N}", Label = label,
            ParentTermId = parent, IntroducedOn = new DateOnly(2026, 1, 1), Status = status,
        };
        s.Db.ThemeTerms.Add(term);
        await s.Db.SaveChangesAsync(Ct);
        return term;
    }

    private static async Task<List<Note>> NotesAsync(Scenario s, int count, Guid? patientId = null)
    {
        var notes = new List<Note>();
        for (var i = 0; i < count; i++)
        {
            var at = Day0.AddDays(7 * i);
            notes.Add(new Note
            {
                Id = Guid.NewGuid(), ProviderId = s.ProviderId, PatientId = patientId ?? s.PatientId, Status = "Signed", InputMode = "Text", Format = "DAP",
                SignedAt = at, SignedByCrp = "000000/00", CreatedAt = at, UpdatedAt = at,
            });
        }
        s.Db.Notes.AddRange(notes);
        await s.Db.SaveChangesAsync(Ct);
        return notes;
    }

    private static async Task<ExcerptRef> TagAsync(
        Scenario s, Note note, ThemeTerm term, string? quote = null, string createdBy = "human", bool removed = false, bool addedBy = true)
    {
        ExcerptRef? excerpt = null;
        if (quote != null)
        {
            excerpt = new ExcerptRef
            {
                Id = Guid.NewGuid(), ProviderId = s.ProviderId, SourceType = "note", SourceId = note.Id, SectionKey = "data",
                Kind = "exact", OffsetStart = 0, OffsetEnd = quote.Length, Quote = quote, DatedAt = Day0,
            };
            s.Db.ExcerptRefs.Add(excerpt);
            await s.Db.SaveChangesAsync(Ct);
        }
        s.Db.ThemeTags.Add(new ThemeTag
        {
            Id = Guid.NewGuid(), NoteId = note.Id, TermId = term.Id, ExcerptRefId = excerpt?.Id, CreatedBy = createdBy, CreatedAt = Day0,
            AddedById = addedBy ? s.ProviderId : null, RemovedAt = removed ? Day0 : null, RemovedById = removed ? s.ProviderId : null,
        });
        await s.Db.SaveChangesAsync(Ct);
        return excerpt!;
    }

    // Twelve weekly notes shaped like dev-seed/analysis-samples.sql: Sono in 1-8, Ansiedade in 5-12.
    private static async Task<(List<Note> Notes, ThemeTerm Sono, ThemeTerm Ansiedade, ExcerptRef LastSono)> SeedShapedAsync(Scenario s)
    {
        var notes = await NotesAsync(s, 12);
        var sono = await TermAsync(s, "Sono");
        var ansiedade = await TermAsync(s, "Ansiedade");
        ExcerptRef last = null!;
        for (var i = 1; i <= 8; i++)
        {
            var excerpt = await TagAsync(s, notes[i - 1], sono, $"acorda de noite {i}");
            last = excerpt;
        }
        for (var i = 5; i <= 12; i++)
        {
            await TagAsync(s, notes[i - 1], ansiedade, $"aperto no peito {i}");
        }
        return (notes, sono, ansiedade, last);
    }

    [Fact]
    public async Task ExecuteAsync_SonoStopsAfterNoteEight_IsFlaggedWithTheDefaultFour()
    {
        // Arrange
        await using var s = await StartAsync();
        var (notes, sono, _, lastSono) = await SeedShapedAsync(s);

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct, Today);

        // Assert
        result!.AbsenceSessions.Should().Be(4);
        var absence = result.Absences.Should().ContainSingle().Subject;
        absence.TermId.Should().Be(sono.Id);
        absence.RecordedIn.Should().Be(8);
        absence.SpanSessions.Should().Be(8);
        absence.SessionsSince.Should().Be(4);
        absence.FirstOn.Should().Be(result.Sessions![0].On);
        absence.LastOn.Should().Be(result.Sessions![7].On);
        absence.DaysSince.Should().Be(Today.DayNumber - absence.LastOn.DayNumber);
        absence.LastOccurrence.Should().Be(new AbsenceLastOccurrence(notes[7].Id, lastSono.Id, "data", "acorda de noite 8"));
    }

    [Fact]
    public async Task ExecuteAsync_UsesTheProvidersN()
    {
        // Arrange
        await using var s = await StartAsync();
        await SeedShapedAsync(s);
        await s.Db.Providers.Where(p => p.Id == s.ProviderId).ExecuteUpdateAsync(u => u.SetProperty(p => p.AbsenceSessions, 5), Ct);

        // Act
        var five = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct, Today);
        await s.Db.Providers.Where(p => p.Id == s.ProviderId).ExecuteUpdateAsync(u => u.SetProperty(p => p.AbsenceSessions, 2), Ct);
        var two = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct, Today);

        // Assert: with 5 the window starts at note 8, where Sono was still recorded.
        five!.AbsenceSessions.Should().Be(5);
        five.Absences.Should().BeEmpty();
        two!.AbsenceSessions.Should().Be(2);
        two.Absences.Should().ContainSingle().Which.SessionsSince.Should().Be(4);
    }

    [Fact]
    public async Task ExecuteAsync_Absences_IgnoreThePeriod()
    {
        // Arrange
        await using var s = await StartAsync();
        var (_, sono, _, _) = await SeedShapedAsync(s);

        // Act: the period holds the last six sessions, where Sono occurs only twice (notes 7 and 8).
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, new DateOnly(2026, 4, 13), null, Ct, Today);

        // Assert
        result!.Available.Should().BeTrue();
        result.SignedSessions.Should().Be(6);
        result.Themes!.Single(t => t.TermId == sono.Id).Occurrences.Should().HaveCount(2);
        var absence = result.Absences.Should().ContainSingle().Subject;
        absence.TermId.Should().Be(sono.Id);
        absence.RecordedIn.Should().Be(8);
        absence.FirstOn.Should().Be(new DateOnly(2026, 3, 2));
    }

    [Fact]
    public async Task ExecuteAsync_PeriodTooShort_IsUnavailable_ButAbsencesStillCoverTheWholeHistory()
    {
        // Arrange
        await using var s = await StartAsync();
        await SeedShapedAsync(s);

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, new DateOnly(2026, 5, 20), null, Ct, Today);

        // Assert
        result!.Available.Should().BeFalse();
        result.Themes.Should().BeNull();
        result.Absences.Should().ContainSingle().Which.RecordedIn.Should().Be(8);
        result.AbsenceSessions.Should().Be(4);
    }

    [Fact]
    public async Task ExecuteAsync_RemovedAndPendingAiTags_DoNotCountTowardsTheThreshold()
    {
        // Arrange
        await using var s = await StartAsync();
        var notes = await NotesAsync(s, 10);
        var term = await TermAsync(s, "Luto");
        await TagAsync(s, notes[0], term);
        await TagAsync(s, notes[1], term);
        await TagAsync(s, notes[2], term, removed: true);
        await TagAsync(s, notes[3], term, createdBy: "ai", addedBy: false);

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct, Today);

        // Assert
        result!.Absences.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SupersededTermsAndParentsWithRecentChildren_AreNotFlagged_ChildrenAre()
    {
        // Arrange
        await using var s = await StartAsync();
        var notes = await NotesAsync(s, 10);
        var old = await TermAsync(s, "Antigo", status: "superseded");
        var parent = await TermAsync(s, "Humor");
        var recent = await TermAsync(s, "Irritabilidade", parent.Id);
        var stopped = await TermAsync(s, "Tristeza", parent.Id);
        foreach (var i in new[] { 0, 1, 2 })
        {
            await TagAsync(s, notes[i], old);
            await TagAsync(s, notes[i], parent);
            await TagAsync(s, notes[i], stopped);
        }
        foreach (var i in new[] { 0, 1, 9 })
        {
            await TagAsync(s, notes[i], recent);
        }

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct, Today);

        // Assert
        result!.Absences.Select(a => a.TermId).Should().Equal(stopped.Id);
        result.Absences[0].ParentId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task ExecuteAsync_Absences_AreScopedToThePatient()
    {
        // Arrange
        await using var s = await StartAsync();
        var other = await s.Seed.AddPatientAsync("Bia", "Costa", cancellationToken: Ct);
        var term = await TermAsync(s, "Sono");
        var otherNotes = await NotesAsync(s, 10, other);
        foreach (var i in new[] { 0, 1, 2 })
        {
            await TagAsync(s, otherNotes[i], term);
        }
        await NotesAsync(s, 10);

        // Act
        var own = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct, Today);
        var theirs = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, other, null, null, Ct, Today);

        // Assert
        own!.Absences.Should().BeEmpty();
        theirs!.Absences.Should().ContainSingle().Which.TermId.Should().Be(term.Id);
    }

    [Fact]
    public async Task Settings_DefaultToFour_SaveAndRead()
    {
        // Arrange
        await using var s = await StartAsync();

        // Act
        var initial = await AnalysisSettings.GetAsync(s.Db, s.ProviderId, Ct);
        await AnalysisSettings.SaveAsync(s.Db, s.ProviderId, new AnalysisSettingsRequest(7), Ct);
        var saved = await AnalysisSettings.GetAsync(s.Db, s.ProviderId, Ct);

        // Assert
        initial.Should().Be(new AnalysisSettingsDto(4));
        saved.Should().Be(new AnalysisSettingsDto(7));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void Settings_Validate_AcceptsTwoToTwenty(int? value, bool valid)
    {
        // Act
        var errors = new AnalysisSettingsRequest(value).Validate();

        // Assert
        errors.Count.Should().Be(valid ? 0 : 1);
        if (!valid)
        {
            errors.Should().ContainKey("absenceSessions");
        }
    }
}
