using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notes;
using FluentAssertions;

namespace Anamnys.Tests.Notes;

public class NotePdfTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 17, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void From_Sections_OrderedLabelledAndEmptyMarked()
    {
        // Arrange
        var (note, sections, patient, provider) = Sample();

        // Act
        var model = NotePdfModel.From(note, sections, patient, provider, null);

        // Assert
        model.Sections.Select(s => s.Title).Should().Equal("Dados", "Avaliação", "Plano");
        model.Sections.Select(s => s.Content).Should().Equal("Relata insônia.", "", "Retorno.");
        model.FormatName.Should().Be("DAP");
        model.Crp.Should().Be("06/123456");
        model.Code.Should().Be(note.Id.ToString("N")[..8].ToUpperInvariant());
        model.FileName.Should().Be($"nota-{model.Code}.pdf");
    }

    [Fact]
    public void From_NeverCarriesTheTranscript()
    {
        // Arrange
        var (note, sections, patient, provider) = Sample();
        note.RawTranscript = "TRANSCRICAO-SECRETA";

        // Act
        var model = NotePdfModel.From(note, sections, patient, provider, null);

        // Assert
        model.Sections.Should().NotContain(s => s.Content.Contains("TRANSCRICAO-SECRETA"));
        new[] { model.ProviderName, model.Crp, model.PatientName, model.FormatName, model.Code }
            .Should().NotContain(v => v.Contains("TRANSCRICAO-SECRETA"));
    }

    [Fact]
    public void From_WhitespaceOnlySection_ContentEmpty()
    {
        // Arrange
        var (note, _, patient, provider) = Sample();
        IReadOnlyList<NoteSection> sections = [new() { Key = "data", Content = "   ", Position = 0 }];

        // Act
        var model = NotePdfModel.From(note, sections, patient, provider, null);

        // Assert
        model.Sections.Should().ContainSingle().Which.Content.Should().BeEmpty();
    }

    [Fact]
    public void From_SessionDate_PrefersScheduledThenActualThenCreated()
    {
        // Arrange
        var (note, sections, patient, provider) = Sample();
        var scheduled = Created.AddDays(-1);
        var actual = Created.AddDays(-2);

        // Act
        var withScheduled = NotePdfModel.From(note, sections, patient, provider, new Session { ScheduledStart = scheduled, ActualStart = actual });
        var withActual = NotePdfModel.From(note, sections, patient, provider, new Session { ActualStart = actual });
        var without = NotePdfModel.From(note, sections, patient, provider, null);

        // Assert
        withScheduled.SessionDate.Should().Be(scheduled);
        withActual.SessionDate.Should().Be(actual);
        without.SessionDate.Should().Be(Created);
    }

    [Fact]
    public void Render_Model_ProducesPdfBytes()
    {
        // Arrange
        var (note, sections, patient, provider) = Sample();
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        // Act
        var bytes = NotePdf.Render(NotePdfModel.From(note, sections, patient, provider, null));

        // Assert
        bytes.Should().NotBeEmpty();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
    }

    private static (Note, IReadOnlyList<NoteSection>, Patient, Provider) Sample()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(), Format = "DAP", Status = "Signed", CreatedAt = Created,
            SignedAt = Created.AddHours(1), SignedByCrp = "06/123456",
        };
        IReadOnlyList<NoteSection> sections =
        [
            new() { Key = "plan", Content = " Retorno. ", Position = 2 },
            new() { Key = "data", Content = "Relata insônia.", Position = 0 },
            new() { Key = "assessment", Content = "", Position = 1 },
        ];
        var patient = new Patient { FirstName = "Ana", LastName = "Silva", DateOfBirth = new DateOnly(1990, 5, 4) };
        var provider = new Provider { Name = "Dra. Paula Souza" };
        return (note, sections, patient, provider);
    }
}
