using Anamnys.Server.LiveSession;
using FluentAssertions;

namespace Anamnys.Tests.LiveSession;

public class LiveSessionRulesTests
{
    [Theory]
    [InlineData("DAP", "pt", true)]
    [InlineData("SOAP", "pt", true)]
    [InlineData("SOAP", "en", false)]
    [InlineData("Biopsychosocial", "pt", true)]
    [InlineData("Biopsychosocial", "es", false)]
    [InlineData("PtFunctional", "pt", false)]
    [InlineData("DAP", "fr", false)]
    [InlineData("dap", "pt", false)]
    public void IsValid_FormatAndLanguage_MatchesTheAllowedLists(string format, string language, bool expected)
    {
        LiveSessionRules.IsValid(format, language).Should().Be(expected);
    }

    [Fact]
    public void MaxAudioBytes_Is90MinutesOfPcm16kMono()
    {
        LiveSessionRules.MaxAudioBytes.Should().Be(90L * 60 * 16000 * 2);
    }
}
