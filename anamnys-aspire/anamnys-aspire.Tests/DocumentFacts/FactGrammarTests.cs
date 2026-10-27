using System.Text;
using System.Text.RegularExpressions;
using Anamnys.Server.DocumentFacts;
using FluentAssertions;

namespace Anamnys.Tests.DocumentFacts;

public class FactGrammarTests
{
    private static readonly int[] Pages = [1, 2];

    // One output item as the model writes it.
    private static string I(string kind, string value, int page = 1, string quote = "frase") =>
        $"{{\"kind\": \"{kind}\", \"page\": {page}, \"quote\": \"{quote}\", \"value\": \"{value}\"}}";

    private static string Array(params string[] items) => "[" + string.Join(", ", items) + "]";

    public static TheoryData<string> Admitted => new()
    {
        "[]",
        Array(I("icd", "F43.1", quote: "CID-10 F43.1 - Transtorno de estresse pós-traumático.")),
        Array(I("icd", "F43")),
        Array(I("medication", "Sertralina 50 mg, 1 comprimido pela manhã", 2), I("issue_date", "14 de setembro de 2026")),
        Array(I("issuer", "Dr. Ricardo"), I("registration", "CRM-SP 000000"), I("conclusion", "diz \\\"melhora\\\"")),
        Array(Enumerable.Range(0, FactGrammar.MaxItems).Select(_ => I("issuer", "x")).ToArray()),
        Array(I("issuer", new string('a', FactGrammar.MaxValue), quote: new string('q', FactGrammar.MaxQuote))),
    };

    [Theory]
    [MemberData(nameof(Admitted))]
    public void For_ItemsOfTheKnownKindsOnTheGivenPages_AreAdmitted(string output)
    {
        // Arrange
        var grammar = Compile(FactGrammar.For(Pages));

        // Act
        var admitted = grammar.IsMatch(output);

        // Assert
        admitted.Should().BeTrue();
    }

    public static TheoryData<string> NotAdmitted => new()
    {
        Array(Enumerable.Range(0, FactGrammar.MaxItems + 1).Select(_ => I("issuer", "x")).ToArray()),
        Array(I("diagnosis", "x")),
        Array(I("issuer", "x", page: 3)),
        Array(I("issuer", "x", page: 0)),
        Array(I("icd", "F43.12")),
        Array(I("icd", "f43.1")),
        Array(I("icd", "CID F43")),
        Array(I("issuer", "")),
        Array(I("issuer", "x", quote: "")),
        Array(I("issuer", new string('a', FactGrammar.MaxValue + 1))),
        Array(I("issuer", "x", quote: new string('q', FactGrammar.MaxQuote + 1))),
        Array(I("issuer", "linha\nquebrada")),
        "[{\"kind\": \"issuer\", \"value\": \"x\", \"page\": 1, \"quote\": \"q\"}]",
        "[{\"kind\":\"issuer\",\"page\":1,\"quote\":\"q\",\"value\":\"x\"}]",
        "[ ]",
    };

    [Theory]
    [MemberData(nameof(NotAdmitted))]
    public void For_AnythingElse_IsNotAdmitted(string output)
    {
        // Arrange
        var grammar = Compile(FactGrammar.For(Pages));

        // Act
        var admitted = grammar.IsMatch(output);

        // Assert
        admitted.Should().BeFalse();
    }

    [Fact]
    public void For_NoPages_AdmitsOnlyTheEmptyArray()
    {
        // Act
        var grammar = FactGrammar.For([]);

        // Assert
        grammar.Should().Be("root ::= \"[]\"");
    }

    // llama.cpp only accepts rule names of letters, digits and '-'; a grammar it cannot parse makes the
    // first Sample crash the process (access violation), so this is checked without the model.
    [Fact]
    public void For_RuleNames_AreWhatLlamaCppParses()
    {
        // Act
        var names = FactGrammar.For(Pages).Split('\n').Select(line => line.Split(" ::= ", 2)[0]).ToList();

        // Assert
        names.Should().OnlyContain(n => Regex.IsMatch(n, "^[a-zA-Z0-9-]+$"));
        names.Should().Contain(["root", "item", "rest-icd", "rest-text", "page", "icd", "qbody", "vbody", "char"]);
    }

