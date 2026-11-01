using Anamnys.Server.Analysis;
using FluentAssertions;

namespace Anamnys.Tests.Analysis;

// The pure part of F-16 (design/specs/2026-10-31-record-search-design.md §3).
public class RecordSearchTests
{
    private static readonly Guid Sono = Guid.NewGuid();
    private static readonly Guid Insonia = Guid.NewGuid();
    private static readonly Guid Pesadelo = Guid.NewGuid();
    private static readonly Guid Familia = Guid.NewGuid();
    private static readonly Guid Culpa = Guid.NewGuid();
    private static readonly Guid Substancias = Guid.NewGuid();

    private static readonly RecordSearchVocabularyTerm[] Vocabulary =
    [
        new(Sono, "sono", "Sono", null),
        new(Insonia, "insonia", "Insônia", Sono),
        new(Pesadelo, "pesadelo_recorrente", "Pesadelos recorrentes", Insonia),
        new(Familia, "familia", "Família", null),
        new(Culpa, "culpa_vergonha", "Culpa e vergonha", null),
        new(Substancias, "uso_substancias", "Uso de substâncias", null),
    ];

    private static string Marked(string text) => text.Replace("[", RecordSearch.MarkStart).Replace("]", RecordSearch.MarkEnd);

    [Theory]
    [InlineData("Quando falamos de luto pela primeira vez?", "luto")]
    [InlineData("Em qual sessão a paciente falou da mãe?", "mae")]
    [InlineData("O que ela disse sobre o trabalho e a chefia?", "trabalho chefia")]
    [InlineData("o sono melhorou?", "sono")]
    [InlineData("o que foi dito sobre o trabalho?", "trabalho")]
    [InlineData("o que aconteceu com a mãe?", "mae")]
    [InlineData("quais são os medos que tive?", "medos")]
    [InlineData("eles estão bem na escola?", "escola")]
    public void Parse_RemovesStopAndQuestionWords_AndAccents(string question, string words)
    {
        // Act
        var parsed = RecordSearch.Parse(question);

        // Assert
        string.Join(' ', parsed.Words).Should().Be(words);
    }

    [Theory]
    [InlineData("quando foi?")]
    [InlineData("o que falamos?")]
    [InlineData("?!...")]
    public void Parse_AQuestionWithNothingToLookFor_HasNoWords(string question)
    {
        // Act
        var parsed = RecordSearch.Parse(question);

        // Assert
        parsed.Words.Should().BeEmpty();
    }

    [Theory]
    [InlineData("quando falamos de luto?", RecordSearch.Chronological)]
    [InlineData("Primeira vez que falamos de luto", RecordSearch.Earliest)]
    [InlineData("luto pela PRIMEIRA   VEZ", RecordSearch.Earliest)]
    [InlineData("Qual foi a última vez que falamos de luto?", RecordSearch.Latest)]
    [InlineData("ultima vez: luto", RecordSearch.Latest)]
    [InlineData("a primeira consulta e o luto", RecordSearch.Chronological)]
    public void Parse_DetectsTheOrderHint(string question, string order)
    {
        // Act
        var parsed = RecordSearch.Parse(question);

        // Assert
        parsed.Order.Should().Be(order);
    }

    [Theory]
    [InlineData("quando falamos de familia?")]
    [InlineData("quando falamos de FAMÍLIA?")]
    public void MatchTerms_ByLabel_IsAccentInsensitive(string question)
    {
        // Act
        var match = RecordSearch.MatchTerms(RecordSearch.Parse(question), Vocabulary);

        // Assert
        match.TermIds.Should().BeEquivalentTo([Familia]);
        match.CoversQuestion.Should().BeTrue();
    }

    [Fact]
    public void MatchTerms_ACompoundLabel_MatchesInFull_ByCode_OrByEitherSide()
    {
        // Act
        var full = RecordSearch.MatchTerms(RecordSearch.Parse("uso de substâncias"), Vocabulary);
        var code = RecordSearch.MatchTerms(RecordSearch.Parse("uso substancias"), Vocabulary);
        var side = RecordSearch.MatchTerms(RecordSearch.Parse("quando falou de vergonha?"), Vocabulary);

        // Assert
        full.TermIds.Should().BeEquivalentTo([Substancias]);
        code.TermIds.Should().BeEquivalentTo([Substancias]);
        side.TermIds.Should().BeEquivalentTo([Culpa]);
    }

    [Fact]
    public void MatchTerms_IncludesTheDescendantsAtAnyDepth()
    {
        // Act
        var match = RecordSearch.MatchTerms(RecordSearch.Parse("sono"), Vocabulary);

        // Assert
        match.TermIds.Should().BeEquivalentTo([Sono, Insonia, Pesadelo]);
    }

