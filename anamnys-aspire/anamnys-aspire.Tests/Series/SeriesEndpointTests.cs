using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Server.Appointments.Series;
using Anamnys.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Anamnys.Tests.Series.SeriesScene;
using AvailabilityScene = Anamnys.Tests.Availability.AvailabilityScene;

namespace Anamnys.Tests.Series;

// design/specs/2026-11-09-appointment-series-design.md §4, §6. The routes answer 401 without a session through the
// running app; the handlers' answers (codes, shapes, 404, no-store) are checked on a scratch database, since the dev
// database the signed-in tests reach may not have the migration applied.
[Collection(SharedAppHostCollection.Name)]
public class SeriesEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string AnyId = "00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("POST", "providers/me/appointment-series")]
    [InlineData("GET", "providers/me/appointment-series/" + AnyId)]
    [InlineData("GET", "providers/me/patients/" + AnyId + "/appointment-series")]
    [InlineData("POST", "providers/me/appointment-series/" + AnyId + "/split/preview")]
    [InlineData("POST", "providers/me/appointment-series/" + AnyId + "/split")]
    [InlineData("POST", "providers/me/appointment-series/" + AnyId + "/end")]
    public async Task Route_WithoutSession_Returns401(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/phi/" + path);
        if (method is "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateGetAndList_AnswerTheirShapesAndCodes()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var created = await ExecuteAsync(http => SeriesEndpoints.CreateAsync(http, db, s.Provider, Weekly(s.Patient, count: 10), Now, Ct));
        var badInterval = await ExecuteAsync(http => SeriesEndpoints.CreateAsync(http, db, s.Provider, Weekly(s.Patient, interval: 4), Now, Ct));
        var badEnd = await ExecuteAsync(http => SeriesEndpoints.CreateAsync(http, db, s.Provider, Weekly(s.Patient, count: 0), Now, Ct));
        var badSlot = await ExecuteAsync(http => SeriesEndpoints.CreateAsync(
            http, db, s.Provider, Weekly(s.Patient) with { DurationMinutes = 2 }, Now, Ct));
        var foreignPatient = await ExecuteAsync(http => SeriesEndpoints.CreateAsync(http, db, s.Provider, Weekly(s.Stranger), Now, Ct));
        var past = await ExecuteAsync(http => SeriesEndpoints.CreateAsync(
            http, db, s.Provider, Weekly(s.Patient, startsAt: Now.AddMinutes(-1)), Now, Ct));
        var overlap = await ExecuteAsync(http => SeriesEndpoints.CreateAsync(http, db, s.Provider, Weekly(s.Patient), Now, Ct));
        using var createdJson = JsonDocument.Parse(created.Body);
        var seriesId = createdJson.RootElement.GetProperty("seriesId").GetGuid();
        var get = await ExecuteAsync(http => SeriesEndpoints.GetAsync(http, db, s.Provider, seriesId, Now, Ct));
        var getForeign = await ExecuteAsync(http => SeriesEndpoints.GetAsync(http, db, s.OtherProvider, seriesId, Now, Ct));
        var list = await ExecuteAsync(http => SeriesEndpoints.ListForPatientAsync(http, db, s.Provider, s.Patient, Now, Ct));
        var listForeign = await ExecuteAsync(http => SeriesEndpoints.ListForPatientAsync(http, db, s.Provider, s.Stranger, Now, Ct));

        // Assert
        foreach (var answer in new[] { created, badInterval, badEnd, badSlot, foreignPatient, past, overlap, get, getForeign, list, listForeign })
        {
            answer.CacheControl.Should().Be("no-store");
        }
        created.Status.Should().Be(StatusCodes.Status201Created);
        createdJson.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("seriesId", "firstAppointmentId", "generated", "skipped");
        createdJson.RootElement.GetProperty("generated").GetInt32().Should().Be(10);
        badInterval.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(badInterval).Should().Be("invalid_interval");
        badEnd.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(badEnd).Should().Be("invalid_end");
        badSlot.Status.Should().Be(StatusCodes.Status400BadRequest);
        foreignPatient.Status.Should().Be(StatusCodes.Status404NotFound);
        past.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(past).Should().Be("start_in_past");
        overlap.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(overlap).Should().Be("overlap");

        get.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(get.Body))
        {
            var root = json.RootElement;
            root.EnumerateObject().Select(p => p.Name).Should().Equal(
                "id", "patientId", "interval", "weekday", "startTime", "timezone", "startsOn", "endsOn", "occurrenceCount", "active",
                "description", "upcoming");
            root.GetProperty("weekday").GetInt32().Should().Be(2);
            root.GetProperty("startTime").GetString().Should().Be("14:00");
            root.GetProperty("startsOn").GetString().Should().Be("2026-11-17");
            root.GetProperty("endsOn").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("occurrenceCount").GetInt32().Should().Be(10);
            root.GetProperty("active").GetBoolean().Should().BeTrue();
            root.GetProperty("description").GetString().Should().Be("toda terça às 14:00");
            root.GetProperty("upcoming").GetArrayLength().Should().Be(10);
            root.GetProperty("upcoming")[0].EnumerateObject().Select(p => p.Name).Should().Equal("id", "startsAt", "status", "seriesIndex", "detached");
        }
        getForeign.Status.Should().Be(StatusCodes.Status404NotFound);
        list.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(list.Body))
        {
            json.RootElement.GetArrayLength().Should().Be(1);
            json.RootElement[0].TryGetProperty("upcoming", out _).Should().BeFalse();
            json.RootElement[0].GetProperty("id").GetGuid().Should().Be(seriesId);
        }
        listForeign.Status.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task SplitAndEnd_AnswerTheirShapesAndCodes()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 6));
        var occurrences = await OccurrencesAsync(s, Ct, created.SeriesId);
        var body = new SplitSeriesRequest(occurrences[2].Id, 4, "10:00", 50, "online");
        await using var db = s.CreateDbContext();

        // Act
        var invalid = await ExecuteAsync(http => SeriesEndpoints.PreviewSplitAsync(http, db, s.Provider, created.SeriesId, body with { StartTime = "25:00" }, Now, Ct));
        var foreign = await ExecuteAsync(http => SeriesEndpoints.PreviewSplitAsync(http, db, s.OtherProvider, created.SeriesId, body, Now, Ct));
        var preview = await ExecuteAsync(http => SeriesEndpoints.PreviewSplitAsync(http, db, s.Provider, created.SeriesId, body, Now, Ct));
        var past = await ExecuteAsync(http => SeriesEndpoints.SplitAsync(
            http, db, s.Provider, created.SeriesId, body, occurrences[2].StartsAt.AddHours(1), Ct));
        var split = await ExecuteAsync(http => SeriesEndpoints.SplitAsync(http, db, s.Provider, created.SeriesId, body, Now, Ct));
        using var splitJson = JsonDocument.Parse(split.Body);
        var newId = splitJson.RootElement.GetProperty("newSeriesId").GetGuid();
        var endForeign = await ExecuteAsync(http => SeriesEndpoints.EndAsync(http, db, s.OtherProvider, newId, new EndSeriesRequest(Today), Now, Ct));
        var endBad = await ExecuteAsync(http => SeriesEndpoints.EndAsync(http, db, s.Provider, newId, null, Now, Ct));
        var end = await ExecuteAsync(http => SeriesEndpoints.EndAsync(http, db, s.Provider, newId, new EndSeriesRequest(Today), Now, Ct));
        var endAgain = await ExecuteAsync(http => SeriesEndpoints.EndAsync(http, db, s.Provider, newId, new EndSeriesRequest(Today), Now, Ct));

        // Assert
        foreach (var answer in new[] { invalid, foreign, preview, past, split, endForeign, endBad, end, endAgain })
        {
            answer.CacheControl.Should().Be("no-store");
        }
        invalid.Status.Should().Be(StatusCodes.Status400BadRequest);
        foreign.Status.Should().Be(StatusCodes.Status404NotFound);
        preview.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(preview.Body))
        {
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("newFirstStartsAt", "moving", "staying");
            json.RootElement.GetProperty("moving").GetInt32().Should().Be(4);
            json.RootElement.GetProperty("staying").GetArrayLength().Should().Be(0);
        }
        past.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(past).Should().Be("invalid_occurrence");
        split.Status.Should().Be(StatusCodes.Status200OK);
        splitJson.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("newSeriesId", "skipped");
        endForeign.Status.Should().Be(StatusCodes.Status404NotFound);
        endBad.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(endBad).Should().Be("invalid_date");
        end.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(end.Body))
        {
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("cancelled");
            json.RootElement.GetProperty("cancelled").GetInt32().Should().Be(4);
        }
        endAgain.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(endAgain).Should().Be("series_ended");
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
