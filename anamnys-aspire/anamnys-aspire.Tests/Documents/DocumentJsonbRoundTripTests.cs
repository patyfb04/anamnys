using Anamnys.Server.Data.Entities;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Documents;

[Collection(SharedAppHostCollection.Name)]
public class DocumentJsonbRoundTripTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaveAndRead_ListColumns_RoundTripThroughJsonb()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patientId = await seed.AddPatientAsync("Ana", "Souza", cancellationToken: Ct);
        var docId = Guid.NewGuid();
        var checkId = Guid.NewGuid();
        await using (var db = seed.CreateDbContext())
        {
            var ruleset = await db.ConformityRulesets.AsNoTracking().FirstAsync(r => r.DocumentKind == "atestado", Ct);
            db.ClinicalDocuments.Add(new ClinicalDocument
            {
                Id = docId, PatientId = patientId, ProviderId = seed.ProviderId, Kind = "atestado", RulesetId = ruleset.Id,
                Fields = new() { ["finalidade"] = "x" }, Confirmations = ["a", "b"],
            });
            await db.SaveChangesAsync(Ct);
            db.ConformityChecks.Add(new ConformityCheck
            {
                Id = checkId, DocumentId = docId, RulesetId = ruleset.Id, MissingElements = ["m1", "m2"], ForbiddenFound = [], Mode = "blocking",
            });
            await db.SaveChangesAsync(Ct);
        }

        // Act
        await using var read = seed.CreateDbContext();
        var doc = await read.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == docId, Ct);
        var check = await read.ConformityChecks.AsNoTracking().SingleAsync(c => c.Id == checkId, Ct);
        var ruleset2 = await read.ConformityRulesets.AsNoTracking().FirstAsync(r => r.DocumentKind == "atestado", Ct);

        // Assert
        doc.Confirmations.Should().Equal("a", "b");
        doc.Fields.Should().Contain("finalidade", "x");
        check.MissingElements.Should().Equal("m1", "m2");
        check.ForbiddenFound.Should().BeEmpty();
        ruleset2.Elements.Should().NotBeEmpty();
    }
}
