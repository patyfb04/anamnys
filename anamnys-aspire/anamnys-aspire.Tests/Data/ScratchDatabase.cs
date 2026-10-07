using Npgsql;

namespace Anamnys.Tests.Data;

// A database created for one test from the pre-split bootstrap script and dropped after.
internal sealed class ScratchDatabase(string adminConnectionString, string name) : IAsyncDisposable
{
    public static async Task<ScratchDatabase> CreateAsync(SharedAppHostFixture fixture, CancellationToken ct, string schemaFile = "Data/Fixtures/schema-2026-10-02.sql")
    {
        var admin = await fixture.GetConnectionStringAsync("anamnysdb", ct)
            ?? throw new InvalidOperationException("No connection string for anamnysdb.");
        var name = $"mig_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync(ct);
        }

        var scratch = new ScratchDatabase(admin, name);
        await using var db = await scratch.OpenAsync(ct);
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, schemaFile), ct);
        await using var load = new NpgsqlCommand(schema, db);
        await load.ExecuteNonQueryAsync(ct);
        return scratch;
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name, Pooling = false }.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
