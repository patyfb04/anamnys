using Anamnys.Api.Jobs;
using Anamnys.Application.Commands.TranscribeAudio;
using Anamnys.Application.DTOs;
using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;
using BillingSystem = Anamnys.Domain.Enums.BillingSystem;
using Anamnys.Infrastructure.Data;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Anamnys.Api.Controllers;

[ApiController]
[Route("api/transcribe")]
[Authorize]
public class TranscribeController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public TranscribeController(IMediator mediator, AppDbContext db, IWebHostEnvironment env)
    {
        _mediator = mediator;
        _db = db;
        _env = env;
    }

    /// <summary>
    /// Upload an audio file for batch transcription (Mode A).
    /// Returns a jobId immediately; pipeline runs in background via Hangfire.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
    public async Task<ActionResult<TranscribeJobDto>> SubmitAudio(
        [FromForm] Guid patientId,
        IFormFile audio,
        [FromForm] string? language = null)
    {
        var providerId = GetProviderId();

        // Determine note format, specialty, and billing system from provider's profile
        var provider = await _db.Providers.FindAsync(providerId);
        var format        = provider?.PreferredNoteFormat ?? NoteFormat.DAP;
        var specialty     = provider?.Specialty           ?? Specialty.MentalHealth;
        var billingSystem = provider?.BillingSystem       ?? BillingSystem.UsCpt;

        if (!Domain.SupportedLanguages.IsValid(language))
            return BadRequest(new { message = $"Unsupported language code '{language}'." });

        // Save audio to temp directory
        var tempDir = Path.Combine(_env.ContentRootPath, "temp");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid()}{Path.GetExtension(audio.FileName)}");
        using (var stream = System.IO.File.Create(tempPath))
            await audio.CopyToAsync(stream);

        // Create note record in DB
        var note = new Note
        {
            PatientId = patientId,
            ProviderId = providerId,
            Status = NoteStatus.Processing,
            InputMode = InputMode.VoiceBatch,
        };
        _db.Notes.Add(note);

        var patient = await TouchLastVisitAsync(patientId, providerId);
        await _db.SaveChangesAsync();

        // Explicit override wins; otherwise fall back to the patient's saved preference; both
        // resolve to auto-detect if unset. Skips a slower + less accurate auto-detect pass
        // when we already know the language.
        var resolvedLanguage = string.IsNullOrWhiteSpace(language) ? patient?.PreferredLanguage : language;

        // Enqueue pipeline job via Hangfire — passes billing system so the correct engine is used
        var jobId = BackgroundJob.Enqueue<PipelineJob>(job =>
            job.ExecuteAsync(Guid.NewGuid().ToString("N"), note.Id, tempPath, format, specialty, billingSystem, resolvedLanguage));

        return Accepted(new TranscribeJobDto(
            JobId: jobId,
            Status: "queued",
            Progress: new PipelineProgressDto("transcribing", "Queued", 0),
            NoteId: note.Id));
    }

    /// <summary>Submit typed text (Mode D — skips speech pipeline).</summary>
    [HttpPost("text")]
    public async Task<ActionResult<TranscribeJobDto>> SubmitText(
        [FromBody] TextSubmitRequest request)
    {
        var providerId = GetProviderId();
        var provider      = await _db.Providers.FindAsync(providerId);
        var format        = provider?.PreferredNoteFormat ?? NoteFormat.DAP;
        var specialty     = provider?.Specialty           ?? Specialty.MentalHealth;
        var billingSystem = provider?.BillingSystem       ?? BillingSystem.UsCpt;

        var note = new Note
        {
            PatientId = request.PatientId,
            ProviderId = providerId,
            Status = NoteStatus.Processing,
            InputMode = InputMode.Text,
            RawTranscript = request.Text,
        };
        _db.Notes.Add(note);

        await TouchLastVisitAsync(request.PatientId, providerId);
        await _db.SaveChangesAsync();

        // For text input, skip transcription; enqueue structure-only job
        // (reuse PipelineJob — it handles null audio path gracefully via pre-existing RawTranscript)
        var jobId = BackgroundJob.Enqueue<PipelineJob>(job =>
            job.ExecuteAsync(Guid.NewGuid().ToString("N"), note.Id, string.Empty, format, specialty, billingSystem));

        return Accepted(new TranscribeJobDto(jobId, "queued",
            new PipelineProgressDto("structuring", "Queued", 0), note.Id));
    }

    /// <summary>
    /// Lightweight job status poll endpoint.
    /// Mobile clients call this as a fallback when SignalR is unavailable (e.g. poor connectivity).
    /// In production this should query Hangfire's job storage for real state;
    /// real-time progress is pushed via ProgressHub SignalR events.
    /// </summary>
    [HttpGet("jobs/{jobId}")]
    public ActionResult<TranscribeJobDto> GetJobStatus(string jobId)
    {
        // TODO: query Hangfire.JobStorage.Current to return real job state
        return Ok(new TranscribeJobDto(jobId, "processing",
            new PipelineProgressDto("transcribing", "Processing", 20), null));
    }

    /// <summary>Extracts authenticated provider's ID from the JWT claim.</summary>
    private Guid GetProviderId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Stamps LastVisit on a patient's record when a note is submitted for them — the only
    /// signal in the app that a session actually happened. Scoped to the requesting provider
    /// so a stray/invalid patientId can't touch another provider's patient. Returns the patient
    /// so callers can also read its PreferredLanguage without a second query.
    /// </summary>
    private async Task<Patient?> TouchLastVisitAsync(Guid patientId, Guid providerId)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(
            p => p.Id == patientId && p.ProviderId == providerId);
        if (patient != null)
            patient.LastVisit = DateTimeOffset.UtcNow;
        return patient;
    }
}

public record TextSubmitRequest(Guid PatientId, string Text);
