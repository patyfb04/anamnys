using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Data;

public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();

        var exists = await db.Database
            .SqlQuery<bool>($"SELECT to_regclass('public.\"Providers\"') IS NOT NULL AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (exists)
        {
            logger.LogInformation("Schema already present; skipping bootstrap.");
            return;
        }

        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream("anamnys-db-script.sql")
            ?? throw new InvalidOperationException("Embedded resource anamnys-db-script.sql not found.");

        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);

        logger.LogInformation("Bootstrapping schema from anamnys-db-script.sql.");

        // EF Core's ExecuteSqlRawAsync(string, CancellationToken) resolves to the
        // params-object[] overload, which treats the SQL as a composite format string
        // and throws FormatException on the literal "{}" JSONB defaults in the script.
        // Executing on the raw ADO.NET connection avoids that parsing entirely.
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
