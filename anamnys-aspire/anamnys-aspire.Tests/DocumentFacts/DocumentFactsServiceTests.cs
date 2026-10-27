using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.DocumentFacts;
using Anamnys.Server.DocumentReading;
using Anamnys.Server.Excerpts;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Storage;
using Anamnys.Tests.ExternalDocuments;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.DocumentFacts;

// Real database, fake extractor (design/specs/2026-10-26-document-facts-design.md §6).
[Collection(SharedAppHostCollection.Name)]
public class DocumentFactsServiceTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Page1 =
        "CLÍNICA HORIZONTE DE SAÚDE MENTAL\nLAUDO PSIQUIÁTRICO\nConclusão diagnóstica\n" +
        "CID-10 F43.1 - Transtorno de estresse pós-traumático.\nSertralina 50 mg, 1 comprimido pela manhã.";
    private const string Page2 = "São Paulo, 14 de setembro de 2026.\nDr. Ricardo Menezes Lobo\nPsiquiatra - CRM-SP 000000 (fictício)";

    private static readonly FactCandidate Icd = new("icd", "F43.1", 1, "CID-10 F43.1 - Transtorno de estresse pós-traumático.");
    private static readonly FactCandidate Medication = new("medication", "Sertralina 50 mg, 1 comprimido pela manhã", 1, "sertralina 50 mg,  1 comprimido pela manhã.");
    private static readonly FactCandidate Date = new("issue_date", "14 de setembro de 2026", 2, "São Paulo, 14 de setembro de 2026.");
    private static readonly FactCandidate Registration = new("registration", "CRM-SP 000000", 2, "Psiquiatra - CRM-SP 000000 (fictício)");

    private sealed record Seeded(PatientSearchSeed Seed, Guid PatientId, Guid DocumentId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Seed.DisposeAsync();
    }

    // A clean document of a new provider's patient, read (two pages, done).
    private async Task<Seeded> SeedAsync(bool archived = false, string readStatus = DocumentReadStatus.Done)
    {
        var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Fernanda", "Barbosa", archivedAt: archived ? Now : null, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, new FakeNoteFileStore(), seed.ProviderId, patient);
        document.ReadStatus = readStatus;
        document.ReadAttempts = readStatus == DocumentReadStatus.Failed ? 3 : 1;
        document.DocumentDate = new DateOnly(2026, 9, 14);
        if (readStatus == DocumentReadStatus.Done)
        {
            document.PageCount = 2;
            db.ExternalDocumentPages.AddRange(
                new ExternalDocumentPage { Id = Guid.NewGuid(), ExternalDocumentId = document.Id, ProviderId = seed.ProviderId, PageNumber = 1, Text = Page1, CreatedAt = Now },
                new ExternalDocumentPage { Id = Guid.NewGuid(), ExternalDocumentId = document.Id, ProviderId = seed.ProviderId, PageNumber = 2, Text = Page2, CreatedAt = Now });
        }
        await db.SaveChangesAsync(Ct);
        return new Seeded(seed, patient, document.Id);
    }

    private static async Task<List<ExtractedFact>> FactsAsync(AnamnysDbContext db, Guid documentId) =>
        await db.ExtractedFacts.AsNoTracking().Where(f => f.ExternalDocumentId == documentId).OrderBy(f => f.Kind).ToListAsync(Ct);

    private static ServiceProvider ServicesFor(PatientSearchSeed seed) =>
        new ServiceCollection().AddScoped(_ => seed.CreateDbContext()).BuildServiceProvider();

    [Fact]
    public async Task Worker_ReadDocument_SavesTheValidLocatedFacts_WithDocumentPageReferences_AndAudits()
    {
        // Arrange
        await using var s = await SeedAsync();
        using var services = ServicesFor(s.Seed);
        var extractor = new FakeFactExtractor(_ =>
        [
            Icd, Medication, Date, Registration,
            new("icd", "F99.99", 1, "CID-10 F43.1"),                     // invalid code
            new("conclusion", "Esquizofrenia", 1, "Esquizofrenia paranoide"), // quote not in the document
            new("issuer", "Dr. Ricardo Menezes Lobo", 1, "Dr. Ricardo Menezes Lobo"), // quote on page 2, not 1
            new("issuer", "Dr. Ricardo Menezes Lobo", 3, "Dr. Ricardo Menezes Lobo"), // page that does not exist
            Icd with { Value = "f43.1" },                                  // repeated
        ]);

        // Act
        var saved = await FactWorker.RunAsync(new FactJob(s.DocumentId, s.Seed.ProviderId), services.GetRequiredService<IServiceScopeFactory>(),
            extractor, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        saved.Should().Be(4);
        extractor.LastPages.Should().Equal(new FactPage(1, Page1), new FactPage(2, Page2));
        await using var db = s.Seed.CreateDbContext();
        var facts = await FactsAsync(db, s.DocumentId);
        facts.Select(f => (f.Kind, f.Value, f.PageRef, f.Status)).Should().BeEquivalentTo(new[]
        {
            ("icd", "F43.1", (int?)1, "pending"),
            ("issue_date", "14 de setembro de 2026", (int?)2, "pending"),
            ("medication", "Sertralina 50 mg, 1 comprimido pela manhã", (int?)1, "pending"),
            ("registration", "CRM-SP 000000", (int?)2, "pending"),
        });
        var medication = facts.Single(f => f.Kind == "medication");
        medication.Quote.Should().Be("Sertralina 50 mg, 1 comprimido pela manhã.");
        medication.ProviderId.Should().Be(s.Seed.ProviderId);
        medication.CreatedAt.Should().Be(Now);
        var reference = await db.ExcerptRefs.AsNoTracking().SingleAsync(r => r.Id == medication.ExcerptRefId, Ct);
        reference.SourceType.Should().Be("document");
        reference.SourceId.Should().Be(s.DocumentId);
        reference.PageNumber.Should().Be(1);
        reference.Kind.Should().Be("exact");
        Page1[reference.OffsetStart!.Value..reference.OffsetEnd!.Value].Should().Be(medication.Quote);
        reference.DatedAt.Should().Be(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero));
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.ActorId == s.Seed.ProviderId && a.Action == "facts_extracted", Ct);
        audit.NewValue.Should().Be($"{s.DocumentId} 4");
        $"{audit.FieldChanged}{audit.NewValue}".Should().NotContain("F43").And.NotContain("Sertralina");
    }

    [Fact]
    public async Task ReadWorker_SavedRead_QueuesTheExtraction()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Fernanda", "Barbosa", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        Guid documentId;
        await using (var db = seed.CreateDbContext())
        {
            var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient);
            store.Objects[document.ObjectKey!] = DocumentReading.TestPdfs.WithPages("CID-10 F43.1 - Transtorno de estresse pos-traumatico.");
            document.ReadStatus = DocumentReadStatus.Pending;
            await db.SaveChangesAsync(Ct);
            documentId = document.Id;
        }
        var facts = new FactQueue();
        using var services = new ServiceCollection()
            .AddScoped(_ => seed.CreateDbContext())
            .AddSingleton<INoteFileStore>(store)
            .AddSingleton<IDocumentReader>(new DocumentReader(new NoRender(), new NoOcr(), Options.Create(new DocumentReadingOptions())))
            .AddSingleton(facts)
            .BuildServiceProvider();

        // Act
        await ReadWorker.ProcessAsync(new ReadJob(documentId, seed.ProviderId), services.GetRequiredService<IServiceScopeFactory>(),
            new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        facts.Reader.TryRead(out var job).Should().BeTrue();
        job.Should().Be(new FactJob(documentId, seed.ProviderId));
    }

    [Fact]
    public async Task ConfirmCorrectDiscard_ChangeTheStatus_KeepWhatWasRead_AndAuditKindAndIdOnly()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId, [Icd, Medication, Date], Now, Ct);
        var facts = await FactsAsync(db, s.DocumentId);
        var icd = facts.Single(f => f.Kind == "icd");
        var medication = facts.Single(f => f.Kind == "medication");
        var date = facts.Single(f => f.Kind == "issue_date");

        // Act
        var confirmed = await FactService.ConfirmAsync(db, s.Seed.ProviderId, s.DocumentId, icd.Id, Now, Ct);
        var again = await FactService.ConfirmAsync(db, s.Seed.ProviderId, s.DocumentId, icd.Id, Now, Ct);
        var invalid = await FactService.CorrectAsync(db, s.Seed.ProviderId, s.DocumentId, date.Id, "31/02/2026", Now, Ct);
        var corrected = await FactService.CorrectAsync(db, s.Seed.ProviderId, s.DocumentId, medication.Id, "Sertralina 100 mg", Now, Ct);
        var discarded = await FactService.DiscardAsync(db, s.Seed.ProviderId, s.DocumentId, date.Id, Now, Ct);
        var confirmDiscarded = await FactService.ConfirmAsync(db, s.Seed.ProviderId, s.DocumentId, date.Id, Now, Ct);

        // Assert
        confirmed.Should().Be(FactOutcome.Ok);
        again.Should().Be(FactOutcome.Ok);
        invalid.Should().Be(FactOutcome.InvalidValue);
        corrected.Should().Be(FactOutcome.Ok);
        discarded.Should().Be(FactOutcome.Ok);
        confirmDiscarded.Should().Be(FactOutcome.NotPending);
        var after = (await FactsAsync(db, s.DocumentId)).ToDictionary(f => f.Kind);
        after["icd"].Status.Should().Be("confirmed");
        after["icd"].ConfirmedBy.Should().Be(s.Seed.ProviderId);
        after["icd"].ConfirmedAt.Should().Be(Now);
        after["medication"].Status.Should().Be("corrected");
        after["medication"].Value.Should().Be("Sertralina 50 mg, 1 comprimido pela manhã");
        after["medication"].CorrectedValue.Should().Be("Sertralina 100 mg");
        after["issue_date"].Status.Should().Be("discarded");
        after["issue_date"].DiscardedAt.Should().Be(Now);
        var audits = await db.AuditEntries.AsNoTracking()
            .Where(a => a.ActorId == s.Seed.ProviderId && a.Action != "facts_extracted").OrderBy(a => a.Action).ToListAsync(Ct);
        audits.Select(a => (a.Action, a.NewValue)).Should().Equal(
            ("fact_confirmed", $"{icd.Id} icd"),
            ("fact_corrected", $"{medication.Id} medication"),
            ("fact_discarded", $"{date.Id} issue_date"));
        audits.Should().OnlyContain(a => a.NoteId == null && a.DocumentId == null && a.PreviousValue == null);
        string.Concat(audits.Select(a => a.NewValue)).Should().NotContain("Sertralina").And.NotContain("setembro");
    }

    [Fact]
    public async Task CorrectAsync_SameAsWhatWasRead_ConfirmsTheFact()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId, [Icd], Now, Ct);
        var icd = (await FactsAsync(db, s.DocumentId)).Single();

        // Act
        var outcome = await FactService.CorrectAsync(db, s.Seed.ProviderId, s.DocumentId, icd.Id, "f43.1", Now, Ct);

        // Assert
        outcome.Should().Be(FactOutcome.Ok);
        var after = (await FactsAsync(db, s.DocumentId)).Single();
        after.Status.Should().Be("confirmed");
        after.CorrectedValue.Should().BeNull();
    }

    [Fact]
    public async Task SaveExtractedAsync_AgainAfterReview_ReplacesOnlyPendingFacts_AndDoesNotRepeatReviewedOnes()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId, [Icd, Medication, Date, Registration], Now, Ct);
        var first = (await FactsAsync(db, s.DocumentId)).ToDictionary(f => f.Kind);
        await FactService.ConfirmAsync(db, s.Seed.ProviderId, s.DocumentId, first["icd"].Id, Now, Ct);
        await FactService.CorrectAsync(db, s.Seed.ProviderId, s.DocumentId, first["medication"].Id, "Sertralina 100 mg", Now, Ct);
        await FactService.DiscardAsync(db, s.Seed.ProviderId, s.DocumentId, first["issue_date"].Id, Now, Ct);

        // Act: the new read finds the same facts again (one as corrected) and the registration again.
        var saved = await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId,
            [Icd, Date, Registration, new("medication", "Sertralina 100 mg", 1, "Sertralina 50 mg")], Now.AddHours(1), Ct);

        // Assert
        saved.Should().Be(1);
        var after = await FactsAsync(db, s.DocumentId);
        after.Should().HaveCount(4);
        after.Single(f => f.Kind == "icd").Id.Should().Be(first["icd"].Id);
        after.Single(f => f.Kind == "medication").Status.Should().Be("corrected");
        after.Single(f => f.Kind == "issue_date").Status.Should().Be("discarded");
        var registration = after.Single(f => f.Kind == "registration");
        registration.Id.Should().NotBe(first["registration"].Id);
        registration.CreatedAt.Should().Be(Now.AddHours(1));
        (await db.ExcerptRefs.AnyAsync(r => r.Id == first["registration"].ExcerptRefId, Ct)).Should().BeFalse();
        (await db.ExcerptRefs.AnyAsync(r => r.Id == first["icd"].ExcerptRefId, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Actions_AnotherProvidersDocumentOrFact_AreNotFound()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId, [Icd], Now, Ct);
        var icd = (await FactsAsync(db, s.DocumentId)).Single();
        var other = await s.Seed.AddProviderAsync(Ct);

        // Act
        var list = await FactService.ListAsync(db, other, s.DocumentId, false, Ct);
        var confirm = await FactService.ConfirmAsync(db, other, s.DocumentId, icd.Id, Now, Ct);
        var correct = await FactService.CorrectAsync(db, other, s.DocumentId, icd.Id, "F40", Now, Ct);
        var discard = await FactService.DiscardAsync(db, other, s.DocumentId, icd.Id, Now, Ct);
        var wrongDocument = await FactService.ConfirmAsync(db, s.Seed.ProviderId, Guid.NewGuid(), icd.Id, Now, Ct);
        var extract = await FactService.RequestExtractionAsync(db, new ReadQueue(), new FactQueue(), other, s.DocumentId, Ct);
        var declared = await FactService.DeclaredForPatientAsync(db, other, s.PatientId, Ct);
        var passage = await ExcerptService.GetPassageAsync(db, other, icd.ExcerptRefId!.Value, Ct);
        var saved = await FactService.SaveExtractedAsync(db, other, s.DocumentId, [Icd], Now, Ct);

        // Assert
        list.Should().BeNull();
        new[] { confirm, correct, discard, wrongDocument, extract.Outcome }.Should().OnlyContain(o => o == FactOutcome.NotFound);
        declared.Should().BeNull();
        passage.Should().BeNull();
        saved.Should().BeNull();
        (await FactsAsync(db, s.DocumentId)).Single().Status.Should().Be("pending");
    }

    [Fact]
    public async Task Mutations_ArchivedPatient_AreRefused()
    {
        // Arrange
        await using var s = await SeedAsync(archived: true);
        await using var db = s.Seed.CreateDbContext();
        await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId, [Icd], Now, Ct);
        var icd = (await FactsAsync(db, s.DocumentId)).Single();
        var facts = new FactQueue();

        // Act
        var confirm = await FactService.ConfirmAsync(db, s.Seed.ProviderId, s.DocumentId, icd.Id, Now, Ct);
        var correct = await FactService.CorrectAsync(db, s.Seed.ProviderId, s.DocumentId, icd.Id, "F40", Now, Ct);
        var discard = await FactService.DiscardAsync(db, s.Seed.ProviderId, s.DocumentId, icd.Id, Now, Ct);
        var extract = await FactService.RequestExtractionAsync(db, new ReadQueue(), facts, s.Seed.ProviderId, s.DocumentId, Ct);
        var list = await FactService.ListAsync(db, s.Seed.ProviderId, s.DocumentId, false, Ct);

        // Assert
        new[] { confirm, correct, discard, extract.Outcome }.Should().OnlyContain(o => o == FactOutcome.Archived);
        facts.Reader.TryRead(out _).Should().BeFalse();
        list!.Facts.Should().ContainSingle(f => f.Status == "pending");
        (await db.AuditEntries.CountAsync(a => a.ActorId == s.Seed.ProviderId && a.Action != "facts_extracted", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task DeclaredForPatientAsync_OnlyConfirmedAndCorrected_OfDocumentsNotDeleted_WithTheCorrection()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId, [Icd, Medication, Date, Registration], Now, Ct);
        var facts = (await FactsAsync(db, s.DocumentId)).ToDictionary(f => f.Kind);
        await FactService.ConfirmAsync(db, s.Seed.ProviderId, s.DocumentId, facts["icd"].Id, Now, Ct);
        await FactService.CorrectAsync(db, s.Seed.ProviderId, s.DocumentId, facts["medication"].Id, "Sertralina 100 mg", Now, Ct);
        await FactService.DiscardAsync(db, s.Seed.ProviderId, s.DocumentId, facts["issue_date"].Id, Now, Ct);
        var deleted = await ExternalDocumentServiceTests.AddDocumentAsync(db, new FakeNoteFileStore(), s.Seed.ProviderId, s.PatientId, deletedAt: Now);
        db.ExtractedFacts.Add(new ExtractedFact
        {
            Id = Guid.NewGuid(), ExternalDocumentId = deleted.Id, ProviderId = s.Seed.ProviderId, Kind = "icd", Value = "F40",
            Status = "confirmed", ConfirmedBy = s.Seed.ProviderId, ConfirmedAt = Now, ExcerptRefId = facts["icd"].ExcerptRefId, CreatedAt = Now,
        });
        await db.SaveChangesAsync(Ct);

        // Act
        var declared = await FactService.DeclaredForPatientAsync(db, s.Seed.ProviderId, s.PatientId, Ct);

        // Assert
        declared.Should().NotBeNull();
        declared!.Select(f => (f.Kind, f.Value, f.ReadValue, f.Corrected)).Should().Equal(
            ("icd", "F43.1", "F43.1", false),
            ("medication", "Sertralina 100 mg", "Sertralina 50 mg, 1 comprimido pela manhã", true));
        declared.Should().OnlyContain(f => f.DocumentId == s.DocumentId && f.DocumentKind == "laudo"
            && f.DocumentDate == new DateOnly(2026, 9, 14) && f.ExcerptRefId != null && f.Page == 1);
    }

    [Fact]
    public async Task ListAsync_ReturnsTheFactsUnderReview_WithoutDiscarded_InKindOrder_AndTheReadState()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId, [Medication, Date, Icd, Registration], Now, Ct);
        var date = (await FactsAsync(db, s.DocumentId)).Single(f => f.Kind == "issue_date");
        await FactService.DiscardAsync(db, s.Seed.ProviderId, s.DocumentId, date.Id, Now, Ct);

        // Act
        var view = await FactService.ListAsync(db, s.Seed.ProviderId, s.DocumentId, true, Ct);
        var items = await ExternalDocumentService.ListAsync(db, s.Seed.ProviderId, s.PatientId, Ct);

        // Assert
        view!.ReadStatus.Should().Be("done");
        view.PageCount.Should().Be(2);
        view.Extracting.Should().BeTrue();
        view.Facts.Select(f => f.Kind).Should().Equal("registration", "icd", "medication");
        view.Facts.Should().OnlyContain(f => f.Status == "pending" && f.Quote != null && f.ExcerptRefId != null);
        var item = items!.Single();
        item.ReadStatus.Should().Be("done");
        item.PendingFacts.Should().Be(3);
    }

    [Fact]
    public async Task GetPassageAsync_DocumentReference_ReturnsThePageTextAndTheSentenceOffsets_UntilTheDocumentIsDeleted()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId, [Date], Now, Ct);
        var date = (await FactsAsync(db, s.DocumentId)).Single();

        // Act
        var passage = await ExcerptService.GetPassageAsync(db, s.Seed.ProviderId, date.ExcerptRefId!.Value, Ct);
        await ExternalDocumentService.DeleteAsync(db, new FakeNoteFileStore(), s.Seed.ProviderId, s.DocumentId, Now, Ct);
        var gone = await ExcerptService.GetPassageAsync(db, s.Seed.ProviderId, date.ExcerptRefId!.Value, Ct);

        // Assert
        passage.Should().NotBeNull();
        passage!.SourceType.Should().Be("document");
        passage.Kind.Should().Be("exact");
        passage.DocumentId.Should().Be(s.DocumentId);
        passage.DocumentKind.Should().Be("laudo");
        passage.PatientId.Should().Be(s.PatientId);
        passage.PageNumber.Should().Be(2);
        passage.PageText.Should().Be(Page2);
        Page2[passage.Start!.Value..passage.End!.Value].Should().Be("São Paulo, 14 de setembro de 2026.");
        passage.NoteId.Should().BeNull();
        gone.Should().BeNull();
    }

    [Fact]
    public async Task RequestExtractionAsync_ReadDone_ResetsTheExtractionAttempts_AndQueuesTheExtraction()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        await db.ExternalDocuments.Where(d => d.Id == s.DocumentId)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.ExtractAttempts, FactWorker.MaxSweepAttempts), Ct);
        var reads = new ReadQueue();
        var facts = new FactQueue();

        // Act
        var result = await FactService.RequestExtractionAsync(db, reads, facts, s.Seed.ProviderId, s.DocumentId, Ct);

        // Assert
        result.Should().Be(new ExtractRequestResult(FactOutcome.Ok, "done"));
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == s.DocumentId, Ct)).ExtractAttempts.Should().Be(0);
        facts.Reader.TryRead(out var job).Should().BeTrue();
        job.Should().Be(new FactJob(s.DocumentId, s.Seed.ProviderId));
        reads.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task RequestExtractionAsync_ReadFailed_ResetsTheAttempts_AndQueuesANewRead()
    {
        // Arrange
        await using var s = await SeedAsync(readStatus: DocumentReadStatus.Failed);
        await using var db = s.Seed.CreateDbContext();
        await db.ExternalDocuments.Where(d => d.Id == s.DocumentId)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.ExtractAttempts, FactWorker.MaxSweepAttempts), Ct);
        var reads = new ReadQueue();
        var facts = new FactQueue();

        // Act
        var result = await FactService.RequestExtractionAsync(db, reads, facts, s.Seed.ProviderId, s.DocumentId, Ct);

        // Assert
        result.Should().Be(new ExtractRequestResult(FactOutcome.Ok, "pending"));
        var document = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == s.DocumentId, Ct);
        document.ReadStatus.Should().Be("pending");
        document.ReadAttempts.Should().Be(0);
        document.ExtractAttempts.Should().Be(0);
        reads.Reader.TryRead(out var job).Should().BeTrue();
        job.Should().Be(new ReadJob(s.DocumentId, s.Seed.ProviderId));
        facts.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task RequestExtractionAsync_ReadInProgress_ChangesNothing_AndNotCleanIsNotAvailable()
    {
        // Arrange
        await using var s = await SeedAsync(readStatus: DocumentReadStatus.Reading);
        await using var db = s.Seed.CreateDbContext();
        var infected = await ExternalDocumentServiceTests.AddDocumentAsync(db, new FakeNoteFileStore(), s.Seed.ProviderId, s.PatientId, status: "infected");
        var reads = new ReadQueue();
        var facts = new FactQueue();

        // Act
        var reading = await FactService.RequestExtractionAsync(db, reads, facts, s.Seed.ProviderId, s.DocumentId, Ct);
        var blocked = await FactService.RequestExtractionAsync(db, reads, facts, s.Seed.ProviderId, infected.Id, Ct);

        // Assert
        reading.Should().Be(new ExtractRequestResult(FactOutcome.Ok, "reading"));
        blocked.Outcome.Should().Be(FactOutcome.NotAvailable);
        reads.Reader.TryRead(out _).Should().BeFalse();
        facts.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_Document_DeletesAllItsFactsAndReferences_ReviewedOnesToo_AndAuditsTheCount()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        await FactService.SaveExtractedAsync(db, s.Seed.ProviderId, s.DocumentId, [Icd, Medication, Date], Now, Ct);
        var facts = (await FactsAsync(db, s.DocumentId)).ToDictionary(f => f.Kind);
        await FactService.ConfirmAsync(db, s.Seed.ProviderId, s.DocumentId, facts["icd"].Id, Now, Ct);
        await FactService.CorrectAsync(db, s.Seed.ProviderId, s.DocumentId, facts["medication"].Id, "Sertralina 100 mg", Now, Ct);
        var other = await ExternalDocumentServiceTests.AddDocumentAsync(db, new FakeNoteFileStore(), s.Seed.ProviderId, s.PatientId);
        var otherRef = ExcerptService.AddDocumentPage(db, s.Seed.ProviderId, other.Id, 1, Page1, new ExcerptSpan(0, 7), Now);
        await db.SaveChangesAsync(Ct);

        // Act
        var outcome = await ExternalDocumentService.DeleteAsync(db, new FakeNoteFileStore(), s.Seed.ProviderId, s.DocumentId, Now, Ct);

        // Assert
        outcome.Should().Be(ExternalDocumentOutcome.Ok);
        (await db.ExtractedFacts.CountAsync(f => f.ExternalDocumentId == s.DocumentId, Ct)).Should().Be(0);
        (await db.ExcerptRefs.CountAsync(r => r.SourceType == "document" && r.SourceId == s.DocumentId, Ct)).Should().Be(0);
        (await db.ExcerptRefs.AnyAsync(r => r.Id == otherRef.Id, Ct)).Should().BeTrue();
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.ActorId == s.Seed.ProviderId && a.Action == "facts_deleted", Ct);
        audit.NewValue.Should().Be($"{s.DocumentId} 3");
        (await FactService.DeclaredForPatientAsync(db, s.Seed.ProviderId, s.PatientId, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_DocumentWithoutFacts_WritesNoFactAudit()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();

        // Act
        await ExternalDocumentService.DeleteAsync(db, new FakeNoteFileStore(), s.Seed.ProviderId, s.DocumentId, Now, Ct);

        // Assert
        (await db.AuditEntries.AnyAsync(a => a.ActorId == s.Seed.ProviderId && a.Action == "facts_deleted", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task SweepAsync_ReadDocumentWithoutAnExtractionSinceItsRead_IsQueuedAgain()
    {
        // Arrange: three read documents of one provider: never extracted, extracted after the read, extracted
        // before a newer read.
        await using var s = await SeedAsync();
        await using (var setup = s.Seed.CreateDbContext())
        {
            var extracted = await ExternalDocumentServiceTests.AddDocumentAsync(setup, new FakeNoteFileStore(), s.Seed.ProviderId, s.PatientId);
            var reread = await ExternalDocumentServiceTests.AddDocumentAsync(setup, new FakeNoteFileStore(), s.Seed.ProviderId, s.PatientId);
            foreach (var d in new[] { extracted, reread })
            {
                d.ReadStatus = DocumentReadStatus.Done;
                d.ReadAt = Now;
            }
            await setup.SaveChangesAsync(Ct);
            await setup.ExternalDocuments.Where(d => d.Id == s.DocumentId).ExecuteUpdateAsync(x => x.SetProperty(d => d.ReadAt, Now), Ct);
            setup.AuditEntries.AddRange(
                new AuditEntry { Id = Guid.NewGuid(), ActorId = s.Seed.ProviderId, Action = "facts_extracted", NewValue = $"{extracted.Id} 0", At = Now.AddMinutes(1) },
                new AuditEntry { Id = Guid.NewGuid(), ActorId = s.Seed.ProviderId, Action = "facts_extracted", NewValue = $"{reread.Id} 2", At = Now.AddMinutes(-1) });
            await setup.SaveChangesAsync(Ct);
            using var services = ServicesFor(s.Seed);
            var queue = new FactQueue();

            // Act
            await FactWorker.SweepAsync(services.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger.Instance, Ct);

            // Assert
            var queued = new List<FactJob>();
            while (queue.Reader.TryRead(out var job))
            {
                queued.Add(job);
            }
            var mine = queued.Where(j => j.ProviderId == s.Seed.ProviderId).Select(j => j.DocumentId).ToList();
            mine.Should().BeEquivalentTo([s.DocumentId, reread.Id]);
        }
    }

    [Fact]
    public async Task SweepAsync_ExtractionStartedTwiceSinceTheRead_IsNotQueuedAgain_AndKeepsItsPendingFacts()
    {
        // Arrange: two read documents never extracted since their read, one with its attempts used up and a
        // pending fact from an earlier extraction, one with a single interrupted attempt.
        await using var s = await SeedAsync();
        await using var setup = s.Seed.CreateDbContext();
        var once = await ExternalDocumentServiceTests.AddDocumentAsync(setup, new FakeNoteFileStore(), s.Seed.ProviderId, s.PatientId);
        once.ReadStatus = DocumentReadStatus.Done;
        once.ReadAt = Now;
        once.ExtractAttempts = 1;
        await setup.SaveChangesAsync(Ct);
        await setup.ExternalDocuments.Where(d => d.Id == s.DocumentId).ExecuteUpdateAsync(x => x
            .SetProperty(d => d.ReadAt, Now)
            .SetProperty(d => d.ExtractAttempts, FactWorker.MaxSweepAttempts), Ct);
        await FactService.SaveExtractedAsync(setup, s.Seed.ProviderId, s.DocumentId, [Icd], Now.AddMinutes(-5), Ct);
        using var services = ServicesFor(s.Seed);
        var queue = new FactQueue();

        // Act
        await FactWorker.SweepAsync(services.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger.Instance, Ct);

        // Assert
        var queued = new List<FactJob>();
        while (queue.Reader.TryRead(out var job))
        {
            queued.Add(job);
        }
        queued.Where(j => j.ProviderId == s.Seed.ProviderId).Select(j => j.DocumentId).Should().Equal(once.Id);
        (await FactsAsync(setup, s.DocumentId)).Should().ContainSingle(f => f.Kind == "icd" && f.Status == FactStatus.Pending);
    }

    [Fact]
    public async Task RunAsync_CountsTheAttempt_BeforeTheModelRuns()
    {
        // Arrange
        await using var s = await SeedAsync();
        using var services = ServicesFor(s.Seed);
        int? during = null;
        var extractor = new FakeFactExtractor(_ =>
        {
            using var check = s.Seed.CreateDbContext();
            during = check.ExternalDocuments.AsNoTracking().Single(d => d.Id == s.DocumentId).ExtractAttempts;
            return [];
        });

        // Act
        await FactWorker.RunAsync(new FactJob(s.DocumentId, s.Seed.ProviderId), services.GetRequiredService<IServiceScopeFactory>(),
            extractor, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        during.Should().Be(1);
    }

    private sealed class NoOcr : IOcrEngine
    {
        public Task<string> RecognizeAsync(byte[] image, CancellationToken ct) => Task.FromResult("");
    }

    private sealed class NoRender : IPageRenderer
    {
        public byte[] RenderPng(byte[] pdf, int pageIndex, int dpi) => [];
    }
}
