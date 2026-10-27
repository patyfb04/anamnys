using Anamnys.Server.Medications;
using FluentAssertions;

namespace Anamnys.Tests.Medications;

// design/specs/2026-10-27-medication-timeline-design.md §6: the sentences of the sample documents.
public class MedicationPrefillTests
{
    private static readonly DateOnly Issue = new(2026, 9, 14);
    private static readonly DateOnly Entered = new(2026, 9, 10);

    [Fact]
    public void From_DrugDoseAndPosology_FillsThem_AndStartsOnTheIssueDate()
    {
        // Arrange
        const string sentence = "Sertralina 50 mg, 1 comprimido pela manhã.";

        // Act
        var draft = MedicationPrefill.From("Sertralina 50 mg, 1 comprimido pela manhã", sentence, Issue, Entered);

        // Assert
        draft.Should().Be(new MedicationDraft("Sertralina", "50 mg", "1 comprimido pela manhã", Issue, null));
    }

    [Fact]
    public void From_NoIssueDate_FallsBackToTheDocumentDateEntered()
    {
        // Act
        var draft = MedicationPrefill.From("Sertralina 50 mg", "Sertralina 50 mg, 1 comprimido pela manhã.", null, Entered);

        // Assert
        draft.StartedOn.Should().Be(Entered);
    }

    [Fact]
    public void From_NoDateAtAll_LeavesTheStartBlank()
    {
        // Act
        var draft = MedicationPrefill.From("Sertralina 50 mg", null, null, null);

        // Assert
        draft.StartedOn.Should().BeNull();
    }

    [Fact]
    public void From_QuotedStart_WinsOverTheFallback_AndIsNotTakenAsPosology()
    {
        // Arrange
        const string sentence = "Bupropiona 150 mg, 1 comprimido pela manhã (início em 10/03/2026).";

        // Act
        var draft = MedicationPrefill.From("Bupropiona 150 mg, 1 comprimido pela manhã", sentence, Issue, Entered);

        // Assert
        draft.Should().Be(new MedicationDraft("Bupropiona", "150 mg", "1 comprimido pela manhã", new DateOnly(2026, 3, 10), null));
    }

    [Theory]
    [InlineData("Em uso desde 5 de março de 2026: Fluoxetina 20 mg", 2026, 3, 5)]
    [InlineData("Fluoxetina 20 mg, desde 05/03/2026", 2026, 3, 5)]
    [InlineData("Fluoxetina 20 mg (início: 1º de abril de 2025)", 2025, 4, 1)]
    public void From_StartMarkers_InNumbersAndWords_AreRead(string sentence, int year, int month, int day)
    {
        // Act
        var draft = MedicationPrefill.From(sentence, sentence, Issue, null);

        // Assert
        draft.StartedOn.Should().Be(new DateOnly(year, month, day));
        draft.Drug.Should().NotBeNull().And.Subject.Should().Contain("Fluoxetina");
    }

    [Fact]
    public void From_Suspended_FillsTheEnd_AndLeavesTheStartBlankWhenTheFallbackIsLater()
    {
        // Arrange
        const string sentence = "Escitalopram 10 mg suspenso em 01/09/2026 por efeitos adversos.";

        // Act
        var draft = MedicationPrefill.From("Escitalopram 10 mg", sentence, Issue, null);

        // Assert
        draft.Should().Be(new MedicationDraft("Escitalopram", "10 mg", null, null, new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void From_SuspendedAfterAFallbackStart_KeepsTheFallback()
    {
        // Act
        var draft = MedicationPrefill.From("Escitalopram 10 mg", "Escitalopram 10 mg interrompido em 20/09/2026.", Issue, null);

        // Assert
        draft.StartedOn.Should().Be(Issue);
        draft.EndedOn.Should().Be(new DateOnly(2026, 9, 20));
    }

    [Fact]
    public void From_PrescriptionLine_TakesThePosologyAfterTomar_AndDropsTheNumbering()
    {
        // Arrange
        const string line = "1. Escitalopram 10 mg ..... 30 comprimidos / Tomar 1 comprimido pela manhã.";

        // Act
        var draft = MedicationPrefill.From("Escitalopram 10 mg", line, Issue, null);

        // Assert
        draft.Should().Be(new MedicationDraft("Escitalopram", "10 mg", "1 comprimido pela manhã", Issue, null));
    }

    [Theory]
    [InlineData("Clonazepam 0,5 mg à noite", "0,5 mg")]
    [InlineData("Lítio 300 mg 2 vezes ao dia", "300 mg")]
    [InlineData("Vitamina D 2000 UI", "2000 UI")]
    [InlineData("Amoxicilina 250 ML", "250 ml")]
    [InlineData("Levotiroxina 75 mcg em jejum", "75 mcg")]
    [InlineData("Melatonina 3mg", "3 mg")]
    public void From_Doses_KeepTheCommaOfDecimals_AndWriteTheUnitPlainly(string value, string expected)
    {
        // Act
        var draft = MedicationPrefill.From(value, null, null, null);

        // Assert
        draft.Dose.Should().Be(expected);
    }

    [Fact]
    public void From_DrugWithoutDoseOrPosology_FillsOnlyTheDrug()
    {
        // Act
        var draft = MedicationPrefill.From("Sertralina", null, null, null);

        // Assert
        draft.Should().Be(new MedicationDraft("Sertralina", null, null, null, null));
    }

    [Fact]
    public void From_DrugWithAComplementBeforeTheDose_KeepsTheWholeName()
    {
        // Act
        var draft = MedicationPrefill.From("Cloridrato de bupropiona 150 mg", null, null, null);

        // Assert
        draft.Drug.Should().Be("Cloridrato de bupropiona");
    }

    [Fact]
    public void From_NothingRecognisable_IsBlank()
    {
        // Act
        var draft = MedicationPrefill.From("  ", null, null, null);

        // Assert
        draft.Should().Be(new MedicationDraft(null, null, null, null, null));
    }

    [Theory]
    [InlineData("Sertralina", "sertralina")]
    [InlineData("  CLORIDRATO  de Bupropiona", "cloridrato de bupropiona")]
    [InlineData("Lítio", "litio")]
    public void Fold_IgnoresCaseAccentsAndSpacing(string text, string expected)
    {
        // Act
        var folded = MedicationPrefill.Fold(text);

        // Assert
        folded.Should().Be(expected);
    }
}
