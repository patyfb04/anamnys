using Anamnys.Server.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Patients;

// PatientRecords against the real Postgres of the shared AppHost. Each test seeds its own
// provider (PatientSearchSeed), so ownership is exercised on every call.
[Collection(SharedAppHostCollection.Name)]
public class PatientRecordsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreatePatientRequest FullRequest() => new(
        " Ana ", "Silva", " ana.silva@example.com ", new DateOnly(1990, 5, 12),
        [new DiagnosisInput("Transtorno depressivo maior", "F33.1", null)],
        [new MedicationInput("Sertralina", "100mg", "1x/dia", new DateOnly(2026, 9, 1), null)],
        ["Reduzir sintomas ansiosos", "Retomar rotina de sono"]);

    [Fact]
    public async Task Create_WritesPatientAndClinicalProfile()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);

        // Act
        Guid id;
        await using (var db = seed.CreateDbContext())
        {
            id = await PatientRecords.CreateAsync(db, seed.ProviderId, FullRequest(), Now, Ct);
        }
        var detail = await GetAsync(seed, id);

        // Assert
        detail.Should().NotBeNull();
        detail!.FirstName.Should().Be("Ana");
        detail.ContactEmail.Should().Be("ana.silva@example.com");
        detail.DateOfBirth.Should().Be(new DateOnly(1990, 5, 12));
        detail.Diagnoses.Should().ContainSingle().Which.IcdCode.Should().Be("F33.1");
        detail.Medications.Should().ContainSingle().Which.Drug.Should().Be("Sertralina");
        detail.Objectives.Select(o => o.Description).Should().BeEquivalentTo(["Reduzir sintomas ansiosos", "Retomar rotina de sono"]);
        detail.NoteStatus.Should().Be(NoteStatusGroup.None);
        detail.CanDelete.Should().BeTrue();
        detail.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task Create_WithoutObjectives_CreatesNoTreatmentPlan()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var id = await PatientRecords.CreateAsync(db, seed.ProviderId, FullRequest() with { TreatmentObjectives = null }, Now, Ct);

        // Assert
        (await db.TreatmentPlans.CountAsync(t => t.PatientId == id, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Get_ReturnsVisitsNotesAndContact()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", email: $"ana.{Guid.NewGuid():N}@mail.test",
            lastVisit: Now.AddDays(-3), cancellationToken: Ct);
        await seed.AddAppointmentAsync(id, Now.AddDays(2), cancellationToken: Ct);
        await seed.AddNoteAsync(id, "Signed", Now.AddDays(-3), cancellationToken: Ct);

        // Act
        var detail = await GetAsync(seed, id);

        // Assert
        detail!.ContactEmail.Should().StartWith("ana.");
        detail.PortalEmail.Should().BeNull();
        detail.LastVisit.Should().Be(Now.AddDays(-3));
        detail.NextAppointmentAt.Should().Be(Now.AddDays(2));
        detail.NoteStatus.Should().Be(NoteStatusGroup.Signed);
        detail.RecentNotes.Should().ContainSingle().Which.Status.Should().Be("Signed");
        detail.CanDelete.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_PortalStatusComesFromLinkedAccount(bool accountDisabled)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var accountId = await seed.BindPortalAccountAsync(id, Ct, disabled: accountDisabled);

        // Act
        var detail = await GetAsync(seed, id);

        // Assert — a disabled account is shown as no portal access.
        detail!.HasPortalAccount.Should().Be(!accountDisabled);
        detail.PortalEmail.Should().Be(accountDisabled ? null : $"{accountId:N}@portal.test");
        detail.CanDelete.Should().BeFalse("a record linked to an account must be archived, not deleted");
    }

    [Fact]
    public async Task EveryOperation_OnAnotherProvidersPatient_IsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var otherProvider = await seed.AddProviderAsync(Ct);
        var foreign = await seed.AddPatientAsync("Ana", "Silva", providerId: otherProvider, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var me = seed.ProviderId;
        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        // Act + Assert
        (await PatientRecords.GetAsync(db, me, foreign, Now, Ct)).Should().BeNull();
        (await PatientRecords.UpdateAsync(db, me, foreign, new UpdatePatientRequest("X", "Y", "x@example.com", new DateOnly(1990, 1, 1)), Now, Ct)).Should().BeFalse();
        (await PatientRecords.DeleteAsync(db, me, foreign, Ct)).Should().Be(RecordOutcome.NotFound);
        (await PatientRecords.SetArchivedAsync(db, me, foreign, true, Now, Ct)).Should().BeFalse();
        (await PatientRecords.AddDiagnosisAsync(db, me, foreign, new DiagnosisInput("x", null, null), Now, Ct)).Should().BeNull();
        (await PatientRecords.AddMedicationAsync(db, me, foreign, new MedicationInput("x", null, null, today, null), Ct)).Should().BeNull();
        (await PatientRecords.AddObjectiveAsync(db, me, foreign, new ObjectiveInput("x"), Now, Ct)).Outcome.Should().Be(RecordOutcome.NotFound);
    }

    [Fact]
    public async Task ClinicalItems_OfAnotherProvidersPatient_CannotBeChanged()
    {
        // Arrange — the other provider's patient owns real items.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var otherProvider = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();
        var foreign = await PatientRecords.CreateAsync(db, otherProvider, FullRequest(), Now, Ct);
        var foreignDetail = (await PatientRecords.GetAsync(db, otherProvider, foreign, Now, Ct))!;
        var me = seed.ProviderId;

        // Act + Assert
        (await PatientRecords.UpdateDiagnosisAsync(db, me, foreign, foreignDetail.Diagnoses[0].Id, new DiagnosisInput("x", null, null), Ct)).Should().BeFalse();
        (await PatientRecords.RemoveDiagnosisAsync(db, me, foreign, foreignDetail.Diagnoses[0].Id, Ct)).Should().BeFalse();
        (await PatientRecords.RemoveMedicationAsync(db, me, foreign, foreignDetail.Medications[0].Id, Ct)).Should().BeFalse();
        (await PatientRecords.RemoveObjectiveAsync(db, me, foreign, foreignDetail.Objectives[0].Id, Ct)).Should().Be(RecordOutcome.NotFound);
        (await PatientRecords.UpdateObjectiveAsync(db, me, foreign, foreignDetail.Objectives[0].Id, new ObjectiveInput("x"), Ct)).Should().Be(RecordOutcome.NotFound);
        (await PatientRecords.GetAsync(db, otherProvider, foreign, Now, Ct))!.Diagnoses.Should().ContainSingle();
    }

    [Fact]
    public async Task Update_ChangesBasics()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var updated = await PatientRecords.UpdateAsync(db, seed.ProviderId, id, new UpdatePatientRequest("Ana Maria", "Souza", "ana.souza@example.com", new DateOnly(1985, 1, 2)), Now, Ct);

        // Assert
        updated.Should().BeTrue();
        var detail = await GetAsync(seed, id);
        detail!.FirstName.Should().Be("Ana Maria");
        detail.LastName.Should().Be("Souza");
        detail.DateOfBirth.Should().Be(new DateOnly(1985, 1, 2));
        detail.ContactEmail.Should().Be("ana.souza@example.com");
    }

    [Fact]
    public async Task Delete_WithoutRecords_RemovesPatientAndClinicalProfile()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var id = await PatientRecords.CreateAsync(db, seed.ProviderId, FullRequest(), Now, Ct);

        // Act
        var outcome = await PatientRecords.DeleteAsync(db, seed.ProviderId, id, Ct);

        // Assert
        outcome.Should().Be(RecordOutcome.Ok);
        (await db.Patients.AnyAsync(p => p.Id == id, Ct)).Should().BeFalse();
        (await db.PatientDiagnoses.AnyAsync(d => d.PatientId == id, Ct)).Should().BeFalse();
        (await db.MedicationEntries.AnyAsync(m => m.PatientId == id, Ct)).Should().BeFalse();
        (await db.TreatmentPlans.AnyAsync(t => t.PatientId == id, Ct)).Should().BeFalse();
    }

    [Theory]
    [InlineData("note")]
    [InlineData("appointment")]
    [InlineData("portal")]
    public async Task Delete_WithRecordsOrPortalAccount_Conflicts(string blocker)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        switch (blocker)
        {
            case "note": await seed.AddNoteAsync(id, "Draft", Now.AddDays(-1), cancellationToken: Ct); break;
            case "appointment": await seed.AddAppointmentAsync(id, Now.AddDays(-1), "attended", cancellationToken: Ct); break;
            case "portal": await seed.BindPortalAccountAsync(id, Ct); break;
        }
        await using var db = seed.CreateDbContext();

        // Act
        var outcome = await PatientRecords.DeleteAsync(db, seed.ProviderId, id, Ct);

        // Assert
        outcome.Should().Be(RecordOutcome.Conflict);
        (await db.Patients.AnyAsync(p => p.Id == id, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Archive_HidesFromSearchAndUnarchiveRestores()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var archived = await PatientRecords.SetArchivedAsync(db, seed.ProviderId, id, true, Now, Ct);
        var whileArchived = await PatientSearchQuery.ExecuteAsync(db, seed.ProviderId, Now, new PatientSearchRequest(), Ct);
        var detail = await PatientRecords.GetAsync(db, seed.ProviderId, id, Now, Ct);
        await PatientRecords.SetArchivedAsync(db, seed.ProviderId, id, false, Now, Ct);
        var afterRestore = await PatientSearchQuery.ExecuteAsync(db, seed.ProviderId, Now, new PatientSearchRequest(), Ct);

        // Assert
        archived.Should().BeTrue();
        whileArchived.TotalCount.Should().Be(0);
        detail!.ArchivedAt.Should().Be(Now);
        afterRestore.Items.Select(i => i.Id).Should().Equal(id);
    }

    [Fact]
    public async Task Diagnosis_AddUpdateResolveRemove()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act + Assert
        var dxId = await PatientRecords.AddDiagnosisAsync(db, seed.ProviderId, id, new DiagnosisInput(" TAG ", "F41.1", null), Now, Ct);
        dxId.Should().NotBeNull();
        (await PatientRecords.UpdateDiagnosisAsync(db, seed.ProviderId, id, dxId!.Value,
            new DiagnosisInput("Transtorno de ansiedade generalizada", "F41.1", new DateOnly(2026, 9, 30)), Ct)).Should().BeTrue();
        var dx = (await GetAsync(seed, id))!.Diagnoses.Single();
        dx.Description.Should().Be("Transtorno de ansiedade generalizada");
        dx.ResolvedOn.Should().Be(new DateOnly(2026, 9, 30));
        (await PatientRecords.RemoveDiagnosisAsync(db, seed.ProviderId, id, dxId.Value, Ct)).Should().BeTrue();
        (await GetAsync(seed, id))!.Diagnoses.Should().BeEmpty();
    }

    [Fact]
    public async Task Medication_AddEndRemove()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var started = new DateOnly(2026, 9, 1);

        // Act + Assert
        var medId = await PatientRecords.AddMedicationAsync(db, seed.ProviderId, id, new MedicationInput("Sertralina", "50mg", null, started, null), Ct);
        (await PatientRecords.UpdateMedicationAsync(db, seed.ProviderId, id, medId!.Value,
            new MedicationInput("Sertralina", "100mg", "1x/dia", started, new DateOnly(2026, 9, 30)), Ct)).Should().BeTrue();
        var med = (await GetAsync(seed, id))!.Medications.Single();
        med.Dose.Should().Be("100mg");
        med.EndedOn.Should().Be(new DateOnly(2026, 9, 30));
        (await PatientRecords.RemoveMedicationAsync(db, seed.ProviderId, id, medId.Value, Ct)).Should().BeTrue();
        (await GetAsync(seed, id))!.Medications.Should().BeEmpty();
    }

    [Fact]
    public async Task Objective_FirstCreatesPlanThenReusesIt()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var first = await PatientRecords.AddObjectiveAsync(db, seed.ProviderId, id, new ObjectiveInput("Dormir melhor"), Now, Ct);
        var second = await PatientRecords.AddObjectiveAsync(db, seed.ProviderId, id, new ObjectiveInput("Retomar exercícios"), Now.AddMinutes(1), Ct);
        var renamed = await PatientRecords.UpdateObjectiveAsync(db, seed.ProviderId, id, first.Id!.Value, new ObjectiveInput("Dormir 7h por noite"), Ct);

        // Assert
        renamed.Should().Be(RecordOutcome.Ok);
        (await db.TreatmentPlans.CountAsync(t => t.PatientId == id, Ct)).Should().Be(1);
        (await GetAsync(seed, id))!.Objectives.Select(o => o.Description).Should().Equal("Dormir 7h por noite", "Retomar exercícios");
        (await PatientRecords.RemoveObjectiveAsync(db, seed.ProviderId, id, second.Id!.Value, Ct)).Should().Be(RecordOutcome.Ok);
    }

    // F-14 fix round 1: an archived record is read-only for objectives too (409 at the endpoint).
    [Fact]
    public async Task Objective_OfAnArchivedPatient_CannotBeAddedChangedOrRemoved()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var existing = await PatientRecords.AddObjectiveAsync(db, seed.ProviderId, id, new ObjectiveInput("Dormir melhor"), Now, Ct);
        (await PatientRecords.SetArchivedAsync(db, seed.ProviderId, id, true, Now, Ct)).Should().BeTrue();

        // Act
        var add = await PatientRecords.AddObjectiveAsync(db, seed.ProviderId, id, new ObjectiveInput("Outro"), Now, Ct);
        var update = await PatientRecords.UpdateObjectiveAsync(db, seed.ProviderId, id, existing.Id!.Value, new ObjectiveInput("Mudado"), Ct);
        var remove = await PatientRecords.RemoveObjectiveAsync(db, seed.ProviderId, id, existing.Id.Value, Ct);

        // Assert
        add.Outcome.Should().Be(RecordOutcome.Conflict);
        update.Should().Be(RecordOutcome.Conflict);
        remove.Should().Be(RecordOutcome.Conflict);
        (await GetAsync(seed, id))!.Objectives.Should().ContainSingle().Which.Description.Should().Be("Dormir melhor");
    }

    // F-14 (design/specs/2026-10-30-plan-adherence-design.md §3): an objective may be linked to an active term of the
    // active vocabulary; a term that is no longer active is refused, except the one the objective already has.
    [Fact]
    public async Task Objective_TermLink_AcceptsActiveTerms_RefusesOthers_KeepsTheCurrentOne()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var id = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var active = await (
            from t in db.ThemeTerms
            join v in db.ThemeVocabularies on t.VocabularyId equals v.Id
            where v.Active && t.Status == "active"
            orderby t.Code
            select new { t.Id, t.Label }).FirstAsync(Ct);
        var vocabulary = new Anamnys.Server.Data.Entities.ThemeVocabulary { Id = Guid.NewGuid(), Version = $"test-{Guid.NewGuid():N}", Active = false };
        var retired = new Anamnys.Server.Data.Entities.ThemeTerm
        {
            Id = Guid.NewGuid(), VocabularyId = vocabulary.Id, Code = $"t-{Guid.NewGuid():N}", Label = "Antigo", IntroducedOn = new DateOnly(2026, 1, 1),
        };
        db.ThemeVocabularies.Add(vocabulary);
        db.ThemeTerms.Add(retired);
        await db.SaveChangesAsync(Ct);

        // Act
        var linked = (await PatientRecords.AddObjectiveAsync(db, seed.ProviderId, id, new ObjectiveInput("Dormir melhor", active.Id), Now, Ct)).Id!.Value;
        await db.PlanObjectives.Where(o => o.Id == linked).ExecuteUpdateAsync(u => u.SetProperty(o => o.TermId, retired.Id), Ct);
        var otherProvider = await seed.AddProviderAsync(Ct);
        var otherPatient = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        var me = seed.ProviderId;

        // Assert
        (await PatientRecords.ObjectiveTermAcceptedAsync(db, me, id, null, active.Id, Ct)).Should().BeTrue();
        (await PatientRecords.ObjectiveTermAcceptedAsync(db, me, id, null, null, Ct)).Should().BeTrue();
        (await PatientRecords.ObjectiveTermAcceptedAsync(db, me, id, null, retired.Id, Ct)).Should().BeFalse();
        (await PatientRecords.ObjectiveTermAcceptedAsync(db, me, id, null, Guid.NewGuid(), Ct)).Should().BeFalse();
        (await PatientRecords.ObjectiveTermAcceptedAsync(db, me, id, linked, retired.Id, Ct)).Should().BeTrue();
        // The retired-term exception is scoped: another provider, or another patient's path, does not see it.
        (await PatientRecords.ObjectiveTermAcceptedAsync(db, otherProvider, id, linked, retired.Id, Ct)).Should().BeFalse();
        (await PatientRecords.ObjectiveTermAcceptedAsync(db, me, otherPatient, linked, retired.Id, Ct)).Should().BeFalse();
        var objective = (await GetAsync(seed, id))!.Objectives.Should().ContainSingle().Subject;
        objective.TermId.Should().Be(retired.Id);
        objective.TermLabel.Should().Be("Antigo");
        (await PatientRecords.UpdateObjectiveAsync(db, seed.ProviderId, id, linked, new ObjectiveInput("Dormir melhor", null), Ct)).Should().Be(RecordOutcome.Ok);
        (await GetAsync(seed, id))!.Objectives.Single().TermId.Should().BeNull();
        await db.ThemeTerms.Where(t => t.Id == retired.Id).ExecuteDeleteAsync(Ct);
        await db.ThemeVocabularies.Where(v => v.Id == vocabulary.Id).ExecuteDeleteAsync(Ct);
    }

    private static async Task<PatientDetailResponse?> GetAsync(PatientSearchSeed seed, Guid id)
    {
        await using var db = seed.CreateDbContext();
        return await PatientRecords.GetAsync(db, seed.ProviderId, id, Now, Ct);
    }
}
