using Anamnys.Server.Scales;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Tests.Scales;

[Collection(SharedAppHostCollection.Name)]
public class ScaleRecordingTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Server.Data.Entities.Instrument> InstrumentAsync(Server.Data.AnamnysDbContext db, string code) =>
        await db.Instruments.AsNoTracking().SingleAsync(i => i.Code == code, Ct);

    private static RecordScaleRequest Request(Guid instrumentId, int score, DateTimeOffset? appliedAt = null, Guid? sessionId = null) =>
        new(instrumentId, score, appliedAt ?? Now.AddDays(-1), sessionId);

    private static async Task<Guid> AddSessionAsync(PatientSearchSeed seed, Guid patientId, Guid? providerId = null)
    {
        var id = Guid.NewGuid();
        await seed.ExecuteSqlAsync(
            """INSERT INTO "Sessions" ("Id", "PatientId", "ProviderId") VALUES (@id, @patient, @provider)""",
            Ct, ("id", id), ("patient", patientId), ("provider", providerId ?? seed.ProviderId));
        return id;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    public async Task RecordAsync_AvailableInstrumentScoreInRange_RecordsOnlyScoreAndDate(int score)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, score), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.Recorded);
        await using var check = seed.CreateDbContext();
        var row = await check.ScaleApplications.AsNoTracking().SingleAsync(a => a.Id == result.Id, Ct);
        row.Score.Should().Be(score);
        row.AppliedBy.Should().Be(seed.ProviderId);
        row.CreatedAt.Should().Be(Now);
        row.RemovedAt.Should().BeNull();
        var connection = check.Database.GetDbConnection();
        await check.Database.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT "RawAnswers" IS NULL FROM "ScaleApplications" WHERE "Id" = @id""", (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("id", result.Id!.Value);
        (await command.ExecuteScalarAsync(Ct)).Should().Be(true);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(-1)]
    public async Task RecordAsync_ScoreOutOfRange_ReturnsInvalidScore(int score)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, score), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.Invalid);
        result.Errors!["score"].Should().Equal("O escore do PHQ-9 vai de 0 a 27.");
    }

    [Fact]
    public async Task RecordAsync_FutureDate_ReturnsInvalidAppliedAt()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5, Now.AddMinutes(6)), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.Invalid);
        result.Errors!["appliedAt"].Should().Equal("A data não pode estar no futuro.");
    }

    [Fact]
    public async Task RecordAsync_AppliedAtEqualsNow_IsAccepted()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5, Now), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.Recorded);
    }

    [Fact]
    public async Task RecordAsync_AppliedAtWithinClockTolerance_IsStoredAsNow()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5, Now.AddMinutes(4)), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.Recorded);
        await using var check = seed.CreateDbContext();
        var row = await check.ScaleApplications.AsNoTracking().SingleAsync(a => a.Id == result.Id, Ct);
        row.AppliedAt.Should().Be(Now);
    }

    [Fact]
    public async Task RecordAsync_MissingFields_ReturnsInvalidWithEachMessage()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, new(null, null, null, null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.Invalid);
        result.Errors!["instrumentId"].Should().Equal("Escolha o instrumento.");
        result.Errors["score"].Should().Equal("Informe o escore.");
        result.Errors["appliedAt"].Should().Equal("Informe a data.");
    }

    [Fact]
    public async Task RecordAsync_NeedsOptInInstrument_RequiresOptInThenRecords()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var gad = await InstrumentAsync(db, "GAD-7");

        // Act
        var before = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(gad.Id, 10), Now, Ct);
        await ScaleCatalog.OptInAsync(db, seed.ProviderId, gad.Id, Now, Ct);
        var after = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(gad.Id, 10), Now, Ct);

        // Assert
        before.Outcome.Should().Be(RecordOutcome.OptInRequired);
        before.Explanation.Should().Be(gad.StatusExplanation);
        after.Outcome.Should().Be(RecordOutcome.Recorded);
    }

    [Fact]
    public async Task RecordAsync_UnavailableInstrument_StaysUnavailableEvenWithOptInRow()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var dass = await InstrumentAsync(db, "DASS-21");

        // Act
        var before = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(dass.Id, 10), Now, Ct);
        await seed.ExecuteSqlAsync(
            """INSERT INTO "ProviderInstrumentOptIns" ("ProviderId", "InstrumentId", "AcknowledgedAt", "ExplanationShown") VALUES (@p, @i, now(), 'x')""",
            Ct, ("p", seed.ProviderId), ("i", dass.Id));
        var after = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(dass.Id, 10), Now, Ct);

        // Assert
        before.Outcome.Should().Be(RecordOutcome.Unavailable);
        before.Explanation.Should().Be(dass.StatusExplanation);
        after.Outcome.Should().Be(RecordOutcome.Unavailable);
        after.Explanation.Should().Be(dass.StatusExplanation);
    }

    [Fact]
    public async Task RecordAsync_ArchivedPatient_ReturnsArchived()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", archivedAt: Now.AddDays(-3), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.Archived);
    }

    [Fact]
    public async Task RecordAsync_OtherProvidersPatient_ReturnsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.NotFound);
    }

    [Fact]
    public async Task RecordAsync_SessionOfAnotherPatient_ReturnsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var otherPatient = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        var session = await AddSessionAsync(seed, otherPatient);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5, sessionId: session), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.NotFound);
    }

    [Fact]
    public async Task RecordAsync_OwnSession_RecordsWithSessionId()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var session = await AddSessionAsync(seed, patient);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");

        // Act
        var result = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5, sessionId: session), Now, Ct);

        // Assert
        result.Outcome.Should().Be(RecordOutcome.Recorded);
        await using var check = seed.CreateDbContext();
        (await check.ScaleApplications.AsNoTracking().SingleAsync(a => a.Id == result.Id, Ct)).SessionId.Should().Be(session);
    }

    [Fact]
    public async Task RemoveAsync_RecordedApplication_RemovesOnceAndHidesFromSeries()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");
        var recorded = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5), Now, Ct);

        // Act
        var first = await ScaleRecording.RemoveAsync(db, seed.ProviderId, patient, recorded.Id!.Value, Now, Ct);
        var second = await ScaleRecording.RemoveAsync(db, seed.ProviderId, patient, recorded.Id.Value, Now, Ct);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
        var series = await ScaleRecording.SeriesAsync(db, seed.ProviderId, patient, null, Ct);
        series.Should().BeEmpty();
        await using var check = seed.CreateDbContext();
        var row = await check.ScaleApplications.AsNoTracking().SingleAsync(a => a.Id == recorded.Id, Ct);
        row.RemovedAt.Should().Be(Now);
        row.RemovedBy.Should().Be(seed.ProviderId);
    }

    [Fact]
    public async Task RemoveAsync_OtherProvider_ReturnsFalse()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");
        var recorded = await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5), Now, Ct);

        // Act
        var removed = await ScaleRecording.RemoveAsync(db, other, patient, recorded.Id!.Value, Now, Ct);

        // Assert
        removed.Should().BeFalse();
    }

    [Fact]
    public async Task SeriesAsync_OtherProvidersPatient_ReturnsNull()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var series = await ScaleRecording.SeriesAsync(db, seed.ProviderId, patient, null, Ct);

        // Assert
        series.Should().BeNull();
    }

    [Fact]
    public async Task SeriesAsync_ManyApplications_GroupsByInstrumentOrderedAndFiltersBySession()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var session = await AddSessionAsync(seed, patient);
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");
        var momD = await InstrumentAsync(db, "MOM-D");
        await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 9, Now.AddDays(-2)), Now, Ct);
        await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 4, Now.AddDays(-9)), Now, Ct);
        await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(momD.Id, 3, Now.AddDays(-1), session), Now, Ct);

        // Act
        var all = (await ScaleRecording.SeriesAsync(db, seed.ProviderId, patient, null, Ct))!;
        var forSession = await ScaleRecording.SeriesAsync(db, seed.ProviderId, patient, session, Ct);

        // Assert
        all.Select(s => s.Code).Should().Equal("MOM-D", "PHQ-9");
        all[1].Entries.Select(e => e.Score).Should().Equal(4, 9);
        all[1].MinScore.Should().Be(0);
        all[1].MaxScore.Should().Be(27);
        forSession!.Should().ContainSingle().Which.Code.Should().Be("MOM-D");
    }

    [Fact]
    public async Task SeriesAsync_SessionLinkedToAppointment_FillsAppointmentStartsAt()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var startsAt = Now.AddDays(-4);
        var appointment = await seed.AddAppointmentAsync(patient, startsAt, cancellationToken: Ct);
        var session = await AddSessionAsync(seed, patient);
        await seed.ExecuteSqlAsync(
            """UPDATE "Appointments" SET "SessionId" = @s WHERE "Id" = @a""", Ct, ("s", session), ("a", appointment));
        await using var db = seed.CreateDbContext();
        var phq = await InstrumentAsync(db, "PHQ-9");
        await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 5, sessionId: session), Now, Ct);
        await ScaleRecording.RecordAsync(db, seed.ProviderId, patient, Request(phq.Id, 6), Now, Ct);

        // Act
        var series = await ScaleRecording.SeriesAsync(db, seed.ProviderId, patient, null, Ct);

        // Assert
        var entries = series!.Single().Entries;
        entries.Single(e => e.SessionId == session).AppointmentStartsAt.Should().Be(startsAt);
        entries.Single(e => e.SessionId == null).AppointmentStartsAt.Should().BeNull();
    }
}
