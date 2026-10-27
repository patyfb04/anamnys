using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Medications;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Medications;

// Real database (design/specs/2026-10-27-medication-timeline-design.md §6). Records from documents are covered in
// MedicationFactsTests.
[Collection(SharedAppHostCollection.Name)]
public class MedicationTimelineTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<MedicationEntry> AddAsync(
        AnamnysDbContext db, Guid patientId, string drug, string? dose, DateOnly startedOn, DateOnly? endedOn = null, Guid? treatmentId = null)
    {
        var id = Guid.NewGuid();
        var entry = new MedicationEntry
        {
            Id = id, TreatmentId = treatmentId ?? id, PatientId = patientId, Drug = drug, Dose = dose, StartedOn = startedOn, EndedOn = endedOn, CreatedAt = Now,
        };
        db.MedicationEntries.Add(entry);
        await db.SaveChangesAsync(Ct);
        return entry;
    }

    private static async Task<ScaleApplication> ApplyAsync(AnamnysDbContext db, Guid patientId, string code, int score, DateTimeOffset at, bool removed = false)
    {
        var instrument = await db.Instruments.AsNoTracking().SingleAsync(i => i.Code == code, Ct);
        var application = new ScaleApplication
        {
            Id = Guid.NewGuid(), PatientId = patientId, InstrumentId = instrument.Id, Score = score, AppliedAt = at, CreatedAt = at,
            RemovedAt = removed ? Now : null, RemovedBy = removed ? Guid.NewGuid() : null,
        };
        db.ScaleApplications.Add(application);
        await db.SaveChangesAsync(Ct);
        return application;
    }

    [Fact]
    public async Task GetAsync_MergesManualMedications_AndScaleSeries_OnDates()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var first = await AddAsync(db, patient, "Sertralina", "25 mg", new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31));
        var second = await AddAsync(db, patient, "Sertralina", "50 mg", new DateOnly(2026, 9, 1), treatmentId: first.TreatmentId);
        var other = await AddAsync(db, patient, "Bupropiona", "150 mg", new DateOnly(2026, 3, 10));
        await ApplyAsync(db, patient, "PHQ-9", 18, new DateTimeOffset(2026, 7, 1, 15, 0, 0, TimeSpan.Zero));
        var later = await ApplyAsync(db, patient, "PHQ-9", 9, new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));
        await ApplyAsync(db, patient, "PHQ-9", 19, new DateTimeOffset(2026, 8, 1, 15, 0, 0, TimeSpan.Zero), removed: true);

        // Act
        var timeline = await MedicationTimeline.GetAsync(db, seed.ProviderId, patient, null, null, Ct);

        // Assert
        timeline.Should().NotBeNull();
        timeline!.Medications.Select(m => m.Drug).Should().Equal("Bupropiona", "Sertralina");
        var sertraline = timeline.Medications[1];
        sertraline.TreatmentId.Should().Be(first.TreatmentId);
        sertraline.Segments.Select(s => (s.MedicationId, s.Dose, s.StartedOn, s.EndedOn)).Should().Equal(
            (first.Id, "25 mg", new DateOnly(2026, 6, 1), (DateOnly?)new DateOnly(2026, 8, 31)),
            (second.Id, "50 mg", new DateOnly(2026, 9, 1), null));
        sertraline.Segments.Should().OnlyContain(s => s.Origin.Kind == "manual" && s.Origin.DocumentId == null);
        timeline.Medications[0].Segments.Single().MedicationId.Should().Be(other.Id);
        var phq = timeline.Scales.Should().ContainSingle().Which;
        phq.Code.Should().Be("PHQ-9");
        phq.Name.Should().NotBeNullOrEmpty();
        phq.MaxScore.Should().Be(27);
        phq.Points.Select(p => (p.Score, p.AppliedOn)).Should().Equal((18, new DateOnly(2026, 7, 1)), (9, new DateOnly(2026, 10, 1)));
        phq.Points[1].ApplicationId.Should().Be(later.Id);
    }

    [Fact]
    public async Task GetAsync_WithAPeriod_KeepsOnlyWhatOverlapsIt()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        await AddAsync(db, patient, "Antiga", "10 mg", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var running = await AddAsync(db, patient, "Em curso", "10 mg", new DateOnly(2025, 6, 1));
        var inside = await AddAsync(db, patient, "Dentro", "10 mg", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));
        await AddAsync(db, patient, "Depois", "10 mg", new DateOnly(2026, 11, 1));
        await ApplyAsync(db, patient, "PHQ-9", 20, new DateTimeOffset(2026, 1, 10, 15, 0, 0, TimeSpan.Zero));
        await ApplyAsync(db, patient, "PHQ-9", 12, new DateTimeOffset(2026, 8, 10, 15, 0, 0, TimeSpan.Zero));

        // Act
        var timeline = await MedicationTimeline.GetAsync(db, seed.ProviderId, patient, new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 26), Ct);

        // Assert
        timeline!.Medications.SelectMany(m => m.Segments).Select(s => s.MedicationId).Should().BeEquivalentTo([running.Id, inside.Id]);
        timeline.Scales.Single().Points.Select(p => p.Score).Should().Equal(12);
    }

    [Fact]
    public async Task GetAsync_ScaleAppliedLateInTheEvening_CountsOnThePracticeDate()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        await ApplyAsync(db, patient, "PHQ-9", 5, new DateTimeOffset(2026, 8, 11, 1, 30, 0, TimeSpan.Zero));

        // Act
        var timeline = await MedicationTimeline.GetAsync(db, seed.ProviderId, patient, null, null, Ct);

        // Assert
        timeline!.Scales.Single().Points.Single().AppliedOn.Should().Be(new DateOnly(2026, 8, 10));
    }

    [Fact]
    public async Task GetAsync_AnotherProvidersPatient_ReturnsNull_AndNeverMixesPatients()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var neighbour = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        await AddAsync(db, neighbour, "Do vizinho", "10 mg", new DateOnly(2026, 1, 1));
        await ApplyAsync(db, neighbour, "PHQ-9", 7, Now);
        var otherProvider = await seed.AddProviderAsync(Ct);

        // Act
        var foreign = await MedicationTimeline.GetAsync(db, otherProvider, patient, null, null, Ct);
        var own = await MedicationTimeline.GetAsync(db, seed.ProviderId, patient, null, null, Ct);

        // Assert
        foreign.Should().BeNull();
        own!.Medications.Should().BeEmpty();
        own.Scales.Should().BeEmpty();
    }
}
