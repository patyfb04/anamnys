using Anamnys.Server.Data;
using Anamnys.Server.Notes;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Pseudonymization;

public sealed record BackfillStatus(int Pending, bool Running, int Total, int Done, int Failed, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

// Backfill of legacy transcripts (design/specs/2026-11-17-pseudonymization-design.md §6). Progress lives in
// PseudonymizationBackfills, so a running backfill survives a restart.
public static class PseudonymizationBackfills
{
    public const string StartedAction = "pseudonymization_backfill_started";

    // The provider's notes the backfill would convert: a non-empty transcript not yet through the pseudonymiser.
    internal static IQueryable<Data.Entities.Note> Pending(AnamnysDbContext db, Guid providerId) =>
        db.Notes.Where(n => n.ProviderId == providerId && n.RawTranscript != null && n.RawTranscript != "" && !n.TranscriptPseudonymized);

    public static async Task<BackfillStatus> StatusAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var pending = await Pending(db, providerId).CountAsync(ct);
        var row = await db.PseudonymizationBackfills.AsNoTracking().SingleOrDefaultAsync(b => b.ProviderId == providerId, ct);
        return row is null
            ? new BackfillStatus(pending, false, 0, 0, 0, null, null)
            : new BackfillStatus(pending, row.FinishedAt is null, row.Total, row.Done, row.Failed, row.StartedAt, row.FinishedAt);
    }

    // False when this provider's backfill is already running. The row is (re)started in one statement, so two starts at once
    // cannot both win; the audit entry (counts only) commits with it.
    public static async Task<bool> TryStartAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        var started = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var total = await Pending(db, providerId).CountAsync(ct);
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "PseudonymizationBackfills" ("ProviderId", "StartedAt", "FinishedAt", "Total", "Done", "Failed")
                VALUES ({providerId}, {now}, NULL, {total}, 0, 0)
                ON CONFLICT ("ProviderId") DO UPDATE
                   SET "StartedAt" = EXCLUDED."StartedAt", "FinishedAt" = NULL, "Total" = EXCLUDED."Total", "Done" = 0, "Failed" = 0
                 WHERE "PseudonymizationBackfills"."FinishedAt" IS NOT NULL
                """, ct);
            started = affected > 0;
            if (!started)
            {
                return;
            }
            db.AuditEntries.Add(new Data.Entities.AuditEntry
            {
                Id = Guid.NewGuid(), ActorId = providerId, Action = StartedAction, NewValue = $"total={total}", At = now,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return started;
    }
}

public static class BackfillEndpoints
{
    public static void MapBackfillEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapGet("providers/me/pseudonymization/backfill", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (await Patients.PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await PseudonymizationBackfills.StatusAsync(db, providerId, ct));
        });

        // 202 started (the body is the status); 409 already running.
        phi.MapPost("providers/me/pseudonymization/backfill", async (HttpContext http, AnamnysDbContext db, BackfillWorker worker,
            TimeProvider clock, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (await Patients.PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            if (!await PseudonymizationBackfills.TryStartAsync(db, providerId, clock.GetUtcNow(), ct))
            {
                return Results.Conflict(new { message = "A pseudonimização das transcrições antigas já está em andamento.", code = "already_running" });
            }
            worker.Wake();
            return Results.Json(await PseudonymizationBackfills.StatusAsync(db, providerId, ct), statusCode: StatusCodes.Status202Accepted);
        });
    }
}

// Converts legacy transcripts one note at a time through the same pseudonymiser as live work, with a detector at
// LlmPriority.Background (it only gets the model when no live work waits). Only RawTranscript and TranscriptPseudonymized
// are written, in one statement guarded by the note still being unconverted: sections, seals and signed content are never
// touched. A note whose detection fails stays unconverted, counts in Failed and is retried on the next start.
// Logs carry ids and exception type names only.
public sealed class BackfillWorker(IServiceScopeFactory scopes, Pseudonymizer pseudonymizer, TimeProvider clock, ILogger<BackfillWorker> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    // How often a running backfill is looked for when nobody woke the worker (a restart resumes it at startup).
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPendingAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Pseudonymization backfill failed: {ExceptionType}", e.GetType().Name);
            }
            try
            {
                await _wake.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Every running backfill, one provider after the other, until each has no note left to try.
    public async Task RunPendingAsync(CancellationToken ct)
    {
        List<Guid> providers;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            providers = await db.PseudonymizationBackfills.AsNoTracking()
                .Where(b => b.FinishedAt == null).OrderBy(b => b.StartedAt).Select(b => b.ProviderId).ToListAsync(ct);
        }
        foreach (var providerId in providers)
        {
            await RunProviderAsync(providerId, ct);
        }
    }

    private async Task RunProviderAsync(Guid providerId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();

        // A run (or a resumed one) starts from what is left: earlier failures are pending again and recounted if they fail again.
        var pending = await PseudonymizationBackfills.Pending(db, providerId).CountAsync(ct);
        await db.PseudonymizationBackfills.Where(b => b.ProviderId == providerId && b.FinishedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Failed, 0).SetProperty(b => b.Total, b => b.Done + pending), ct);

        // The cursor moves past every note tried, so a failed (or concurrently changed) note is not tried twice in a run.
        var last = Guid.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            var cursor = last;
            var next = await PseudonymizationBackfills.Pending(db, providerId).AsNoTracking()
                .Where(n => n.Id > cursor).OrderBy(n => n.Id)
                .Select(n => new { n.Id, n.PatientId, Text = n.RawTranscript! })
                .FirstOrDefaultAsync(ct);
            if (next is null)
            {
                break;
            }
            last = next.Id;
            await ConvertAsync(db, providerId, next.Id, next.PatientId, next.Text, ct);
        }

        await db.PseudonymizationBackfills.Where(b => b.ProviderId == providerId && b.FinishedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.FinishedAt, clock.GetUtcNow()), ct);
    }

    private async Task ConvertAsync(AnamnysDbContext db, Guid providerId, Guid noteId, Guid patientId, string text, CancellationToken ct)
    {
        PseudonymizationResult result;
        try
        {
            result = await pseudonymizer.PseudonymizeAsync(db, providerId, patientId, text, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Exception type only: a message could echo the text.
            logger.LogError("Pseudonymization backfill failed for note {NoteId}: {ExceptionType}", noteId, e.GetType().Name);
            db.ChangeTracker.Clear();
            await db.PseudonymizationBackfills.Where(b => b.ProviderId == providerId && b.FinishedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Failed, b => b.Failed + 1), CancellationToken.None);
            return;
        }

        var now = clock.GetUtcNow();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var converted = await db.Notes
                .Where(n => n.Id == noteId && n.ProviderId == providerId && !n.TranscriptPseudonymized && n.RawTranscript == text)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.RawTranscript, result.Text)
                    .SetProperty(n => n.TranscriptPseudonymized, true), ct) > 0;
            if (converted)
            {
                NoteAudit.Add(db, noteId, null, PseudonymizationStep.DoneAction, "transcript", null, result.AuditValue, now);
                await db.SaveChangesAsync(ct);
                await db.PseudonymizationBackfills.Where(b => b.ProviderId == providerId && b.FinishedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Done, b => b.Done + 1), ct);
            }
            else
            {
                // The note changed or was converted meanwhile: nothing to write, and it no longer counts in the total.
                await db.PseudonymizationBackfills.Where(b => b.ProviderId == providerId && b.FinishedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Total, b => b.Total - 1), ct);
            }
            await tx.CommitAsync(ct);
        });
    }
}
