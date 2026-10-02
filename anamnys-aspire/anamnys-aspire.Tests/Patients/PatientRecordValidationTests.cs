using Anamnys.Server.Patients;
using FluentAssertions;

namespace Anamnys.Tests.Patients;

public class PatientRecordValidationTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static CreatePatientRequest ValidCreate() => new(
        "Ana", "Silva", new DateOnly(1990, 5, 12),
        [new DiagnosisInput("Transtorno depressivo maior", "F33.1", null)],
        [new MedicationInput("Sertralina", "100mg", "1x/dia", new DateOnly(2026, 9, 1), null)],
        ["Reduzir sintomas ansiosos"]);

    [Fact]
    public void Create_ValidRequest_HasNoErrors()
    {
        ValidCreate().Validate(Today).Should().BeEmpty();
    }

    [Fact]
    public void Create_WithoutClinicalLists_IsValid()
    {
        new CreatePatientRequest("Ana", "Silva", new DateOnly(1990, 5, 12), null, null, null).Validate(Today).Should().BeEmpty();
    }

    [Fact]
    public void Create_MissingBasics_ReportsEachField()
    {
        var errors = new CreatePatientRequest(" ", null, null, null, null, null).Validate(Today);

        errors.Keys.Should().BeEquivalentTo(["firstName", "lastName", "dateOfBirth"]);
    }

    [Theory]
    [InlineData(2026, 10, 2)]
    [InlineData(1899, 12, 31)]
    public void Create_DateOfBirthOutOfRange_IsRejected(int year, int month, int day)
    {
        var request = ValidCreate() with { DateOfBirth = new DateOnly(year, month, day) };

        request.Validate(Today).Should().ContainKey("dateOfBirth");
    }

    [Fact]
    public void Create_InvalidItems_AreKeyedByListAndIndex()
    {
        var request = ValidCreate() with
        {
            Diagnoses = [new DiagnosisInput("ok", null, null), new DiagnosisInput(" ", new string('X', 11), Today.AddDays(1))],
            Medications = [new MedicationInput("", null, null, null, null)],
            TreatmentObjectives = ["ok", new string('a', 501)],
        };

        var errors = request.Validate(Today);

        errors.Keys.Should().BeEquivalentTo([
            "diagnoses[1].description",
            "diagnoses[1].icdCode",
            "diagnoses[1].resolvedOn",
            "medications[0].drug",
            "medications[0].startedOn",
            "treatmentObjectives[1]",
        ]);
    }

    [Fact]
    public void Create_TooManyItems_ReportsTheList()
    {
        var request = ValidCreate() with { TreatmentObjectives = Enumerable.Repeat("x", 51).ToArray() };

        request.Validate(Today).Should().ContainKey("treatmentObjectives");
    }

    [Fact]
    public void Medication_EndBeforeStartOrInFuture_IsRejected()
    {
        var before = new MedicationInput("Sertralina", null, null, new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 9)).Validate(Today);
        var future = new MedicationInput("Sertralina", null, null, new DateOnly(2026, 9, 10), Today.AddDays(1)).Validate(Today);
        var futureStart = new MedicationInput("Sertralina", null, null, Today.AddDays(1), null).Validate(Today);

        before.Should().ContainKey("endedOn");
        future.Should().ContainKey("endedOn");
        futureStart.Should().ContainKey("startedOn");
    }

    [Fact]
    public void Medication_DoseAndPosologyOver200_AreRejected()
    {
        var errors = new MedicationInput("Sertralina", new string('d', 201), new string('p', 201), Today, null).Validate(Today);

        errors.Keys.Should().BeEquivalentTo(["dose", "posology"]);
    }

    [Fact]
    public void Objective_Blank_IsRejected()
    {
        new ObjectiveInput("  ").Validate().Should().ContainKey("description");
    }

    [Fact]
    public void Update_ValidatesBasics()
    {
        new UpdatePatientRequest("Ana", "", Today.AddDays(1)).Validate(Today).Keys
            .Should().BeEquivalentTo(["lastName", "dateOfBirth"]);
    }
}
