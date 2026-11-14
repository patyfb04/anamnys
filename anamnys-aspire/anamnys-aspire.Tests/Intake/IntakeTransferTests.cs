using Anamnys.Server.Intakes;
using Anamnys.Server.Medications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static Anamnys.Tests.Intakes.IntakeScene;

namespace Anamnys.Tests.Intakes;

// design/specs/2026-11-12-structured-intake-design.md §3 Transfers; §6 "Transfers".
[Collection(SharedAppHostCollection.Name)]
public class IntakeTransferTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_BeforeSigning_IsEmpty_AndAfterSigning_ShowsCurrentRegistrationValues()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "Phone" = '11 1111-1111', "DateOfBirth" = '1991-01-01' WHERE "Id" = @pa""", Ct, ("pa", s.Patient));
        var intake = await s.StartAsync(Ct);
        await s.AnswerRequiredAndDestinationsAsync(intake, Ct);
        var noteId = await s.GenerateNoteAsync(intake, Ct);

        // Act
        IReadOnlyList<IntakeTransferView>? before;
        await using (var db = s.CreateDbContext())
        {
            before = await IntakeTransfers.ListAsync(db, s.Provider, intake, Ct);
        }
        await s.SignAsync(noteId, Ct);
        await using var check = s.CreateDbContext();
        var after = (await IntakeTransfers.ListAsync(check, s.Provider, intake, Ct))!;
        var foreign = await IntakeTransfers.ListAsync(check, s.OtherProvider, intake, Ct);

        // Assert
        before.Should().BeEmpty();
        foreign.Should().BeNull();
        after.Select(t => t.TargetField).Should().Equal(
            "patient.dateOfBirth", "patient.phone", "patient.contactEmail", "diagnosis", "medication", "plan.objective");
        after.Single(t => t.TargetField == "patient.dateOfBirth").Should().Match<IntakeTransferView>(t =>
            t.Value == "1990-05-04" && t.CurrentValue == "1991-01-01" && t.QuestionLabel == "Data de nascimento" && t.SectionKey == "identificacao");
        after.Single(t => t.TargetField == "patient.phone").CurrentValue.Should().Be("11 1111-1111");
        after.Single(t => t.TargetField == "patient.contactEmail").CurrentValue.Should().BeNull();
        after.Single(t => t.TargetField == "medication").CurrentValue.Should().BeNull();
        after.Should().OnlyContain(t => t.Status == "pending");
    }

    [Fact]
    public async Task Confirm_EachDestination_WritesTheRecordWithAuditAndExcerpt()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var (intake, noteId) = await s.SignedIntakeAsync(Ct);
        var transfers = await s.TransfersAsync(intake, Ct);

        // Act
        var results = new List<IntakeResult<IntakeTransferView>>();
        foreach (var transfer in transfers)
        {
            await using var db = s.CreateDbContext();
            results.Add(await IntakeTransfers.ConfirmAsync(db, s.Provider, intake, transfer.Id, Now, Ct));
        }

        // Assert
        results.Should().OnlyContain(r => r.Outcome == IntakeOutcome.Ok && r.Value!.Status == "confirmed");
        await using var check = s.CreateDbContext();
        var patient = await check.Patients.AsNoTracking().SingleAsync(p => p.Id == s.Patient, Ct);
        (patient.DateOfBirth, patient.Phone, patient.ContactEmail).Should().Be((new DateOnly(1990, 5, 4), "11 98888-7777", "benedita@example.test"));
        var medication = await check.MedicationEntries.AsNoTracking().SingleAsync(m => m.PatientId == s.Patient, Ct);
        (medication.Drug, medication.StartedOn, medication.TreatmentId).Should().Be(("Sertralina 50 mg", new DateOnly(2026, 11, 12), medication.Id));
        medication.SourceNoteId.Should().Be(noteId);
        var timeline = await MedicationTimeline.GetAsync(check, s.Provider, s.Patient, null, null, Ct, Now);
        var origin = timeline!.Medications.Single().Segments.Single().Origin;
        (origin.Kind, origin.NoteId).Should().Be(("intake", noteId));
        var diagnosis = await check.PatientDiagnoses.AsNoTracking().SingleAsync(d => d.PatientId == s.Patient, Ct);
        diagnosis.Description.Should().Be("Transtorno de ansiedade generalizada");
        var objective = await (
            from o in check.PlanObjectives.AsNoTracking()
            join p in check.TreatmentPlans.AsNoTracking() on o.PlanId equals p.Id
            where p.PatientId == s.Patient
            select o).SingleAsync(Ct);
        objective.Description.Should().Be("Falar em público sem crises");

        var decided = await s.TransfersAsync(intake, Ct);
        decided.Should().OnlyContain(t => t.Status == "confirmed" && t.DecidedAt == Now && t.ResultId != null && t.ExcerptRefId != null);
        decided.Single(t => t.TargetField == "medication").ResultId.Should().Be(medication.Id);
        decided.Single(t => t.TargetField == "diagnosis").ResultId.Should().Be(diagnosis.Id);
        decided.Single(t => t.TargetField == "plan.objective").ResultId.Should().Be(objective.Id);
        decided.Single(t => t.TargetField == "patient.phone").ResultId.Should().Be(s.Patient);

        var refs = await check.ExcerptRefs.AsNoTracking().Where(r => r.SourceId == noteId).ToListAsync(Ct);
        refs.Should().HaveCount(6).And.OnlyContain(r => r.SourceType == "note" && r.Kind == "exact");
        var medicationRef = refs.Single(r => r.Id == decided.Single(t => t.TargetField == "medication").ExcerptRefId);
        origin.ExcerptId.Should().Be(medicationRef.Id);
        (medicationRef.SectionKey, medicationRef.Quote).Should().Be(("saude", "Sertralina 50 mg"));
        refs.Single(r => r.Id == decided.Single(t => t.TargetField == "patient.dateOfBirth").ExcerptRefId).Quote.Should().Be("04/05/1990");

        var audit = (await s.AuditAsync(Ct)).Where(a => a.StartsWith("intake_transfer_confirmed")).ToList();
        audit.Should().HaveCount(6);
        audit.Should().Contain($"intake_transfer_confirmed {decided.Single(t => t.TargetField == "medication").Id} intake {intake} medication {medication.Id}");
        audit.Should().NotContain(a => a.Contains("Sertralina") || a.Contains("example.test") || a.Contains("1990"));
    }

    [Fact]
    public async Task Ignore_WritesNothing_AndDecidedOrForeignTransfersAreRefused()
    {
        // Arrange
        await using var s = await IntakeScene.CreateAsync(fixture, Ct);
        var (intake, _) = await s.SignedIntakeAsync(Ct);
        var transfers = await s.TransfersAsync(intake, Ct);
        var medication = transfers.Single(t => t.TargetField == "medication");
        var email = transfers.Single(t => t.TargetField == "patient.contactEmail");
        await using var db = s.CreateDbContext();

        // Act
        var ignored = await IntakeTransfers.IgnoreAsync(db, s.Provider, intake, medication.Id, Now, Ct);
        var confirmIgnored = await IntakeTransfers.ConfirmAsync(db, s.Provider, intake, medication.Id, Now, Ct);
        var ignoreAgain = await IntakeTransfers.IgnoreAsync(db, s.Provider, intake, medication.Id, Now, Ct);
        var foreign = await IntakeTransfers.ConfirmAsync(db, s.OtherProvider, intake, email.Id, Now, Ct);
        var wrongIntake = await IntakeTransfers.ConfirmAsync(db, s.Provider, Guid.NewGuid(), email.Id, Now, Ct);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @pa""", Ct, ("pa", s.Patient));
        var archived = await IntakeTransfers.ConfirmAsync(db, s.Provider, intake, email.Id, Now, Ct);

        // Assert
        ignored.Outcome.Should().Be(IntakeOutcome.Ok);
        ignored.Value!.Status.Should().Be("ignored");
        confirmIgnored.Outcome.Should().Be(IntakeOutcome.NotPending);
        ignoreAgain.Outcome.Should().Be(IntakeOutcome.NotPending);
        foreign.Outcome.Should().Be(IntakeOutcome.NotFound);
        wrongIntake.Outcome.Should().Be(IntakeOutcome.NotFound);
        archived.Outcome.Should().Be(IntakeOutcome.PatientArchived);
        await using var check = s.CreateDbContext();
        (await check.MedicationEntries.AnyAsync(m => m.PatientId == s.Patient, Ct)).Should().BeFalse();
        (await check.Patients.AsNoTracking().SingleAsync(p => p.Id == s.Patient, Ct)).ContactEmail.Should().BeNull();
        var stored = await s.TransfersAsync(intake, Ct);
        stored.Single(t => t.Id == medication.Id).Should().Match<Anamnys.Server.Data.Entities.IntakeTransfer>(t =>
            t.Status == "ignored" && t.ResultId == null && t.ExcerptRefId == null && t.DecidedAt == Now);
        (await s.AuditAsync(Ct)).Should().Contain($"intake_transfer_ignored {medication.Id} intake {intake} medication");
    }
}
