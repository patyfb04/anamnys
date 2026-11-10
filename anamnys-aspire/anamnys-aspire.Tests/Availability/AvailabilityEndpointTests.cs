using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Server.Appointments;
using Anamnys.Server.Availability;
using Anamnys.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Anamnys.Tests.Availability.AvailabilityScene;

namespace Anamnys.Tests.Availability;

// design/specs/2026-11-09-availability-design.md §4, §6. The routes answer 401 without a session through the running app;
// the handlers' answers (codes, shapes, 404, no-store) are checked on a scratch database, since the dev database the
// signed-in tests reach may not have the migration applied.
[Collection(SharedAppHostCollection.Name)]
public class AvailabilityEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string AnyId = "00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("GET", "providers/me/availability")]
    [InlineData("PUT", "providers/me/availability")]
    [InlineData("DELETE", "providers/me/availability/upcoming/2030-01-01")]
    [InlineData("GET", "providers/me/holiday-settings")]
    [InlineData("PUT", "providers/me/holiday-settings")]
    [InlineData("GET", "providers/me/blocks?from=2026-11-01T00:00:00Z&to=2026-12-01T00:00:00Z")]
    [InlineData("POST", "providers/me/blocks/preview")]
    [InlineData("POST", "providers/me/blocks")]
    [InlineData("DELETE", "providers/me/blocks/" + AnyId)]
    [InlineData("GET", "providers/me/calendar-overlay?from=2026-11-01&to=2026-11-30")]
    public async Task Route_WithoutSession_Returns401(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/phi/" + path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Availability_SaveGetAndCancel_AnswerTheirShapesAndCodes()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var body = new SaveAvailabilityRequest(Today.AddDays(3), [new SaveAvailabilityDay(1, [new("08:00", "12:00")])]);

        // Act
        var saved = await ExecuteAsync(http => AvailabilityEndpoints.SaveAvailabilityAsync(http, db, s.Provider, body, Now, Ct));
        var past = await ExecuteAsync(http => AvailabilityEndpoints.SaveAvailabilityAsync(
            http, db, s.Provider, body with { EffectiveFrom = Today.AddDays(-1) }, Now, Ct));
        var invalid = await ExecuteAsync(http => AvailabilityEndpoints.SaveAvailabilityAsync(
            http, db, s.Provider, body with { Days = [new SaveAvailabilityDay(1, [new("08:00", "08:00")])] }, Now, Ct));
        var get = await ExecuteAsync(http => AvailabilityEndpoints.GetAvailabilityAsync(http, db, s.Provider, Now, Ct));
        var foreignCancel = await ExecuteAsync(http => AvailabilityEndpoints.CancelUpcomingAsync(
            http, db, s.OtherProvider, Today.AddDays(3).ToString("yyyy-MM-dd"), Now, Ct));
        var badDate = await ExecuteAsync(http => AvailabilityEndpoints.CancelUpcomingAsync(http, db, s.Provider, "amanha", Now, Ct));
        var cancel = await ExecuteAsync(http => AvailabilityEndpoints.CancelUpcomingAsync(
            http, db, s.Provider, Today.AddDays(3).ToString("yyyy-MM-dd"), Now, Ct));

        // Assert
        foreach (var answer in new[] { saved, past, invalid, get, foreignCancel, badDate, cancel })
        {
            answer.CacheControl.Should().Be("no-store");
        }
        saved.Status.Should().Be(StatusCodes.Status200OK);
        past.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(past).Should().Be("effective_in_past");
        invalid.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(invalid).Should().Be("invalid_intervals");
        get.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(get.Body))
        {
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("current", "upcoming");
            json.RootElement.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null);
            var upcoming = json.RootElement.GetProperty("upcoming")[0];
            upcoming.GetProperty("effectiveFrom").GetString().Should().Be(Today.AddDays(3).ToString("yyyy-MM-dd"));
            var monday = upcoming.GetProperty("days")[1];
            monday.GetProperty("weekday").GetInt32().Should().Be(1);
            monday.GetProperty("intervals")[0].GetProperty("start").GetString().Should().Be("08:00");
            monday.GetProperty("intervals")[0].GetProperty("end").GetString().Should().Be("12:00");
        }
        foreignCancel.Status.Should().Be(StatusCodes.Status404NotFound);
        badDate.Status.Should().Be(StatusCodes.Status404NotFound);
        cancel.Status.Should().Be(StatusCodes.Status204NoContent);
    }

    [Fact]
    public async Task HolidaySettings_AnswerTheirShape()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var saved = await ExecuteAsync(http => AvailabilityEndpoints.SaveHolidaySettingsAsync(
            http, db, s.Provider, new SaveHolidaySettingsRequest(true, ["natal"]), Now, Ct));
        var bad = await ExecuteAsync(http => AvailabilityEndpoints.SaveHolidaySettingsAsync(
            http, db, s.Provider, new SaveHolidaySettingsRequest(true, ["natalino"]), Now, Ct));
        var get = await ExecuteAsync(http => AvailabilityEndpoints.GetHolidaySettingsAsync(http, db, s.Provider, Now, Ct));

        // Assert
        saved.Status.Should().Be(StatusCodes.Status200OK);
        bad.Status.Should().Be(StatusCodes.Status400BadRequest);
        get.CacheControl.Should().Be("no-store");
        using var json = JsonDocument.Parse(get.Body);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("worksOnHolidays", "disabledHolidays", "holidays");
        json.RootElement.GetProperty("worksOnHolidays").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("disabledHolidays")[0].GetString().Should().Be("natal");
        var first = json.RootElement.GetProperty("holidays")[0];
        first.EnumerateObject().Select(p => p.Name).Should().Equal("key", "name", "date");
        first.GetProperty("date").GetString().Should().Be("2026-01-01");
    }

    [Fact]
    public async Task Blocks_PreviewCreateListDelete_AnswerTheirShapesCodesAnd404()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        var monday10 = new DateTimeOffset(2026, 11, 16, 13, 0, 0, TimeSpan.Zero);
        var booked = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, monday10, "scheduled", Ct);
        var foreign = await s.AddBlockAsync(s.OtherProvider, monday10, monday10.AddHours(1), Ct);
        await using var db = s.CreateDbContext();
        var block = new BlockRequest("block", monday10.AddMinutes(-30), monday10.AddHours(2), Reason: "Consulta médica");

        // Act
        var preview = await ExecuteAsync(http => AvailabilityEndpoints.PreviewBlockAsync(http, db, s.Provider, block, Now, Ct));
        var badKind = await ExecuteAsync(http => AvailabilityEndpoints.PreviewBlockAsync(http, db, s.Provider, block with { Kind = "x" }, Now, Ct));
        var badRange = await ExecuteAsync(http => AvailabilityEndpoints.CreateBlockAsync(
            http, db, s.Provider, block with { EndsAt = block.StartsAt }, Now, Ct));
        var created = await ExecuteAsync(http => AvailabilityEndpoints.CreateBlockAsync(http, db, s.Provider, block, Now, Ct));
        var changed = await ExecuteAsync(http => AvailabilityEndpoints.CreateBlockAsync(
            http, db, s.Provider, block with { CancelAffected = true, ExpectedAppointmentIds = [] }, Now, Ct));
        var required = await ExecuteAsync(http => AvailabilityEndpoints.CreateBlockAsync(
            http, db, s.Provider, block with { CancelAffected = true }, Now, Ct));
        var list = await ExecuteAsync(http => AvailabilityEndpoints.ListBlocksAsync(http, db, s.Provider, monday10.AddDays(-1), monday10.AddDays(1), Ct));
        var badList = await ExecuteAsync(http => AvailabilityEndpoints.ListBlocksAsync(http, db, s.Provider, null, monday10, Ct));
        var deleteForeign = await ExecuteAsync(http => AvailabilityEndpoints.DeleteBlockAsync(http, db, s.Provider, foreign, Now, Ct));

        // Assert
        foreach (var answer in new[] { preview, badKind, badRange, created, changed, required, list, badList, deleteForeign })
        {
            answer.CacheControl.Should().Be("no-store");
        }
        preview.Status.Should().Be(StatusCodes.Status200OK);
        using (var json = JsonDocument.Parse(preview.Body))
        {
            var item = json.RootElement.EnumerateArray().Single();
            item.EnumerateObject().Select(p => p.Name).Should().Equal("id", "startsAt", "endsAt", "patientId", "patientName");
            item.GetProperty("id").GetGuid().Should().Be(booked);
        }
        Code(badKind).Should().Be("invalid_kind");
        Code(badRange).Should().Be("invalid_range");
        created.Status.Should().Be(StatusCodes.Status201Created);
        using (var json = JsonDocument.Parse(created.Body))
        {
            json.RootElement.GetProperty("cancelledAppointmentIds").GetArrayLength().Should().Be(0);
        }
        using (var json = JsonDocument.Parse(list.Body))
        {
            var item = json.RootElement.EnumerateArray().Single();
            item.EnumerateObject().Select(p => p.Name).Should().Equal("id", "kind", "startsAt", "endsAt", "allDay", "reason");
            item.GetProperty("reason").GetString().Should().Be("Consulta médica");
        }
        changed.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(changed).Should().Be("changed");
        using (var json = JsonDocument.Parse(changed.Body))
        {
            var item = json.RootElement.GetProperty("appointments").EnumerateArray().Single();
            item.EnumerateObject().Select(p => p.Name).Should().Equal("id", "startsAt", "endsAt", "patientId", "patientName");
            item.GetProperty("id").GetGuid().Should().Be(booked);
        }
        required.Status.Should().Be(StatusCodes.Status400BadRequest);
        Code(required).Should().Be("expected_appointments_required");
        Code(badList).Should().Be("invalid_range");
        deleteForeign.Status.Should().Be(StatusCodes.Status404NotFound);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Appointments" WHERE "Status" = 'scheduled'""", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Overlay_AnswersItsShape_AndRefusesARangeOver62Days()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var month = await ExecuteAsync(http => AvailabilityEndpoints.OverlayAsync(http, db, s.Provider, "2026-11-01", "2027-01-01", Ct)); // 62 days
        var tooLong = await ExecuteAsync(http => AvailabilityEndpoints.OverlayAsync(http, db, s.Provider, "2026-11-01", "2027-01-02", Ct));
        var reversed = await ExecuteAsync(http => AvailabilityEndpoints.OverlayAsync(http, db, s.Provider, "2026-11-10", "2026-11-09", Ct));
        var missing = await ExecuteAsync(http => AvailabilityEndpoints.OverlayAsync(http, db, s.Provider, null, "2026-11-09", Ct));

        // Assert
        month.Status.Should().Be(StatusCodes.Status200OK);
        month.CacheControl.Should().Be("no-store");
        using (var json = JsonDocument.Parse(month.Body))
        {
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("days", "blocks");
            json.RootElement.GetProperty("days").GetArrayLength().Should().Be(62);
            var day = json.RootElement.GetProperty("days").EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-11-16");
            day.EnumerateObject().Select(p => p.Name).Should().Equal("date", "windows", "holiday");
            day.GetProperty("windows")[0].EnumerateObject().Select(p => p.Name).Should().Equal("startsAt", "endsAt");
            day.GetProperty("holiday").ValueKind.Should().Be(JsonValueKind.Null);
            var holiday = json.RootElement.GetProperty("days").EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-11-02");
            holiday.GetProperty("holiday").GetProperty("key").GetString().Should().Be("finados");
            holiday.GetProperty("holiday").GetProperty("name").GetString().Should().Be("Finados");
        }
        foreach (var refused in new[] { tooLong, reversed, missing })
        {
            refused.Status.Should().Be(StatusCodes.Status400BadRequest);
            Code(refused).Should().Be("invalid_range");
        }
    }

    [Fact]
    public async Task AppointmentConflict_CarriesTheReasonsWithoutEmptyFields()
    {
        // Arrange
        var result = new AppointmentWriteResult(AppointmentOutcome.OutsideAvailability, Reasons:
        [
            new OutsideAvailabilityReason("outside_hours"),
            new OutsideAvailabilityReason("block", new DateTimeOffset(2027, 1, 10, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2027, 1, 21, 3, 0, 0, TimeSpan.Zero), BlockKind: "vacation"),
            new OutsideAvailabilityReason("holiday", HolidayName: "Tiradentes"),
        ]);

        // Act
        var answer = await ExecuteAsync(_ => Task.FromResult(AppointmentEndpoints.ToResult(result)));

        // Assert
        answer.Status.Should().Be(StatusCodes.Status409Conflict);
        using var json = JsonDocument.Parse(answer.Body);
        json.RootElement.GetProperty("code").GetString().Should().Be("outside_availability");
        var reasons = json.RootElement.GetProperty("reasons").EnumerateArray().ToList();
        reasons[0].EnumerateObject().Select(p => p.Name).Should().Equal("kind");
        reasons[1].EnumerateObject().Select(p => p.Name).Should().Equal("kind", "from", "to", "blockKind");
        reasons[1].GetProperty("blockKind").GetString().Should().Be("vacation");
        reasons[2].EnumerateObject().Select(p => p.Name).Should().Equal("kind", "holidayName");
        reasons[2].GetProperty("holidayName").GetString().Should().Be("Tiradentes");
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
