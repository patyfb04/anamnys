using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Anamnys.Server.Exports;
using Anamnys.Server.Retention;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Exports;

// Export (F-60) with disposal terms (design/specs/2026-10-28-retention-panel-design.md §3, §6 "Export includes the
// term"). Runs on the retention scratch database, which has the F-06 columns.
[Collection(SharedAppHostCollection.Name)]
public class AccountExportDisposalTermTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private const string Root = "anamnys-exportacao-2026-10-27";
    private const string Terms = $"{Root}/conta/termos-de-descarte";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static AccountExportDisposalTermTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static async Task<(DiscardServiceTests.Record Record, Guid TermId)> DiscardOneAsync(RetentionScratch s, FakeNoteFileStore store)
    {
        var r = await DiscardServiceTests.SeedExpiredAsync(s, store);
        await s.AddPatientAsync(r.Provider, "Bia", Ct);
        await using var db = s.CreateDbContext();
        var result = await DiscardService.DiscardAsync(db, store, NullLogger.Instance, r.Provider, r.Patient, "Ana Silva", Now, Ct);
        result.Outcome.Should().Be(DiscardOutcome.Ok);
        return (r, result.TermId!.Value);
    }

    private static async Task<Dictionary<string, byte[]>> BuildAsync(RetentionScratch s, FakeNoteFileStore store, Guid providerId)
    {
        await using var db = s.CreateDbContext();
        var builder = new AccountExportBuilder(db, store, new FakeTimeProvider(Now));
        using var zip = new MemoryStream();
        await builder.BuildAsync(providerId, zip, Ct);
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entries = new Dictionary<string, byte[]>();
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Ct);
            entries[entry.FullName] = buffer.ToArray();
        }
        return entries;
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('﻿');

    [Fact]
    public async Task BuildAsync_DiscardedPatient_AppearsOnlyThroughTheTerm()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var (r, termId) = await DiscardOneAsync(s, store);
        var stored = store.Objects[DisposalTerms.ObjectKeyFor(r.Provider, termId)];

        // Act
        var entries = await BuildAsync(s, store, r.Provider);

        // Assert: the stored PDF and its JSON row.
        entries[$"{Terms}/2026-10-27-termo.pdf"].Should().Equal(stored);
        var terms = JsonDocument.Parse(Text(entries[$"{Terms}/termos.json"])).RootElement;
        terms.GetArrayLength().Should().Be(1);
        var term = terms[0];
        term.GetProperty("id").GetGuid().Should().Be(termId);
        term.GetProperty("paciente").GetString().Should().Be("Ana Silva");
        term.GetProperty("periodoInicio").GetString().Should().Be("2019-06-01");
        term.GetProperty("periodoFim").GetString().Should().Be("2020-01-01");
        term.GetProperty("anosGuarda").GetInt32().Should().Be(5);
        term.GetProperty("guardaAte").GetString().Should().Be("2025-01-01");
        term.GetProperty("contagens").GetProperty("notes").GetInt32().Should().Be(2);
        term.GetProperty("pdf").GetString().Should().Be("conta/termos-de-descarte/2026-10-27-termo.pdf");

        // The discarded record has no patient folder; the other patient keeps theirs.
        entries.Keys.Where(k => k.StartsWith($"{Root}/pacientes/", StringComparison.Ordinal))
            .Select(k => k.Split('/')[2]).Distinct().Should().BeEquivalentTo(["001-BS"]);
        Text(entries[$"{Root}/index.html"]).Should().Contain("Termos de descarte").And.Contain("conta/termos-de-descarte/2026-10-27-termo.pdf");
        Text(entries[$"{Root}/LEIA-ME.txt"]).Should().Contain("termos.json");
        Text(entries[$"{Root}/conta/auditoria.csv"]).Should().Contain(DiscardService.AuditAction).And.NotContain("section_edited");
        Text(entries[$"{Root}/conta/agenda.csv"]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
    }

    [Fact]
    public async Task BuildAsync_TermObjectMissing_RendersTheTermAgain()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var (r, termId) = await DiscardOneAsync(s, store);
        store.Objects.Remove(DisposalTerms.ObjectKeyFor(r.Provider, termId));

        // Act
        var entries = await BuildAsync(s, store, r.Provider);

        // Assert
        Encoding.ASCII.GetString(entries[$"{Terms}/2026-10-27-termo.pdf"][..5]).Should().Be("%PDF-");
    }

    [Fact]
    public async Task BuildAsync_NoDiscard_HasNoTermsFolder()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        await s.AddPatientAsync(provider, "Bia", Ct);

        // Act
        var entries = await BuildAsync(s, new FakeNoteFileStore(), provider);

        // Assert
        entries.Keys.Should().NotContain(k => k.StartsWith(Terms, StringComparison.Ordinal));
        Text(entries[$"{Root}/index.html"]).Should().NotContain("Termos de descarte");
    }
}
