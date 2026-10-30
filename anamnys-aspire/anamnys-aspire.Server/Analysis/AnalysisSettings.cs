using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Analysis;

public sealed record AnalysisSettingsDto(int AbsenceSessions);

public sealed record AnalysisSettingsRequest(int? AbsenceSessions)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (AbsenceSessions is null or < ThemeAbsence.MinSessions or > ThemeAbsence.MaxSessions)
        {
            errors["absenceSessions"] = [$"Use entre {ThemeAbsence.MinSessions} e {ThemeAbsence.MaxSessions} sessões."];
        }
        return errors;
    }
}

// The provider's "Análises" settings (design/specs/2026-10-29-theme-absence-design.md §3): the number of
// consecutive signed sessions without a record after which a repeated theme is listed as absent.
public static class AnalysisSettings
{
    public static async Task<int> AbsenceSessionsAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct) =>
        await db.Providers.AsNoTracking().Where(p => p.Id == providerId).Select(p => (int?)p.AbsenceSessions).SingleOrDefaultAsync(ct)
        ?? ThemeAbsence.DefaultSessions;

    public static async Task<AnalysisSettingsDto> GetAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct) =>
        new(await AbsenceSessionsAsync(db, providerId, ct));

    // The caller validates the request first.
    public static Task SaveAsync(AnamnysDbContext db, Guid providerId, AnalysisSettingsRequest request, CancellationToken ct)
    {
        var sessions = request.AbsenceSessions!.Value;
        return db.Providers.Where(p => p.Id == providerId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.AbsenceSessions, sessions), ct);
    }
}
