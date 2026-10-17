using Anamnys.Server.LiveSession;
using Anamnys.Server.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteCreationTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAsync_AiMode_CreatesPendingTextNoteEnqueuesAndAudits()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var queue = new TranscriptionQueue();

        // Act
        var result = await NoteCreation.CreateAsync(db, queue, seed.ProviderId,
            new CreateNoteRequest(patient, null, "SOAP", "ai", "  Relata insônia.  "), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.Created);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        note.InputMode.Should().Be("Text");
        note.Status.Should().Be("ReadyForReview");
        note.Format.Should().Be("SOAP");
        note.RawTranscript.Should().Be("Relata insônia.");
        note.StructureStatus.Should().Be("pending");
        note.SessionId.Should().BeNull();
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job.Should().BeEquivalentTo(new StructureJob(result.NoteId!.Value, seed.ProviderId, "SOAP"));
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.NoteId == result.NoteId, Ct);
        audit.Action.Should().Be("note_created");
        audit.NewValue.Should().Be("Text,SOAP");
    }

    [Fact]
    public async Task CreateAsync_ManualMode_CreatesBlankSectionsWithoutJob()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var queue = new TranscriptionQueue();

        // Act
        var result = await NoteCreation.CreateAsync(db, queue, seed.ProviderId,
            new CreateNoteRequest(patient, null, "DAP", "manual", "   "), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.Created);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        note.RawTranscript.Should().BeNull();
        note.StructureStatus.Should().Be("done");
        var keys = await db.NoteSections.AsNoTracking().Where(s => s.NoteId == result.NoteId).OrderBy(s => s.Position).Select(s => s.Key).ToListAsync(Ct);
        keys.Should().Equal("data", "assessment", "plan");
        queue.Reader.TryRead(out _).Should().BeFalse();
        var actions = await db.AuditEntries.AsNoTracking().Where(a => a.NoteId == result.NoteId).Select(a => a.Action).ToListAsync(Ct);
        actions.Should().BeEquivalentTo(["note_created", "sections_created"]);
    }

    [Fact]
    public async Task CreateAsync_WithStartedAppointment_LinksSessionAndMarksAttended()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-3), "confirmed", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId, Ct);
        appointment.SessionId.Should().Be(note.SessionId);
        appointment.Status.Should().Be("attended");
        (await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == note.SessionId, Ct)).Attendance.Should().Be("attended");
    }

    [Fact]
    public async Task CreateAsync_OtherProvidersPatient_NotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, null, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.NotFound);
        (await db.Notes.AsNoTracking().CountAsync(n => n.PatientId == patient, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_OtherPatientsAppointment_NotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var other = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(other, Now.AddHours(-3), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.NotFound);
    }

    [Fact]
    public async Task CreateAsync_ArchivedPatient_Archived()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", archivedAt: Now.AddDays(-1), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, null, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.Archived);
    }

    [Fact]
    public async Task CreateAsync_CancelledAppointment_AppointmentUnavailable()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-3), "cancelled", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.AppointmentUnavailable);
    }

    [Fact]
    public async Task CreateAsync_FutureAppointment_AppointmentUnavailable()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(3), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.AppointmentUnavailable);
        (await db.Notes.AsNoTracking().CountAsync(n => n.PatientId == patient, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_AppointmentOlderThanWindow_AppointmentUnavailable()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddDays(-15), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.AppointmentUnavailable);
        (await db.Notes.AsNoTracking().CountAsync(n => n.PatientId == patient, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_NoShowAppointment_AppointmentUnavailable()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-3), "no_show", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.AppointmentUnavailable);
        (await db.Notes.AsNoTracking().CountAsync(n => n.PatientId == patient, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_OtherProvidersAppointment_NotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-3), providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.NotFound);
    }

    [Fact]
    public async Task CreateAsync_TwoAtOnceForOneAppointment_OneNoteOneUnavailable()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-3), cancellationToken: Ct);
        await using var first = seed.CreateDbContext();
        await using var second = seed.CreateDbContext();
        var request = new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null);

        // Act
        var results = await Task.WhenAll(
            NoteCreation.CreateAsync(first, new TranscriptionQueue(), seed.ProviderId, request, Now, Ct),
            NoteCreation.CreateAsync(second, new TranscriptionQueue(), seed.ProviderId, request, Now, Ct));

        // Assert
        results.Select(r => r.Outcome).Should().BeEquivalentTo([CreateNoteOutcome.Created, CreateNoteOutcome.AppointmentUnavailable]);
        await using var check = seed.CreateDbContext();
        (await check.Notes.AsNoTracking().CountAsync(n => n.PatientId == patient, Ct)).Should().Be(1);
    }

    [Theory]
    [InlineData(null, "DAP", "ai", "x", "patientId")]
    [InlineData("p", "PtFunctional", "ai", "x", "format")]
    [InlineData("p", "DAP", "auto", "x", "mode")]
    [InlineData("p", "DAP", "ai", "   ", "text")]
    [InlineData("p", "DAP", "ai", null, "text")]
    public void Validate_InvalidField_ReportsIt(string? patient, string format, string mode, string? text, string field)
    {
        // Arrange
        var request = new CreateNoteRequest(patient is null ? null : Guid.NewGuid(), null, format, mode, text);

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey(field);
    }

    [Fact]
    public void Validate_TextOverLimit_ReportsText()
    {
        // Arrange
        var request = new CreateNoteRequest(Guid.NewGuid(), null, "DAP", "manual", new string('a', CreateNoteRequest.MaxText + 1));

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey("text");
    }

    [Fact]
    public async Task CandidatesAsync_Window_ReturnsRecentUnnotedNotCancelledNotNoShowNewestFirst()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var recent = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), cancellationToken: Ct);
        var older = await seed.AddAppointmentAsync(patient, Now.AddDays(-13), "attended", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(-15), cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(-1), "cancelled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(-2), "no_show", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddHours(3), cancellationToken: Ct);
        var noted = await seed.AddAppointmentAsync(patient, Now.AddDays(-3), cancellationToken: Ct);
        await using (var setup = seed.CreateDbContext())
        {
            await NoteCreation.CreateAsync(setup, new TranscriptionQueue(), seed.ProviderId,
                new CreateNoteRequest(patient, noted, "DAP", "manual", null), Now, Ct);
        }
        await using var db = seed.CreateDbContext();

        // Act
        var candidates = await NoteCreation.CandidatesAsync(db, seed.ProviderId, patient, Now, Ct);

        // Assert
        candidates!.Select(c => c.AppointmentId).Should().Equal(recent, older);
    }

    [Fact]
    public async Task CandidatesAsync_OtherProvidersPatient_ReturnsNull()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var candidates = await NoteCreation.CandidatesAsync(db, seed.ProviderId, patient, Now, Ct);

        // Assert
        candidates.Should().BeNull();
    }
}
