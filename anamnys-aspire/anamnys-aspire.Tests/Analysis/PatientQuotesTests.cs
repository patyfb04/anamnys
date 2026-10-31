using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Server.Analysis;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Exports;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Patients;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Analysis;

// Real database (design/specs/2026-10-30-patient-quotes-design.md §7): a scratch database from the bootstrap script,
// which carries the F-15 columns, so these tests do not depend on the dev database having had the migration applied.
[Collection(SharedAppHostCollection.Name)]
public class PatientQuotesTests(SharedAppHostFixture fixture)
{
    private const string Text = "Relata que voltou a acordar de madrugada.";
    private const string Passage = "voltou a acordar";
    private const int Start = 11;
    private const int End = 27;
    private static readonly DateTimeOffset Day0 = new(2026, 1, 5, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RecordQuoteRequest Selection = new("evolucao", Start, End, Passage);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static PatientQuotesTests() => QuestPDF.Settings.License = LicenseType.Community;

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

    // A term of a private, inactive vocabulary: fine to read back, refused when recording.
    private static async Task<ThemeTerm> TermAsync(Scenario s, string label, Guid? parent = null)
    {
        await using var db = s.Scratch.CreateDbContext();
        var term = new ThemeTerm
        {
            Id = Guid.NewGuid(), VocabularyId = s.VocabularyId, Code = $"t-{Guid.NewGuid():N}", Label = label,
            ParentTermId = parent, IntroducedOn = new DateOnly(2026, 1, 1),
        };
        db.ThemeTerms.Add(term);
        await db.SaveChangesAsync(Ct);
        return term;
    }

    private static async Task<ThemeTerm> ActiveTermAsync(Scenario s)
    {
        await using var db = s.Scratch.CreateDbContext();
        return await (
            from t in db.ThemeTerms.AsNoTracking()
            join v in db.ThemeVocabularies.AsNoTracking() on t.VocabularyId equals v.Id
            where v.Active && t.Status == "active"
            orderby t.Code
            select t).FirstAsync(Ct);
    }

    private static async Task<Guid> NoteAsync(Scenario s, DateTimeOffset at, bool signed = true, Guid? patientId = null, string? status = null)
    {
        var note = await s.Scratch.AddNoteAsync(s.ProviderId, patientId ?? s.PatientId, at, Ct, signedAt: signed ? at : null, status: status);
        await s.Scratch.ExecuteAsync("""INSERT INTO "NoteSections" ("NoteId","Key","Content") VALUES (@n, 'evolucao', @t)""", Ct, ("n", note), ("t", Text));
        return note;
    }

    private static async Task<List<Guid>> SignedNotesAsync(Scenario s, int count)
    {
        var notes = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            notes.Add(await NoteAsync(s, Day0.AddDays(7 * i)));
        }
        return notes;
    }

    // Directly in the table: lets a test use a term of the inactive vocabulary or choose the recording instant.
    // inText: the passage is in the note's section; false, it was edited away after being marked.
    private static async Task<Guid> QuoteAsync(
        Scenario s, Guid noteId, string text, Guid? termId = null, DateTimeOffset? recordedAt = null, bool removed = false, bool withExcerpt = true, bool inText = true)
    {
        if (inText)
        {
            await s.Scratch.ExecuteAsync("""UPDATE "NoteSections" SET "Content" = "Content" || ' ' || @t WHERE "NoteId" = @n""", Ct, ("n", noteId), ("t", text));
        }
        await using var db = s.Scratch.CreateDbContext();
        var reference = new ExcerptRef
        {
            Id = Guid.NewGuid(), ProviderId = s.ProviderId, SourceType = "note", SourceId = noteId, SectionKey = "evolucao", Kind = "exact",
            OffsetStart = Start, OffsetEnd = End, Quote = text, DatedAt = Day0,
        };
        var quote = new PatientQuote
        {
            Id = Guid.NewGuid(), NoteId = noteId, TermId = termId, Text = text, RecordedAt = recordedAt ?? Day0,
            ExcerptRefId = withExcerpt ? reference.Id : null, AddedById = s.ProviderId,
            RemovedAt = removed ? Day0 : null, RemovedById = removed ? s.ProviderId : null,
        };
        if (withExcerpt)
        {
            db.ExcerptRefs.Add(reference);
        }
        db.PatientQuotes.Add(quote);
        await db.SaveChangesAsync(Ct);
        return quote.Id;
    }

