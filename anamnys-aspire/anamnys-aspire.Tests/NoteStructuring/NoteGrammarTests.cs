using Anamnys.Server.NoteStructuring;
using FluentAssertions;

namespace Anamnys.Tests.NoteStructuring;

public class NoteGrammarTests
{
    [Fact]
    public void For_DapKeys_RequiresExactlyThoseKeysInOrder()
    {
        var gbnf = NoteGrammar.For(["data", "assessment", "plan"]);

        gbnf.Should().StartWith("root ::= \"{\" ws ");
        var root = gbnf.Split('\n')[0];
        root.IndexOf("\\\"data\\\"").Should().BeLessThan(root.IndexOf("\\\"assessment\\\""));
        root.IndexOf("\\\"assessment\\\"").Should().BeLessThan(root.IndexOf("\\\"plan\\\""));
        root.Should().EndWith("ws \"}\"");
        gbnf.Should().Contain("string ::=").And.Contain("ws ::=");
    }

    [Fact]
    public void For_RejectsKeysThatNeedEscaping()
    {
        var act = () => NoteGrammar.For(["bad\"key"]);

        act.Should().Throw<ArgumentException>();
    }
}
