using Anamnys.Server.Intakes;
using FluentAssertions;

namespace Anamnys.Tests.Intakes;

public class IntakeNegativeAnswersTests
{
    [Theory]
    [InlineData("Nada fora do normal.")]
    [InlineData("Nenhuma")]
    [InlineData("nenhum")]
    [InlineData("Não toma medicação")]
    [InlineData("Nao")]
    [InlineData("Nega uso de medicamentos")]
    [InlineData("Sem diagnóstico prévio")]
    [InlineData("N/A")]
    [InlineData("na")]
    [InlineData("-")]
    [InlineData("  Nada.  ")]
    [InlineData("")]
    public void IsNegative_NegativeOrEmptyStatement_ReturnsTrue(string answer)
    {
        // Arrange

        // Act
        var result = IntakeNegativeAnswers.IsNegative(answer);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("Sertralina 50 mg")]
    [InlineData("Melatonina 30 g")]
    [InlineData("Ansiedade generalizada")]
    [InlineData("Nadador (uso de suplementos)")]
    [InlineData("Naltrexona 50 mg")]
    [InlineData("Nenhumas vezes")]
    [InlineData("Semaglutida 1 mg")]
    [InlineData("Negativismo desafiador")]
    [InlineData("Na infância tomava fluoxetina")]
    public void IsNegative_RealAnswer_ReturnsFalse(string answer)
    {
        // Arrange

        // Act
        var result = IntakeNegativeAnswers.IsNegative(answer);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("diagnosis", "Nada.", false)]
    [InlineData("medication", "Não toma", false)]
    [InlineData("diagnosis", "Ansiedade", true)]
    [InlineData("plan.objective", "Nada a declarar", true)]
    [InlineData("patient.phone", "-", true)]
    public void IsOfferable_AppliesOnlyToDiagnosisAndMedication(string targetField, string answer, bool expected)
    {
        // Arrange

        // Act
        var result = IntakeNegativeAnswers.IsOfferable(targetField, answer);

        // Assert
        result.Should().Be(expected);
    }
}