    private static async Task<PatientQuotePage?> ListAsync(
        Scenario s, DateOnly? from = null, DateOnly? to = null, Guid? termId = null, int? page = null, Guid? providerId = null, Guid? patientId = null)
    {
        await using var db = s.Scratch.CreateDbContext();
        return await PatientQuotesQuery.ListAsync(db, providerId ?? s.ProviderId, patientId ?? s.PatientId, from, to, termId, page, Ct);
    }

    private static async Task<QuoteResult> RecordAsync(Scenario s, Guid noteId, RecordQuoteRequest? request = null, Guid? providerId = null)
    {
        await using var db = s.Scratch.CreateDbContext();
        return await PatientQuotesService.RecordAsync(db, providerId ?? s.ProviderId, noteId, request ?? Selection, Now, Ct);
    }

    private static async Task<QuoteOutcome> RemoveAsync(Scenario s, Guid id, Guid? providerId = null)
    {
        await using var db = s.Scratch.CreateDbContext();
        return await PatientQuotesService.RemoveAsync(db, providerId ?? s.ProviderId, id, Now, Ct);
    }

    private static Task<long> AuditCountAsync(Scenario s, string action) =>
        s.Scratch.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "Action" = @a""", Ct, ("a", action));

    private static Task<long> RowCountAsync(Scenario s, string table) =>
        s.Scratch.CountAsync($"""SELECT count(*) FROM "{table}" """, Ct);

    private static Task ArchiveAsync(Scenario s, Guid? patientId = null) =>
        s.Scratch.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = @at WHERE "Id" = @p""", Ct, ("at", Day0), ("p", patientId ?? s.PatientId));

    [Fact]
    public async Task Record_ASelectionOnANoteInReview_KeepsThePassageOfTheText_WithAnExactReference_AndIsAudited()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0, signed: false);
        var term = await ActiveTermAsync(s);

        // Act: the quote sent differs in spacing; the stored text is the passage of the note, not the request's.
        var result = await RecordAsync(s, note, new RecordQuoteRequest("evolucao", Start, End, "  voltou  a acordar ", term.Id));

        // Assert
        result.Outcome.Should().Be(QuoteOutcome.Ok);
        var item = result.Quote!;
        item.Text.Should().Be(Passage);
        item.NoteId.Should().Be(note);
        item.Section.Should().Be("evolucao");
        item.On.Should().Be(new DateOnly(2026, 1, 5));
        item.Term.Should().Be(new PatientQuoteTerm(term.Id, term.Label));

        await using var db = s.Scratch.CreateDbContext();
        var stored = await db.PatientQuotes.AsNoTracking().SingleAsync(q => q.Id == item.Id, Ct);
        stored.Text.Should().Be(Passage);
        stored.AddedById.Should().Be(s.ProviderId);
        stored.RecordedAt.Should().Be(Now);
        stored.TermId.Should().Be(term.Id);
        stored.ExcerptRefId.Should().Be(item.ExcerptId);
        var reference = await db.ExcerptRefs.AsNoTracking().SingleAsync(r => r.Id == item.ExcerptId, Ct);
        (reference.Kind, reference.SourceId, reference.OffsetStart, reference.OffsetEnd, reference.Quote).Should().Be(("exact", note, Start, End, Passage));

        // Audit: ids only.
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == PatientQuotesService.QuoteRecorded, Ct);
        (audit.NoteId, audit.ActorId, audit.NewValue).Should().Be((note, s.ProviderId, item.Id.ToString()));
        audit.PreviousValue.Should().BeNull();
    }

    [Theory]
    [InlineData("Signed")]
    [InlineData("Exported")]
    public async Task Record_OnASignedNote_IsAccepted_WithoutATerm(string status)
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0);
        if (status == "Exported")
        {
            await s.Scratch.ExecuteAsync(
                """UPDATE "Notes" SET "Status" = 'Exported', "PdfObjectKey" = 'k', "PdfSha256" = repeat('a', 64), "ExportedAt" = now() WHERE "Id" = @n""", Ct, ("n", note));
        }

        // Act
        var result = await RecordAsync(s, note);

        // Assert
        result.Outcome.Should().Be(QuoteOutcome.Ok);
        result.Quote!.Term.Should().BeNull();
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Processing")]
    public async Task Record_OnANoteNotReady_IsRefused_AndLeavesNoTrace(string status)
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0, signed: false, status: status);

        // Act
        var result = await RecordAsync(s, note);

        // Assert
        result.Outcome.Should().Be(QuoteOutcome.NoteNotReady);
        (await RowCountAsync(s, "PatientQuotes")).Should().Be(0);
        (await RowCountAsync(s, "ExcerptRefs")).Should().Be(0);
        (await AuditCountAsync(s, PatientQuotesService.QuoteRecorded)).Should().Be(0);
    }

    [Fact]
    public async Task Record_OnAnArchivedRecord_IsRefused_AndADiscardedOrForeignOneIsNotFound()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0);
        var other = await s.Scratch.AddProviderAsync(Ct);
        var goneOwner = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct);
        var gone = await NoteAsync(s, Day0, patientId: goneOwner);
        await s.Scratch.MarkDiscardedAsync(goneOwner, Now, Ct);
        var archivedOwner = await s.Scratch.AddPatientAsync(s.ProviderId, "Cia", Ct);
        var archived = await NoteAsync(s, Day0, patientId: archivedOwner);
        await ArchiveAsync(s, archivedOwner);

        // Act
        var foreign = await RecordAsync(s, note, providerId: other);
        var discarded = await RecordAsync(s, gone);
        var unknown = await RecordAsync(s, Guid.NewGuid());
        var readOnly = await RecordAsync(s, archived);

        // Assert
        foreign.Outcome.Should().Be(QuoteOutcome.NotFound);
        discarded.Outcome.Should().Be(QuoteOutcome.NotFound);
        unknown.Outcome.Should().Be(QuoteOutcome.NotFound);
        readOnly.Outcome.Should().Be(QuoteOutcome.Archived);
        (await RowCountAsync(s, "PatientQuotes")).Should().Be(0);
    }

    public static TheoryData<string, int, int, string> BadSelections => new()
    {
        { "evolucao", 10, 5, Passage },                     // reversed
        { "evolucao", 0, 500, Passage },                    // past the end
        { "plano", Start, End, Passage },                   // no such section
        { "evolucao", Start, End, "outro trecho qualquer" }, // the text is not what the client saw
    };

    [Theory]
    [MemberData(nameof(BadSelections))]
    public async Task Record_AnInvalidSelection_IsRefused_AndLeavesNoTrace(string section, int start, int end, string quote)
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0);

        // Act
        var result = await RecordAsync(s, note, new RecordQuoteRequest(section, start, end, quote));

        // Assert
        result.Outcome.Should().Be(QuoteOutcome.InvalidSelection);
        (await RowCountAsync(s, "PatientQuotes")).Should().Be(0);
        (await RowCountAsync(s, "ExcerptRefs")).Should().Be(0);
    }

    [Fact]
    public void Request_WithoutTheSectionOffsetsOrQuote_OrALongQuote_IsIncomplete()
    {
        // Assert
        Selection.IsComplete.Should().BeTrue();
        new RecordQuoteRequest(null, Start, End, Passage).IsComplete.Should().BeFalse();
        new RecordQuoteRequest("evolucao", null, End, Passage).IsComplete.Should().BeFalse();
        new RecordQuoteRequest("evolucao", Start, null, Passage).IsComplete.Should().BeFalse();
        new RecordQuoteRequest("evolucao", Start, End, "  ").IsComplete.Should().BeFalse();
        new RecordQuoteRequest("evolucao", Start, End, new string('a', 201)).IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Record_WithAnInactiveOrUnknownTerm_IsRefused()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0);
        var inactive = await TermAsync(s, "Sono");

        // Act
        var first = await RecordAsync(s, note, Selection with { TermId = inactive.Id });
        var second = await RecordAsync(s, note, Selection with { TermId = Guid.NewGuid() });

        // Assert
        first.Outcome.Should().Be(QuoteOutcome.TermInactive);
        second.Outcome.Should().Be(QuoteOutcome.TermInactive);
        (await RowCountAsync(s, "ExcerptRefs")).Should().Be(0);
    }

    [Fact]
    public async Task List_ShowsOnlySignedNotes_Chronologically_ByTheSessionDateRule_WithoutRemovedOnes()
    {
        // Arrange: a note signed in March whose session started in January comes first; the review note, the removed
        // quote and the quote that lost its reference are left out.
        await using var s = await StartAsync();
        var session = await s.Scratch.AddSessionAsync(s.ProviderId, s.PatientId, Day0, Ct, actualStart: Day0.AddDays(1));
        var february = await NoteAsync(s, Day0.AddDays(30));
        var january = await NoteAsync(s, Day0.AddDays(60));
        await s.Scratch.ExecuteAsync("""UPDATE "Notes" SET "SessionId" = @s WHERE "Id" = @n""", Ct, ("s", session), ("n", january));
        var review = await NoteAsync(s, Day0.AddDays(5), signed: false);
        var term = await TermAsync(s, "Sono");
        var second = await QuoteAsync(s, february, "fala de fevereiro");
        var late = await QuoteAsync(s, january, "fala de janeiro, depois", recordedAt: Day0.AddDays(2));
        var early = await QuoteAsync(s, january, "fala de janeiro", term.Id, recordedAt: Day0.AddDays(1));
        await QuoteAsync(s, review, "fala em revisão");
        await QuoteAsync(s, january, "fala removida", removed: true);
        await QuoteAsync(s, january, "fala sem referência", withExcerpt: false);

        // Act
        var page = await ListAsync(s);

        // Assert
        page!.Total.Should().Be(3);
        page.PageSize.Should().Be(12);
        page.Page.Should().Be(1);
        page.Items.Select(q => q.Id).Should().Equal(early, late, second);
        var first = page.Items[0];
        first.On.Should().Be(new DateOnly(2026, 1, 6));
        first.NoteId.Should().Be(january);
        first.Text.Should().Be("fala de janeiro");
        first.Section.Should().Be("evolucao");
        first.ExcerptId.Should().NotBeEmpty();
        first.Term.Should().Be(new PatientQuoteTerm(term.Id, "Sono"));
        page.Items[2].Term.Should().BeNull();
    }

    [Fact]
    public async Task APassageEditedAway_LeavesThePatientList_ButStaysInTheNoteView()
    {
        // Arrange: same words in another case do not count; any section of the note does.
        await using var s = await StartAsync();
        var signed = await NoteAsync(s, Day0);
        var review = await NoteAsync(s, Day0.AddDays(7), signed: false);
        await s.Scratch.ExecuteAsync("""INSERT INTO "NoteSections" ("NoteId","Key","Content") VALUES (@n, 'plano', 'Trecho movido para o plano.')""", Ct, ("n", signed));
        var moved = await QuoteAsync(s, signed, "Trecho movido", inText: false);
        var otherCase = await QuoteAsync(s, signed, "trecho MOVIDO", inText: false);
        var away = await QuoteAsync(s, signed, "passagem apagada", inText: false);
        var inReview = await QuoteAsync(s, review, "passagem apagada", inText: false);
        await using var db = s.Scratch.CreateDbContext();

        // Act
        var page = await ListAsync(s);
        var signedView = await PatientQuotesQuery.ForNoteAsync(db, s.ProviderId, signed, Ct);
        var reviewView = await PatientQuotesQuery.ForNoteAsync(db, s.ProviderId, review, Ct);

        // Assert
        page!.Items.Select(q => q.Id).Should().Equal(moved);
        page.Total.Should().Be(1);
        signedView!.Select(q => q.Id).Should().BeEquivalentTo([moved, otherCase, away]);
        reviewView!.Select(q => q.Id).Should().Equal(inReview);
    }

    [Fact]
    public async Task List_FilteredByAParentTheme_IncludesItsDescendants_AndAChildOnlyItself()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = (await SignedNotesAsync(s, 1))[0];
        var sono = await TermAsync(s, "Sono");
        var insonia = await TermAsync(s, "Insônia", sono.Id);
        var precoce = await TermAsync(s, "Despertar precoce", insonia.Id);
        var outro = await TermAsync(s, "Trabalho");
        var parent = await QuoteAsync(s, note, "a", sono.Id, Day0.AddMinutes(1));
        var child = await QuoteAsync(s, note, "b", insonia.Id, Day0.AddMinutes(2));
        var grandchild = await QuoteAsync(s, note, "c", precoce.Id, Day0.AddMinutes(3));
        await QuoteAsync(s, note, "d", outro.Id, Day0.AddMinutes(4));
        await QuoteAsync(s, note, "e", null, Day0.AddMinutes(5));

        // Act
        var byParent = await ListAsync(s, termId: sono.Id);
        var byChild = await ListAsync(s, termId: insonia.Id);
        var all = await ListAsync(s);

        // Assert
        byParent!.Items.Select(q => q.Id).Should().Equal(parent, child, grandchild);
        byParent.Total.Should().Be(3);
        byChild!.Items.Select(q => q.Id).Should().Equal(child, grandchild);
        all!.Total.Should().Be(5);
    }

    [Fact]
    public async Task List_Period_IsInclusive_OnTheSessionDay()
    {
        // Arrange
        await using var s = await StartAsync();
        var notes = await SignedNotesAsync(s, 3);
        await QuoteAsync(s, notes[0], "um");
        var second = await QuoteAsync(s, notes[1], "dois");
        await QuoteAsync(s, notes[2], "três");

        // Act
        var page = await ListAsync(s, from: new DateOnly(2026, 1, 12), to: new DateOnly(2026, 1, 12));

        // Assert
        page!.Items.Select(q => q.Id).Should().Equal(second);
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task List_Pages_By12_AndAPageBelowOneIsTheFirst_AndAPagePastTheEndIsEmpty()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = (await SignedNotesAsync(s, 1))[0];
        var ids = new List<Guid>();
        for (var i = 0; i < 14; i++)
        {
            ids.Add(await QuoteAsync(s, note, $"fala {i}", recordedAt: Day0.AddMinutes(i)));
        }

        // Act
        var first = await ListAsync(s);
        var second = await ListAsync(s, page: 2);
        var below = await ListAsync(s, page: 0);
        var past = await ListAsync(s, page: int.MaxValue);

        // Assert
        first!.Items.Select(q => q.Id).Should().Equal(ids.Take(12));
        first.Total.Should().Be(14);
        second!.Items.Select(q => q.Id).Should().Equal(ids.Skip(12));
        second.Page.Should().Be(2);
        below!.Page.Should().Be(1);
        past!.Items.Should().BeEmpty();
        past.Total.Should().Be(14);
    }

    [Fact]
    public async Task List_IsScopedToTheProvider_BlockedForADiscardedRecord_AndReadableWhenArchived()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = (await SignedNotesAsync(s, 1))[0];
        await QuoteAsync(s, note, "fala");
        var other = await s.Scratch.AddProviderAsync(Ct);
        var discarded = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct, discardedAt: Now);
        await ArchiveAsync(s);

        // Act
        var foreign = await ListAsync(s, providerId: other);
        var gone = await ListAsync(s, patientId: discarded);
        var archived = await ListAsync(s);

        // Assert
        foreign.Should().BeNull();
        gone.Should().BeNull();
        archived!.Total.Should().Be(1);
    }

    [Fact]
    public async Task ForNote_ListsItsActiveQuotes_InReviewToo_AndIsScopedToTheProvider_AndBlockedForADiscardedRecord()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0.AddDays(3), signed: false);
        var elsewhere = await NoteAsync(s, Day0);
        var quote = await QuoteAsync(s, note, "fala em revisão");
        await QuoteAsync(s, note, "removida", removed: true);
        await QuoteAsync(s, note, "sem referência", withExcerpt: false);
        await QuoteAsync(s, elsewhere, "outra nota");
        var other = await s.Scratch.AddProviderAsync(Ct);
        var goneOwner = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct);
        var gone = await NoteAsync(s, Day0, patientId: goneOwner);
        await s.Scratch.MarkDiscardedAsync(goneOwner, Now, Ct);
        await using var db = s.Scratch.CreateDbContext();

        // Act
        var items = (await PatientQuotesQuery.ForNoteAsync(db, s.ProviderId, note, Ct))!;

        // Assert
        items.Select(q => q.Id).Should().Equal(quote);
        items[0].On.Should().Be(new DateOnly(2026, 1, 8));
        (await PatientQuotesQuery.ForNoteAsync(db, other, note, Ct)).Should().BeNull();
        (await PatientQuotesQuery.ForNoteAsync(db, s.ProviderId, gone, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_MarksItRemoved_Audited_LeavesTheLists_AndARepeatIsANoOp()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0);
        var quote = await QuoteAsync(s, note, "fala");

        // Act
        var first = await RemoveAsync(s, quote);
        var second = await RemoveAsync(s, quote);

        // Assert
        first.Should().Be(QuoteOutcome.Ok);
        second.Should().Be(QuoteOutcome.Ok);
        await using var db = s.Scratch.CreateDbContext();
        var stored = await db.PatientQuotes.AsNoTracking().SingleAsync(q => q.Id == quote, Ct);
        (stored.RemovedAt, stored.RemovedById).Should().Be((Now, s.ProviderId));
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == PatientQuotesService.QuoteRemoved, Ct);
        (audit.NoteId, audit.ActorId, audit.NewValue).Should().Be((note, s.ProviderId, quote.ToString()));
        (await ListAsync(s))!.Total.Should().Be(0);
        (await PatientQuotesQuery.ForNoteAsync(db, s.ProviderId, note, Ct))!.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_OnAnArchivedRecord_IsRefused_AndADiscardedForeignOrUnknownOneIsNotFound()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0);
        var quote = await QuoteAsync(s, note, "fala");
        var other = await s.Scratch.AddProviderAsync(Ct);
        var goneOwner = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct);
        var goneQuote = await QuoteAsync(s, await NoteAsync(s, Day0, patientId: goneOwner), "fala");
        await s.Scratch.MarkDiscardedAsync(goneOwner, Now, Ct);
        var archivedOwner = await s.Scratch.AddPatientAsync(s.ProviderId, "Cia", Ct);
        var archivedQuote = await QuoteAsync(s, await NoteAsync(s, Day0, patientId: archivedOwner), "fala");
        await ArchiveAsync(s, archivedOwner);

        // Act
        var foreign = await RemoveAsync(s, quote, other);
        var discarded = await RemoveAsync(s, goneQuote);
        var unknown = await RemoveAsync(s, Guid.NewGuid());
        var archived = await RemoveAsync(s, archivedQuote);

        // Assert
        foreign.Should().Be(QuoteOutcome.NotFound);
        discarded.Should().Be(QuoteOutcome.NotFound);
        unknown.Should().Be(QuoteOutcome.NotFound);
        archived.Should().Be(QuoteOutcome.Archived);
        (await AuditCountAsync(s, PatientQuotesService.QuoteRemoved)).Should().Be(0);
    }

    [Fact]
    public async Task Record_RacingAnOpenDiscard_WaitsForIt_AndLeavesNoReferenceBehind()
    {
        // Arrange: a discard in progress holds the patient FOR UPDATE and has already deleted the note's references.
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0);
        await using var discard = await s.Scratch.OpenAsync(Ct);
        await using var tx = await discard.BeginTransactionAsync(Ct);
        await using (var start = new Npgsql.NpgsqlCommand(
            """
            SELECT 1 FROM "Patients" WHERE "Id" = @pa FOR UPDATE;
            DELETE FROM "ExcerptRefs" WHERE "SourceId" = @n;
            UPDATE "Patients" SET "DiscardedAt" = now() WHERE "Id" = @pa;
            """, discard, tx))
        {
            start.Parameters.AddWithValue("pa", s.PatientId);
            start.Parameters.AddWithValue("n", note);
            await start.ExecuteNonQueryAsync(Ct);
        }

        // Act: the discard goes on to delete the note and commits while the quote waits.
        var pending = RecordAsync(s, note);
        var finishedEarly = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1), Ct)) == pending;
        await using (var finish = new Npgsql.NpgsqlCommand(
            """DELETE FROM "NoteSections" WHERE "NoteId" = @n; DELETE FROM "Notes" WHERE "Id" = @n;""", discard, tx))
        {
            finish.Parameters.AddWithValue("n", note);
            await finish.ExecuteNonQueryAsync(Ct);
        }
        await tx.CommitAsync(Ct);
        var result = await pending;

        // Assert
        finishedEarly.Should().BeFalse("the quote waits for the discard's row lock");
        result.Outcome.Should().Be(QuoteOutcome.NotFound);
        (await RowCountAsync(s, "ExcerptRefs")).Should().Be(0);
        (await RowCountAsync(s, "PatientQuotes")).Should().Be(0);
    }

    [Fact]
    public async Task Record_RacingADiscardThatRollsBack_GoesThrough()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0);
        await using var discard = await s.Scratch.OpenAsync(Ct);
        await using var tx = await discard.BeginTransactionAsync(Ct);
        await using (var start = new Npgsql.NpgsqlCommand("""SELECT 1 FROM "Patients" WHERE "Id" = @pa FOR UPDATE""", discard, tx))
        {
            start.Parameters.AddWithValue("pa", s.PatientId);
            await start.ExecuteNonQueryAsync(Ct);
        }

        // Act
        var pending = RecordAsync(s, note);
        await tx.RollbackAsync(Ct);

        // Assert
        (await pending).Outcome.Should().Be(QuoteOutcome.Ok);
    }

    [Fact]
    public void ExportView_SkipsAQuoteWhoseNoteIsNotLoaded()
    {
        // Arrange
        var patient = new Patient { Id = Guid.NewGuid(), FirstName = "Ana", LastName = "Silva" };
        var quote = new ExportQuote(patient.Id, Guid.NewGuid(), "fala", Guid.NewGuid(), Day0, "evolucao", null);
        var data = new AccountData(
            new Provider(), [], [], Empty<NoteSection>(), new Dictionary<Guid, Session>(), Empty<ExportTheme>(), [],
            new Dictionary<Guid, ConformityRuleset>(), Empty<PatientDiagnosis>(), Empty<MedicationEntry>(), Empty<ExportObjective>(),
            Empty<ExportFollowUp>(), new[] { quote }.ToLookup(q => q.PatientId), Empty<ExportScale>(), [], [], []);

        // Act
        var view = ExportViews.Patient(data, new ExportPatient(patient, "001-AS"));

        // Assert
        view.Json.FalasRegistradas.Should().BeEmpty();
    }

    private static ILookup<Guid, T> Empty<T>() => Array.Empty<T>().ToLookup(_ => Guid.Empty);

    [Fact]
    public async Task Export_ListsRecordedQuotes_AlsoOfNotesInReview_RemovedOnesAndDiscardedRecordsStayOut()
    {
        // Arrange
        await using var s = await StartAsync();
        var note = await NoteAsync(s, Day0);
        var review = await NoteAsync(s, Day0.AddDays(7), signed: false);
        var term = await ActiveTermAsync(s);
        var kept = await QuoteAsync(s, note, "melhora do sono", term.Id, Day0.AddDays(1));
        var inReview = await QuoteAsync(s, review, "fala em revisao", recordedAt: Day0.AddDays(8), inText: false);
        await QuoteAsync(s, note, "Fala removida pelo profissional", removed: true, inText: false);
        await QuoteAsync(s, note, "Fala apagada da nota assinada", inText: false);
        var goneOwner = await s.Scratch.AddPatientAsync(s.ProviderId, "Bia", Ct);
        await QuoteAsync(s, await NoteAsync(s, Day0, patientId: goneOwner), "Fala do prontuario descartado", inText: false);
        await s.Scratch.MarkDiscardedAsync(goneOwner, Now, Ct);
        await using var db = s.Scratch.CreateDbContext();
        var builder = new AccountExportBuilder(db, new FakeNoteFileStore(), new FakeTimeProvider(Now));

        // Act
        using var zip = new MemoryStream();
        await builder.BuildAsync(s.ProviderId, zip, Ct);

        // Assert
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>();
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith(".pdf", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            entries[entry.FullName] = await reader.ReadToEndAsync(Ct);
        }
        var csv = entries.Single(e => e.Key.EndsWith("/falas-registradas.csv", StringComparison.Ordinal)).Value.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        csv.Should().HaveCount(3);
        csv[0].TrimStart('﻿').Should().Be("id;fala;nota_id;data_nota;secao;tema;registrada_em");
        csv[1].Should().StartWith($"{kept};melhora do sono;{note};").And.Contain($";evolucao;{term.Label};");
        csv[2].Should().StartWith($"{inReview};fala em revisao;{review};");
        using var json = JsonDocument.Parse(entries.Single(e => e.Key.EndsWith("/paciente.json", StringComparison.Ordinal)).Value);
        var first = json.RootElement.GetProperty("falasRegistradas")[0];
        first.GetProperty("id").GetGuid().Should().Be(kept);
        first.GetProperty("fala").GetString().Should().Be("melhora do sono");
        first.GetProperty("notaId").GetGuid().Should().Be(note);
        first.GetProperty("secao").GetString().Should().Be("evolucao");
        first.GetProperty("tema").GetString().Should().Be(term.Label);
        entries.Values.Should().NotContain(e => e.Contains("Fala removida pelo profissional") || e.Contains("Fala do prontuario descartado") || e.Contains("Fala apagada da nota assinada"));
    }

    // The refusals below are decided before PatientQuotes is read, so they hold on a dev database without the migration.
    [Fact]
    public async Task Post_OverHttp_Refusals_CarryTheirCodes()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            new Uri("http://localhost:5273/"), "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var provider = await db.Providers.AsNoTracking().Where(p => p.Email == "dev.provider@anamnys.local").Select(p => p.Id).SingleAsync(Ct);
        var open = await seed.AddPatientAsync("Teste", "Fala", providerId: provider, cancellationToken: Ct);
        var archived = await seed.AddPatientAsync("Teste", "Arquivada", archivedAt: DateTimeOffset.UtcNow.AddDays(-1), providerId: provider, cancellationToken: Ct);
        try
        {
            await seed.AddNoteAsync(open, "ReadyForReview", DateTimeOffset.UtcNow, providerId: provider, cancellationToken: Ct);
            await seed.AddNoteAsync(archived, "ReadyForReview", DateTimeOffset.UtcNow, providerId: provider, cancellationToken: Ct);
            var openNote = await db.Notes.Where(n => n.PatientId == open).Select(n => n.Id).SingleAsync(Ct);
            var archivedNote = await db.Notes.Where(n => n.PatientId == archived).Select(n => n.Id).SingleAsync(Ct);

            async Task<(HttpStatusCode Status, string? Code)> PostAsync(Guid note, object body)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/phi/providers/me/notes/{note}/quotes") { Content = JsonContent.Create(body) };
                using var response = await session.SendAsync(request, Ct);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                return (response.StatusCode, json.RootElement.GetProperty("code").GetString());
            }

            // Act
            var conflict = await PostAsync(archivedNote, new { sectionKey = "data", offsetStart = 0, offsetEnd = 4, quote = "Text" });
            var selection = await PostAsync(openNote, new { sectionKey = "nao-existe", offsetStart = 0, offsetEnd = 4, quote = "Text" });
            var incomplete = await PostAsync(openNote, new { sectionKey = "data" });
            var term = await PostAsync(openNote, new { sectionKey = "data", offsetStart = 0, offsetEnd = 4, quote = "Text", termId = Guid.NewGuid() });

            // Assert
            conflict.Should().Be((HttpStatusCode.Conflict, "archived"));
            selection.Should().Be((HttpStatusCode.BadRequest, "invalid_selection"));
            incomplete.Should().Be((HttpStatusCode.BadRequest, "invalid_selection"));
            term.Should().Be((HttpStatusCode.BadRequest, "term_inactive"));
        }
        finally
        {
            foreach (var patient in new[] { open, archived })
            {
                await seed.ExecuteSqlAsync("""DELETE FROM "NoteSections" WHERE "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = @p)""", CancellationToken.None, ("p", patient));
                await seed.ExecuteSqlAsync("""DELETE FROM "Notes" WHERE "PatientId" = @p""", CancellationToken.None, ("p", patient));
                await seed.ExecuteSqlAsync("""DELETE FROM "Patients" WHERE "Id" = @p""", CancellationToken.None, ("p", patient));
            }
        }
    }
}
