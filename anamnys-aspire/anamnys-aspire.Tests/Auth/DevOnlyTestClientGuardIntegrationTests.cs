using Anamnys.Server.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

[Collection(SharedAppHostCollection.Name)]
public class DevOnlyTestClientGuardIntegrationTests(SharedAppHostFixture fixture)
{
    [Fact]
    public async Task EnsureNoDevOnlyTestClientsExistAsync_WhenTestClientsExistOutsideDevelopment_ThrowsNamingTheClient()
    {
        // Arrange — the real Keycloak instance this suite runs against does
        // have anamnys-test-provider in its realm, because the isolation
        // tests above need it. The guard is exercised here exactly as
        // Program.cs calls it, just with isDevelopment forced to false.

        // Act
        var act = () => DevOnlyTestClientGuard.EnsureNoDevOnlyTestClientsExistAsync(
            isDevelopment: false,
            fixture.KeycloakAdminUsername,
            fixture.KeycloakAdminPassword,
            fixture.KeycloakBaseAddress.ToString(),
            TestContext.Current.CancellationToken);

        // Assert
        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain("anamnys-test-provider");
    }
}
