using Anamnys.Server.ExternalDocuments;
using FluentAssertions;

namespace Anamnys.Tests.ExternalDocuments;

public class FileTypeSnifferTests
{
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n"u8];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];

    [Theory]
    [InlineData("laudo.pdf", "application/pdf")]
    [InlineData("LAUDO.PDF", "application/pdf")]
    [InlineData("foto.jpg", "image/jpeg")]
    [InlineData("foto.JPEG", "image/jpeg")]
    [InlineData("exame.png", "image/png")]
    public void Sniff_ContentAndExtensionAgree_ReturnsTheMimeType(string fileName, string mime)
    {
        // Arrange
        var content = mime switch { "application/pdf" => Pdf, "image/jpeg" => Jpeg, _ => Png };

        // Act
        var result = FileTypeSniffer.Sniff(fileName, content);

        // Assert
        result.Outcome.Should().Be(SniffOutcome.Ok);
        result.MimeType.Should().Be(mime);
    }

    [Theory]
    [InlineData("laudo.png", "pdf")]
    [InlineData("laudo.jpg", "pdf")]
    [InlineData("laudo.exe", "pdf")]
    [InlineData("laudo", "pdf")]
    [InlineData("foto.pdf", "jpeg")]
    [InlineData("foto.png", "jpeg")]
    [InlineData("exame.jpg", "png")]
    [InlineData("exame.jpeg", "png")]
    public void Sniff_ExtensionDisagreesWithTheContent_IsATypeMismatch(string fileName, string actual)
    {
        // Arrange
        var content = actual switch { "pdf" => Pdf, "jpeg" => Jpeg, _ => Png };

        // Act
        var result = FileTypeSniffer.Sniff(fileName, content);

        // Assert
        result.Outcome.Should().Be(SniffOutcome.TypeMismatch);
        result.MimeType.Should().BeNull();
    }

    [Theory]
    [InlineData("nota.pdf", "just text")]
    [InlineData("nota.txt", "just text")]
    [InlineData("programa.pdf", "MZ\u0090\u0000")]
    [InlineData("quase.pdf", "%PDF")]
    [InlineData("quase.jpg", "ÿØ")]
    public void Sniff_UnknownContent_IsUnsupported(string fileName, string text)
    {
        // Arrange
        var content = text.Select(c => (byte)c).ToArray();

        // Act
        var result = FileTypeSniffer.Sniff(fileName, content);

        // Assert
        result.Outcome.Should().Be(SniffOutcome.UnsupportedType);
    }

    [Fact]
    public void Sniff_EmptyFile_IsEmpty()
    {
        // Act
        var result = FileTypeSniffer.Sniff("vazio.pdf", []);

        // Assert
        result.Outcome.Should().Be(SniffOutcome.Empty);
    }

    [Fact]
    public void Sniff_TheSizeLimit_IsTwentyMegabytes()
    {
        // Assert
        ExternalDocumentService.MaxBytes.Should().Be(20 * 1024 * 1024);
    }
}
