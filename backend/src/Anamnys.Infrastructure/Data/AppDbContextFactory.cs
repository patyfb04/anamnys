using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Anamnys.Infrastructure.Data;

/// <summary>
/// Used by the EF Core CLI tools (dotnet ef migrations add / update) at design time.
/// Reads the connection string from appsettings.Development.json so you don't need
/// the full app running to generate or apply migrations.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Walk up from Infrastructure to the Api project to find appsettings
        var apiProjectPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "Anamnys.Api");

        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(
                Path.Combine(apiProjectPath, "appsettings.json"),
                optional: true)
            .AddJsonFile(
                Path.Combine(apiProjectPath, "appsettings.Development.json"),
                optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = config.GetConnectionString("Default")
            ?? "Host=localhost;Port=5432;Database=anamnys_dev;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new AppDbContext(optionsBuilder.Options);
    }
}
