using System.Diagnostics;
using Anamnys.Server.Analysis;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Analysis;

// Real database (design/specs/2026-10-28-case-analysis-design.md §6). Terms live in a private inactive vocabulary
// so the shared dev database's active vocabulary is left alone.
[Collection(SharedAppHostCollection.Name)]
public class CaseAnalysisQueryTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Day0 = new(2026, 3, 2, 15, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Scenario(PatientSearchSeed seed, AnamnysDbContext db, Guid patientId, Guid vocabularyId)
    {
        public PatientSearchSeed Seed { get; } = seed;
        public AnamnysDbContext Db { get; } = db;
        public Guid ProviderId => Seed.ProviderId;
        public Guid PatientId { get; } = patientId;
        public Guid VocabularyId { get; } = vocabularyId;
    }

    private async Task<(Scenario Scenario, IAsyncDisposable Cleanup)> StartAsync()
    {
        var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var db = seed.CreateDbContext();
        var vocabulary = new ThemeVocabulary { Id = Guid.NewGuid(), Version = $"test-{Guid.NewGuid():N}", Active = false };
        db.ThemeVocabularies.Add(vocabulary);
        await db.SaveChangesAsync(Ct);
        return (new Scenario(seed, db, patient, vocabulary.Id), new Cleanup(seed, db));
    }

    private sealed class Cleanup(PatientSearchSeed seed, AnamnysDbContext db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await seed.DisposeAsync();
        }
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

    private static async Task<Note> NoteAsync(
        Scenario s, DateTimeOffset signedAt, string status = "Signed", Guid? sessionId = null, Guid? patientId = null)
    {
        var note = new Note
        {
            Id = Guid.NewGuid(), ProviderId = s.ProviderId, PatientId = patientId ?? s.PatientId, SessionId = sessionId, Status = status, InputMode = "Text", Format = "DAP",
            SignedAt = status is "Signed" or "Exported" ? signedAt : null, SignedByCrp = status is "Signed" or "Exported" ? "000000/00" : null,
            CreatedAt = signedAt, UpdatedAt = signedAt,
            PdfObjectKey = status == "Exported" ? $"notes/test/{Guid.NewGuid()}.pdf" : null, PdfSha256 = status == "Exported" ? new string('a', 64) : null,
            ExportedAt = status == "Exported" ? signedAt : null,
        };
        s.Db.Notes.Add(note);
        await s.Db.SaveChangesAsync(Ct);
        return note;
    }

    private static async Task<ThemeTag> TagAsync(
        Scenario s, Guid noteId, Guid termId, string createdBy = "human", bool removed = false, Guid? excerptId = null, bool addedBy = true)
    {
        var tag = new ThemeTag
        {
            Id = Guid.NewGuid(), NoteId = noteId, TermId = termId, ExcerptRefId = excerptId, CreatedBy = createdBy, CreatedAt = Day0,
            AddedById = addedBy ? s.ProviderId : null, RemovedAt = removed ? Day0 : null, RemovedById = removed ? s.ProviderId : null,
        };
        s.Db.ThemeTags.Add(tag);
        await s.Db.SaveChangesAsync(Ct);
        return tag;
    }

    // Six signed notes, one per week, none of them tagged.
    private static async Task<List<Note>> SixNotesAsync(Scenario s)
    {
        var notes = new List<Note>();
        for (var i = 0; i < 6; i++)
        {
            notes.Add(await NoteAsync(s, Day0.AddDays(7 * i)));
        }
        return notes;
    }

    [Fact]
    public async Task ExecuteAsync_FewerThanSixSignedNotes_IsUnavailableWithCountOnly()
    {
        // Arrange
        var (s, cleanup) = await StartAsync();
        await using var _ = cleanup;
        for (var i = 0; i < 5; i++)
        {
            await NoteAsync(s, Day0.AddDays(7 * i));
        }
        await NoteAsync(s, Day0.AddDays(60), status: "Draft");

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct);

        // Assert
        result.Should().NotBeNull();
        result!.Available.Should().BeFalse();
        result.SignedSessions.Should().Be(5);
        result.MinimumSessions.Should().Be(6);
        result.Sessions.Should().BeNull();
        result.Themes.Should().BeNull();
        result.Cooccurrence.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_SixSignedAndExportedNotes_ListsOnlySignedOrderedByDate()
    {
        // Arrange
        var (s, cleanup) = await StartAsync();
        await using var _ = cleanup;
        var late = await NoteAsync(s, Day0.AddDays(50), status: "Exported");
        var early = await NoteAsync(s, Day0);
        for (var i = 1; i < 5; i++)
        {
            await NoteAsync(s, Day0.AddDays(7 * i));
        }
        await NoteAsync(s, Day0.AddDays(9), status: "ReadyForReview");

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct);

        // Assert
        result!.Available.Should().BeTrue();
        result.SignedSessions.Should().Be(6);
        result.Sessions!.Select(x => x.NoteId).First().Should().Be(early.Id);
        result.Sessions!.Select(x => x.NoteId).Last().Should().Be(late.Id);
        result.Sessions.Select(x => x.On).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ExecuteAsync_DateRule_SessionThenAppointmentThenSigningDayInPracticeTime()
    {
        // Arrange: practice time is UTC-3, so 01:00Z on the 10th is still the 9th.
        var (s, cleanup) = await StartAsync();
        await using var _ = cleanup;
        var withSession = new Session
        {
            Id = Guid.NewGuid(), ProviderId = s.ProviderId, PatientId = s.PatientId, ActualStart = new DateTimeOffset(2026, 4, 10, 1, 0, 0, TimeSpan.Zero),
            ScheduledStart = new DateTimeOffset(2026, 4, 1, 13, 0, 0, TimeSpan.Zero), CreatedAt = Day0,
        };
        var onlyAppointment = new Session { Id = Guid.NewGuid(), ProviderId = s.ProviderId, PatientId = s.PatientId, CreatedAt = Day0 };
        s.Db.Sessions.AddRange(withSession, onlyAppointment);
        await s.Db.SaveChangesAsync(Ct);
        var appointmentOfSession = new Appointment
        {
            Id = Guid.NewGuid(), ProviderId = s.ProviderId, PatientId = s.PatientId, StartsAt = new DateTimeOffset(2026, 4, 2, 13, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 4, 2, 13, 50, 0, TimeSpan.Zero), Timezone = "America/Sao_Paulo", Modality = "online", Status = "attended",
            SessionId = withSession.Id, CreatedAt = Day0,
        };
        var appointmentOnly = new Appointment
        {
            Id = Guid.NewGuid(), ProviderId = s.ProviderId, PatientId = s.PatientId, StartsAt = new DateTimeOffset(2026, 4, 20, 13, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 4, 20, 13, 50, 0, TimeSpan.Zero), Timezone = "America/Sao_Paulo", Modality = "online", Status = "attended",
            SessionId = onlyAppointment.Id, CreatedAt = Day0,
        };
        s.Db.Appointments.AddRange(appointmentOfSession, appointmentOnly);
        await s.Db.SaveChangesAsync(Ct);
        var viaSession = await NoteAsync(s, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), sessionId: withSession.Id);
        var viaAppointment = await NoteAsync(s, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), sessionId: onlyAppointment.Id);
        var viaSigning = await NoteAsync(s, new DateTimeOffset(2026, 5, 1, 1, 30, 0, TimeSpan.Zero));
        for (var i = 0; i < 3; i++)
        {
            await NoteAsync(s, new DateTimeOffset(2026, 5, 10 + i, 15, 0, 0, TimeSpan.Zero));
        }

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct);

        // Assert
        var byNote = result!.Sessions!.ToDictionary(x => x.NoteId);
        byNote[viaSession.Id].On.Should().Be(new DateOnly(2026, 4, 9));
        byNote[viaSession.Id].SessionId.Should().Be(withSession.Id);
        byNote[viaSession.Id].AppointmentId.Should().Be(appointmentOfSession.Id);
        byNote[viaAppointment.Id].On.Should().Be(new DateOnly(2026, 4, 20));
        byNote[viaAppointment.Id].AppointmentId.Should().Be(appointmentOnly.Id);
        byNote[viaSigning.Id].On.Should().Be(new DateOnly(2026, 4, 30));
        byNote[viaSigning.Id].SessionId.Should().BeNull();
        byNote[viaSigning.Id].AppointmentId.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RemovedTagsAndPendingAiProposals_AreExcluded_AcceptedAiCounts()
    {
        // Arrange
        var (s, cleanup) = await StartAsync();
        await using var _ = cleanup;
        var notes = await SixNotesAsync(s);
        var kept = await TermAsync(s, "Sono");
        var removed = await TermAsync(s, "Luto");
        var pending = await TermAsync(s, "Trabalho");
        var accepted = await TermAsync(s, "Família");
        await TagAsync(s, notes[0].Id, kept.Id);
        await TagAsync(s, notes[0].Id, removed.Id, removed: true);
        await TagAsync(s, notes[0].Id, pending.Id, createdBy: "ai", addedBy: false);
        await TagAsync(s, notes[1].Id, accepted.Id, createdBy: "ai", addedBy: true);

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct);

        // Assert
        result!.Themes!.Select(t => t.TermId).Should().BeEquivalentTo([kept.Id, accepted.Id]);
    }

    [Fact]
    public async Task ExecuteAsync_ThemeOccurrences_CarryOriginAndHierarchyAndSuccession()
    {
        // Arrange
        var (s, cleanup) = await StartAsync();
        await using var _ = cleanup;
        var notes = await SixNotesAsync(s);
        var parent = await TermAsync(s, "Humor");
        var child = await TermAsync(s, "Irritabilidade", parent.Id);
        var old = await TermAsync(s, "Ansiedade antiga", status: "superseded");
        var successor = await TermAsync(s, "Ansiedade");
        var staff = new Staff { Id = Guid.NewGuid(), ExternalSubject = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@test.local", Name = "S", Role = "ops", CreatedAt = Day0, UpdatedAt = Day0 };
        s.Db.Staff.Add(staff);
        s.Db.ThemeTermSuccessions.Add(new ThemeTermSuccession { Id = Guid.NewGuid(), FromTermId = old.Id, ToTermId = successor.Id, Kind = "split", At = Day0, StaffId = staff.Id });
        var second = await TermAsync(s, "Aflição");
        s.Db.ThemeTermSuccessions.Add(new ThemeTermSuccession { Id = Guid.NewGuid(), FromTermId = old.Id, ToTermId = second.Id, Kind = "split", At = Day0.AddDays(1), StaffId = staff.Id });
        var excerpt = new ExcerptRef
        {
            Id = Guid.NewGuid(), ProviderId = s.ProviderId, SourceType = "note", SourceId = notes[2].Id, SectionKey = "subjective",
            Kind = "exact", OffsetStart = 0, OffsetEnd = 14, Quote = "Dormiu mal hoje", DatedAt = Day0,
        };
        s.Db.ExcerptRefs.Add(excerpt);
        await s.Db.SaveChangesAsync(Ct);
        await TagAsync(s, notes[2].Id, child.Id, excerptId: excerpt.Id);
        await TagAsync(s, notes[0].Id, old.Id);

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct);

        // Assert
        var childTheme = result!.Themes!.Single(t => t.TermId == child.Id);
        childTheme.ParentId.Should().Be(parent.Id);
        childTheme.Active.Should().BeTrue();
        childTheme.Occurrences.Should().ContainSingle();
        var occurrence = childTheme.Occurrences[0];
        occurrence.NoteId.Should().Be(notes[2].Id);
        occurrence.ExcerptId.Should().Be(excerpt.Id);
        occurrence.Section.Should().Be("subjective");
        occurrence.Quote.Should().Be("Dormiu mal hoje");
        var oldTheme = result.Themes!.Single(t => t.TermId == old.Id);
        oldTheme.Active.Should().BeFalse();
        oldTheme.SupersededBy.Should().Equal(new CaseAnalysisSuperseded(successor.Id, "Ansiedade"), new CaseAnalysisSuperseded(second.Id, "Aflição"));
        oldTheme.Occurrences.Single().ExcerptId.Should().BeNull();
        result.Themes!.Should().NotContain(t => t.TermId == successor.Id);
        var parentTheme = result.Themes!.Single(t => t.TermId == parent.Id);
        parentTheme.Occurrences.Should().BeEmpty();
        parentTheme.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_Cooccurrence_CountsSessionsPerPairOrderedByCountThenLabels()
    {
        // Arrange
        var (s, cleanup) = await StartAsync();
        await using var _ = cleanup;
        var notes = await SixNotesAsync(s);
        var sono = await TermAsync(s, "Sono");
        var luto = await TermAsync(s, "Luto");
        var familia = await TermAsync(s, "Família");
        foreach (var n in notes.Take(3))
        {
            await TagAsync(s, n.Id, sono.Id);
            await TagAsync(s, n.Id, luto.Id);
        }
        await TagAsync(s, notes[3].Id, sono.Id);
        await TagAsync(s, notes[3].Id, familia.Id);
        await TagAsync(s, notes[4].Id, luto.Id);
        await TagAsync(s, notes[4].Id, familia.Id);

        // Act
        var result = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct);

        // Assert: pairs are ordered by label inside (a before b), strongest first, ties by labels.
        result!.Cooccurrence.Should().Equal(
            new CaseAnalysisPair(luto.Id, sono.Id, 3),
            new CaseAnalysisPair(familia.Id, luto.Id, 1),
            new CaseAnalysisPair(familia.Id, sono.Id, 1));
    }

    [Fact]
    public async Task ExecuteAsync_Period_FiltersSessionsByPracticeDayAndAppliesTheMinimum()
    {
        // Arrange
        var (s, cleanup) = await StartAsync();
        await using var _ = cleanup;
        var notes = new List<Note>();
        for (var i = 0; i < 8; i++)
        {
            notes.Add(await NoteAsync(s, Day0.AddDays(10 * i)));
        }
        var term = await TermAsync(s, "Sono");
        await TagAsync(s, notes[0].Id, term.Id);
        await TagAsync(s, notes[7].Id, term.Id);

        // Act
        var inside = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, new DateOnly(2026, 3, 12), new DateOnly(2026, 5, 20), Ct);
        var tooFew = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, new DateOnly(2026, 4, 1), null, Ct);

        // Assert
        inside!.Available.Should().BeTrue();
        inside.SignedSessions.Should().Be(7);
        inside.Themes!.Single().Occurrences.Should().ContainSingle().Which.NoteId.Should().Be(notes[7].Id);
        tooFew!.Available.Should().BeFalse();
        tooFew.SignedSessions.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteAsync_OtherProvidersOrDiscardedPatient_IsNull()
    {
        // Arrange
        var (s, cleanup) = await StartAsync();
        await using var _ = cleanup;
        await SixNotesAsync(s);
        var discardedPatient = await s.Seed.AddPatientAsync("Bia", "Costa", cancellationToken: Ct);
        for (var i = 0; i < 6; i++)
        {
            await NoteAsync(s, Day0.AddDays(i), patientId: discardedPatient);
        }
        await s.Db.Patients.Where(p => p.Id == discardedPatient).ExecuteUpdateAsync(u => u.SetProperty(p => p.DiscardedAt, Day0), Ct);

        // Act
        var other = await CaseAnalysisQuery.ExecuteAsync(s.Db, Guid.NewGuid(), s.PatientId, null, null, Ct);
        var discarded = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, discardedPatient, null, null, Ct);
        var own = await CaseAnalysisQuery.ExecuteAsync(s.Db, s.ProviderId, s.PatientId, null, null, Ct);

        // Assert
        other.Should().BeNull();
        discarded.Should().BeNull();
        own.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_TwoHundredSignedNotes_AnswersQuicklyWithBoundedQueries()
    {
        // Arrange
        var (s, cleanup) = await StartAsync();
        await using var _ = cleanup;
        var terms = new List<ThemeTerm>();
        for (var i = 0; i < 12; i++)
        {
            terms.Add(await TermAsync(s, $"Tema {i:00}"));
        }
        var notes = new List<Note>();
        for (var i = 0; i < 200; i++)
        {
            notes.Add(new Note
            {
                Id = Guid.NewGuid(), ProviderId = s.ProviderId, PatientId = s.PatientId, Status = "Signed", InputMode = "Text", Format = "DAP",
                SignedAt = Day0.AddDays(i), SignedByCrp = "000000/00", CreatedAt = Day0.AddDays(i), UpdatedAt = Day0.AddDays(i),
            });
        }
        s.Db.Notes.AddRange(notes);
        await s.Db.SaveChangesAsync(Ct);
        for (var i = 0; i < notes.Count; i++)
        {
            for (var k = 0; k < 4; k++)
            {
                s.Db.ThemeTags.Add(new ThemeTag
                {
                    Id = Guid.NewGuid(), NoteId = notes[i].Id, TermId = terms[(i + k * 3) % terms.Count].Id, CreatedBy = "human",
                    CreatedAt = Day0, AddedById = s.ProviderId,
                });
            }
        }
        await s.Db.SaveChangesAsync(Ct);
        await using var fresh = s.Seed.CreateDbContext();

        // Act
        var watch = Stopwatch.StartNew();
        var result = await CaseAnalysisQuery.ExecuteAsync(fresh, s.ProviderId, s.PatientId, null, null, Ct);
        watch.Stop();

        // Assert
        result!.Available.Should().BeTrue();
        result.SignedSessions.Should().Be(200);
        result.Themes.Should().HaveCount(12);
        result.Themes!.Sum(t => t.Occurrences.Count).Should().Be(800);
        result.Cooccurrence.Should().NotBeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
