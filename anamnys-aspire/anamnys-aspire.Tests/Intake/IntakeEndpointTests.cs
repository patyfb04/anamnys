using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Server.Intakes;
using Anamnys.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Anamnys.Tests.Intakes.IntakeScene;

namespace Anamnys.Tests.Intakes;

// design/specs/2026-11-12-structured-intake-design.md §4, §6. The routes answer 401 without a session through the
// running app; the handlers' answers (codes, shapes, 404, no-store) are checked on a scratch database, since the dev
// database the signed-in tests reach may not have the migration applied.
[Collection(SharedAppHostCollection.Name)]
public class IntakeEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string AnyId = "00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("GET", "providers/me/intake-templates")]
    [InlineData("GET", "providers/me/intake-templates/" + AnyId)]
    [InlineData("POST", "providers/me/intake-templates/" + AnyId + "/adapt")]
    [InlineData("PUT", "providers/me/intake-templates/" + AnyId)]
    [InlineData("POST", "providers/me/intake-templates/" + AnyId + "/archive")]
    [InlineData("PUT", "providers/me/intake-settings")]
    [InlineData("GET", "providers/me/patients/" + AnyId + "/intakes")]
    [InlineData("POST", "providers/me/patients/" + AnyId + "/intakes")]
    [InlineData("GET", "providers/me/intakes/" + AnyId)]
    [InlineData("PUT", "providers/me/intakes/" + AnyId + "/answers")]
    [InlineData("POST", "providers/me/intakes/" + AnyId + "/note")]
    [InlineData("DELETE", "providers/me/intakes/" + AnyId)]
    [InlineData("POST", "providers/me/intakes/" + AnyId + "/unlock")]
    [InlineData("GET", "providers/me/intakes/" + AnyId + "/transfers")]
    [InlineData("POST", "providers/me/intakes/" + AnyId + "/transfers/" + AnyId + "/confirm")]
    [InlineData("POST", "providers/me/intakes/" + AnyId + "/transfers/" + AnyId + "/ignore")]
    public async Task Route_WithoutSession_Returns401(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/phi/" + path);
        if (method is "POST" or "PUT")
        {
            request.Content = path.EndsWith("answers") ? JsonContent.Create(Array.Empty<object>()) : JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TemplateHandlers_AnswerTheirShapesAndCodes()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var request = await IntakeTemplateTests.RequestFromAsync(s, IntakeRules.SystemDefaultTemplateId);

        // Act
        var list = await ExecuteAsync(http => IntakeEndpoints.ListTemplatesAsync(http, db, s.Provider, Ct));
        var get = await ExecuteAsync(http => IntakeEndpoints.GetTemplateAsync(http, db, s.Provider, IntakeRules.SystemDefaultTemplateId, Ct));
        var missing = await ExecuteAsync(http => IntakeEndpoints.GetTemplateAsync(http, db, s.Provider, Guid.NewGuid(), Ct));
        var putDefault = await ExecuteAsync(http => IntakeEndpoints.UpdateTemplateAsync(http, db, s.Provider, IntakeRules.SystemDefaultTemplateId, request, Now, Ct));
        var putInvalid = await ExecuteAsync(http => IntakeEndpoints.UpdateTemplateAsync(
            http, db, s.Provider, IntakeRules.SystemDefaultTemplateId, request with { Questions = [] }, Now, Ct));
        var adapted = await ExecuteAsync(http => IntakeEndpoints.AdaptTemplateAsync(http, db, s.Provider, IntakeRules.SystemDefaultTemplateId, null, Now, Ct));
        using var adaptedJson = JsonDocument.Parse(adapted.Body);
        var adaptedId = adaptedJson.RootElement.GetProperty("id").GetGuid();
        var nameTaken = await ExecuteAsync(http => IntakeEndpoints.AdaptTemplateAsync(
            http, db, s.Provider, IntakeRules.SystemDefaultTemplateId, new("Minha anamnese"), Now, Ct));
        var settings = await ExecuteAsync(http => IntakeEndpoints.SetSettingsAsync(http, db, s.Provider, new(adaptedId), Ct));
        var archive = await ExecuteAsync(http => IntakeEndpoints.ArchiveTemplateAsync(http, db, s.Provider, adaptedId, Now, Ct));
        var settingsArchived = await ExecuteAsync(http => IntakeEndpoints.SetSettingsAsync(http, db, s.Provider, new(adaptedId), Ct));
        var settingsBack = await ExecuteAsync(http => IntakeEndpoints.SetSettingsAsync(http, db, s.Provider, new(null), Ct));

        // Assert
        foreach (var answer in new[] { list, get, missing, putDefault, putInvalid, adapted, nameTaken, settings, archive, settingsArchived, settingsBack })
        {
            answer.CacheControl.Should().Be("no-store");
        }
        list.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(list.Body))
        {
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("defaultTemplateId", "templates");
            json.RootElement.GetProperty("templates")[0].EnumerateObject().Select(p => p.Name).Should()
                .Equal("id", "name", "version", "isDefault", "basedOnTemplateId", "archived", "inUse");
        }
        get.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(get.Body))
        {
            json.RootElement.EnumerateObject().Select(p => p.Name).Should()
                .Equal("id", "name", "version", "isDefault", "basedOnTemplateId", "archived", "inUse", "sections");
            var section = json.RootElement.GetProperty("sections")[0];
            section.EnumerateObject().Select(p => p.Name).Should().Equal("key", "title", "questions");
            section.GetProperty("questions")[0].EnumerateObject().Select(p => p.Name).Should()
                .Equal("id", "key", "label", "helpText", "answerType", "choices", "required", "targetField", "disabled", "position");
        }
        missing.Status.Should().Be(StatusCodes.Status404NotFound);
        putDefault.Status.Should().Be(StatusCodes.Status403Forbidden);
        Code(putDefault).Should().Be("template_read_only");
        putInvalid.Status.Should().Be(StatusCodes.Status400BadRequest);
        adapted.Status.Should().Be(StatusCodes.Status201Created);
        nameTaken.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(nameTaken).Should().Be("name_taken");
        settings.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(settings.Body))
        {
            json.RootElement.GetProperty("defaultTemplateId").GetGuid().Should().Be(adaptedId);
        }
        archive.Status.Should().Be(StatusCodes.Status200OK);
        settingsArchived.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(settingsArchived).Should().Be("template_archived");
        using (var json = JsonDocument.Parse(settingsBack.Body))
        {
            json.RootElement.GetProperty("defaultTemplateId").GetGuid().Should().Be(IntakeRules.SystemDefaultTemplateId);
        }
    }

    [Fact]
    public async Task IntakeHandlers_AnswerTheirShapesAndCodes()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var archivedPatient = await s.Db.AddPatientAsync(s.Provider, "Arquivada", Ct);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @pa""", Ct, ("pa", archivedPatient));
        var questions = await s.QuestionsAsync(Ct);
        await using var db = s.CreateDbContext();

        // Act
        var started = await ExecuteAsync(http => IntakeEndpoints.StartAsync(http, db, s.Provider, s.Patient, Now, Ct));
        using var startedJson = JsonDocument.Parse(started.Body);
        var id = startedJson.RootElement.GetProperty("id").GetGuid();
        var again = await ExecuteAsync(http => IntakeEndpoints.StartAsync(http, db, s.Provider, s.Patient, Now, Ct));
        var archived = await ExecuteAsync(http => IntakeEndpoints.StartAsync(http, db, s.Provider, archivedPatient, Now, Ct));
        var foreign = await ExecuteAsync(http => IntakeEndpoints.StartAsync(http, db, s.Provider, s.Stranger, Now, Ct));
        var invalid = await ExecuteAsync(http => IntakeEndpoints.SaveAnswersAsync(
            http, db, s.Provider, id, [new IntakeAnswerInput(questions["intensidade"], "x")], Now, Ct));
        var noteMissing = await ExecuteAsync(http => IntakeEndpoints.GenerateNoteAsync(http, db, s.Provider, id, Now, Ct));
        var saved = await ExecuteAsync(http => IntakeEndpoints.SaveAnswersAsync(http, db, s.Provider, id,
            [new IntakeAnswerInput(questions["queixa_principal"], "Queixa"), new IntakeAnswerInput(questions["risco"], "Nunca")], Now, Ct));
        var note = await ExecuteAsync(http => IntakeEndpoints.GenerateNoteAsync(http, db, s.Provider, id, Now, Ct));
        var get = await ExecuteAsync(http => IntakeEndpoints.GetAsync(http, db, s.Provider, id, Ct));
        var getForeign = await ExecuteAsync(http => IntakeEndpoints.GetAsync(http, db, s.OtherProvider, id, Ct));
        var list = await ExecuteAsync(http => IntakeEndpoints.ListAsync(http, db, s.Provider, s.Patient, Ct));
        var transfers = await ExecuteAsync(http => IntakeEndpoints.ListTransfersAsync(http, db, s.Provider, id, Ct));
        var decideMissing = await ExecuteAsync(http => IntakeEndpoints.DecideAsync(http, db, s.Provider, id, Guid.NewGuid(), true, Now, Ct));

        // Assert
        foreach (var answer in new[] { started, again, archived, foreign, invalid, noteMissing, saved, note, get, getForeign, list, transfers, decideMissing })
        {
            answer.CacheControl.Should().Be("no-store");
        }
        started.Status.Should().Be(StatusCodes.Status201Created);
        startedJson.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("id");
        again.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(again).Should().Be("intake_in_progress");
        archived.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(archived).Should().Be("patient_archived");
        foreign.Status.Should().Be(StatusCodes.Status404NotFound);
        invalid.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(invalid).Should().Be("invalid_answer");
        using (var json = JsonDocument.Parse(invalid.Body))
        {
            json.RootElement.GetProperty("questionIds")[0].GetGuid().Should().Be(questions["intensidade"]);
        }
        noteMissing.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(noteMissing).Should().Be("required_missing");
        using (var json = JsonDocument.Parse(noteMissing.Body))
        {
            json.RootElement.GetProperty("questionIds").GetArrayLength().Should().Be(2);
        }
        saved.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(saved.Body))
        {
            json.RootElement.GetArrayLength().Should().Be(2);
            json.RootElement[0].EnumerateObject().Select(p => p.Name).Should().Equal("questionId", "answer", "updatedAt");
        }
        note.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(note.Body))
        {
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("noteId");
        }
        get.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(get.Body))
        {
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
                "id", "patientId", "status", "createdAt", "updatedAt", "signedAt", "noteId", "noteStatus", "pendingTransfers", "template",
                "sections", "answers");
            json.RootElement.GetProperty("noteStatus").GetString().Should().Be("ReadyForReview");
        }
        getForeign.Status.Should().Be(StatusCodes.Status404NotFound);
        using (var json = JsonDocument.Parse(list.Body))
        {
            json.RootElement[0].EnumerateObject().Select(p => p.Name).Should()
                .Equal("id", "status", "createdAt", "signedAt", "noteId", "pendingTransfers");
        }
        transfers.Status.Should().Be(StatusCodes.Status200OK);
        transfers.Body.Should().Be("[]");
        decideMissing.Status.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task DraftAndTransferHandlers_MapTheirCodes()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var draft = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(draft, Ct);
        await s.GenerateNoteAsync(draft, Ct);
        var questions = await s.QuestionsAsync(Ct);
        await using var db = s.CreateDbContext();

        // Act
        var locked = await ExecuteAsync(http => IntakeEndpoints.SaveAnswersAsync(
            http, db, s.Provider, draft, [new IntakeAnswerInput(questions["lazer"], "Leitura")], Now, Ct));
        var unlock = await ExecuteAsync(http => IntakeEndpoints.UnlockAsync(http, db, s.Provider, draft, Now, Ct));
        var discard = await ExecuteAsync(http => IntakeEndpoints.DiscardAsync(http, db, s.Provider, draft, Now, Ct));
        var discardMissing = await ExecuteAsync(http => IntakeEndpoints.DiscardAsync(http, db, s.Provider, draft, Now, Ct));
        var (signed, _) = await s.SignedIntakeAsync(Ct);
        var transfer = (await s.TransfersAsync(signed, Ct)).First();
        var notDraft = await ExecuteAsync(http => IntakeEndpoints.SaveAnswersAsync(
            http, db, s.Provider, signed, [new IntakeAnswerInput(questions["lazer"], "Leitura")], Now, Ct));
        var discardSigned = await ExecuteAsync(http => IntakeEndpoints.DiscardAsync(http, db, s.Provider, signed, Now, Ct));
        var ignored = await ExecuteAsync(http => IntakeEndpoints.DecideAsync(http, db, s.Provider, signed, transfer.Id, false, Now, Ct));
        var notPending = await ExecuteAsync(http => IntakeEndpoints.DecideAsync(http, db, s.Provider, signed, transfer.Id, true, Now, Ct));
        var other = (await s.TransfersAsync(signed, Ct)).First(t => t.Id != transfer.Id);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @pa""", Ct, ("pa", s.Patient));
        var archived = await ExecuteAsync(http => IntakeEndpoints.DecideAsync(http, db, s.Provider, signed, other.Id, true, Now, Ct));

        // Assert
        foreach (var answer in new[] { locked, unlock, discard, discardMissing, notDraft, discardSigned, ignored, notPending, archived })
        {
            answer.CacheControl.Should().Be("no-store");
        }
        locked.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(locked).Should().Be("answers_locked");
        unlock.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(unlock.Body))
        {
            json.RootElement.GetProperty("noteId").ValueKind.Should().Be(JsonValueKind.Null);
        }
        discard.Status.Should().Be(StatusCodes.Status204NoContent);
        discardMissing.Status.Should().Be(StatusCodes.Status404NotFound);
        notDraft.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(notDraft).Should().Be("not_draft");
        discardSigned.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(discardSigned).Should().Be("not_draft");
        ignored.Status.Should().Be(StatusCodes.Status200OK);
        notPending.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(notPending).Should().Be("not_pending");
        archived.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(archived).Should().Be("patient_archived");
    }

    private static string? Code(Answer answer)
    {
        using var json = JsonDocument.Parse(answer.Body);
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed record Answer(int Status, string? CacheControl, byte[] Bytes)
    {
        public string Body => System.Text.Encoding.UTF8.GetString(Bytes);
    }

    // Runs a handler's result the way the pipeline does, against an in-memory response.
    private static async Task<Answer> ExecuteAsync(Func<HttpContext, Task<IResult>> handler)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        http.Response.Body = body;
        var result = await handler(http);
        await result.ExecuteAsync(http);
        return new Answer(http.Response.StatusCode, http.Response.Headers.CacheControl.ToString(), body.ToArray());
    }
}
