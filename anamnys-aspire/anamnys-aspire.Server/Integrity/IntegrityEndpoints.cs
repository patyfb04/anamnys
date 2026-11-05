using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Integrity;

// design/specs/2026-11-03-integrity-chain-design.md §8. PHI group, scoped to the signed-in provider; unknown or foreign ids
// answer 404; every answer is no-store and carries ids, dates and results only. The handlers resolve the provider and
// delegate to the internal methods below, which the tests call directly.
public static class IntegrityEndpoints
{
    public const string ReportDownloadedAction = "integrity_report_downloaded";

    public static void AddIntegrity(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Integrity").Get<IntegrityOptions>() ?? new IntegrityOptions();
        if (IntegrityOptions.Problem(options, builder.Environment.IsDevelopment()) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        builder.Services.AddSingleton(options);
        builder.Services.AddHttpClient<ITimestampAuthority, HttpTimestampAuthority>(client => client.Timeout = TimeSpan.FromSeconds(30));
        // After DatabaseInitializer (registered first): the schema exists before the backfill reads it.
        builder.Services.AddHostedService<IntegrityBackfillService>();
    }

    public static void MapIntegrityEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapGet("notes/{noteId:guid}/integrity", async (
            Guid noteId, HttpContext http, AnamnysDbContext db, ITimestampAuthority tsa, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await SubjectAsync(http, db, tsa, providerId, IntegritySeal.Note, noteId, ct)
                : Results.Unauthorized());

        phi.MapGet("documents/{documentId:guid}/integrity", async (
            Guid documentId, HttpContext http, AnamnysDbContext db, ITimestampAuthority tsa, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await SubjectAsync(http, db, tsa, providerId, IntegritySeal.Document, documentId, ct)
                : Results.Unauthorized());

        phi.MapGet("providers/me/integrity", async (
            HttpContext http, AnamnysDbContext db, ITimestampAuthority tsa, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ChainAsync(http, db, tsa, providerId, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        phi.MapGet("providers/me/integrity/report.pdf", async (
            HttpContext http, AnamnysDbContext db, ITimestampAuthority tsa, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ReportAsync(http, db, tsa, providerId, clock.GetUtcNow(), ct)
                : Results.Unauthorized());
    }

    internal static async Task<IResult> SubjectAsync(
        HttpContext http, AnamnysDbContext db, ITimestampAuthority tsa, Guid providerId, string subjectType, Guid subjectId, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        return await IntegrityVerifier.VerifySubjectAsync(db, providerId, subjectType, subjectId, tsa, ct) is { } result
            ? Results.Ok(result)
            : Results.NotFound();
    }

    internal static async Task<IResult> ChainAsync(
        HttpContext http, AnamnysDbContext db, ITimestampAuthority tsa, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!await db.Providers.AsNoTracking().AnyAsync(p => p.Id == providerId, ct))
        {
            return Results.NotFound();
        }
        return Results.Ok(await IntegrityVerifier.VerifyChainAsync(db, providerId, tsa, now, ct));
    }

    // Audited as integrity_report_downloaded before the bytes are returned.
    internal static async Task<IResult> ReportAsync(
        HttpContext http, AnamnysDbContext db, ITimestampAuthority tsa, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var provider = await db.Providers.AsNoTracking()
            .Where(p => p.Id == providerId)
            .Select(p => new { p.Name, p.CrpNumber, p.CrpRegion })
            .SingleOrDefaultAsync(ct);
        if (provider is null)
        {
            return Results.NotFound();
        }
        var (chain, anchors, head) = await IntegrityVerifier.VerifyChainDetailedAsync(db, providerId, tsa, now, ct);
        var pdf = IntegrityReportPdf.Render(new IntegrityReportModel(
            provider.Name,
            provider.CrpNumber is null ? "" : $"{provider.CrpNumber}/{provider.CrpRegion}",
            now,
            chain,
            head?.Sequence,
            head?.Hash,
            anchors.Select(a => new IntegrityReportAnchor(a.Anchor.Sequence, a.Anchor.HeadHash, a.Anchor.TsaUrl, a.Anchor.AnchoredAt, a.Valid)).ToList()));

        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorId = providerId,
            Action = ReportDownloadedAction,
            FieldChanged = IntegrityJobSteps.Subject,
            NewValue = chain.FirstProblem is { } problem ? $"{chain.TotalSeals} {problem.Sequence} {problem.Kind}" : $"{chain.TotalSeals} intact",
            At = now.ToUniversalTime(),
        });
        await db.SaveChangesAsync(ct);

        http.Response.Headers.ContentDisposition = $"attachment; filename=\"comprovante-integridade-{now.ToUniversalTime():yyyy-MM-dd}.pdf\"";
        return Results.Bytes(pdf, "application/pdf");
    }
}
