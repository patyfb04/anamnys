using System.Text;
using System.Text.RegularExpressions;
using Anamnys.Server.Themes;
using FluentAssertions;

namespace Anamnys.Tests.Themes;

public class ThemeGrammarTests
{
    private static readonly string[] Codes = ["sono", "ansiedade", "trabalho", "trabalho_burnout", "luto", "familia"];

    // One output item as the model writes it.
    private static string I(string code, string quote = "x") => $"{{\"code\": \"{code}\", \"quote\": \"{quote}\"}}";

    private static string Array(params string[] items) => "[" + string.Join(", ", items) + "]";

    public static TheoryData<string> Admitted => new()
    {
        "[]",
        Array(I("sono", "não dorme há 3 dias")),
        Array(I("sono"), I("trabalho_burnout", "")),
        Array(I("sono"), I("ansiedade"), I("trabalho"), I("luto"), I("familia")),
        Array(I("sono"), I("sono")),
        Array(I("sono", "diz \\\"chega\\\" [sic]")),
        Array(I("sono", new string('a', ThemeGrammar.MaxQuote))),
    };

    [Theory]
    [MemberData(nameof(Admitted))]
    public void For_ListedCodesWithQuotesUpToFive_AreAdmitted(string output)
    {
        // Arrange
        var grammar = Compile(ThemeGrammar.For(Codes));

        // Act
        var admitted = grammar.IsMatch(output);

        // Assert
        admitted.Should().BeTrue();
    }

    public static TheoryData<string> NotAdmitted => new()
    {
        Array(I("sono"), I("ansiedade"), I("trabalho"), I("luto"), I("familia"), I("trabalho_burnout")),
        Array(I("insonia")),
        "[\"sono\"]",
        "[{\"code\": \"sono\"}]",
        "[{\"quote\": \"x\", \"code\": \"sono\"}]",
        "[{\"code\": \"sono\", \"quote\": \"x\", \"extra\": 1}]",
        Array(I("sono", new string('a', ThemeGrammar.MaxQuote + 1))),
        Array(I("sono", "linha\nquebrada")),
        Array(I("sono", "aspas \" soltas")),
        Array(I("sono")) + ",",
        "{\"code\": \"sono\", \"quote\": \"x\"}",
        // Spacing is fixed so that Forced can feed the fixed text without sampling.
        "[ ]",
        "[{\"code\":\"sono\",\"quote\":\"a\"}]",
        "[" + I("sono") + "," + I("luto") + "]",
    };

    [Theory]
    [MemberData(nameof(NotAdmitted))]
    public void For_UnlistedCodesBadShapeLongQuotesOrMoreThanFive_AreNotAdmitted(string output)
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
        var any = grammar.IsMatch(Array(I("sono")));

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
        var quotes = () => ThemeGrammar.ForQuotes(["sono", code]);

        // Act & Assert
        act.Should().Throw<ArgumentException>();
        quotes.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ForQuotes_AdmitsExactlyTheGivenCodesInOrder()
    {
        // Arrange
        var grammar = Compile(ThemeGrammar.ForQuotes(["luto", "sono"]));

        // Act & Assert
        grammar.IsMatch(Array(I("luto", "perdeu o pai"), I("sono", "não dorme"))).Should().BeTrue();
        grammar.IsMatch(Array(I("sono"), I("luto"))).Should().BeFalse();
        grammar.IsMatch(Array(I("luto"))).Should().BeFalse();
        grammar.IsMatch(Array(I("luto"), I("sono"), I("familia"))).Should().BeFalse();
        grammar.IsMatch("[]").Should().BeFalse();
        Compile(ThemeGrammar.ForQuotes([])).IsMatch("[]").Should().BeTrue();
    }

    [Fact]
    public void Parse_KnownCodes_ReturnsThemDeduplicatedInOrderWithQuotes()
    {
        // Arrange
        var output = Array(I("luto", "perdeu o pai"), I("sono", ""), I("luto", "outra"));

        // Act
        var items = ThemeGrammar.Parse(output, Codes);

        // Assert
        items.Should().Equal(new ThemeCodeQuote("luto", "perdeu o pai"), new ThemeCodeQuote("sono", null));
    }