    [Fact]
    public void MatchTerms_NoTermInTheQuestion_MatchesNothing()
    {
        // Act: part of a word is not the word, and part of a multi-word label is not the label.
        var match = RecordSearch.MatchTerms(RecordSearch.Parse("sonolência e pesadelos"), Vocabulary);

        // Assert
        match.TermIds.Should().BeEmpty();
    }

    [Fact]
    public void MatchTerms_WordsBeyondTheTerm_DoNotCoverTheQuestion()
    {
        // Act
        var match = RecordSearch.MatchTerms(RecordSearch.Parse("sono na escola"), Vocabulary);

        // Assert
        match.TermIds.Should().Contain(Sono);
        match.CoversQuestion.Should().BeFalse();
    }

    [Fact]
    public void MatchTerms_ATermInsideALongerTermsMatch_IsNotNamed_WithItsChildren()
    {
        // Arrange
        var trabalho = Guid.NewGuid();
        var chefia = Guid.NewGuid();
        var conflito = Guid.NewGuid();
        RecordSearchVocabularyTerm[] vocabulary =
        [
            new(trabalho, "trabalho", "Trabalho", null),
            new(chefia, "chefia", "Chefia", trabalho),
            new(conflito, "conflito_trabalho", "Conflito no trabalho", trabalho),
        ];

        // Act
        var inside = RecordSearch.MatchTerms(RecordSearch.Parse("quando falamos de conflito no trabalho?"), vocabulary);
        var alsoAlone = RecordSearch.MatchTerms(RecordSearch.Parse("trabalho e conflito no trabalho"), vocabulary);

        // Assert
        inside.TermIds.Should().BeEquivalentTo([conflito]);
        inside.CoversQuestion.Should().BeTrue();
        alsoAlone.TermIds.Should().BeEquivalentTo([trabalho, chefia, conflito]);
    }

    [Fact]
    public void TextQuery_RequiresEveryWord_AndJoinsTheEndingsTheStemmerKeepsApart() =>
        RecordSearch.TextQuery(["relacoes", "mae"]).Should().Be("('relacao' | 'relacoes' | 'relacaos') & 'mae'");

    [Fact]
    public void Snippet_ReturnsTheSentenceAroundTheMatch_InPlainText_WithOffsetsIntoIt()
    {
        // Arrange
        var marked = Marked("Chegou calma. Relata [sono] ruim e [sonhos] ruins! Depois falou do trabalho.");

        // Act
        var (snippet, highlights) = RecordSearch.Snippet(marked);

        // Assert
        snippet.Should().Be("Relata sono ruim e sonhos ruins!");
        highlights.Select(h => snippet[h.Start..h.End]).Should().Equal("sono", "sonhos");
    }

    [Fact]
    public void Snippet_PrefersTheSentenceWithMoreMatches()
    {
        // Arrange
        var marked = Marked("Falou do [sono].\nDepois, do [sono] e da [escola]. Fim.");

        // Act
        var (snippet, highlights) = RecordSearch.Snippet(marked);

        // Assert
        snippet.Should().Be("Depois, do sono e da escola.");
        highlights.Should().HaveCount(2);
    }

    [Fact]
    public void Snippet_ALongSentence_IsCutAroundTheMatch_AndOffsetsStayRight()
    {
        // Arrange
        var filler = string.Join(' ', Enumerable.Repeat("palavra", 200));
        var marked = Marked($"{filler} [sono] {filler}");

        // Act
        var (snippet, highlights) = RecordSearch.Snippet(marked);

        // Assert
        snippet.Length.Should().BeLessThanOrEqualTo(RecordSearch.MaxSnippetLength + 2);
        snippet.Should().StartWith("…").And.EndWith("…");
        highlights.Select(h => snippet[h.Start..h.End]).Should().Equal("sono");
    }

    [Fact]
    public void Snippet_AnUnbalancedMark_DoesNotThrow()
    {
        // Act
        var (snippet, highlights) = RecordSearch.Snippet($"um {RecordSearch.MarkEnd}texto{RecordSearch.MarkStart} solto");

        // Assert
        snippet.Should().Be("um texto solto");
        highlights.Should().BeEmpty();
    }

    [Fact]
    public void Opening_IsTheFirstSentenceOfTheSection()
    {
        // Act
        var opening = RecordSearch.Opening("  Primeira frase. Segunda frase.");

        // Assert
        opening.Should().Be("Primeira frase.");
    }
}
