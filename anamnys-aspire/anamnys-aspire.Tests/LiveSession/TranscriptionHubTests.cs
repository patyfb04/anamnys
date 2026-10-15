using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;

namespace Anamnys.Tests.LiveSession;

[Collection(SharedAppHostCollection.Name)]
public class TranscriptionHubTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/hubs/transcription/negotiate?negotiateVersion=1")]
    [InlineData("/hubs/progress/negotiate?negotiateVersion=1")]
    public async Task Negotiate_WithoutSession_Returns401(string path)
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        using var response = await client.PostAsync(path, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Negotiate_WithOwnersRealmCookie_Returns401OnBothHubs()
    {
        // The admin dev server also proxies /hubs, and it is the origin the owners cookie belongs to.
        using var session = await BrowserSession.LoginAsync(
            new Uri("http://localhost:5276/"), "/admin/", "owner", "dev.owner@anamnys.local", "SupportStaff!2026Dev", Ct);
        // Positive control: the cookie is a live owners session, so the 401s below come from the realm.
        using var probe = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/probe"), Ct);
        probe.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var hub in new[] { "/hubs/transcription", "/hubs/progress" })
        {
            using var response = await session.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, $"{hub}/negotiate?negotiateVersion=1"), Ct);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{hub} accepts only the providers cookie");
        }
    }

    [Fact]
    public async Task FullRecording_BeginChunksFinalize_ReturnsTheSameNoteTwice()
    {
        using var session = await LoginAsProviderAsync();
        var providerId = await ProviderIdAsync(session);
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Hub", "Teste", providerId: providerId, cancellationToken: Ct);
        await using var hub = await ConnectAsync(session, "/hubs/transcription");
        var recordingId = Guid.NewGuid();

        var begin = await hub.InvokeAsync<JsonElement>("BeginRecording", recordingId, patient, (Guid?)null, "DAP", "pt", Ct);
        var first = await hub.InvokeAsync<JsonElement>("SendAudioChunk", recordingId, 0, new byte[3200], Ct);
        var again = await hub.InvokeAsync<JsonElement>("SendAudioChunk", recordingId, 0, new byte[3200], Ct);
        var gap = await hub.InvokeAsync<JsonElement>("SendAudioChunk", recordingId, 5, new byte[3200], Ct);
        var finalized = await hub.InvokeAsync<JsonElement>("FinalizeStream", recordingId, Ct);
        var repeated = await hub.InvokeAsync<JsonElement>("FinalizeStream", recordingId, Ct);

        begin.GetProperty("status").GetString().Should().Be("ok");
        first.GetProperty("status").GetString().Should().Be("accepted");
        again.GetProperty("status").GetString().Should().Be("duplicate");
        gap.GetProperty("status").GetString().Should().Be("gap");
        gap.GetProperty("lastSeq").GetInt32().Should().Be(0);
        finalized.GetProperty("status").GetString().Should().Be("ok");
        repeated.GetProperty("noteId").GetGuid().Should().Be(finalized.GetProperty("noteId").GetGuid());
    }

    [Fact]
    public async Task BeginRecording_OtherProvidersPatient_IsNotFound()
    {
        using var session = await LoginAsProviderAsync();
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var stranger = await seed.AddPatientAsync("Outro", "Paciente", cancellationToken: Ct);
        await using var hub = await ConnectAsync(session, "/hubs/transcription");

        var begin = await hub.InvokeAsync<JsonElement>("BeginRecording", Guid.NewGuid(), stranger, (Guid?)null, "DAP", "pt", Ct);

        begin.GetProperty("status").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task SubscribeToNote_OtherProvidersNote_ReturnsNull()
    {
        using var session = await LoginAsProviderAsync();
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var stranger = await seed.AddPatientAsync("Outro", "Paciente", cancellationToken: Ct);
        await seed.AddNoteAsync(stranger, "Draft", DateTimeOffset.UtcNow, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var noteId = db.Notes.Single(n => n.PatientId == stranger).Id;
        await using var hub = await ConnectAsync(session, "/hubs/progress");

        var snapshot = await hub.InvokeAsync<JsonElement?>("SubscribeToNote", noteId, Ct);

        (snapshot is null || snapshot.Value.ValueKind == JsonValueKind.Null).Should().BeTrue();
    }

    private static async Task<HubConnection> ConnectAsync(BrowserSession session, string path)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(ProviderBaseAddress, path), o =>
                o.Headers["Cookie"] = string.Join("; ", session.Jar.Select(kv => $"{kv.Key}={kv.Value}")))
            .Build();
        await connection.StartAsync(Ct);
        return connection;
    }

    private static async Task<Guid> ProviderIdAsync(BrowserSession session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/phi/probe");
        using var response = await session.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("localId").GetGuid();
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
