using Anamnys.Server.Integrity;
using Anamnys.Server.Notifications;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Integrity;

// The nightly run (design/specs/2026-11-03-integrity-chain-design.md §7, §10 "Job").
[Collection(SharedAppHostCollection.Name)]
public class IntegrityJobTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IntegrityJobTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Fact]
    public async Task RunAsync_IntactChain_AnchorsAndRaisesNothing()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await SignedNoteAsync(s, provider, patient, Now, Ct);
        using var fake = new FakeTimestampAuthority();
        await using var db = s.CreateDbContext();

        // Act
        var result = await IntegrityJobSteps.RunAsync(db, fake, true, NullLogger.Instance, Now, Ct, provider);

        // Assert
        result.Should().Be(new IntegrityRunResult(1, 0, 0, 0));
        (await s.CountAsync("""SELECT count(*) FROM "Notifications" WHERE "ProviderId" = @p""", Ct, ("p", provider))).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_Problem_RaisesOneNoticeAndOneAudit_NotRepeatedTheNextNight()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var note = await SignedNoteAsync(s, provider, patient, Now, Ct);
        await s.ExecuteAsync("""UPDATE "NoteSections" SET "Content" = 'alterado' WHERE "NoteId" = @n AND "Key" = 'plan'""", Ct, ("n", note));
        using var fake = new FakeTimestampAuthority();
        await using var db = s.CreateDbContext();

        // Act
        var first = await IntegrityJobSteps.RunAsync(db, fake, false, NullLogger.Instance, Now, Ct, provider);
        var second = await IntegrityJobSteps.RunAsync(db, fake, false, NullLogger.Instance, Now.AddDays(1), Ct, provider);

        // Assert
        first.Should().Be(new IntegrityRunResult(0, 0, 1, 1));
        second.Should().Be(new IntegrityRunResult(0, 0, 1, 0));
        (await s.CountAsync(
            """
            SELECT count(*) FROM "Notifications"
            WHERE "ProviderId" = @p AND "Kind" = 'integrity_problem' AND "Channel" = 'in_app' AND "SubjectType" = 'integrity'
              AND "SubjectId" IS NULL AND "DedupeKey" = 'integrity:1:content_changed'
            """, Ct, ("p", provider))).Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "Notifications" WHERE "ProviderId" = @p""", Ct, ("p", provider))).Should().Be(1);
        (await s.CountAsync(
            """
            SELECT count(*) FROM "AuditEntries"
            WHERE "ActorId" = @p AND "Action" = 'integrity_check_failed' AND "NoteId" IS NULL AND "DocumentId" IS NULL
              AND "NewValue" = '1 content_changed' AND "PreviousValue" IS NULL
            """, Ct, ("p", provider))).Should().Be(1);
        fake.Requests.Should().Be(0, "stamping was off");

        var inbox = await ProviderNotifications.ListAsync(db, provider, 1, Ct);
        inbox.Items.Should().ContainSingle(i => i.Kind == IntegrityJobSteps.ProblemKind && i.PatientName == null);
        inbox.UnreadCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ANewProblem_IsANewNotice()
    {
        // Arrange: a content change at 2, then a removed seal at 1 the next night.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var first = await SignedNoteAsync(s, provider, patient, Now, Ct);
        var second = await SignedNoteAsync(s, provider, patient, Now.AddMinutes(1), Ct);
        await s.ExecuteAsync("""UPDATE "NoteSections" SET "Content" = 'alterado' WHERE "NoteId" = @n AND "Key" = 'data'""", Ct, ("n", second));
        using var fake = new FakeTimestampAuthority();
        await using var db = s.CreateDbContext();
        await IntegrityJobSteps.RunAsync(db, fake, false, NullLogger.Instance, Now, Ct, provider);

        // Act
        await TamperSealsAsync(s, """DELETE FROM "IntegritySeals" WHERE "SubjectId" = @n""", Ct, ("n", first));
        var next = await IntegrityJobSteps.RunAsync(db, fake, false, NullLogger.Instance, Now.AddDays(1), Ct, provider);

        // Assert
        next.Notified.Should().Be(1);
        (await s.CountAsync(
            """SELECT count(*) FROM "Notifications" WHERE "ProviderId" = @p AND "DedupeKey" IN ('integrity:2:content_changed', 'integrity:1:sequence_gap')""",
            Ct, ("p", provider))).Should().Be(2);
    }
}
