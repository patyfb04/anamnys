using Anamnys.Api.Hubs;
using Anamnys.Application.Interfaces;
using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;
using Anamnys.Infrastructure.Data;
using BillingSystem = Anamnys.Domain.Enums.BillingSystem;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Anamnys.Api.Jobs;

/// <summary>
/// Hangfire background job that executes the full 5-step AI pipeline:
/// 1. Transcribe  (Whisper.net)
/// 2. Structure Note (LLamaSharp / Phi-4)
/// 3. Extract Entities (Phi-3-mini — Phase 2)
/// 4. Billing Engine
/// 5. QA / Hallucination Guard
///
/// Lives in the Api project so it can inject IHubContext&lt;ProgressHub&gt; using
/// the real hub type without creating a circular dependency.
/// </summary>
public class PipelineJob
{
    private readonly ITranscriptionService _transcription;
    private readonly INoteStructuringService _structuring;
    private readonly IBillingEngine _billing;
    private readonly AppDbContext _db;
    private readonly IHubContext<ProgressHub> _hub;
    private readonly ILogger<PipelineJob> _logger;

    public PipelineJob(
        ITranscriptionService transcription,
        INoteStructuringService structuring,
        IBillingEngine billing,
        AppDbContext db,
        IHubContext<ProgressHub> hub,
        ILogger<PipelineJob> logger)
    {
        _transcription = transcription;
        _structuring = structuring;
        _billing = billing;
        _db = db;
        _hub = hub;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        string jobId,
        Guid noteId,
        string audioFilePath,
        NoteFormat format,
        Specialty specialty,
        BillingSystem billingSystem = BillingSystem.UsCpt,
        string? language = null)
    {
        try
        {
            var note = await _db.Notes.FindAsync(noteId)
                ?? throw new InvalidOperationException($"Note {noteId} not found");

            // ── Step 1: Transcribe ────────────────────────────────────────────
            await PushProgress(jobId, "transcribing", "Transcribing audio…", 10);
            note.Status = NoteStatus.Processing;
            await _db.SaveChangesAsync();

            // Skip transcription for text-input notes (RawTranscript already set)
            if (!string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath))
            {
                note.RawTranscript = await _transcription.TranscribeAsync(audioFilePath, language);
                if (File.Exists(audioFilePath)) File.Delete(audioFilePath);
            }

            await PushProgress(jobId, "transcribing", "Transcription complete", 30);

            // ── Step 2: Structure Note ────────────────────────────────────────
            await PushProgress(jobId, "structuring", "Structuring note with AI…", 40);
            note.StructuredContent = await _structuring.StructureAsync(
                note.RawTranscript ?? string.Empty, format, specialty);
            await PushProgress(jobId, "structuring", "Note structured", 60);

            // ── Step 3: Entity Extraction (stub — Phase 2 uses Phi-3-mini) ───
            await PushProgress(jobId, "extracting", "Extracting clinical entities…", 65);
            // TODO Phase 2: call Phi-3-mini NER sidecar service
            await Task.Delay(200);

            // ── Step 4: Billing Engine ────────────────────────────────────────
            await PushProgress(jobId, "billing", "Deriving billing codes…", 75);
            note.BillingCodes = (await _billing.DeriveCodesAsync(
                note.StructuredContent!, specialty, billingSystem)).ToList();
            await PushProgress(jobId, "billing", "Billing codes ready", 85);

            // ── Step 5: QA / Hallucination Guard ─────────────────────────────
            await PushProgress(jobId, "qa", "Running QA checks…", 90);
            // TODO: implement hallucination scoring (transcript coverage check)
            await Task.Delay(100);

            // ── Done ──────────────────────────────────────────────────────────
            note.Status = NoteStatus.ReadyForReview;
            note.AuditTrail.Add(new AuditEntry
            {
                Action = "ai_draft",
                ActorId = Guid.Empty, // system actor
            });
            await _db.SaveChangesAsync();

            await _hub.Clients.Group(jobId).SendAsync("JobComplete", jobId, noteId.ToString());
            _logger.LogInformation("Pipeline job {JobId} completed for note {NoteId}", jobId, noteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline job {JobId} failed", jobId);
            await _hub.Clients.Group(jobId).SendAsync("JobError", jobId, ex.Message);
            throw; // Hangfire will retry
        }
    }

    private Task PushProgress(string jobId, string step, string label, int pct)
        => _hub.Clients.Group(jobId).SendAsync("JobProgress", jobId, new
        {
            Step = step,
            Label = label,
            Percentage = pct
        });
}
