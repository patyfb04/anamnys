using System.Security.Cryptography;
using System.Text;
using Anamnys.Server.Patients;
using FluentAssertions;

namespace Anamnys.Tests.Patients;

public class PatientInvitationUnitTests
{
    [Fact]
    public void NewToken_Is32RandomBytesInBase64Url()
    {
        var a = PatientInvitationTokens.NewToken();
        var b = PatientInvitationTokens.NewToken();

        a.Should().NotBe(b);
        a.Should().MatchRegex("^[A-Za-z0-9_-]{43}$", "32 bytes are 43 base64url characters without padding");
    }

    [Fact]
    public void Hash_IsSha256OfTheToken()
    {
        var token = PatientInvitationTokens.NewToken();

        PatientInvitationTokens.Hash(token).Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    [Fact]
    public void Message_NamesTheProviderAndLinksTheToken_WithNothingElse()
    {
        // Act
        var message = PatientInvitationMessage.Compose(
            "ana@example.com", "Ana", "Dra. Clara <Mendes>", new Uri("http://localhost:5274/patient/convite/abc"),
            new DateTimeOffset(2026, 10, 10, 15, 0, 0, TimeSpan.Zero));

        // Assert
        message.ToEmail.Should().Be("ana@example.com");
        message.Subject.Should().Be("Seu convite para o portal de pacientes Anamnys");
        message.Text.Should().Contain("Olá, Ana.").And.Contain("Dra. Clara <Mendes> convidou você")
            .And.Contain("http://localhost:5274/patient/convite/abc").And.Contain("10/10/2026");
        message.Html.Should().Contain("Dra. Clara &lt;Mendes&gt;", "names are HTML-encoded")
            .And.Contain("href=\"http://localhost:5274/patient/convite/abc\"");
    }
}
