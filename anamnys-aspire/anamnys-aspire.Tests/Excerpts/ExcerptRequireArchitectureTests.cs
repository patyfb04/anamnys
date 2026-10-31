using FluentAssertions;

namespace Anamnys.Tests.Excerpts;

// Hard rule of F-03: classes of Anamnys.Server.Analysis (created by F-10) that query ThemeTags,
// PatientQuotes or ExtractedFacts must use the RequireExcerpt helper. A source scan: a query is the set
// accessed on the context (".Name") or the table in SQL ("\"Name\""), so a class merely named after a
// table does not count.
public class ExcerptRequireArchitectureTests
{
    private static readonly string[] Guarded = ["ThemeTags", "PatientQuotes", "ExtractedFacts"];

    private static IReadOnlyList<string> Violations(IEnumerable<(string Name, string Source)> files) =>
        files
            .Where(f => Guarded.Any(g => f.Source.Contains($".{g}", StringComparison.Ordinal) || f.Source.Contains($"\"{g}\"", StringComparison.Ordinal))
                        && !f.Source.Contains(".RequireExcerpt(", StringComparison.Ordinal))
            .Select(f => f.Name)
            .ToList();

    [Fact]
    public void AnalysisNamespace_EveryClassQueryingDerivedItems_UsesRequireExcerpt()
    {
        // Arrange
        var directory = ServerDirectory();
        var files = Directory.EnumerateFiles(Path.Combine(directory, "Analysis"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(directory, f), File.ReadAllText(f)))
            .ToList();

        // Act
        var violations = Violations(files);

        // Assert
        violations.Should().BeEmpty("analysis queries over ThemeTags, PatientQuotes and ExtractedFacts must call RequireExcerpt");
    }

    [Fact]
    public void Violations_ClassQueryingThemeTagsWithoutTheHelper_IsReported()
    {
        // Act
        var violations = Violations([("Analyses/Bad.cs", "db.ThemeTags.Where(t => t.RemovedAt == null)")]);

        // Assert
        violations.Should().Equal("Analyses/Bad.cs");
    }

    [Fact]
    public void Violations_ClassUsingTheHelperOrNotQueryingDerivedItems_IsAccepted()
    {
        // Act
        var violations = Violations([
            ("Analyses/Good.cs", "db.PatientQuotes.RequireExcerpt(q => q.ExcerptRefId)"),
            ("Analyses/Other.cs", "db.Notes.Where(n => n.Id == id)"),
            ("Analyses/Named.cs", "PatientQuotesQuery.ListAsync(db)"),
        ]);

        // Assert
        violations.Should().BeEmpty();
    }

    private static string ServerDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "anamnys-aspire.Server");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("anamnys-aspire.Server not found above the test output directory.");
    }
}
