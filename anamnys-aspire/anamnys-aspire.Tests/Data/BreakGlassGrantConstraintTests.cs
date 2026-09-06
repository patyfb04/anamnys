using Aspire.Hosting;
using Aspire.Hosting.Testing;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

public class BreakGlassGrantConstraintTests
{
    [Fact]
    public async Task Insert_WhenAuthorizedByEqualsStaffId_IsRejectedByDatabase()
    {
        // Arrange
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.anamnys_aspire_AppHost>(TestContext.Current.CancellationToken);
        await using var app = await appHost.BuildAsync(TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        healthTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        await app.ResourceNotifications.WaitForResourceHealthyAsync("server", healthTimeout.Token);

        var connectionString = await app.GetConnectionStringAsync("anamnysdb", TestContext.Current.CancellationToken);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var sameId = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO "BreakGlassGrants"
                ("StaffId", "ProviderId", "TicketRef", "Reason", "AuthorizedBy", "ExpiresAt")
            VALUES (@id, @provider, 'TICKET-1', 'self-authorised', @id, now() + interval '1 hour')
            """, connection);
        command.Parameters.AddWithValue("id", sameId);
        command.Parameters.AddWithValue("provider", Guid.NewGuid());

        // Act
        var act = async () => await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // Assert
        var exception = await act.Should().ThrowAsync<PostgresException>();
        exception.Which.ConstraintName.Should().Be("BreakGlassGrants_TwoPerson_ck");
    }
}
