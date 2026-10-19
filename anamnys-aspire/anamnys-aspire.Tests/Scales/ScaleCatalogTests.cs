using Anamnys.Server.Scales;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Scales;

[Collection(SharedAppHostCollection.Name)]
public class ScaleCatalogTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListAsync_SeededCatalog_ReturnsExpectedRegimePerInstrument()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var items = await ScaleCatalog.ListAsync(db, seed.ProviderId, Ct);

        // Assert
        var regimes = items.ToDictionary(i => i.Code, i => i.Regime);
        regimes["PHQ-9"].Should().Be(ScaleRegime.Available);
        regimes["MOM-D"].Should().Be(ScaleRegime.Available);
        regimes["MOM-A"].Should().Be(ScaleRegime.Available);
        regimes["GAD-7"].Should().Be(ScaleRegime.NeedsOptIn);
        regimes["PCL-5"].Should().Be(ScaleRegime.NeedsOptIn);
        string[] unavailable = ["DASS-21", "HAD", "ROSENBERG", "EPDS", "MBI", "BSQ", "BDI-2", "BAI", "ORS", "SRS"];
        foreach (var code in unavailable)
        {
            regimes[code].Should().Be(ScaleRegime.Unavailable, code);
        }
        items.Select(i => i.Code).Should().Contain(["PHQ-9", "GAD-7", "SRS"]);
        items.Should().OnlyContain(i => i.Explanation != "");
    }

    [Fact]
    public async Task ListAsync_SeededCatalog_OrdersByRegimeRankThenCode()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var gad = await db.Instruments.AsNoTracking().SingleAsync(i => i.Code == "GAD-7", Ct);
        await ScaleCatalog.OptInAsync(db, seed.ProviderId, gad.Id, Now, Ct);

        // Act
        var items = await ScaleCatalog.ListAsync(db, seed.ProviderId, Ct);

        // Assert
        var rank = new Dictionary<string, int>
        {
            [ScaleRegime.Available] = 0,
            [ScaleRegime.OptedIn] = 1,
            [ScaleRegime.NeedsOptIn] = 2,
            [ScaleRegime.Unavailable] = 3,
        };
        items.Select(i => (rank[i.Regime], i.Code)).Should().BeInAscendingOrder();
        items.Should().Contain(i => i.Code == "GAD-7" && i.Regime == ScaleRegime.OptedIn);
    }

    [Fact]
    public async Task OptInAsync_NeedsOptInInstrument_OptsInThenReportsAlreadyOptedIn()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var gad = await db.Instruments.AsNoTracking().SingleAsync(i => i.Code == "GAD-7", Ct);

        // Act
        var first = await ScaleCatalog.OptInAsync(db, seed.ProviderId, gad.Id, Now, Ct);
        var second = await ScaleCatalog.OptInAsync(db, seed.ProviderId, gad.Id, Now.AddMinutes(1), Ct);

        // Assert
        first.Outcome.Should().Be(OptInOutcome.OptedIn);
        second.Outcome.Should().Be(OptInOutcome.AlreadyOptedIn);
        await using var check = seed.CreateDbContext();
        var row = await check.ProviderInstrumentOptIns.AsNoTracking()
            .SingleAsync(o => o.ProviderId == seed.ProviderId && o.InstrumentId == gad.Id, Ct);
        row.ExplanationShown.Should().Be(gad.StatusExplanation);
        row.AcknowledgedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData("BDI-2")]
    [InlineData("ORS")]
    [InlineData("DASS-21")]
    [InlineData("PHQ-9")]
    public async Task OptInAsync_InstrumentWithoutOptInRegime_ReturnsNotAllowed(string code)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var instrument = await db.Instruments.AsNoTracking().SingleAsync(i => i.Code == code, Ct);

        // Act
        var outcome = await ScaleCatalog.OptInAsync(db, seed.ProviderId, instrument.Id, Now, Ct);

        // Assert
        outcome.Outcome.Should().Be(OptInOutcome.NotAllowed);
        outcome.Explanation.Should().Be(instrument.StatusExplanation).And.NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task OptInAsync_UnknownInstrument_ReturnsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await ScaleCatalog.OptInAsync(db, seed.ProviderId, Guid.NewGuid(), Now, Ct);

        // Assert
        outcome.Outcome.Should().Be(OptInOutcome.NotFound);
    }
}
