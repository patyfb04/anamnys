using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Tests.Patients;

// Seeds one fresh provider per test: the provider's own Guid isolates every test's rows
// from every other test's (and from the dev data) without any cleanup. Raw SQL rather
// than EF because the Appointment and Note entities map only a column subset — the
// tables have NOT NULL columns (EndsAt, InputMode) that EF does not know about.
public sealed class PatientSearchSeed : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly string _connectionString;

    private PatientSearchSeed(NpgsqlConnection connection, string connectionString)
    {
        _connection = connection;
        _connectionString = connectionString;
    }

    public Guid ProviderId { get; private set; }

    public static async Task<PatientSearchSeed> CreateAsync(SharedAppHostFixture fixture, CancellationToken cancellationToken)
    {
        var connectionString = await fixture.GetConnectionStringAsync("anamnysdb", cancellationToken)
            ?? throw new InvalidOperationException("No connection string for anamnysdb.");
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var seed = new PatientSearchSeed(connection, connectionString);
        seed.ProviderId = await seed.AddProviderAsync(cancellationToken);
        return seed;
    }

    // The original string, not _connection.ConnectionString: Npgsql drops the password
    // from the latter once the connection is open.
    public AnamnysDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AnamnysDbContext>().UseNpgsql(_connectionString).Options);

    public async Task<Guid> AddProviderAsync(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """INSERT INTO "Providers" ("Id", "Email", "ExternalSubject", "Name") VALUES (@id, @email, @sub, 'Test Provider')""",
            cancellationToken, ("id", id), ("email", $"{id:N}@test.local"), ("sub", Guid.NewGuid()));
        return id;
    }

    public async Task<Guid> AddPatientAsync(
        string firstName,
        string lastName,
        string? email = null,
        DateTimeOffset? lastVisit = null,
        DateTimeOffset? archivedAt = null,
        Guid? providerId = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "Patients" ("Id", "ProviderId", "FirstName", "LastName", "ContactEmail", "LastVisit", "ArchivedAt")
            VALUES (@id, @provider, @first, @last, @email, @lastVisit, @archivedAt)
            """,
            cancellationToken,
            ("id", id),
            ("provider", providerId ?? ProviderId),
            ("first", firstName),
            ("last", lastName),
            ("email", (object?)email ?? DBNull.Value),
            ("lastVisit", (object?)lastVisit?.ToUniversalTime() ?? DBNull.Value),
            ("archivedAt", (object?)archivedAt?.ToUniversalTime() ?? DBNull.Value));
        return id;
    }

    // What a bound patient-portal login looks like on the row (see Patient.cs).
    // Creates a portal account and links it to the record; returns the account id.
    public async Task<Guid> BindPortalAccountAsync(Guid patientId, CancellationToken cancellationToken, bool disabled = false)
    {
        var accountId = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "PatientAccounts" ("Id", "ExternalSubject", "Email", "FirstName", "LastName", "DisabledAt")
            VALUES (@account, @sub, @email, 'Portal', 'User', CASE WHEN @disabled THEN now() END);
            UPDATE "Patients" SET "AccountId" = @account WHERE "Id" = @id;
            """,
            cancellationToken,
            ("account", accountId), ("sub", Guid.NewGuid()), ("email", $"{accountId:N}@portal.test"),
            ("disabled", disabled), ("id", patientId));
        return accountId;
    }

    public async Task<Guid> AddAccountAsync(CancellationToken cancellationToken, string? email = null)
    {
        var accountId = Guid.NewGuid();
        await ExecuteAsync(
            """INSERT INTO "PatientAccounts" ("Id", "ExternalSubject", "Email", "FirstName", "LastName") VALUES (@id, @sub, @email, 'Portal', 'User')""",
            cancellationToken, ("id", accountId), ("sub", Guid.NewGuid()), ("email", email ?? $"{accountId:N}@portal.test"));
        return accountId;
    }

    public Task LinkAccountAsync(Guid patientId, Guid accountId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """UPDATE "Patients" SET "AccountId" = @account WHERE "Id" = @id""",
            cancellationToken, ("account", accountId), ("id", patientId));

    public Task ExecuteSqlAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) =>
        ExecuteAsync(sql, cancellationToken, parameters);

    public Task AddAppointmentAsync(
        Guid patientId,
        DateTimeOffset startsAt,
        string status = "scheduled",
        Guid? providerId = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            """
            INSERT INTO "Appointments" ("ProviderId", "PatientId", "StartsAt", "EndsAt", "Status", "CancelledAt")
            VALUES (@provider, @patient, @starts, @starts + interval '50 minutes', @status,
                    CASE WHEN @status = 'cancelled' THEN now() END)
            """,
            cancellationToken,
            ("provider", providerId ?? ProviderId),
            ("patient", patientId),
            ("starts", startsAt.ToUniversalTime()),
            ("status", status));

    public Task AddNoteAsync(
        Guid patientId,
        string status,
        DateTimeOffset createdAt,
        Guid? providerId = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            """
            INSERT INTO "Notes" ("ProviderId", "PatientId", "Status", "InputMode", "SignedAt", "CreatedAt")
            VALUES (@provider, @patient, @status, 'Text',
                    CASE WHEN @status IN ('Signed', 'Exported') THEN @created END, @created)
            """,
            cancellationToken,
            ("provider", providerId ?? ProviderId),
            ("patient", patientId),
            ("status", status),
            ("created", createdAt.ToUniversalTime()));

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, _connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
