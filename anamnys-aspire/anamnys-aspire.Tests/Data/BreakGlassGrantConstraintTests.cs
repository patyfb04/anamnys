using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

[Collection(SharedAppHostCollection.Name)]
public class BreakGlassGrantConstraintTests(SharedAppHostFixture fixture)
{
    [Fact]
    public async Task Insert_WhenAuthorizedByEqualsStaffId_IsRejectedByDatabase()
    {
        // Arrange
        var connectionString = await fixture.GetConnectionStringAsync("anamnysdb", TestContext.Current.CancellationToken);
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
