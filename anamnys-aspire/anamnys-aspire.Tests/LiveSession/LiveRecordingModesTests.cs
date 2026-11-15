using Anamnys.Server.LiveSession;
using Anamnys.Server.RecordingConsents;
using Anamnys.Tests.RecordingConsents;
using FluentAssertions;
using static Anamnys.Tests.RecordingConsents.ConsentScene;

namespace Anamnys.Tests.LiveSession;

// design/specs/2026-11-15-recording-consent-design.md §4–§5: a session recording needs a signed consent (and a minor's
// assent); a dictation needs the provider's confirmation and is audited. Uses the consent scene's scratch database.
[Collection(SharedAppHostCollection.Name)]
public class LiveRecordingModesTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static LiveRecordingModesTests() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    private static async Task<BeginResult> BeginAsync(ConsentScene s, Guid patient, string mode, bool confirmed = false, DateTimeOffset? now = null)
    {
        await using var db = s.CreateDbContext();
        return await LiveRecordings.CheckBeginAsync(db, s.Provider, patient, null, "DAP", "pt", mode, confirmed, now ?? Later, Ct);
    }

    [Fact]
    public async Task Session_WithoutAnyConsent_IsRefusedWithNone()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);

        var result = await BeginAsync(s, s.Adult, "session");

        result.Should().Be(new BeginResult(BeginCheck.ConsentRequired, "none"));
    }

    [Fact]
    public async Task Session_ConsentOnlySent_IsRefusedWithSent()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        (await s.SendAsync(s.Adult, Ct)).Outcome.Should().Be(ConsentOutcome.Ok);

        var result = await BeginAsync(s, s.Adult, "session");

        result.Should().Be(new BeginResult(BeginCheck.ConsentRequired, "sent"));
    }

    [Fact]
    public async Task Session_RevokedConsent_IsRefusedWithRevoked()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);
        (await s.RevokeByLinkAsync(s.RevokeToken(), AdultBirth, Ct)).Outcome.Should().Be(PublicConsentOutcome.Ok);

        var result = await BeginAsync(s, s.Adult, "session");

        result.Should().Be(new BeginResult(BeginCheck.ConsentRequired, "revoked"));
    }

    [Fact]
    public async Task Session_BeginAgainWithTheSameRecordingIdAfterRevocation_IsRefusedWithRevoked()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);
        // The check is stateless (the reconnect's begin with the same recordingId runs the same check), so a second call is that begin.
        await using var db = s.CreateDbContext();
        var first = await LiveRecordings.CheckBeginAsync(db, s.Provider, s.Adult, null, "DAP", "pt", "session", false, Later, Ct);
        (await s.RevokeByLinkAsync(s.RevokeToken(), AdultBirth, Ct)).Outcome.Should().Be(PublicConsentOutcome.Ok);

        var again = await LiveRecordings.CheckBeginAsync(db, s.Provider, s.Adult, null, "DAP", "pt", "session", false, Later, Ct);

        first.Should().Be(new BeginResult(BeginCheck.Ok, null));
        again.Should().Be(new BeginResult(BeginCheck.ConsentRequired, "revoked"));
    }

    [Fact]
    public async Task Session_ExpiredConsent_IsRefusedWithExpired()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (consent, _) = await s.SignedAsync(s.Adult, Ct);
        await s.Db.ExecuteAsync(
            """UPDATE "RecordingConsents" SET "State" = 'EXPIRED', "ExpiredReason" = 'new_version' WHERE "Id" = @c""", Ct, ("c", consent));

        var result = await BeginAsync(s, s.Adult, "session");

        result.Should().Be(new BeginResult(BeginCheck.ConsentRequired, "expired"));
    }

    [Fact]
    public async Task Session_MinorSignedWithoutAssent_IsRefusedWithAssentMissing()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Minor, Ct);

        var result = await BeginAsync(s, s.Minor, "session");

        result.Should().Be(new BeginResult(BeginCheck.ConsentRequired, "assent_missing"));
    }

    [Fact]
    public async Task Session_SignedConsent_IsAllowed()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);

        var result = await BeginAsync(s, s.Adult, "session");

        result.Should().Be(new BeginResult(BeginCheck.Ok));
    }

    [Fact]
    public async Task Session_ArchivedPatient_IsArchivedNotConsentRequired()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @pa""", Ct, ("pa", s.Adult));

        var result = await BeginAsync(s, s.Adult, "session");

        result.Check.Should().Be(BeginCheck.Archived);
    }

    [Fact]
    public async Task Dictation_WithoutConfirmation_IsRefusedAndNotAudited()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);

        var result = await BeginAsync(s, s.Adult, "dictation", confirmed: false);

        result.Should().Be(new BeginResult(BeginCheck.DictationNotConfirmed));
        (await s.AuditAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Dictation_Confirmed_IsAllowedWithoutConsentAndAuditedWithIdsOnly()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);

        var result = await BeginAsync(s, s.Adult, "dictation", confirmed: true);

        result.Should().Be(new BeginResult(BeginCheck.Ok));
        (await s.AuditAsync(Ct)).Should().Equal($"dictation_confirmed {s.Adult}");
    }

    [Fact]
    public async Task Dictation_OtherProvidersPatient_IsNotFoundAndNotAudited()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);

        var result = await BeginAsync(s, s.Stranger, "dictation", confirmed: true);

        result.Check.Should().Be(BeginCheck.NotFound);
        (await s.AuditAsync(Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Session")]
    [InlineData("voice")]
    public async Task UnknownMode_IsInvalid(string mode)
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);

        var result = await BeginAsync(s, s.Adult, mode, confirmed: true);

        result.Check.Should().Be(BeginCheck.Invalid);
    }
}