    [Fact]
    public void Forced_WalkingAnOutput_FeedsExactlyItsFixedJson_AndLeavesTheChoicesToTheModel()
    {
        // Arrange
        var target = Array(I("icd", "F43.1", 1, "CID-10 F43.1"), I("medication", "Prazosina 1 mg", 2, "Prazosina 1 mg, ao deitar."));
        var output = new StringBuilder("[");
        var sampled = 1;

        // Act: the "model" writes one character whenever nothing is forced.
        while (output.Length < target.Length)
        {
            var forced = FactGrammar.Forced(output.ToString());
            if (forced.Length > 0)
            {
                target[output.Length..].Should().StartWith(forced);
                output.Append(forced);
                continue;
            }
            output.Append(target[output.Length]);
            sampled++;
        }

        // Assert
        output.ToString().Should().Be(target);
        FactGrammar.IsComplete(output.ToString()).Should().BeTrue();
        // Only kinds, pages, quotes, values, the separator choice and the closing bracket were sampled.
        // Per item: the kind and its closing quote, the page digit and the comma after it, the quote and the
        // value with their closing quotes; the brace of the first item (an item or the end); between items
        // the comma; at the end the bracket.
        static int Item(string kind, string quote, string value) => kind.Length + 1 + 2 + quote.Length + 1 + value.Length + 1;
        sampled.Should().Be(1 + 1 + Item("icd", "CID-10 F43.1", "F43.1") + 1 + Item("medication", "Prazosina 1 mg, ao deitar.", "Prazosina 1 mg") + 1);
    }

    [Theory]
    [InlineData("[", "")]
    [InlineData("[{", "\"kind\": \"")]
    [InlineData("[{\"kind\": \"icd\"", ", \"page\": ")]
    [InlineData("[{\"kind\": \"icd\", \"page\": 1", "")]
    [InlineData("[{\"kind\": \"icd\", \"page\": 1,", " \"quote\": \"")]
    [InlineData("[{\"kind\": \"icd\", \"page\": 1, \"quote\": \"a \\\"b", "")]
    [InlineData("[{\"kind\": \"icd\", \"page\": 1, \"quote\": \"a\"", ", \"value\": \"")]
    [InlineData("[{\"kind\": \"icd\", \"page\": 1, \"quote\": \"a\", \"value\": \"F43\"", "}")]
    [InlineData("[{\"kind\": \"icd\", \"page\": 1, \"quote\": \"a\", \"value\": \"F43\"}", "")]
    [InlineData("[{\"kind\": \"icd\", \"page\": 1, \"quote\": \"a\", \"value\": \"F43\"},", " ")]
    [InlineData("[{\"kind\": \"icd\", \"page\": 1, \"quote\": \"a\", \"value\": \"F43\"}, ", "{\"kind\": \"")]
    [InlineData("[{\"kind\": \"icd\" ", "")]
    public void Forced_AfterAPrefix_ReturnsTheFixedTextOrNothing(string output, string expected)
    {
        // Act
        var forced = FactGrammar.Forced(output);

        // Assert
        forced.Should().Be(expected);
    }

    [Fact]
    public void Forced_LastAllowedItemClosed_ClosesTheArray()
    {
        // Arrange
        var output = "[" + I("issuer", "x");

        // Act
        var forced = FactGrammar.Forced(output, maxItems: 1);

        // Assert
        forced.Should().Be("]");
    }

    [Fact]
    public void Parse_WholeArray_ReturnsEveryItemWithItsPageQuoteAndUnescapedValue()
    {
        // Arrange
        var output = Array(I("icd", "F43.1", 1, "CID-10 F43.1"), I("conclusion", "diz \\\"melhora\\\"", 2, "q"));

        // Act
        var facts = FactGrammar.Parse(output);

        // Assert
        facts.Should().Equal(
            new FactCandidate("icd", "F43.1", 1, "CID-10 F43.1"),
            new FactCandidate("conclusion", "diz \"melhora\"", 2, "q"));
    }

    [Fact]
    public void Parse_OutputCutShort_KeepsTheItemsClosedBeforeTheCut()
    {
        // Arrange
        var output = "[" + I("issuer", "Clínica Horizonte") + ", {\"kind\": \"icd\", \"page\": 1, \"quote\": \"CID";

        // Act
        var facts = FactGrammar.Parse(output);

        // Assert
        facts.Should().Equal(new FactCandidate("issuer", "Clínica Horizonte", 1, "frase"));
    }

    [Fact]
    public void Parse_UnknownKindOrNothing_YieldsNoFact()
    {
        // Act
        var unknown = FactGrammar.Parse(Array(I("diagnosis", "x")));
        var empty = FactGrammar.Parse("[");

        // Assert
        unknown.Should().BeEmpty();
        empty.Should().BeEmpty();
    }

    // GBNF (the subset FactGrammar writes) to a .NET regex, to check what the grammar admits.
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
                while (i < body.Length && (char.IsLetterOrDigit(body[i]) || body[i] is '_' or '-'))
                {
                    i++;
                }
                regex.Append("(?:").Append(ToRegex(rules[body[start..i]], rules)).Append(')');
            }
        }
        return regex.ToString();
    }
}
