using System.Data.Common;
using Anamnys.Server.LiveSession;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.LiveSession;

[Collection(SharedAppHostCollection.Name)]
public class LiveRecordingsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckBegin_OwnPatient_IsOk()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        (await LiveRecordings.CheckBeginAsync(db, seed.ProviderId, patient, null, "DAP", "pt", Ct)).Should().Be(BeginCheck.Ok);
    }

    [Fact]
    public async Task CheckBegin_OtherProvidersPatient_IsNotFound()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        (await LiveRecordings.CheckBeginAsync(db, seed.ProviderId, patient, null, "DAP", "pt", Ct)).Should().Be(BeginCheck.NotFound);
    }

    [Fact]
    public async Task CheckBegin_AppointmentOfAnotherPatient_IsNotFound()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var ana = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var bia = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        await seed.AddAppointmentAsync(bia, Now, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var appointment = await db.Appointments.Where(a => a.PatientId == bia).Select(a => a.Id).SingleAsync(Ct);

        (await LiveRecordings.CheckBeginAsync(db, seed.ProviderId, ana, appointment, "DAP", "pt", Ct)).Should().Be(BeginCheck.NotFound);
    }

    [Fact]
    public async Task CheckBegin_UnknownFormat_IsInvalid()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        (await LiveRecordings.CheckBeginAsync(db, seed.ProviderId, patient, null, "XYZ", "pt", Ct)).Should().Be(BeginCheck.Invalid);
    }

    [Fact]
    public async Task Finalize_WithoutAppointment_CreatesAttendedSessionAndProcessingNote()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, null);

        var result = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);

        result.Status.Should().Be(FinalizeStatus.Ok);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        note.Status.Should().Be("Processing");
        note.InputMode.Should().Be("VoiceLive");
        note.Format.Should().Be("SOAP");
        note.RecordingId.Should().Be(recordingId);
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == note.SessionId, Ct);
        session.Attendance.Should().Be("attended");
        session.ScheduledStart.Should().BeNull();
        session.ActualStart.Should().Be(Now.AddMinutes(-30));
        session.ActualEnd.Should().Be(Now);
        queue.PositionOf(note.Id).Should().Be(1);
        buffers.Peek(recordingId, seed.ProviderId).Should().BeNull();
    }

    [Fact]
    public async Task Finalize_WithAppointment_CreatesSessionFromItAndLinksIt()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddMinutes(-30), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.PatientId == patient, Ct);
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, appointment.Id);

        var result = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);

        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == note.SessionId, Ct);
        session.ScheduledStart.Should().Be(appointment.StartsAt);
        session.ScheduledMinutes.Should().Be(50);
        session.Modality.Should().Be(appointment.Modality);
        (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointment.Id, Ct)).SessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task Finalize_AppointmentWithSession_ReusesIt()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddMinutes(-30), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.PatientId == patient, Ct);
        var first = Recording(seed.ProviderId, patient, appointment.Id);
        var firstNote = (await LiveRecordings.FinalizeAsync(db, first.Buffers, first.Queue, seed.ProviderId, first.RecordingId, Now, Ct)).NoteId;
        var second = Recording(seed.ProviderId, patient, appointment.Id);

        var secondNote = (await LiveRecordings.FinalizeAsync(db, second.Buffers, second.Queue, seed.ProviderId, second.RecordingId, Now.AddMinutes(5), Ct)).NoteId;

        var sessions = await db.Notes.AsNoTracking().Where(n => n.Id == firstNote || n.Id == secondNote).Select(n => n.SessionId).Distinct().ToListAsync(Ct);
        sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task Finalize_Twice_ReturnsTheSameNote()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, null);

        var first = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);
        var second = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);

        second.Should().Be(first);
        (await db.Notes.CountAsync(n => n.RecordingId == recordingId, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Finalize_WithoutBufferOrNote_IsExpired()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var buffers = new RecordingBuffers(new FakeTimeProvider(Now));

        var result = await LiveRecordings.FinalizeAsync(db, buffers, new TranscriptionQueue(), seed.ProviderId, Guid.NewGuid(), Now, Ct);

        result.Should().Be(new FinalizeResult(FinalizeStatus.Expired, null));
    }

    [Fact]
    public async Task FailOrphans_ProcessingLiveNote_BecomesFailedServerRestart()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, null);
        var noteId = (await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct)).NoteId;

        await LiveRecordings.FailOrphansAsync(db, Now.AddMinutes(1), Ct);

        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.Status.Should().Be("Failed");
        note.FailureReason.Should().Be("server_restart");
    }

    [Fact]
    public async Task Finalize_DatabaseFailure_PropagatesAndLeavesNoBufferNoteOrJob()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, Guid.NewGuid());

        // Act
        var act = () => LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        buffers.Peek(recordingId, seed.ProviderId).Should().BeNull();
        (await db.Notes.AsNoTracking().CountAsync(n => n.RecordingId == recordingId, Ct)).Should().Be(0);
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Finalize_CallerCancelsAfterBufferIsTaken_StillCommitsTheNoteAndEnqueuesTheJob()
    {
        // Arrange: the hub's ConnectionAborted fires once the transaction has begun, i.e. after
        // the buffer left RecordingBuffers. From then on the audio exists only in this call.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        using var aborted = new CancellationTokenSource();
        await using var db = seed.CreateDbContext(new CancelOnTransactionStart(aborted));
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, null);

        // Act
        var result = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, aborted.Token);

        // Assert
        aborted.IsCancellationRequested.Should().BeTrue("the token was cancelled mid-finalize");
        result.Status.Should().Be(FinalizeStatus.Ok);
        await using var check = seed.CreateDbContext();
        (await check.Notes.AsNoTracking().SingleAsync(n => n.RecordingId == recordingId, Ct)).Status.Should().Be("Processing");
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.NoteId.Should().Be(result.NoteId!.Value);
    }

    [Fact]
    public async Task Finalize_CallerCancelledBeforeTheCall_ThrowsAndLeavesTheBufferForARetry()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, null);

        // Act
        var act = () => LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, new CancellationToken(true));

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        buffers.Peek(recordingId, seed.ProviderId).Should().NotBeNull();
        (await db.Notes.AsNoTracking().CountAsync(n => n.RecordingId == recordingId, Ct)).Should().Be(0);
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    private sealed class CancelOnTransactionStart(CancellationTokenSource source) : DbTransactionInterceptor
    {
        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private static (RecordingBuffers Buffers, TranscriptionQueue Queue, Guid RecordingId) Recording(Guid provider, Guid patient, Guid? appointment)
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Now));
        var id = Guid.NewGuid();
        buffers.Begin(id, new RecordingMeta(provider, patient, appointment, "SOAP", "pt", Now.AddMinutes(-30)));
        buffers.Append(id, provider, 0, [1, 0, 2, 0]);
        return (buffers, new TranscriptionQueue(), id);
    }
}
