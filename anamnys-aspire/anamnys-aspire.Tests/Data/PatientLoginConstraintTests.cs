using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// Patients.Email is the portal login and exists only on a bound row. Without this
// constraint an unclaimed record carrying an email is exactly what a future "bind by
// email" step would attach to (design/specs/2026-10-01-patient-records-design.md §8).
[Collection(SharedAppHostCollection.Name)]
public class PatientLoginConstraintTests(SharedAppHostFixture fixture)
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Insert_WithLoginEmailAndSubjectNotTogether_IsRejectedByDatabase(bool withEmail, bool withSubject)
    {
        // Arrange
        var connectionString = await fixture.GetConnectionStringAsync("anamnysdb", TestContext.Current.CancellationToken);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO "Patients" ("Id", "FirstName", "LastName", "Email", "ExternalSubject")
            VALUES (@id, 'Ana', 'Silva', @email, @subject)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("email", withEmail ? $"{id:N}@test.local" : DBNull.Value);
        command.Parameters.AddWithValue("subject", withSubject ? Guid.NewGuid() : DBNull.Value);

        // Act
        var act = async () => await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // Assert
        var exception = await act.Should().ThrowAsync<PostgresException>();
        exception.Which.ConstraintName.Should().Be("Patients_Login_ck");
    }
}
