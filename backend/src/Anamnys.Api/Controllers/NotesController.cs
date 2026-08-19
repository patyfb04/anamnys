using Anamnys.Api.Pdf;
using Anamnys.Application.DTOs;
using Anamnys.Application.Interfaces;
using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;
using Anamnys.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using System.Security.Claims;

namespace Anamnys.Api.Controllers;

[ApiController]
[Route("api/notes")]
[Authorize]
public class NotesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IStorageService _storage;

    public NotesController(AppDbContext db, IStorageService storage)
    {
        _db      = db;
        _storage = storage;
    }

    /// <summary>
    /// Returns all notes for a given patient, scoped to the authenticated provider.
    /// The double filter (patientId + providerId) enforces that a provider can only
    /// access their own patients — a core HIPAA minimum-necessary access requirement.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NoteDto>>> List([FromQuery] Guid patientId)
    {
        var providerId = GetProviderId();
        var notes = await _db.Notes
            .Where(n => n.PatientId == patientId && n.ProviderId == providerId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();

        return Ok(notes.Select(ToDto).ToList());
    }

    /// <summary>
    /// Returns a single note by ID, again scoped to the requesting provider.
    /// Returns 404 (not 403) when the note exists but belongs to another provider
    /// — avoids leaking the existence of records the caller has no access to.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NoteDto>> Get(Guid id)
    {
        var providerId = GetProviderId();
        var note = await _db.Notes.FirstOrDefaultAsync(
            n => n.Id == id && n.ProviderId == providerId);
        return note == null ? NotFound() : Ok(ToDto(note));
    }

    /// <summary>
    /// Allows the provider to edit AI-generated note sections before signing.
    /// Blocked once the note is Signed or Exported (immutability requirement for HIPAA audit trail).
    /// Every edit appends a "provider_edit" entry to the audit trail with the actor ID and timestamp.
    /// </summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<NoteDto>> Update(Guid id, [FromBody] NotePatchRequest patch)
    {
        var providerId = GetProviderId();
        var note = await _db.Notes.FirstOrDefaultAsync(
            n => n.Id == id && n.ProviderId == providerId);
        if (note == null) return NotFound();

        // Enforce immutability: signed/exported notes cannot be altered
        if (note.Status == NoteStatus.Signed || note.Status == NoteStatus.Exported)
            return BadRequest(new { message = "Cannot edit a signed note." });

        if (patch.StructuredContent != null)
        {
            note.StructuredContent ??= new StructuredNote();
            if (patch.StructuredContent.Sections != null)
                note.StructuredContent.Sections = patch.StructuredContent.Sections;

            // Record who edited and when — required for HIPAA audit trail
            note.AuditTrail.Add(new AuditEntry
            {
                Action = "provider_edit",
                ActorId = providerId,
            });
        }

        await _db.SaveChangesAsync();
        return Ok(ToDto(note));
    }

    /// <summary>
    /// Locks the note with an electronic signature timestamp.
    /// Only notes in ReadyForReview status can be signed — enforces the AI-draft → review → sign
    /// workflow and prevents signing of incomplete or still-processing notes.
    /// SignedAt is stored in UTC and included in the exported PDF as the legal signature timestamp.
    /// </summary>
    [HttpPost("{id:guid}/sign")]
    public async Task<ActionResult<NoteDto>> Sign(Guid id)
    {
        var providerId = GetProviderId();
        var note = await _db.Notes.FirstOrDefaultAsync(
            n => n.Id == id && n.ProviderId == providerId);
        if (note == null) return NotFound();

        if (note.Status == NoteStatus.Signed || note.Status == NoteStatus.Exported)
            return BadRequest(new { message = "Note is already signed." });

        if (note.Status != NoteStatus.ReadyForReview)
            return BadRequest(new { message = "Note must be ready for review before signing." });

        note.Status = NoteStatus.Signed;
        note.SignedAt = DateTimeOffset.UtcNow;
        note.AuditTrail.Add(new AuditEntry
        {
            Action = "signed",
            ActorId = providerId,
        });

        await _db.SaveChangesAsync();
        return Ok(ToDto(note));
    }

    [HttpPost("{id:guid}/export")]
    public async Task<ActionResult<object>> Export(Guid id, CancellationToken ct)
    {
        var providerId = GetProviderId();

        // Load note with patient + provider for PDF rendering
        var note = await _db.Notes
            .Include(n => n.Patient)
            .FirstOrDefaultAsync(n => n.Id == id && n.ProviderId == providerId, ct);
        if (note == null) return NotFound();

        if (note.Status != NoteStatus.Signed)
            return BadRequest(new { message = "Only signed notes can be exported." });

        var patient = note.Patient
            ?? await _db.Patients.FindAsync([note.PatientId], ct);
        if (patient == null) return NotFound(new { message = "Patient not found." });

        var provider = await _db.Providers.FindAsync([providerId], ct);
        if (provider == null) return NotFound(new { message = "Provider not found." });

        // ── Generate PDF ─────────────────────────────────────────────────────
        QuestPDF.Settings.License = LicenseType.Community;

        var pdfDoc  = new NotePdfDocument(note, patient, provider);
        var pdfBytes = pdfDoc.GeneratePdf();

        // ── Upload to storage ────────────────────────────────────────────────
        var key = $"exports/{providerId}/{id}/{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.pdf";
        using var stream = new MemoryStream(pdfBytes);
        await _storage.UploadAsync(stream, key, "application/pdf", ct);

        // Presigned URL valid for 1 hour
        var url = await _storage.GetPresignedUrlAsync(key, TimeSpan.FromHours(1), ct);

        // ── Persist status ───────────────────────────────────────────────────
        note.Status = NoteStatus.Exported;
        note.AuditTrail.Add(new AuditEntry { Action = "exported", ActorId = providerId });
        await _db.SaveChangesAsync(ct);

        return Ok(new { url, key, expiresInSeconds = 3600 });
    }

    /// <summary>
    /// Extracts the authenticated provider's ID from the JWT NameIdentifier claim.
    /// Used by every action to scope queries — no action should query notes without this filter.
    /// </summary>
    private Guid GetProviderId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Maps the Note domain entity to a NoteDto for API responses.
    /// Keeps domain logic out of the response shape and avoids over-exposing internal fields.
    /// </summary>
    private static NoteDto ToDto(Note n) => new(
        n.Id, n.PatientId, n.ProviderId,
        n.Status.ToString().ToLowerInvariant().Replace("readyforreview", "ready_for_review"),
        n.InputMode.ToString().ToLowerInvariant(),
        n.RawTranscript,
        n.StructuredContent == null ? null : new StructuredNoteDto(
            n.StructuredContent.Format.ToString(),
            n.StructuredContent.Sections,
            n.StructuredContent.GeneratedAt),
        n.BillingCodes.Select(b => new BillingCodeDto(
            b.CptCode, b.Icd10Code, b.Description,
            b.ConfidenceScore, b.DenialRiskScore,
            b.Modifiers, b.MissingDocumentation)).ToList(),
        n.PriorAuthLetter,
        n.AuditTrail.Select(a => new AuditEntryDto(
            a.Timestamp, a.Action, a.FieldChanged,
            a.PreviousValue, a.NewValue, a.ActorId)).ToList(),
        n.CreatedAt,
        n.SignedAt
    );
}

public record NotePatchRequest(StructuredNotePatch? StructuredContent);
public record StructuredNotePatch(Dictionary<string, string>? Sections);
