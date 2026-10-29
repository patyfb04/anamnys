using Anamnys.Server.Appointments;
using Anamnys.Server.Documents;
using Anamnys.Server.Notes;
using Anamnys.Server.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Retention;

// The retention clock (design/specs/2026-10-28-retention-panel-design.md §3, §6 "Clock").
[Collection(SharedAppHostCollection.Name)]
public class RetentionClockTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Utc(int y, int m, int d, int h = 15) => new(y, m, d, h, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecomputeAsync_TakesTheLatestOfSignedNoteSignedDocumentAndAttendedSession()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await s.AddNoteAsync(provider, patient, Utc(2020, 3, 1), Ct, signedAt: Utc(2020, 3, 1));
        await s.AddSignedDocumentAsync(provider, patient, Utc(2021, 5, 10), Ct);
        await s.AddSessionAsync(provider, patient, Utc(2022, 7, 15), Ct);
        await s.AddNoteAsync(provider, patient, Utc(2023, 1, 1), Ct);
        await s.AddSessionAsync(provider, patient, Utc(2024, 1, 1), Ct, attendance: "scheduled");
        await using var db = s.CreateDbContext();

        // Act
        await RetentionClock.RecomputeAsync(db, Ct, providerId: provider);

        // Assert
        (await s.RetentionOfAsync(patient, Ct)).Should().Be((new DateOnly(2022, 7, 15), new DateOnly(2027, 7, 15)));
    }

    [Fact]
    public async Task RecomputeAsync_UsesThePracticeLocalDay_AndTheSessionStartWhenKnown()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var signed = await s.AddPatientAsync(provider, "Ana", Ct);
        var session = await s.AddPatientAsync(provider, "Bia", Ct);
        // 01:30 UTC on 2 Mar is still 1 Mar at the practice.
        await s.AddNoteAsync(provider, signed, Utc(2020, 3, 1), Ct, signedAt: Utc(2020, 3, 2, 1).AddMinutes(30));
        await s.AddSessionAsync(provider, session, Utc(2021, 6, 20), Ct, actualStart: Utc(2021, 6, 10));
        await using var db = s.CreateDbContext();

        // Act
        await RetentionClock.RecomputeAsync(db, Ct, providerId: provider);

        // Assert
        (await s.RetentionOfAsync(signed, Ct)).LastRecordAt.Should().Be(new DateOnly(2020, 3, 1));
        (await s.RetentionOfAsync(session, Ct)).LastRecordAt.Should().Be(new DateOnly(2021, 6, 10));
    }

    [Fact]
    public async Task RecomputeAsync_WithoutRecords_LeavesNoDeadline_AndNeverTouchesADiscardedOrForeignPatient()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var other = await s.AddProviderAsync(Ct);
        var empty = await s.AddPatientAsync(provider, "Ana", Ct);
        var discarded = await s.AddPatientAsync(provider, "Bia", Ct);
        var foreign = await s.AddPatientAsync(other, "Cid", Ct);
        await s.AddSessionAsync(provider, discarded, Utc(2019, 1, 1), Ct);
        await s.MarkDiscardedAsync(discarded, Utc(2026, 1, 1), Ct);
        await s.AddSessionAsync(other, foreign, Utc(2019, 1, 1), Ct);
        await s.SetRetentionAsync(empty, new DateOnly(2018, 1, 1), new DateOnly(2023, 1, 1), Ct);
        await using var db = s.CreateDbContext();

        // Act
        await RetentionClock.RecomputeAsync(db, Ct, providerId: provider);

        // Assert
        (await s.RetentionOfAsync(empty, Ct)).Should().Be(((DateOnly?)null, (DateOnly?)null));
        (await s.RetentionOfAsync(discarded, Ct)).Should().Be(((DateOnly?)null, (DateOnly?)null));
        (await s.RetentionOfAsync(foreign, Ct)).Should().Be(((DateOnly?)null, (DateOnly?)null));
    }

    [Fact]
    public async Task PushAsync_ANewRecord_MovesTheDeadlineForward_AndNeverBack()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await s.SetRetentionAsync(patient, new DateOnly(2024, 2, 29), new DateOnly(2029, 2, 28), Ct);
        await using var db = s.CreateDbContext();

        // Act
        await RetentionClock.PushAsync(db, provider, patient, Utc(2023, 1, 1), Ct);
        var older = await s.RetentionOfAsync(patient, Ct);
        await RetentionClock.PushAsync(db, provider, patient, Now, Ct);

        // Assert
        older.Should().Be((new DateOnly(2024, 2, 29), new DateOnly(2029, 2, 28)));
        (await s.RetentionOfAsync(patient, Ct)).Should().Be((new DateOnly(2026, 10, 27), new DateOnly(2031, 10, 27)));
    }

    [Fact]
    public async Task SaveAsync_RaisingTheGuardYears_RecomputesTheProvidersDeadlines()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var other = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var foreign = await s.AddPatientAsync(other, "Bia", Ct);
        await s.AddSessionAsync(provider, patient, Utc(2022, 7, 15), Ct);
        await s.AddSessionAsync(other, foreign, Utc(2022, 7, 15), Ct);
        await using var db = s.CreateDbContext();
        await RetentionClock.RecomputeAsync(db, Ct);

        // Act
        var before = await RetentionSettings.GetAsync(db, provider, Ct);
        await RetentionSettings.SaveAsync(db, provider, new RetentionSettingsRequest(8, 3), Ct);
        var after = await RetentionSettings.GetAsync(db, provider, Ct);

        // Assert
        before.Should().Be(new RetentionSettingsDto(5, 7));
        after.Should().Be(new RetentionSettingsDto(8, 3));
        (await s.RetentionOfAsync(patient, Ct)).RetentionUntil.Should().Be(new DateOnly(2030, 7, 15));
        (await s.RetentionOfAsync(foreign, Ct)).RetentionUntil.Should().Be(new DateOnly(2027, 7, 15));
    }

    [Theory]
    [InlineData(4, 7, "guardYears")]
    [InlineData(null, 7, "guardYears")]
    [InlineData(5, 0, "unsignedNoteDays")]
    [InlineData(5, null, "unsignedNoteDays")]
    public void Validate_BelowTheMinimum_IsRefused(int? guardYears, int? unsignedNoteDays, string field)
    {
        // Act
        var errors = new RetentionSettingsRequest(guardYears, unsignedNoteDays).Validate();

        // Assert
        errors.Keys.Should().Equal(field);
    }

    [Fact]
    public async Task SignAsync_ANote_PushesThePatientsDeadline()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var note = await s.AddNoteAsync(provider, patient, Utc(2026, 10, 20), Ct);
        await s.AddSectionAsync(note, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var outcome = await NoteEditing.SignAsync(db, provider, note, Now, Ct);

        // Assert
        outcome.Should().Be(SignOutcome.Signed);
        (await s.RetentionOfAsync(patient, Ct)).Should().Be((new DateOnly(2026, 10, 27), new DateOnly(2031, 10, 27)));
    }

    [Fact]
    public async Task SignAsync_ADocument_PushesThePatientsDeadline()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true, checkMode: "advisory");
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await using var db = s.CreateDbContext();
        var created = await ClinicalDocumentService.CreateAsync(db, provider, patient, "declaracao", null, Utc(2026, 10, 20), Ct);
        await s.ExecuteAsync(
            """UPDATE "ClinicalDocuments" d SET "Confirmations" = r."Prohibitions" FROM "ConformityRulesets" r WHERE r."Id" = d."RulesetId" AND d."Id" = @d""",
            Ct, ("d", created.Id!.Value));

        // Act
        var signed = await ClinicalDocumentService.SignAsync(db, provider, created.Id!.Value, acknowledgeMissing: true, Now, Ct);

        // Assert
        signed.Outcome.Should().Be(DocumentOutcome.Ok);
        (await s.RetentionOfAsync(patient, Ct)).LastRecordAt.Should().Be(new DateOnly(2026, 10, 27));
    }

    [Fact]
    public async Task AttendAsync_ASession_PushesThePatientsDeadline()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await using var db = s.CreateDbContext();

        // Act
        await NoteSessions.AttendAsync(db, provider, patient, null, Utc(2026, 10, 26), null, Now, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        (await s.RetentionOfAsync(patient, Ct)).LastRecordAt.Should().Be(new DateOnly(2026, 10, 26));
    }

    [Fact]
    public async Task RecomputeAsync_CountsAppointmentsMarkedAttended_Only()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await s.AddAppointmentAsync(provider, patient, Utc(2023, 5, 10), "attended", Ct);
        await s.AddAppointmentAsync(provider, patient, Utc(2024, 5, 10), "no_show", Ct);
        await s.AddAppointmentAsync(provider, patient, Utc(2024, 6, 10), "cancelled", Ct);
        await s.AddAppointmentAsync(provider, patient, Now.AddDays(7), "scheduled", Ct);
        await using var db = s.CreateDbContext();

        // Act
        await RetentionClock.RecomputeAsync(db, Ct, providerId: provider);

        // Assert
        (await s.RetentionOfAsync(patient, Ct)).Should().Be((new DateOnly(2023, 5, 10), new DateOnly(2028, 5, 10)));
    }

    [Fact]
    public async Task SetStatusAsync_MarkingAttended_PushesTheDeadline_AndUndoingItRecomputes()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await s.AddNoteAsync(provider, patient, Utc(2020, 1, 1), Ct, signedAt: Utc(2020, 1, 1));
        var appointment = await s.AddAppointmentAsync(provider, patient, Utc(2026, 10, 26), "scheduled", Ct);
        await using var db = s.CreateDbContext();
        await RetentionClock.RecomputeAsync(db, Ct, providerId: provider);

        // Act
        var attended = await Server.Appointments.Appointments.SetStatusAsync(
            db, provider, appointment, new ChangeAppointmentStatusRequest("attended", null), Now, Ct);
        var afterAttended = await s.RetentionOfAsync(patient, Ct);
        db.ChangeTracker.Clear();
        var undone = await Server.Appointments.Appointments.SetStatusAsync(
            db, provider, appointment, new ChangeAppointmentStatusRequest("scheduled", null), Now, Ct);

        // Assert
        attended.Should().Be(AppointmentOutcome.Ok);
        undone.Should().Be(AppointmentOutcome.Ok);
        afterAttended.Should().Be((new DateOnly(2026, 10, 26), new DateOnly(2031, 10, 26)));
        (await s.RetentionOfAsync(patient, Ct)).Should().Be((new DateOnly(2020, 1, 1), new DateOnly(2025, 1, 1)));
    }

    [Fact]
    public async Task RecomputeAsync_OnALeapDay_EndsOnThe28thOfFebruary()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await s.AddSessionAsync(provider, patient, Utc(2024, 2, 29), Ct);
        await using var db = s.CreateDbContext();

        // Act
        await RetentionClock.RecomputeAsync(db, Ct, providerId: provider);

        // Assert
        (await s.RetentionOfAsync(patient, Ct)).Should().Be((new DateOnly(2024, 2, 29), new DateOnly(2029, 2, 28)));
    }

    [Fact]
    public async Task SignAsync_TheCorrectionOfADocument_PushesTheDeadlineAgain()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true, checkMode: "advisory");
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await using var db = s.CreateDbContext();
        var original = (await ClinicalDocumentService.CreateAsync(db, provider, patient, "declaracao", null, Utc(2026, 9, 1), Ct)).Id!.Value;
        await ConfirmAllAsync(s, original);
        await ClinicalDocumentService.SignAsync(db, provider, original, acknowledgeMissing: true, Utc(2026, 9, 2), Ct);
        var firstSignature = await s.RetentionOfAsync(patient, Ct);
        var copy = (await ClinicalDocumentService.CorrectAsync(db, provider, original, Utc(2026, 10, 1), Ct)).Id!.Value;
        await ConfirmAllAsync(s, copy);

        // Act
        var signed = await ClinicalDocumentService.SignAsync(db, provider, copy, acknowledgeMissing: true, Now, Ct);

        // Assert
        signed.Outcome.Should().Be(DocumentOutcome.Ok);
        firstSignature.LastRecordAt.Should().Be(new DateOnly(2026, 9, 2));
        (await s.RetentionOfAsync(patient, Ct)).Should().Be((new DateOnly(2026, 10, 27), new DateOnly(2031, 10, 27)));
    }

    [Fact]
    public async Task RecomputeAsync_WaitsForAPushInFlight_AndKeepsItsDeadline()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await s.AddSessionAsync(provider, patient, Utc(2021, 1, 10), Ct);
        await using var signing = s.CreateDbContext();
        await using var job = s.CreateDbContext();
        Task<int>? recompute = null;
        var waitedForThePush = false;

        // Act: a signature transaction records a session and pushes; the job recomputes while it is still open.
        await signing.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await signing.Database.BeginTransactionAsync(Ct);
            await signing.Database.ExecuteSqlAsync($"""
                INSERT INTO "Sessions" ("PatientId","ProviderId","Attendance","CreatedAt") VALUES ({patient}, {provider}, 'attended', {Now})
                """, Ct);
            await RetentionClock.PushAsync(signing, provider, patient, Now, Ct);
            recompute = RetentionClock.RecomputeAsync(job, Ct, providerId: provider);
            waitedForThePush = await Task.WhenAny(recompute, Task.Delay(TimeSpan.FromMilliseconds(750), Ct)) != recompute;
            await tx.CommitAsync(Ct);
        });
        await recompute!;

        // Assert
        waitedForThePush.Should().BeTrue();
        (await s.RetentionOfAsync(patient, Ct)).Should().Be((new DateOnly(2026, 10, 27), new DateOnly(2031, 10, 27)));
    }

    private static Task ConfirmAllAsync(RetentionScratch s, Guid documentId) =>
        s.ExecuteAsync(
            """UPDATE "ClinicalDocuments" d SET "Confirmations" = r."Prohibitions" FROM "ConformityRulesets" r WHERE r."Id" = d."RulesetId" AND d."Id" = @d""",
            Ct, ("d", documentId));
}
