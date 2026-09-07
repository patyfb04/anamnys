using Anamnys.Server.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

public class DevOnlyTestClientGuardTests
{
    [Fact]
    public async Task EnsureNoDevOnlyTestClientsExistAsync_WhenDevelopment_ReturnsWithoutCallingKeycloak()
    {
        // Arrange — nulls for every other parameter. If the guard did
        // anything but return immediately, it would throw before ever
        // reaching a real check.
        // Act
        var act = () => DevOnlyTestClientGuard.EnsureNoDevOnlyTestClientsExistAsync(
            isDevelopment: true,
            adminUsername: null,
            adminPassword: null,
            keycloakAdminBaseAddress: null,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureNoDevOnlyTestClientsExistAsync_WhenAdminCredentialsMissingOutsideDevelopment_ThrowsAndFailsClosed()
    {
        // Arrange
        // Act
        var act = () => DevOnlyTestClientGuard.EnsureNoDevOnlyTestClientsExistAsync(
            isDevelopment: false,
            adminUsername: null,
            adminPassword: null,
            keycloakAdminBaseAddress: null,
            TestContext.Current.CancellationToken);

        // Assert — the check cannot run without credentials, and that is
        // itself a startup failure, not a silently skipped pass.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task EnsureNoDevOnlyTestClientsExistAsync_WhenBaseAddressMissingOutsideDevelopment_ThrowsNamingTheSetting()
    {
        // Arrange — admin credentials present, but no base address configured.
        // This is the failure mode a non-Aspire deployment hits if
        // KEYCLOAK_ADMIN_BASE_ADDRESS is never set: it must fail with a
        // message naming the missing setting, not an opaque connection error
        // from whatever the previous default (an Aspire service-discovery
        // pseudo-scheme) would have thrown.
        // Act
        var act = () => DevOnlyTestClientGuard.EnsureNoDevOnlyTestClientsExistAsync(
            isDevelopment: false,
            adminUsername: "admin",
            adminPassword: "irrelevant",
            keycloakAdminBaseAddress: null,
            TestContext.Current.CancellationToken);

        // Assert
        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Message.Should().Contain("KEYCLOAK_ADMIN_BASE_ADDRESS");
    }
}
