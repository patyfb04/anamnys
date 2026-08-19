using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;
using BillingSystem = Anamnys.Domain.Enums.BillingSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Anamnys.Infrastructure.Data;

/// <summary>
/// Seeds a default provider account for local development.
/// Only runs when no providers exist — safe to call on every startup.
/// NEVER use in production.
/// </summary>
public static class DevDataSeeder
{
    public static async Task SeedAsync(AppDbContext db, ILogger logger)
    {
        if (await db.Providers.AnyAsync())
            return;

        var provider = new Provider
        {
            Id = Guid.NewGuid(),
            Email = "dev@clinicaldraft.local",
            Name = "Dev Provider",
            // Password: Dev1234!
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Dev1234!"),
            Specialty = Specialty.MentalHealth,
            PreferredNoteFormat = NoteFormat.DAP,
            BillingSystem = BillingSystem.UsCpt,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Providers.Add(provider);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Dev seed: created provider {Email} (password: Dev1234!)", provider.Email);
    }
}
