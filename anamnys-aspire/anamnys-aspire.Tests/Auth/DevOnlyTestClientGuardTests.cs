using Anamnys.Server.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

public class DevOnlyTestClientGuardTests
{
    [Fact]
    public async Task EnsureNoDevOnlyTestClientsExistAsync_WhenDevelopment_ReturnsWithoutCallingKeycloak()
    {
        // Arrange — a base address nothing is listening on. If the guard ever
        // called out to it, this would throw a connection failure instead of
        // returning normally.
        using var client = new HttpClient { BaseAddress = new Uri("https://127.0.0.1:1/") };

        // Act
        var act = () => DevOnlyTestClientGuard.EnsureNoDevOnlyTestClientsExistAsync(
            isDevelopment: true,
            adminUsername: null,
            adminPassword: null,
            keycloakClient: client,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureNoDevOnlyTestClientsExistAsync_WhenAdminCredentialsMissingOutsideDevelopment_ThrowsAndFailsClosed()
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = new Uri("https://127.0.0.1:1/") };

        // Act
        var act = () => DevOnlyTestClientGuard.EnsureNoDevOnlyTestClientsExistAsync(
            isDevelopment: false,
            adminUsername: null,
            adminPassword: null,
            keycloakClient: client,
            TestContext.Current.CancellationToken);

        // Assert — the check cannot run without credentials, and that is
        // itself a startup failure, not a silently skipped pass.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

[Collection(SchemeIsolationCollection.Name)]
public class DevOnlyTestClientGuardIntegrationTests(SchemeIsolationFixture fixture)
{
    [Fact]
    public async Task EnsureNoDevOnlyTestClientsExistAsync_WhenTestClientsExistOutsideDevelopment_ThrowsNamingTheClient()
    {
        // Arrange — the real Keycloak instance this suite runs against does
        // have anamnys-test-provider in its realm, because the isolation
        // tests above need it. The guard is exercised here exactly as
        // Program.cs calls it, just with isDevelopment forced to false.
        using var client = InsecureKeycloakHttpClient.Create(fixture.KeycloakBaseAddress);

        // Act
        var act = () => DevOnlyTestClientGuard.EnsureNoDevOnlyTestClientsExistAsync(
            isDevelopment: false,
            fixture.KeycloakAdminUsername,
            fixture.KeycloakAdminPassword,
            client,
            TestContext.Current.CancellationToken);

        // Assert
        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain("anamnys-test-provider");
    }
}
