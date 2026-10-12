using Anamnys.Server.NoteStructuring;
using FluentAssertions;

namespace Anamnys.Tests.NoteStructuring;

public class NoteFormatsTests
{
    [Fact]
    public void KeysFor_EachFormat_ReturnsKeysInOrder()
    {
        NoteFormats.KeysFor("DAP").Should().Equal("data", "assessment", "plan");
        NoteFormats.KeysFor("SOAP").Should().Equal("subjective", "objective", "assessment", "plan");
        NoteFormats.KeysFor("Biopsychosocial").Should().Equal("biological", "psychological", "social", "plan");
    }

    [Theory]
    [InlineData("DAP", true)]
    [InlineData("SOAP", true)]
    [InlineData("Biopsychosocial", true)]
    [InlineData("PtFunctional", false)]
    [InlineData("dap", false)]
    public void IsStructurable_MatchesTheThreeFormats(string format, bool expected)
    {
        NoteFormats.IsStructurable(format).Should().Be(expected);
    }

    [Fact]
    public void KeysFor_UnknownFormat_Throws()
    {
        var act = () => NoteFormats.KeysFor("PtFunctional");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Describe_EveryKey_HasAPortugueseDescription()
    {
        foreach (var format in new[] { "DAP", "SOAP", "Biopsychosocial" })
        {
            foreach (var key in NoteFormats.KeysFor(format))
            {
                NoteFormats.Describe(key).Should().NotBeNullOrWhiteSpace();
            }
        }
    }
}
