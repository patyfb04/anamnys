using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notifications;

public sealed record BookingPolicyDto(string AutoCancelMode, int AutoCancelHours);

public sealed record BookingPolicyRequest(string? AutoCancelMode, int? AutoCancelHours)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (AutoCancelMode is null || !AutoCancelModes.All.Contains(AutoCancelMode))
        {
            errors["autoCancelMode"] = ["Escolha uma regra válida."];
        }
        if (AutoCancelHours is null or < 1 or > ConfirmationRules.MaxHours)
        {
            errors["autoCancelHours"] = [$"Use entre 1 e {ConfirmationRules.MaxHours} horas."];
        }
        return errors;
    }
}

// Per-provider automatic cancellation rule; providers without a row get the defaults.
public static class BookingPolicies
{
    public static async Task<BookingPolicyDto> GetAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct) =>
        await db.BookingPolicies.AsNoTracking()
            .Where(p => p.ProviderId == providerId)
            .Select(p => new BookingPolicyDto(p.AutoCancelMode, p.AutoCancelHours))
            .SingleOrDefaultAsync(ct)
        ?? new BookingPolicyDto(ConfirmationRules.DefaultMode, ConfirmationRules.DefaultHours);

    // The caller validates the request first.
    public static async Task SaveAsync(AnamnysDbContext db, Guid providerId, BookingPolicyRequest request, CancellationToken ct)
    {
        var mode = request.AutoCancelMode!;
        var hours = request.AutoCancelHours!.Value;
        var updated = await db.BookingPolicies
            .Where(p => p.ProviderId == providerId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.AutoCancelMode, mode).SetProperty(p => p.AutoCancelHours, hours), ct);
        if (updated > 0)
        {
            return;
        }
        db.BookingPolicies.Add(new BookingPolicy { Id = Guid.NewGuid(), ProviderId = providerId, AutoCancelMode = mode, AutoCancelHours = hours });
        await db.SaveChangesAsync(ct);
    }
}