    [Fact]
    public void Parse_EscapedQuote_IsUnescaped()
    {
        // Act
        var items = ThemeGrammar.Parse(Array(I("sono", "diz \\\"chega\\\"")), Codes);

        // Assert
        items.Single().Quote.Should().Be("diz \"chega\"");
    }

    public static TheoryData<string> Malformed => new()
    {
        Array(I("insonia")),
        "[\"sono\"]",
        "[{\"code\": \"sono\"}]",
        "[{\"code\": \"sono\", \"quote\": 1}]",
        "[{\"code\": \"sono\", \"quote\": \"x\", \"extra\": \"y\"}]",
        Array(I("sono", new string('a', ThemeGrammar.MaxQuote + 1))),
        Array(I("sono"), I("ansiedade"), I("trabalho"), I("luto"), I("familia"), I("trabalho_burnout")),
        "[" + I("sono"),
        "null",
        "",
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Parse_UnknownCodeOrMalformedOutput_ThrowsFormatException(string output)
    {
        // Arrange
        var act = () => ThemeGrammar.Parse(output, Codes);

        // Act & Assert
        act.Should().Throw<FormatException>();
    }

    public static TheoryData<string, string[], string?[]> Cut => new()
    {
        { "[", [], [] },
        { "", [], [] },
        { "[" + I("sono", "não dorme"), ["sono"], ["não dorme"] },
        { "[" + I("sono", "não dorme") + ", {\"code\": \"lu", ["sono"], ["não dorme"] },
        { "[" + I("sono", "não dorme") + ", {\"code\": \"luto\", \"quote\": \"perdeu o", ["sono", "luto"], ["não dorme", null] },
        { "[" + I("sono") + "," + I("insonia") + "," + I("luto", "pai"), ["sono", "luto"], ["x", "pai"] },
        {
            "[" + string.Join(",", I("sono"), I("ansiedade"), I("trabalho"), I("luto"), I("familia"), I("trabalho_burnout")),
            ["sono", "ansiedade", "trabalho", "luto", "familia"], ["x", "x", "x", "x", "x"]
        },
    };

    [Theory]
    [MemberData(nameof(Cut))]
    public void ParseOrRecover_CutOutput_KeepsCompleteCodesAndClosedQuotes(string output, string[] codes, string?[] quotes)
    {
        // Act
        var items = ThemeGrammar.ParseOrRecover(output, Codes);

        // Assert
        items.Select(i => i.Code).Should().Equal(codes);
        items.Select(i => i.Quote).Should().Equal(quotes);
    }

    [Fact]
    public void ParseOrRecover_ValidArray_SameAsParse()
    {
        // Arrange
        var output = Array(I("luto", "pai"), I("sono", "noite"), I("luto"));

        // Act & Assert
        ThemeGrammar.ParseOrRecover(output, Codes).Should().Equal(ThemeGrammar.Parse(output, Codes));
    }

    [Fact]
    public void For_LargerMaxItems_AdmitsUpToThatMany()
    {
        // Arrange
        var grammar = Compile(ThemeGrammar.For(Codes, 7));
        var seven = Array(Enumerable.Repeat(I("sono"), 7).ToArray());
        var eight = Array(Enumerable.Repeat(I("sono"), 8).ToArray());

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
        var some = ThemeGrammar.Pick(Array(I("sono"), I("luto", "pai"), I("familia")), Codes, ["sono", "familia"]);
        var none = ThemeGrammar.Pick(Array(I("sono"), I("trabalho"), I("familia")), Codes, ["sono", "trabalho", "familia"]);

        // Assert
        some.Should().Equal(new ThemeCodeQuote("luto", "pai"));
        none.Should().BeEmpty();
    }

    [Fact]
    public void Pick_ExcludedCodesNamedFirst_DoNotCrowdOutOthers()
    {
        // Arrange: three excluded plus five others, eight items in all.
        var output = Array(I("sono"), I("trabalho"), I("familia"), I("luto"), I("ansiedade"), I("trabalho_burnout"), I("sono"), I("luto"));

        // Act
        var items = ThemeGrammar.Pick(output, Codes, ["sono", "trabalho", "familia"]);

        // Assert
        items.Select(i => i.Code).Should().Equal("luto", "ansiedade", "trabalho_burnout");
    }

    [Fact]
    public void Pick_UnknownOrCutOutput_KeepsOnlyKnownNotExcluded()
    {
        // Act
        var items = ThemeGrammar.Pick("[" + I("sono") + "," + I("insonia") + "," + I("luto") + ", {\"code\": \"lu", Codes, ["sono"]);

        // Assert
        items.Select(i => i.Code).Should().Equal("luto");
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"x\"}]", true)]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"a]", false)]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"a]\"}", false)]
    [InlineData("[", false)]
    public void IsComplete_OnlyAWholeArray(string output, bool expected)
    {
        // Act & Assert
        ThemeGrammar.IsComplete(output).Should().Be(expected);
    }

    [Theory]
    [InlineData("", "[")]
    [InlineData("[", "")]
    [InlineData("[{", "\"code\": \"")]
    [InlineData("[{\"code\": \"so", "")]
    [InlineData("[{\"code\": \"sono\"", ", \"quote\": \"")]
    [InlineData("[{\"code\": \"sono\", \"quo", "te\": \"")]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"diz \\\"", "")]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"x\"", "}")]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"x\"}", "")]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"x\"},", " ")]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"x\"}, ", "{\"code\": \"")]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"x\"}]", "")]
    [InlineData("[x", "")]
    public void Forced_Free_IsTheFixedTextAfterThePrefix(string output, string expected)
    {
        // Act & Assert
        ThemeGrammar.Forced(output, 5).Should().Be(expected);
    }

    [Fact]
    public void Forced_LastItem_ClosesTheArray()
    {
        // Act & Assert
        ThemeGrammar.Forced("[{\"code\": \"sono\", \"quote\": \"x\"}", 1).Should().Be("]");
    }

    [Theory]
    [InlineData("[", "{\"code\": \"luto\", \"quote\": \"")]
    [InlineData("[{\"code\": \"luto\", \"quote\": \"pai", "")]
    [InlineData("[{\"code\": \"luto\", \"quote\": \"pai\"", "}")]
    [InlineData("[{\"code\": \"luto\", \"quote\": \"pai\"}", ", ")]
    [InlineData("[{\"code\": \"luto\", \"quote\": \"pai\"}, ", "{\"code\": \"sono\", \"quote\": \"")]
    [InlineData("[{\"code\": \"luto\", \"quote\": \"pai\"}, {\"code\": \"sono\", \"quote\": \"noite\"}", "]")]
    public void Forced_FixedCodes_LeavesOnlyTheQuotesToTheModel(string output, string expected)
    {
        // Act & Assert
        ThemeGrammar.Forced(output, 2, ["luto", "sono"]).Should().Be(expected);
    }

    // Every forced text keeps the output inside the grammar: walking a whole valid output, the forced
    // text at each prefix is what the output has next.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Forced_AlongAValidOutput_MatchesItAndIsAdmitted(bool fixedCodes)
    {
        // Arrange
        var output = Array(I("luto", "diz \\\"pai\\\""), I("sono", "noite"));
        var codes = fixedCodes ? new[] { "luto", "sono" } : null;
        var grammar = Compile(fixedCodes ? ThemeGrammar.ForQuotes(codes!) : ThemeGrammar.For(Codes));

        // Act & Assert
        grammar.IsMatch(output).Should().BeTrue();
        for (var n = 0; n < output.Length; n++)
        {
            var forced = ThemeGrammar.Forced(output[..n], 5, codes);
            output[n..].Should().StartWith(forced);
        }
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("[ ]", true)]
    [InlineData("[\"sono\", \"trabalho_burnout\"]", true)]
    [InlineData("[\"sono\",\"ansiedade\",\"trabalho\",\"luto\",\"familia\"]", true)]
    [InlineData("[\"sono\",\"ansiedade\",\"trabalho\",\"luto\",\"familia\",\"trabalho_burnout\"]", false)]
    [InlineData("[\"insonia\"]", false)]
    [InlineData("[{\"code\": \"sono\", \"quote\": \"x\"}]", false)]
    public void ForCodes_AdmitsOnlyArraysOfListedCodes(string output, bool expected)
    {
        // Act & Assert
        Compile(ThemeGrammar.ForCodes(Codes)).IsMatch(output).Should().Be(expected);
    }

    [Fact]
    public void PickCodes_KeepsCompleteKnownCodesWithoutQuotes_DropsExcluded()
    {
        // Act
        var cut = ThemeGrammar.PickCodes("[\"sono\", \"insonia\", \"luto\", \"lu", Codes, []);
        var excluded = ThemeGrammar.PickCodes("[\"sono\",\"trabalho\",\"familia\",\"luto\",\"ansiedade\",\"trabalho_burnout\",\"sono\"]", Codes,
            ["sono", "trabalho", "familia"]);

        // Assert
        cut.Should().Equal(new ThemeCodeQuote("sono", null), new ThemeCodeQuote("luto", null));
        excluded.Select(i => i.Code).Should().Equal("luto", "ansiedade", "trabalho_burnout");
    }

    public static TheoryData<string, int, string[]?> Walks => new()
    {
        // Codes sharing a prefix: the model writes the code, nothing is forced inside it.
        { Array(I("trabalho", "a"), I("trabalho_burnout", "b")), 5, null },
        { Array(I("trabalho_burnout", "b"), I("trabalho", "a")), 5, null },
        { Array(I("trabalho", "a"), I("trabalho_burnout", "b")), 2, ["trabalho", "trabalho_burnout"] },
        // A quote at the 200-char limit with escapes (each escape is one character).
        { Array(I("sono", new string('a', ThemeGrammar.MaxQuote - 5) + "\\\"x\\\"\\\\\\n")), 5, null },
        // The excluded allowance: eight items, then the array must close.
        {
            Array(I("sono"), I("ansiedade"), I("trabalho"), I("luto"), I("familia"), I("trabalho_burnout"), I("sono"), I("luto")),
            ThemeGrammar.ItemsWithExcluded(3), null
        },
    };

    [Theory]
    [MemberData(nameof(Walks))]
    public void Forced_Walk_StaysOnTheOutputAndInsideTheGrammar(string output, int maxItems, string[]? fixedCodes)
    {
        // Arrange
        var grammar = Compile(fixedCodes is null ? ThemeGrammar.For(Codes, maxItems) : ThemeGrammar.ForQuotes(fixedCodes));

        // Act & Assert
        grammar.IsMatch(output).Should().BeTrue();
        for (var n = 0; n < output.Length; n++)
        {
            var forced = ThemeGrammar.Forced(output[..n], maxItems, fixedCodes);
            output[n..].Should().StartWith(forced);
            grammar.IsMatch(output[..n] + forced).Should().Be(n + forced.Length == output.Length);
        }
    }

    [Fact]
    public void Forced_AtTheAllowance_ClosesTheArray()
    {
        // Arrange
        var max = ThemeGrammar.ItemsWithExcluded(3);
        var full = "[" + string.Join(", ", Enumerable.Repeat(I("sono"), max));

        // Act & Assert
        ThemeGrammar.Forced(full, max).Should().Be("]");
        Compile(ThemeGrammar.For(Codes, max)).IsMatch(full + ", " + I("sono") + "]").Should().BeFalse();
    }

    // Translates the GBNF subset the grammar uses (literals, char classes, rule references, grouping,
    // | ? and {m,n}) into an anchored regex, so admission can be checked without the model.
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
            else if (c == '{')
            {
                var end = body.IndexOf('}', i);
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
