using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.LiveSession;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public sealed record CreateNoteRequest(Guid? PatientId, Guid? AppointmentId, string? Format, string? Mode, string? Text)
{
    public const int MaxText = 20_000;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (PatientId is null || PatientId == Guid.Empty)
        {
            errors["patientId"] = ["Escolha o paciente."];
        }
        if (Format is null || !LiveSessionRules.Formats.Contains(Format, StringComparer.Ordinal))
        {
            errors["format"] = ["Formato inválido."];
        }
        if (Mode is not ("ai" or "manual"))
        {
            errors["mode"] = ["Modo inválido."];
        }
        var text = Text?.Trim() ?? "";
        if (Mode == "ai" && text.Length == 0)
        {
            errors["text"] = ["Escreva as anotações para a IA organizar."];
        }
        else if (text.Length > MaxText)
        {
            errors["text"] = [$"Use no máximo {MaxText} caracteres."];
        }
        return errors;
    }
}

public enum CreateNoteOutcome { Created, NotFound, Archived, AppointmentUnavailable }

public sealed record CreateNoteResult(CreateNoteOutcome Outcome, Guid? NoteId);

public sealed record NoteCandidate(Guid AppointmentId, DateTimeOffset StartsAt, string Modality, string Status);

// Typed notes (design/specs/2026-10-15-note-editor-design.md §4.3). Call Validate first.
public static class NoteCreation
{
    public static readonly TimeSpan CandidateWindow = TimeSpan.FromDays(14);

    // One rule for what a typed note may link to, shared by the candidate list and the create check.
    private static Expression<Func<Appointment, bool>> IsCandidate(DateTimeOffset nowUtc)
    {
        var from = nowUtc - CandidateWindow;
        return a => a.StartsAt >= from && a.StartsAt <= nowUtc
            && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow;
    }

    public static async Task<CreateNoteResult> CreateAsync(
        AnamnysDbContext db, TranscriptionQueue queue, Guid providerId, CreateNoteRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var patientId = request.PatientId!.Value;
        var format = request.Format!;
        var ai = request.Mode == "ai";
        var text = request.Text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

        var result = new CreateNoteResult(CreateNoteOutcome.NotFound, null);
        var isCandidate = IsCandidate(now.ToUniversalTime()).Compile();
        // The request is valid from here: writes ignore the caller's token so a client disconnect cannot
        // commit a pending AI note while skipping the enqueue below (same as LiveRecordings.FinalizeAsync).
        var write = CancellationToken.None;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            result = new CreateNoteResult(CreateNoteOutcome.NotFound, null);
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(write);

            var patient = await db.Patients.AsNoTracking()
                .Where(p => p.Id == patientId && p.ProviderId == providerId)
                .Select(p => new { p.ArchivedAt })
                .SingleOrDefaultAsync(write);
            if (patient is null)
            {
                return;
            }
            if (patient.ArchivedAt is not null)
            {
                result = new CreateNoteResult(CreateNoteOutcome.Archived, null);
                return;
            }

            Session? session = null;
            if (request.AppointmentId is { } appointmentId)
            {
                var appointment = await NoteSessions.LockAppointmentAsync(db, providerId, patientId, appointmentId, write);
                if (appointment is null)
                {
                    return;
                }
                if (!isCandidate(appointment) || !await NoteSessions.IsAvailableForNoteAsync(db, appointment, write))
                {
                    result = new CreateNoteResult(CreateNoteOutcome.AppointmentUnavailable, null);
                    return;
                }
                session = await NoteSessions.AttendAsync(db, providerId, patientId, appointment, null, null, now, write);
            }

            var note = new Note
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                PatientId = patientId,
                SessionId = session?.Id,
                Status = "ReadyForReview",
                InputMode = "Text",
                Format = format,
                RawTranscript = text,
                StructureStatus = ai ? "pending" : null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Notes.Add(note);
            await db.SaveChangesAsync(write);
            NoteAudit.Add(db, note.Id, providerId, "note_created", null, null, $"Text,{format}", now);
            if (!ai)
            {
                NoteEditing.AddBlankSections(db, note, providerId, now);
            }
            await db.SaveChangesAsync(write);
            await tx.CommitAsync(write);
            result = new CreateNoteResult(CreateNoteOutcome.Created, note.Id);
        });

        if (ai && result.Outcome == CreateNoteOutcome.Created)
        {
            queue.Enqueue(new StructureJob(result.NoteId!.Value, providerId, format));
        }
        return result;
    }

    public static async Task<IReadOnlyList<NoteCandidate>?> CandidatesAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, ct))
        {
            return null;
        }
        return await db.Appointments.AsNoTracking()
            .Where(a => a.ProviderId == providerId && a.PatientId == patientId)
            .Where(IsCandidate(now.ToUniversalTime()))
            .Where(a => !(a.SessionId != null && db.Notes.Any(n => n.SessionId == a.SessionId)))
            .OrderByDescending(a => a.StartsAt)
            .Select(a => new NoteCandidate(a.Id, a.StartsAt, a.Modality, a.Status))
            .ToListAsync(ct);
    }
}
