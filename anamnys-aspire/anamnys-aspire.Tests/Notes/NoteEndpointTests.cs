using System.Net;
using System.Net.Http.Json;
using Anamnys.Server.LiveSession;
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

    [Fact]
    public async Task Restructure_WithoutSession_Returns401()
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        using var response = await client.PostAsJsonAsync($"/api/phi/providers/me/notes/{Guid.NewGuid()}/restructure", new { format = "SOAP" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_WithoutSession_Returns401()
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        using var response = await client.PostAsync($"/api/phi/providers/me/notes/{Guid.NewGuid()}/export", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("PUT", "sections/data")]
    [InlineData("POST", "sections/blank")]
    [InlineData("POST", "sign")]
    public async Task EditingRoutes_WithoutSession_Return401(string method, string route)
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/phi/providers/me/notes/{Guid.NewGuid()}/{route}")
        {
            Content = JsonContent.Create(new { content = "x", expectedRevision = 0 }),
        };

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EditSection_NullContent_Returns400()
    {
        using var session = await LoginAsProviderAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/phi/providers/me/notes/{Guid.NewGuid()}/sections/data")
        {
            Content = new StringContent("""{ "content": null, "expectedRevision": 0 }""", System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await session.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAsync_ReturnsRevisionSignatureAndCrpFlag()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);
        await seed.ExecuteSqlAsync(
            """INSERT INTO "NoteSections" ("NoteId", "Key", "Content", "Position", "Revision") VALUES (@id, 'data', 'x', 0, 3)""",
            Ct, ("id", noteId));
        await using var db = seed.CreateDbContext();

        var before = await NoteQueries.GetAsync(db, seed.ProviderId, noteId, Ct);

        before!.Sections.Single().Revision.Should().Be(3);
        before.SignedAt.Should().BeNull();
        before.SignedByCrp.Should().BeNull();
        before.ProviderHasCrp.Should().BeFalse();

        await seed.ExecuteSqlAsync("""UPDATE "Providers" SET "CrpNumber" = '123456', "CrpRegion" = '06' WHERE "Id" = @p""", Ct, ("p", seed.ProviderId));
        await seed.ExecuteSqlAsync("""UPDATE "Notes" SET "Status" = 'Signed', "SignedAt" = now(), "SignedByCrp" = '123456/06' WHERE "Id" = @id""", Ct, ("id", noteId));

        var after = await NoteQueries.GetAsync(db, seed.ProviderId, noteId, Ct);

        after!.ProviderHasCrp.Should().BeTrue();
        after.SignedAt.Should().NotBeNull();
        after.SignedByCrp.Should().Be("123456/06");
    }

    [Fact]
    public async Task GetAsync_WithSections_ReturnsThemOrdered()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);
        foreach (var (key, position) in new[] { ("plan", 2), ("data", 0), ("assessment", 1) })
        {
            await seed.ExecuteSqlAsync(
                """INSERT INTO "NoteSections" ("NoteId", "Key", "Content", "Position") VALUES (@id, @key, 'x', @pos)""",
                Ct, ("id", noteId), ("key", key), ("pos", position));
        }
        await using var db = seed.CreateDbContext();

        var detail = await NoteQueries.GetAsync(db, seed.ProviderId, noteId, Ct);

        detail!.Sections.Select(s => s.Key).Should().Equal("data", "assessment", "plan");
    }

    [Fact]
    public async Task RequestAsync_ReadyNote_AcceptsSetsPendingAndEnqueues()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);
        await using var db = seed.CreateDbContext();
        var queue = new TranscriptionQueue();

        var outcome = await NoteRestructure.RequestAsync(db, queue, seed.ProviderId, noteId, "SOAP", DateTimeOffset.UtcNow, Ct);

        outcome.Should().Be(RestructureOutcome.Accepted);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.Format.Should().Be("DAP");
        note.StructureStatus.Should().Be("pending");
        note.StructureFailure.Should().BeNull();
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job.Should().BeEquivalentTo(new StructureJob(noteId, seed.ProviderId, "SOAP"));
    }

    [Fact]
    public async Task RequestAsync_EditedSection_Conflict()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);
        await seed.ExecuteSqlAsync(
            """INSERT INTO "NoteSections" ("NoteId", "Key", "Content", "Position", "EditedAt") VALUES (@id, 'data', 'x', 0, now())""",
            Ct, ("id", noteId));

        await AssertConflictUnchangedAsync(seed, noteId);
    }

    [Fact]
    public async Task RequestAsync_PendingStructure_Conflict()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);
        await seed.ExecuteSqlAsync("""UPDATE "Notes" SET "StructureStatus" = 'pending' WHERE "Id" = @id""", Ct, ("id", noteId));

        await AssertConflictUnchangedAsync(seed, noteId);
    }

    [Fact]
    public async Task RequestAsync_SignedNote_Conflict()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);
        await seed.ExecuteSqlAsync("""UPDATE "Notes" SET "Status" = 'Signed', "SignedAt" = now(), "SignedByCrp" = '000000/00' WHERE "Id" = @id""", Ct, ("id", noteId));

        await AssertConflictUnchangedAsync(seed, noteId);
    }

    [Fact]
    public async Task RequestAsync_OtherProvider_NotFound()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);
        await using var db = seed.CreateDbContext();

        var outcome = await NoteRestructure.RequestAsync(db, new TranscriptionQueue(), Guid.NewGuid(), noteId, "DAP", DateTimeOffset.UtcNow, Ct);

        outcome.Should().Be(RestructureOutcome.NotFound);
    }

    [Fact]
    public async Task RequestAsync_UnknownFormat_Invalid()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);

        (await RequestAsync(seed, noteId, "Free")).Should().Be(RestructureOutcome.Invalid);
    }

    [Fact]
    public async Task RequestAsync_NullFormat_Invalid()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);

        (await RequestAsync(seed, noteId, null!)).Should().Be(RestructureOutcome.Invalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task RequestAsync_EmptyTranscript_Conflict(string? transcript)
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);
        await seed.ExecuteSqlAsync(
            """UPDATE "Notes" SET "RawTranscript" = @t WHERE "Id" = @id""",
            Ct, ("id", noteId), ("t", (object?)transcript ?? DBNull.Value));

        await AssertConflictUnchangedAsync(seed, noteId);
    }

    [Fact]
    public async Task RequestAsync_NotReadyForReview_Conflict()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddReadyNoteAsync(seed);
        await seed.ExecuteSqlAsync("""UPDATE "Notes" SET "Status" = 'Processing' WHERE "Id" = @id""", Ct, ("id", noteId));

        await AssertConflictUnchangedAsync(seed, noteId);
    }

    private async Task AssertConflictUnchangedAsync(PatientSearchSeed seed, Guid noteId)
    {
        await using var db = seed.CreateDbContext();
        var before = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        var queue = new TranscriptionQueue();

        var outcome = await NoteRestructure.RequestAsync(db, queue, seed.ProviderId, noteId, "SOAP", DateTimeOffset.UtcNow, Ct);

        outcome.Should().Be(RestructureOutcome.Conflict);
        var after = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        after.Format.Should().Be(before.Format);
        after.StructureStatus.Should().Be(before.StructureStatus);
        after.StructureFailure.Should().Be(before.StructureFailure);
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    private async Task<RestructureOutcome> RequestAsync(PatientSearchSeed seed, Guid noteId, string format)
    {
        await using var db = seed.CreateDbContext();
        return await NoteRestructure.RequestAsync(db, new TranscriptionQueue(), seed.ProviderId, noteId, format, DateTimeOffset.UtcNow, Ct);
    }

    private static async Task<Guid> AddReadyNoteAsync(PatientSearchSeed seed)
    {
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddNoteAsync(patient, "ReadyForReview", DateTimeOffset.UtcNow, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var noteId = await db.Notes.Where(n => n.PatientId == patient).Select(n => n.Id).SingleAsync(Ct);
        await db.Notes.Where(n => n.Id == noteId).ExecuteUpdateAsync(s => s.SetProperty(n => n.RawTranscript, "Texto ditado."), Ct);
        return noteId;
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/phi/providers/me/notes")
        {
            Content = new StringContent("""{ "patientId": null, "format": "DAP", "mode": "ai", "text": "x" }""", System.Text.Encoding.UTF8, "application/json"),
        };

        // Act
        using var response = await session.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithoutSession_Returns401()
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        // Act
        using var response = await client.PostAsJsonAsync("/api/phi/providers/me/notes", new { }, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAsync_SectionsByAi_FollowsLatestOfGeneratedOrCreated()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var manual = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, null, "DAP", "manual", "x"), DateTimeOffset.UtcNow, Ct);

        // Act
        var beforeAi = await NoteQueries.GetAsync(db, seed.ProviderId, manual.NoteId!.Value, Ct);
        db.AuditEntries.Add(new Anamnys.Server.Data.Entities.AuditEntry
        {
            Id = Guid.NewGuid(), NoteId = manual.NoteId, Action = "ai_generated", At = DateTimeOffset.UtcNow.AddMinutes(1),
        });
        await db.SaveChangesAsync(Ct);
        var afterAi = await NoteQueries.GetAsync(db, seed.ProviderId, manual.NoteId!.Value, Ct);

        // Assert
        beforeAi!.SectionsByAi.Should().BeFalse();
        afterAi!.SectionsByAi.Should().BeTrue();
    }

    [Fact]
    public async Task Search_WithoutSession_Returns401()
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        // Act
        using var response = await client.PostAsJsonAsync("/api/phi/providers/me/notes/search", new { }, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_InvalidPage_Returns400()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/phi/providers/me/notes/search")
        {
            Content = new StringContent("""{ "page": 0 }""", System.Text.Encoding.UTF8, "application/json"),
        };

        // Act
        using var response = await session.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_ManualForOwnPatient_Returns201WithNoteId()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await AddDevProviderPatientAsync(seed, archived: false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/phi/providers/me/notes")
            {
                Content = JsonContent.Create(new { patientId = patient, format = "DAP", mode = "manual", text = (string?)null }),
            };

            // Act
            using var response = await session.SendAsync(request, Ct);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            body.RootElement.GetProperty("noteId").GetGuid().Should().NotBeEmpty();
        }
        finally
        {
            await DeletePatientAsync(seed, patient);
        }
    }

    [Fact]
    public async Task Create_ArchivedPatient_Returns409WithArchivedCode()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await AddDevProviderPatientAsync(seed, archived: true);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/phi/providers/me/notes")
            {
                Content = JsonContent.Create(new { patientId = patient, format = "DAP", mode = "manual", text = (string?)null }),
            };

            // Act
            using var response = await session.SendAsync(request, Ct);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            body.RootElement.GetProperty("code").GetString().Should().Be("archived");
            body.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await DeletePatientAsync(seed, patient);
        }
    }

    // The logged-in dev provider's row already exists once LoginAsProviderAsync has run; the patient is
    // seeded under it and removed again so the dev database stays clean.
    private async Task<Guid> AddDevProviderPatientAsync(PatientSearchSeed seed, bool archived)
    {
        await using var db = seed.CreateDbContext();
        var devProviderId = await db.Providers.AsNoTracking()
            .Where(p => p.Email == "dev.provider@anamnys.local").Select(p => p.Id).SingleAsync(Ct);
        return await seed.AddPatientAsync("Teste", "Nota", archivedAt: archived ? DateTimeOffset.UtcNow.AddDays(-1) : null, providerId: devProviderId, cancellationToken: Ct);
    }

    private static async Task DeletePatientAsync(PatientSearchSeed seed, Guid patient)
    {
        var ids = ("p", (object)patient);
        await seed.ExecuteSqlAsync("""DELETE FROM "AuditEntries" WHERE "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = @p)""", CancellationToken.None, ids);
        await seed.ExecuteSqlAsync("""DELETE FROM "NoteSections" WHERE "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = @p)""", CancellationToken.None, ids);
        await seed.ExecuteSqlAsync("""DELETE FROM "Notes" WHERE "PatientId" = @p""", CancellationToken.None, ids);
        await seed.ExecuteSqlAsync("""DELETE FROM "Patients" WHERE "Id" = @p""", CancellationToken.None, ids);
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
