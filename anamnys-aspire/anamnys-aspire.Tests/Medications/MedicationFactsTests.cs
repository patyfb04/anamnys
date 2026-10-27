using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.DocumentFacts;
using Anamnys.Server.DocumentReading;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Medications;
using Anamnys.Tests.ExternalDocuments;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Medications;

// Real database (design/specs/2026-10-27-medication-timeline-design.md §6).
[Collection(SharedAppHostCollection.Name)]
public class MedicationFactsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 26);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Page1 =
        "CLÍNICA HORIZONTE DE SAÚDE MENTAL\nLAUDO PSIQUIÁTRICO\nConclusão diagnóstica\n" +
        "CID-10 F43.1 - Transtorno de estresse pós-traumático.\nSertralina 50 mg, 1 comprimido pela manhã.";
    private const string Page2 = "São Paulo, 14 de setembro de 2026.\nDr. Ricardo Menezes Lobo\nPsiquiatra - CRM-SP 000000 (fictício)";

    private static readonly FactCandidate SertralineFact = new("medication", "Sertralina 50 mg, 1 comprimido pela manhã", 1, "sertralina 50 mg,  1 comprimido pela manhã.");
    private static readonly FactCandidate IssueDateFact = new("issue_date", "14 de setembro de 2026", 2, "São Paulo, 14 de setembro de 2026.");
    private static readonly FactCandidate IcdFact = new("icd", "F43.1", 1, "CID-10 F43.1 - Transtorno de estresse pós-traumático.");

    private sealed record Seeded(PatientSearchSeed Seed, Guid PatientId, Guid DocumentId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Seed.DisposeAsync();
    }

    private async Task<Seeded> SeedAsync(bool archived = false)
    {
        var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Fernanda", "Barbosa", archivedAt: archived ? Now : null, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, new FakeNoteFileStore(), seed.ProviderId, patient);
        document.ReadStatus = DocumentReadStatus.Done;
        document.ReadAttempts = 1;
        document.PageCount = 2;
        document.DocumentDate = new DateOnly(2026, 9, 10);
        db.ExternalDocumentPages.AddRange(
            new ExternalDocumentPage { Id = Guid.NewGuid(), ExternalDocumentId = document.Id, ProviderId = seed.ProviderId, PageNumber = 1, Text = Page1, CreatedAt = Now },
            new ExternalDocumentPage { Id = Guid.NewGuid(), ExternalDocumentId = document.Id, ProviderId = seed.ProviderId, PageNumber = 2, Text = Page2, CreatedAt = Now });
        await db.SaveChangesAsync(Ct);
        await FactService.SaveExtractedAsync(db, seed.ProviderId, document.Id, [SertralineFact, IssueDateFact, IcdFact], Now, Ct);
        return new Seeded(seed, patient, document.Id);
    }

    private static Task<ExtractedFact> FactAsync(AnamnysDbContext db, Guid documentId, string kind) =>
        db.ExtractedFacts.AsNoTracking().SingleAsync(f => f.ExternalDocumentId == documentId && f.Kind == kind, Ct);

    private static async Task<MedicationEntry> AddManualAsync(
        AnamnysDbContext db, Guid patientId, string drug, string? dose, DateOnly startedOn, DateOnly? endedOn = null)
    {
        var id = Guid.NewGuid();
        var entry = new MedicationEntry
        {
            Id = id, TreatmentId = id, PatientId = patientId, Drug = drug, Dose = dose, StartedOn = startedOn, EndedOn = endedOn, CreatedAt = Now,
        };
        db.MedicationEntries.Add(entry);
        await db.SaveChangesAsync(Ct);
        return entry;
    }

    private static MedicationFromFactRequest NewRequest(DateOnly? startedOn = null, string drug = "Sertralina", string? dose = "50 mg", string? posology = "1 comprimido pela manhã") =>
        new(MedicationFromFactRequest.New, null, drug, dose, posology, startedOn ?? new DateOnly(2026, 9, 14), null);

    [Fact]
    public async Task RecordAsync_New_WritesTheRecordLinkedToTheFact_ConfirmsIt_AndAuditsWithoutTheDrug()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, NewRequest(), Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.Ok);
        var record = await db.MedicationEntries.AsNoTracking().SingleAsync(m => m.PatientId == s.PatientId, Ct);
        record.Id.Should().Be(result.MedicationId!.Value);
        (record.Drug, record.Dose, record.Posology).Should().Be(("Sertralina", "50 mg", "1 comprimido pela manhã"));
        (record.StartedOn, record.EndedOn).Should().Be((new DateOnly(2026, 9, 14), (DateOnly?)null));
        record.SourceFactId.Should().Be(fact.Id);
        record.FromDocument.Should().BeTrue();
        record.TreatmentId.Should().Be(record.Id);
        record.CreatedAt.Should().Be(Now);
        var after = await FactAsync(db, s.DocumentId, "medication");
        after.Status.Should().Be("confirmed");
        after.CorrectedValue.Should().BeNull();
        after.ConfirmedBy.Should().Be(s.Seed.ProviderId);
        var audits = await db.AuditEntries.AsNoTracking()
            .Where(a => a.ActorId == s.Seed.ProviderId && a.Action != "facts_extracted").OrderBy(a => a.Action).ToListAsync(Ct);
        audits.Select(a => (a.Action, a.NewValue)).Should().Equal(
            ("fact_confirmed", $"{fact.Id} medication"),
            ("medication_from_fact", $"{fact.Id} new {record.Id}"));
        audits.Should().OnlyContain(a => a.NoteId == null && a.DocumentId == null);
        string.Concat(audits.Select(a => a.NewValue)).Should().NotContain("Sertralina");
    }

    [Fact]
    public async Task RecordAsync_New_WithTextDifferentFromTheFact_CorrectsTheFact()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, NewRequest(dose: "100 mg"), Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.Ok);
        var after = await FactAsync(db, s.DocumentId, "medication");
        after.Status.Should().Be("corrected");
        after.Value.Should().Be("Sertralina 50 mg, 1 comprimido pela manhã");
        after.CorrectedValue.Should().Be("Sertralina 100 mg, 1 comprimido pela manhã");
        (await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "fact_corrected" && a.NewValue == $"{fact.Id} medication", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task RecordAsync_DoseChange_ClosesTheActiveRecordTheDayBefore_AndKeepsTheTreatment()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var old = await AddManualAsync(db, s.PatientId, "Sertralina", "25 mg", new DateOnly(2026, 6, 1));
        var request = new MedicationFromFactRequest(MedicationFromFactRequest.DoseChange, old.Id, "Sertralina", "50 mg", "1 comprimido pela manhã", new DateOnly(2026, 9, 14), null);

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, request, Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.Ok);
        var records = await db.MedicationEntries.AsNoTracking().Where(m => m.PatientId == s.PatientId).OrderBy(m => m.StartedOn).ToListAsync(Ct);
        records.Should().HaveCount(2);
        records[0].Id.Should().Be(old.Id);
        records[0].EndedOn.Should().Be(new DateOnly(2026, 9, 13));
        records[1].Id.Should().Be(result.MedicationId!.Value);
        records[1].TreatmentId.Should().Be(old.TreatmentId);
        records[1].EndedOn.Should().BeNull();
        records[1].SourceFactId.Should().Be(fact.Id);
        (await FactAsync(db, s.DocumentId, "medication")).Status.Should().Be("confirmed");
    }

    [Fact]
    public async Task RecordAsync_DoseChange_BeforeTheTargetStarted_IsInvalid_AndChangesNothing()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var old = await AddManualAsync(db, s.PatientId, "Sertralina", "25 mg", new DateOnly(2026, 9, 20));
        var request = new MedicationFromFactRequest(MedicationFromFactRequest.DoseChange, old.Id, "Sertralina", "50 mg", null, new DateOnly(2026, 9, 14), null);

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, request, Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.Invalid);
        result.Errors.Should().ContainKey("startedOn");
        (await db.MedicationEntries.AsNoTracking().CountAsync(m => m.PatientId == s.PatientId, Ct)).Should().Be(1);
        (await db.MedicationEntries.AsNoTracking().SingleAsync(m => m.Id == old.Id, Ct)).EndedOn.Should().BeNull();
        (await FactAsync(db, s.DocumentId, "medication")).Status.Should().Be("pending");
    }

    [Fact]
    public async Task RecordAsync_Stop_ClosesTheActiveRecord_AndConfirmsTheFact_WithoutANewRecord()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var old = await AddManualAsync(db, s.PatientId, "Sertralina", "50 mg", new DateOnly(2026, 6, 1));
        var request = new MedicationFromFactRequest(MedicationFromFactRequest.Stop, old.Id, null, null, null, null, new DateOnly(2026, 9, 1));

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, request, Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.Ok);
        result.MedicationId.Should().Be(old.Id);
        var records = await db.MedicationEntries.AsNoTracking().Where(m => m.PatientId == s.PatientId).ToListAsync(Ct);
        records.Should().ContainSingle().Which.EndedOn.Should().Be(new DateOnly(2026, 9, 1));
        (await FactAsync(db, s.DocumentId, "medication")).Status.Should().Be("confirmed");
        (await db.AuditEntries.AsNoTracking().AnyAsync(a => a.Action == "medication_from_fact" && a.NewValue == $"{fact.Id} stop {old.Id}", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task RecordAsync_New_WithOnlyFormattingDifferences_ConfirmsTheFact()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var request = NewRequest(drug: "SERTRALINA", dose: "50mg", posology: "1 comprimido,  pela manhã.");

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, request, Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.Ok);
        var after = await FactAsync(db, s.DocumentId, "medication");
        after.Status.Should().Be("confirmed");
        after.CorrectedValue.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_Stop_DoesNotMarkTheFactRecorded_AndTheSameFactCanBeAppliedAgain()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var first = await AddManualAsync(db, s.PatientId, "Sertralina", "50 mg", new DateOnly(2026, 6, 1));
        var second = await AddManualAsync(db, s.PatientId, "Sertralina", "100 mg", new DateOnly(2026, 7, 1));
        MedicationFromFactRequest Stop(Guid target) =>
            new(MedicationFromFactRequest.Stop, target, null, null, null, null, new DateOnly(2026, 9, 1));

        // Act
        var stopFirst = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, Stop(first.Id), Today, Now, Ct);
        var draft = await MedicationFacts.DraftAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, Ct);
        var stopSecond = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, Stop(second.Id), Today, Now, Ct);
        var added = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, NewRequest(), Today, Now, Ct);

        // Assert
        stopFirst.Outcome.Should().Be(MedicationFactOutcome.Ok);
        draft.Draft!.Recorded.Should().BeFalse();
        stopSecond.Outcome.Should().Be(MedicationFactOutcome.Ok);
        added.Outcome.Should().Be(MedicationFactOutcome.Ok);
        (await db.MedicationEntries.AsNoTracking().CountAsync(m => m.SourceFactId == fact.Id, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_FlagsTheFactsTurnedIntoRecords()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var before = await FactService.ListAsync(db, s.Seed.ProviderId, s.DocumentId, false, Ct);

        // Act
        await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, NewRequest(), Today, Now, Ct);
        var after = await FactService.ListAsync(db, s.Seed.ProviderId, s.DocumentId, false, Ct);

        // Assert
        before!.Facts.Should().OnlyContain(f => !f.Recorded);
        after!.Facts.Where(f => f.Recorded).Select(f => f.Id).Should().Equal(fact.Id);
    }

    [Fact]
    public async Task RecordAsync_TargetAlreadyEnded_ReturnsTargetNotActive()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var ended = await AddManualAsync(db, s.PatientId, "Sertralina", "50 mg", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1));
        var request = new MedicationFromFactRequest(MedicationFromFactRequest.Stop, ended.Id, null, null, null, null, new DateOnly(2026, 9, 1));

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, request, Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.TargetNotActive);
    }

    [Fact]
    public async Task RecordAsync_TargetOfAnotherPatient_ReturnsNotFound()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var otherPatient = await s.Seed.AddPatientAsync("Outra", "Paciente", cancellationToken: Ct);
        var foreign = await AddManualAsync(db, otherPatient, "Sertralina", "50 mg", new DateOnly(2026, 1, 1));
        var request = new MedicationFromFactRequest(MedicationFromFactRequest.DoseChange, foreign.Id, "Sertralina", null, null, new DateOnly(2026, 9, 14), null);

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, request, Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.NotFound);
        (await db.MedicationEntries.AsNoTracking().SingleAsync(m => m.Id == foreign.Id, Ct)).EndedOn.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_TheSameFactTwice_ReturnsAlreadyRecorded()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, NewRequest(), Today, Now, Ct);

        // Act
        var again = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, NewRequest(), Today, Now, Ct);

        // Assert
        again.Outcome.Should().Be(MedicationFactOutcome.AlreadyRecorded);
        (await db.MedicationEntries.AsNoTracking().CountAsync(m => m.PatientId == s.PatientId, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task RecordAsync_ArchivedPatient_ReturnsArchived_AndWritesNothing()
    {
        // Arrange
        await using var s = await SeedAsync(archived: true);
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, NewRequest(), Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.Archived);
        (await db.MedicationEntries.AsNoTracking().CountAsync(m => m.PatientId == s.PatientId, Ct)).Should().Be(0);
        (await FactAsync(db, s.DocumentId, "medication")).Status.Should().Be("pending");
    }

    [Fact]
    public async Task RecordAsync_AnotherProvider_ReturnsNotFound()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var other = await s.Seed.AddProviderAsync(Ct);

        // Act
        var result = await MedicationFacts.RecordAsync(db, other, s.DocumentId, fact.Id, NewRequest(), Today, Now, Ct);
        var draft = await MedicationFacts.DraftAsync(db, other, s.DocumentId, fact.Id, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.NotFound);
        draft.Outcome.Should().Be(MedicationFactOutcome.NotFound);
        (await db.MedicationEntries.AsNoTracking().CountAsync(m => m.PatientId == s.PatientId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task RecordAsync_DiscardedFact_OrAnotherKind_IsRefused()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var medication = await FactAsync(db, s.DocumentId, "medication");
        var icd = await FactAsync(db, s.DocumentId, "icd");
        await FactService.DiscardAsync(db, s.Seed.ProviderId, s.DocumentId, medication.Id, Now, Ct);

        // Act
        var discarded = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, medication.Id, NewRequest(), Today, Now, Ct);
        var notMedication = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, icd.Id, NewRequest(), Today, Now, Ct);

        // Assert
        discarded.Outcome.Should().Be(MedicationFactOutcome.NotPending);
        notMedication.Outcome.Should().Be(MedicationFactOutcome.NotMedication);
    }

    [Theory]
    [InlineData("new", "2026-10-27", null, "startedOn")]
    [InlineData("new", "2026-09-14", "2026-09-01", "endedOn")]
    [InlineData("new", null, null, "startedOn")]
    [InlineData("dose_change", "2026-09-14", null, "targetMedicationId")]
    [InlineData("stop", null, null, "endedOn")]
    [InlineData("other", "2026-09-14", null, "action")]
    public async Task RecordAsync_InvalidDates_OrAction_ReturnsInvalid(string action, string? startedOn, string? endedOn, string field)
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var request = new MedicationFromFactRequest(
            action, null, "Sertralina", null, null,
            startedOn is null ? null : DateOnly.Parse(startedOn), endedOn is null ? null : DateOnly.Parse(endedOn));

        // Act
        var result = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, request, Today, Now, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.Invalid);
        result.Errors.Should().ContainKey(field);
        (await db.MedicationEntries.AsNoTracking().CountAsync(m => m.PatientId == s.PatientId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task DraftAsync_ReturnsThePrefill_UsingTheIssueDateFact_AndTheActiveRecordsOfTheSameDrug()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var active = await AddManualAsync(db, s.PatientId, "SERTRALINA", "25 mg", new DateOnly(2026, 6, 1));
        await AddManualAsync(db, s.PatientId, "Sertralina", "25 mg", new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1));
        await AddManualAsync(db, s.PatientId, "Escitalopram", "10 mg", new DateOnly(2026, 6, 1));

        // Act
        var result = await MedicationFacts.DraftAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, Ct);

        // Assert
        result.Outcome.Should().Be(MedicationFactOutcome.Ok);
        var draft = result.Draft!;
        draft.Prefill.Should().Be(new MedicationDraft("Sertralina", "50 mg", "1 comprimido pela manhã", new DateOnly(2026, 9, 14), null));
        draft.ActiveMatches.Should().ContainSingle().Which.Id.Should().Be(active.Id);
        draft.Composed.Should().Be("Sertralina 50 mg, 1 comprimido pela manhã");
        draft.Recorded.Should().BeFalse();
    }

    [Fact]
    public async Task DeletingTheDocument_KeepsTheRecord_ClearsItsFact_AndTheTimelineShowsDocumentDeleted()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Seed.CreateDbContext();
        var fact = await FactAsync(db, s.DocumentId, "medication");
        var recorded = await MedicationFacts.RecordAsync(db, s.Seed.ProviderId, s.DocumentId, fact.Id, NewRequest(), Today, Now, Ct);
        var before = await MedicationTimeline.GetAsync(db, s.Seed.ProviderId, s.PatientId, null, null, Ct);

        // Act
        var deleted = await ExternalDocumentService.DeleteAsync(db, new FakeNoteFileStore(), s.Seed.ProviderId, s.DocumentId, Now, Ct);
        var after = await MedicationTimeline.GetAsync(db, s.Seed.ProviderId, s.PatientId, null, null, Ct);

        // Assert
        deleted.Should().Be(ExternalDocumentOutcome.Ok);
        before!.Medications.Single().Segments.Single().Origin.Should().Match<TimelineOrigin>(o =>
            o.Kind == "document" && o.DocumentId == s.DocumentId && o.DocumentKind == "laudo" && o.Page == 1 && o.ExcerptId != null
            && o.DocumentDate == new DateOnly(2026, 9, 10));
        var record = await db.MedicationEntries.AsNoTracking().SingleAsync(m => m.Id == recorded.MedicationId, Ct);
        record.SourceFactId.Should().BeNull();
        record.FromDocument.Should().BeTrue();
        after!.Medications.Single().Segments.Single().Origin.Should().Be(new TimelineOrigin("document_deleted"));
    }
}
