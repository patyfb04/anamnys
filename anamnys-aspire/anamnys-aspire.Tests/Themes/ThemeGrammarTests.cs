using System.Text;
using System.Text.RegularExpressions;
using Anamnys.Server.Themes;
using FluentAssertions;

namespace Anamnys.Tests.Themes;

public class ThemeGrammarTests
{
    private static readonly string[] Codes = ["sono", "ansiedade", "trabalho", "trabalho_burnout", "luto", "familia"];

    [Theory]
    [InlineData("[]")]
    [InlineData("[ ]")]
    [InlineData("[\"sono\"]")]
    [InlineData("[\"sono\", \"trabalho_burnout\"]")]
    [InlineData("[\"sono\",\"ansiedade\",\"trabalho\",\"luto\",\"familia\"]")]
    [InlineData("[\"sono\",\"sono\"]")]
    public void For_ListedCodesUpToFive_AreAdmitted(string output)
    {
        // Arrange
        var grammar = Compile(ThemeGrammar.For(Codes));

        // Act
        var admitted = grammar.IsMatch(output);

        // Assert
        admitted.Should().BeTrue();
    }

    [Theory]
    [InlineData("[\"sono\",\"ansiedade\",\"trabalho\",\"luto\",\"familia\",\"trabalho_burnout\"]")]
    [InlineData("[\"insonia\"]")]
    [InlineData("[\"son\"]")]
    [InlineData("[sono]")]
    [InlineData("{\"sono\": 1}")]
    [InlineData("[\"sono\",]")]
    [InlineData("\"sono\"")]
    public void For_UnlistedCodesOrMoreThanFive_AreNotAdmitted(string output)
    {
        // Arrange
        var grammar = Compile(ThemeGrammar.For(Codes));

        // Act
        var admitted = grammar.IsMatch(output);

        // Assert
        admitted.Should().BeFalse();
    }

    [Fact]
    public void For_NoCodes_AdmitsOnlyEmptyArray()
    {
        // Arrange
        var grammar = Compile(ThemeGrammar.For([]));

        // Act
        var empty = grammar.IsMatch("[]");
        var any = grammar.IsMatch("[\"sono\"]");

        // Assert
        empty.Should().BeTrue();
        any.Should().BeFalse();
    }

