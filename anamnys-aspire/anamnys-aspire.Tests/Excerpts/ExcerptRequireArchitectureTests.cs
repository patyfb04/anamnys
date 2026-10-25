using FluentAssertions;

namespace Anamnys.Tests.Excerpts;

// Hard rule of F-03: classes of Anamnys.Server.Analyses (created by F-10) that query ThemeTags,
// PatientQuotes or ExtractedFacts must use the RequireExcerpt helper. A source scan, so it holds
// before the namespace exists and fails as soon as a class skips the helper.
public class ExcerptRequireArchitectureTests
{
    private static readonly string[] Guarded = ["ThemeTags", "PatientQuotes", "ExtractedFacts"];

    private static IReadOnlyList<string> Violations(IEnumerable<(string Name, string Source)> files) =>
        files
            .Where(f => Guarded.Any(g => f.Source.Contains(g, StringComparison.Ordinal))
                        && !f.Source.Contains(".RequireExcerpt(", StringComparison.Ordinal))
            .Select(f => f.Name)
            .ToList();

    [Fact]
    public void AnalysesNamespace_EveryClassQueryingDerivedItems_UsesRequireExcerpt()
    {
        // Arrange
        var directory = ServerDirectory();
        var analyses = Path.Combine(directory, "Analyses");
        var files = Directory.Exists(analyses)
            ? Directory.EnumerateFiles(analyses, "*.cs", SearchOption.AllDirectories)
                .Select(f => (Path.GetRelativePath(directory, f), File.ReadAllText(f))).ToList()
            : [];

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
