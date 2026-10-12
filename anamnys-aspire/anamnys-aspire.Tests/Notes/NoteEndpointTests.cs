using System.Net;
using Anamnys.Server.Notes;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_WithoutSession_Returns401()
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        using var response = await client.GetAsync($"/api/phi/providers/me/notes/{Guid.NewGuid()}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_OtherProvidersNote_Returns404()
    {
        using var session = await LoginAsProviderAsync();
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var stranger = await seed.AddPatientAsync("Outro", "Paciente", cancellationToken: Ct);
        await seed.AddNoteAsync(stranger, "Draft", DateTimeOffset.UtcNow, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var noteId = await db.Notes.Where(n => n.PatientId == stranger).Select(n => n.Id).SingleAsync(Ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/phi/providers/me/notes/{noteId}");
        using var response = await session.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAsync_OwnNote_ReturnsTranscriptAndPatientName()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddNoteAsync(patient, "Draft", DateTimeOffset.UtcNow, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var noteId = await db.Notes.Where(n => n.PatientId == patient).Select(n => n.Id).SingleAsync(Ct);
        await db.Notes.Where(n => n.Id == noteId).ExecuteUpdateAsync(s => s.SetProperty(n => n.RawTranscript, "Texto."), Ct);

        var detail = await NoteQueries.GetAsync(db, seed.ProviderId, noteId, Ct);

        detail.Should().NotBeNull();
        detail!.PatientName.Should().Be("Ana Silva");
        detail.RawTranscript.Should().Be("Texto.");
        detail.InputMode.Should().Be("Text");
        (await NoteQueries.GetAsync(db, Guid.NewGuid(), noteId, Ct)).Should().BeNull();
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
