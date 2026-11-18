using Anamnys.Server.LiveSession;
using Anamnys.Server.RecordingConsents;
using Anamnys.Tests.RecordingConsents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static Anamnys.Tests.RecordingConsents.ConsentScene;

namespace Anamnys.Tests.LiveSession;

// design/specs/2026-11-15-recording-consent-design.md §4–§5: a session recording needs a signed consent (and a minor's
// assent); a dictation needs the provider's confirmation and is audited. Uses the consent scene's scratch database.
[Collection(SharedAppHostCollection.Name)]
public class LiveRecordingModesTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static LiveRecordingModesTests() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    private static async Task<BeginResult> BeginAsync(
        ConsentScene s, Guid patient, string mode, bool confirmed = false, DateTimeOffset? now = null, string source = RecordingSources.InPerson)
    {
        await using var db = s.CreateDbContext();
        return await LiveRecordings.CheckBeginAsync(db, s.Provider, patient, null, "DAP", "pt", mode, confirmed, source, now ?? Later, Ct);
    }

    // A session recording in progress (consent signed), as the hub opens it.
    private static (RecordingBuffers Buffers, Guid Id) Open(
        ConsentScene s, string mode, string source = RecordingSources.InPerson)
    {
        var buffers = new RecordingBuffers(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Later));
        var id = Guid.NewGuid();
        buffers.Begin(id, LiveRecordings.NewMeta(s.Provider, s.Adult, null, "DAP", "pt", mode, source, Later)).Should().Be(BeginStatus.Opened);
        return (buffers, id);
    }

    private static async Task<SwitchStatus> SwitchAsync(ConsentScene s, RecordingBuffers buffers, Guid id, string source, Guid? provider = null)
    {
        await using var db = s.CreateDbContext();
        return await LiveRecordings.SwitchSourceAsync(db, buffers, provider ?? s.Provider, id, source, Later, Ct);
    }

    [Theory]
    [InlineData("in_person")]
    [InlineData("online_capture")]
    [InlineData("online_speaker")]
    public async Task Session_AcceptsEveryKnownSource(string source)
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);

        var result = await BeginAsync(s, s.Adult, "session", source: source);

        result.Should().Be(new BeginResult(BeginCheck.Ok));
    }

    [Theory]
    [InlineData("")]
    [InlineData("phone")]
    [InlineData("In_Person")]
    public async Task UnknownSource_IsInvalid_ForSessionAndDictation(string source)
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);

        var session = await BeginAsync(s, s.Adult, "session", source: source);
        var dictation = await BeginAsync(s, s.Adult, "dictation", confirmed: true, source: source);

        session.Check.Should().Be(BeginCheck.Invalid);
        dictation.Check.Should().Be(BeginCheck.Invalid);
    }

    [Theory]
    [InlineData("online_capture")]
    [InlineData("online_speaker")]
    public async Task Dictation_RefusesOnlineSources_AndIsNotAudited(string source)
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);

        var result = await BeginAsync(s, s.Adult, "dictation", confirmed: true, source: source);

        result.Check.Should().Be(BeginCheck.Invalid);
        (await s.AuditAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public void NewMeta_StoresTheInitialSource()
    {
        var meta = LiveRecordings.NewMeta(Guid.NewGuid(), Guid.NewGuid(), null, "DAP", "pt", "session", "online_capture", Later);

        meta.Sources.Should().Equal("online_capture");
        meta.Mode.Should().Be("session");
    }

    [Fact]
    public async Task Resume_AddsItsSourceWhenNew_AndKeepsTheOrder()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (buffers, id) = Open(s, "session", RecordingSources.OnlineCapture);

        var resumedSame = buffers.Begin(id, LiveRecordings.NewMeta(s.Provider, s.Adult, null, "DAP", "pt", "session", RecordingSources.OnlineCapture, Later));
        var resumedNew = buffers.Begin(id, LiveRecordings.NewMeta(s.Provider, s.Adult, null, "DAP", "pt", "session", RecordingSources.OnlineSpeaker, Later));

        resumedSame.Should().Be(BeginStatus.Resumed);
        resumedNew.Should().Be(BeginStatus.Resumed);
        buffers.Peek(id, s.Provider)!.Sources.Should().Equal(RecordingSources.OnlineCapture, RecordingSources.OnlineSpeaker);
    }

    [Fact]
    public async Task Resume_WithAnotherMode_IsRefused_AndChangesNothing()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (buffers, id) = Open(s, "session", RecordingSources.OnlineCapture);

        var resumed = buffers.Begin(id, LiveRecordings.NewMeta(s.Provider, s.Adult, null, "DAP", "pt", "dictation", RecordingSources.InPerson, Later));

        resumed.Should().Be(BeginStatus.ModeMismatch);
        var meta = buffers.Peek(id, s.Provider)!;
        meta.Mode.Should().Be("session");
        meta.Sources.Should().Equal(RecordingSources.OnlineCapture);
    }

    [Fact]
    public async Task SwitchSource_AddsTheSourceToTheMeta_AndIsAuditedWithIdsAndCodeOnly()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (buffers, id) = Open(s, "session", RecordingSources.OnlineCapture);

        var status = await SwitchAsync(s, buffers, id, RecordingSources.OnlineSpeaker);
        var again = await SwitchAsync(s, buffers, id, RecordingSources.OnlineSpeaker);

        status.Should().Be(SwitchStatus.Ok);
        again.Should().Be(SwitchStatus.Ok);
        buffers.Peek(id, s.Provider)!.Sources.Should().Equal(RecordingSources.OnlineCapture, RecordingSources.OnlineSpeaker);
        await using var db = s.CreateDbContext();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.Action == "recording_source_switched").ToListAsync(Ct);
        audit.Should().HaveCount(2);
        audit.Should().OnlyContain(a => a.ActorId == s.Provider && a.NoteId == null && a.NewValue == $"{id} online_speaker");
    }

    [Fact]
    public async Task SwitchSource_UnknownSource_IsInvalid_AndChangesNothing()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (buffers, id) = Open(s, "session");

        var status = await SwitchAsync(s, buffers, id, "phone");

        status.Should().Be(SwitchStatus.Invalid);
        buffers.Peek(id, s.Provider)!.Sources.Should().Equal(RecordingSources.InPerson);
        await using var db = s.CreateDbContext();
        (await db.AuditEntries.CountAsync(a => a.Action == "recording_source_switched", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task SwitchSource_Dictation_IsRefused()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (buffers, id) = Open(s, "dictation");

        var status = await SwitchAsync(s, buffers, id, RecordingSources.OnlineSpeaker);

        status.Should().Be(SwitchStatus.Invalid);
        buffers.Peek(id, s.Provider)!.Sources.Should().Equal(RecordingSources.InPerson);
        await using var db = s.CreateDbContext();
        (await db.AuditEntries.CountAsync(a => a.Action == "recording_source_switched", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task SwitchSource_UnknownOrFinishedRecording_IsExpired()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (buffers, id) = Open(s, "session");
        buffers.Take(id, s.Provider)!.Audio.Destroy();

        var gone = await SwitchAsync(s, buffers, id, RecordingSources.OnlineSpeaker);
        var never = await SwitchAsync(s, buffers, Guid.NewGuid(), RecordingSources.OnlineSpeaker);

        gone.Should().Be(SwitchStatus.Expired);
        never.Should().Be(SwitchStatus.Expired);
    }

    [Fact]
    public async Task SwitchSource_OtherProvidersRecording_IsForbidden_AndChangesNothing()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (buffers, id) = Open(s, "session");

        var status = await SwitchAsync(s, buffers, id, RecordingSources.OnlineSpeaker, provider: s.OtherProvider);

        status.Should().Be(SwitchStatus.Forbidden);
        buffers.Peek(id, s.Provider)!.Sources.Should().Equal(RecordingSources.InPerson);
        await using var db = s.CreateDbContext();
        (await db.AuditEntries.CountAsync(a => a.Action == "recording_source_switched", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task SwitchSource_KeepsTheSameBufferAndSequence()
    {
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (buffers, id) = Open(s, "session", RecordingSources.OnlineCapture);
        buffers.Append(id, s.Provider, 0, new byte[3200]).Status.Should().Be(ChunkStatus.Accepted);

        await SwitchAsync(s, buffers, id, RecordingSources.OnlineSpeaker);

        buffers.Append(id, s.Provider, 0, new byte[3200]).Status.Should().Be(ChunkStatus.Duplicate);
        buffers.Append(id, s.Provider, 1, new byte[3200]).Status.Should().Be(ChunkStatus.Accepted);
        buffers.Take(id, s.Provider)!.Audio.Length.Should().Be(6400);
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
        var first = await LiveRecordings.CheckBeginAsync(db, s.Provider, s.Adult, null, "DAP", "pt", "session", false, "in_person", Later, Ct);
        (await s.RevokeByLinkAsync(s.RevokeToken(), AdultBirth, Ct)).Outcome.Should().Be(PublicConsentOutcome.Ok);

        var again = await LiveRecordings.CheckBeginAsync(db, s.Provider, s.Adult, null, "DAP", "pt", "session", false, "in_person", Later, Ct);

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