    [Theory]
    [InlineData("Sono")]
    [InlineData("so\"no")]
    [InlineData("1sono")]
    [InlineData("")]
    public void For_CodeThatIsNotPlainIdentifier_Throws(string code)
    {
        // Arrange
        var act = () => ThemeGrammar.For(["sono", code]);

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_KnownCodes_ReturnsThemDeduplicatedInOrder()
    {
        // Arrange
        const string output = "[\"luto\", \"sono\", \"luto\"]";

        // Act
        var codes = ThemeGrammar.Parse(output, Codes);

        // Assert
        codes.Should().Equal("luto", "sono");
    }

    [Theory]
    [InlineData("[\"insonia\"]")]
    [InlineData("[\"sono\", null]")]
    [InlineData("[\"sono\",\"ansiedade\",\"trabalho\",\"luto\",\"familia\",\"trabalho_burnout\"]")]
    [InlineData("{\"sono\": \"x\"}")]
    [InlineData("[\"sono\"")]
    [InlineData("null")]
    [InlineData("")]
    public void Parse_UnknownCodeOrMalformedOutput_ThrowsFormatException(string output)
    {
        // Arrange
        var act = () => ThemeGrammar.Parse(output, Codes);

        // Act & Assert
        act.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("[", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("[\"sono\"", new[] { "sono" })]
    [InlineData("[\"sono\", \"lu", new[] { "sono" })]
    [InlineData("[\"sono\", \"insonia\", \"luto\"", new[] { "sono", "luto" })]
    [InlineData("[\"sono\",\"ansiedade\",\"trabalho\",\"luto\",\"familia\",\"trabalho_burnout\"", new[] { "sono", "ansiedade", "trabalho", "luto", "familia" })]
    public void ParseOrRecover_CutOutput_KeepsCompleteKnownCodes(string output, string[] expected)
    {
        // Act
        var codes = ThemeGrammar.ParseOrRecover(output, Codes);

        // Assert
        codes.Should().Equal(expected);
    }

    [Fact]
    public void ParseOrRecover_ValidArray_SameAsParse()
    {
        // Act
        var codes = ThemeGrammar.ParseOrRecover("[\"luto\", \"sono\", \"luto\"]", Codes);

        // Assert
        codes.Should().Equal("luto", "sono");
    }

    [Fact]
    public void For_LargerMaxItems_AdmitsUpToThatMany()
    {
        // Arrange
        var grammar = Compile(ThemeGrammar.For(Codes, 7));
        var seven = "[" + string.Join(",", Enumerable.Repeat("\"sono\"", 7)) + "]";
        var eight = "[" + string.Join(",", Enumerable.Repeat("\"sono\"", 8)) + "]";

        // Act & Assert
        grammar.IsMatch(seven).Should().BeTrue();
        grammar.IsMatch(eight).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(3, 8)]
    [InlineData(5, 10)]
    [InlineData(40, 10)]
    public void ItemsWithExcluded_AddsUpToMaxItems(int excluded, int expected)
    {
        // Act & Assert
        ThemeGrammar.ItemsWithExcluded(excluded).Should().Be(expected);
    }

    [Fact]
    public void Pick_ExcludedCodes_AreDroppedFromOutput()
    {
        // Act
        var some = ThemeGrammar.Pick("[\"sono\", \"luto\", \"familia\"]", Codes, ["sono", "familia"]);
        var none = ThemeGrammar.Pick("[\"sono\", \"trabalho\", \"familia\"]", Codes, ["sono", "trabalho", "familia"]);

        // Assert
        some.Should().Equal("luto");
        none.Should().BeEmpty();
    }

    [Fact]
    public void Pick_ExcludedCodesNamedFirst_DoNotCrowdOutOthers()
    {
        // Arrange: three excluded plus five others, eight items in all.
        const string output = "[\"sono\",\"trabalho\",\"familia\",\"luto\",\"ansiedade\",\"trabalho_burnout\",\"sono\",\"luto\"]";

        // Act
        var codes = ThemeGrammar.Pick(output, Codes, ["sono", "trabalho", "familia"]);

        // Assert
        codes.Should().Equal("luto", "ansiedade", "trabalho_burnout");
    }

    [Fact]
    public void Pick_UnknownOrCutOutput_KeepsOnlyKnownNotExcluded()
    {
        // Act
        var codes = ThemeGrammar.Pick("[\"sono\", \"insonia\", \"luto\", \"lu", Codes, ["sono"]);

        // Assert
        codes.Should().Equal("luto");
    }

    // Translates the GBNF subset the grammar uses (literals, char classes, rule references,
    // grouping, | and ?) into an anchored regex, so admission can be checked without the model.
    private static Regex Compile(string gbnf)
    {
        var rules = gbnf.Split('\n')
            .Select(line => line.Split(" ::= ", 2))
            .ToDictionary(parts => parts[0], parts => parts[1]);
        return new Regex($"^{ToRegex(rules["root"], rules)}$");
    }

    private static string ToRegex(string body, IReadOnlyDictionary<string, string> rules)
    {
        var regex = new StringBuilder();
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (c == ' ')
            {
                i++;
            }
            else if (c == '"')
            {
                var literal = new StringBuilder();
                i++;
                while (body[i] != '"')
                {
                    if (body[i] == '\\')
                    {
                        i++;
                        literal.Append(body[i] switch { 't' => '\t', 'n' => '\n', var e => e });
                    }
                    else
                    {
                        literal.Append(body[i]);
                    }
                    i++;
                }
                i++;
                regex.Append(Regex.Escape(literal.ToString()));
            }
            else if (c == '[')
            {
                var end = body.IndexOf(']', i);
                regex.Append(body, i, end - i + 1);
                i = end + 1;
            }
            else if (c is '(' or ')' or '|' or '?')
            {
                regex.Append(c == '(' ? "(?:" : c.ToString());
                i++;
            }
            else
            {
                var start = i;
                while (i < body.Length && (char.IsLetterOrDigit(body[i]) || body[i] == '_'))
                {
                    i++;
                }
                regex.Append("(?:").Append(ToRegex(rules[body[start..i]], rules)).Append(')');
            }
        }
        return regex.ToString();
    }
}
