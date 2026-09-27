using System.Text.Json;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

public class RealmConfigTests
{
    private static readonly string[] StaffRoleNames = ["owner", "support", "ops"];

    private static string FindRealmsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "keycloak", "realms")))
            {
                return Path.Combine(directory.FullName, "keycloak", "realms");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find a keycloak/realms directory walking up from {AppContext.BaseDirectory}.");
    }

    private static IEnumerable<JsonDocument> LoadRealmDocuments()
    {
        var realmsDirectory = FindRealmsDirectory();
        foreach (var file in Directory.EnumerateFiles(realmsDirectory, "*.json"))
        {
            yield return JsonDocument.Parse(File.ReadAllText(file));
        }
    }

    [Fact]
    public void RealmJson_WhenRegistrationAllowed_UsesEmailAsUsername()
    {
        // Arrange
        using var documents = new DisposableList<JsonDocument>(LoadRealmDocuments());

        // Act
        var offenders = documents
            .Where(doc => doc.RootElement.TryGetProperty("registrationAllowed", out var allowed)
                && allowed.GetBoolean())
            .Where(doc => !doc.RootElement.TryGetProperty("registrationEmailAsUsername", out var emailAsUsername)
                || !emailAsUsername.GetBoolean())
            .Select(doc => doc.RootElement.GetProperty("realm").GetString())
            .ToList();

        // Assert
        offenders.Should().BeEmpty(
            "every realm with registrationAllowed must also set registrationEmailAsUsername, " +
            "or an anonymous registrant can take a reserved dev username in production");
    }

    [Fact]
    public void OwnersRealmJson_DefaultRoles_ContainNoStaffRole()
    {
        // Arrange
        var realmsDirectory = FindRealmsDirectory();
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(realmsDirectory, "anamnys-owners.json")));
        var root = document.RootElement;

        // Act
        var found = new List<string>();
        if (root.TryGetProperty("defaultRole", out var defaultRole)
            && defaultRole.TryGetProperty("composites", out var composites)
            && composites.TryGetProperty("realm", out var realmComposites))
        {
            found.AddRange(realmComposites.EnumerateArray().Select(r => r.GetString()!));
        }
        if (root.TryGetProperty("defaultRoles", out var defaultRoles))
        {
            found.AddRange(defaultRoles.EnumerateArray().Select(r => r.GetString()!));
        }
        if (root.TryGetProperty("defaultGroups", out var defaultGroups))
        {
            found.AddRange(defaultGroups.EnumerateArray().Select(r => r.GetString()!));
        }

        // Assert
        found.Should().NotContain(
            StaffRoleNames,
            "a staff role must never be granted by default membership in the owners realm; " +
            "it must be assigned explicitly per user");
    }

    private sealed class DisposableList<T>(IEnumerable<T> items) : List<T>(items), IDisposable
        where T : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in this)
            {
                item.Dispose();
            }
        }
    }
}
