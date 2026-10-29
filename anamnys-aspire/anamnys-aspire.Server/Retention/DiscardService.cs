using System.Globalization;
using System.Text;
using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Retention;

public enum DiscardOutcome { Ok, NotFound, NotExpired, FutureAppointment, NameMismatch, AlreadyDiscarded, StorageUnavailable }

public sealed record DiscardResult(DiscardOutcome Outcome, Guid? TermId = null, IReadOnlyDictionary<string, int>? Counts = null);

// The irreversible discard of an expired record (design/specs/2026-10-28-retention-panel-design.md §1, §3). One
// transaction deletes every row of the patient's clinical content, stores the disposal term (PDF first, so a store
// failure rolls everything back), keeps the patient row with the name and DiscardedAt, and audits ids and counts.
// The stored objects of the deleted rows go after the commit, with retries; a failure is logged by key only.
public static class DiscardService
{
    public const string AuditAction = "patient_discarded";
    public const int DeleteAttempts = 3;

    public static async Task<DiscardResult> DiscardAsync(
        AnamnysDbContext db, INoteFileStore store, ILogger logger, Guid providerId, Guid patientId, string? confirmName,
        DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var today = PatientSearchQuery.PracticeToday(now);
        var termId = Guid.NewGuid();
        var termKey = DisposalTerms.ObjectKeyFor(providerId, termId);
        // Shared by the attempts of this call: the execution strategy may retry after a commit whose outcome it
        // could not see, so the keys of the deleted objects and whether a commit was tried outlive one attempt.
        List<string> objectKeys = [];
        var stored = false;
        var committing = false;

        var strategy = db.Database.CreateExecutionStrategy();
        DiscardResult result;
        try
        {
            result = await strategy.ExecuteAsync(ct => AttemptAsync(ct), ct);
        }
        catch when (stored && !committing)
        {
            // Failed after the upload and before any commit: the term PDF has no row and is removed. Once a commit
            // was tried the row may exist, so the PDF is never deleted from here.
            await DeleteWithRetriesAsync(store, logger, [termKey]);
            throw;
        }

        if (result.Outcome == DiscardOutcome.Ok)
        {
            await DeleteWithRetriesAsync(store, logger, objectKeys);
        }
        else if (stored && !committing)
        {
            // An earlier attempt uploaded the term, a later one refused (the record changed in between).
            await DeleteWithRetriesAsync(store, logger, [termKey]);
        }
        return result;

        async Task<DiscardResult> AttemptAsync(CancellationToken ct)
        {
            db.ChangeTracker.Clear();
            // Disposing without a commit rolls back: every early return below leaves the record untouched.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Same lock order as the clock (provider advisory lock, then patient rows), so a discard and a concurrent
            // push or recompute of the same provider never wait on each other in a cycle.
            await RetentionClock.LockAsync(db, providerId, ct);
            // FOR UPDATE also holds back every new row that references the patient until the discard commits.
            var locked = await db.Database
                .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Patients" WHERE "Id" = {patientId} AND "ProviderId" = {providerId} FOR UPDATE""")
                .ToListAsync(ct);
            if (locked.Count == 0)
            {
                return new DiscardResult(DiscardOutcome.NotFound);
            }
            await RetentionClock.RecomputeAsync(db, ct, providerId, patientId);
            var patient = await db.Patients.SingleAsync(p => p.Id == patientId, ct);
            if (patient.DiscardedAt is not null)
            {
                // This call's own earlier attempt committed: report it, and the object deletes still run.
                if (committing && await db.DisposalTerms.AsNoTracking().SingleOrDefaultAsync(t => t.Id == termId, ct) is { } mine)
                {
                    return new DiscardResult(DiscardOutcome.Ok, termId, mine.Counts);
                }
                return new DiscardResult(DiscardOutcome.AlreadyDiscarded);
            }
            if (patient.RetentionUntil is not { } until || until >= today)
            {
                return new DiscardResult(DiscardOutcome.NotExpired);
            }
            var future = await db.Appointments.AnyAsync(a => a.ProviderId == providerId && a.PatientId == patientId && a.StartsAt > nowUtc
                && (a.Status == AppointmentStatus.Scheduled || a.Status == AppointmentStatus.Confirmed), ct);
            if (future)
            {
                return new DiscardResult(DiscardOutcome.FutureAppointment);
            }
            if (!NameMatches(confirmName, patient.FirstName, patient.LastName))
            {
                return new DiscardResult(DiscardOutcome.NameMismatch);
            }

            var rule = await db.RetentionRules.AsNoTracking()
                .SingleOrDefaultAsync(r => r.ProviderId == providerId && r.RecordKind == RetentionRule.Prontuario, ct);
            var period = await RetentionClock.PeriodAsync(db, providerId, patientId, ct);
            objectKeys = await ObjectKeysAsync(db, providerId, patientId, ct);
            var counts = await DeleteContentAsync(db, providerId, patientId, patient.AccountId, ct);

            var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
            var term = new DisposalTerm
            {
                Id = termId,
                ProviderId = providerId,
                PatientId = patientId,
                CreatedAt = nowUtc,
                ObjectKey = termKey,
                PeriodFrom = period.From ?? patient.LastRecordAt!.Value,
                PeriodTo = period.To ?? patient.LastRecordAt!.Value,
                GuardYears = rule?.GuardYears ?? RetentionRule.DefaultGuardYears,
                Basis = rule?.Basis ?? RetentionRule.DefaultBasis,
                Counts = counts,
                RetentionUntil = until,
            };
            var pdf = DisposalTermPdf.Render(DisposalTermPdfModel.From(term, $"{patient.FirstName} {patient.LastName}", provider));
            try
            {
                await store.PutAsync(termKey, pdf, "application/pdf", ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new DiscardResult(DiscardOutcome.StorageUnavailable);
            }
            stored = true;

            db.DisposalTerms.Add(term);
            patient.DiscardedAt = nowUtc;
            patient.ArchivedAt ??= nowUtc;
            patient.UpdatedAt = nowUtc;
            patient.AccountId = null;
            patient.DateOfBirth = null;
            patient.ContactEmail = null;
            patient.Phone = null;
            patient.PreferredLanguage = null;
            patient.LastVisit = null;
            patient.LastRecordAt = null;
            patient.RetentionUntil = null;
            db.AuditEntries.Add(new AuditEntry
            {
                Id = Guid.NewGuid(),
                ActorId = providerId,
                Action = AuditAction,
                FieldChanged = "patient",
                NewValue = $"{patientId} {termId} " + string.Join(",", counts.Select(c => $"{c.Key}={c.Value}")),
                At = nowUtc,
            });
            // The term is already in the store: a caller cancellation must not leave it without its row.
            await db.SaveChangesAsync(CancellationToken.None);
            committing = true;
            await tx.CommitAsync(CancellationToken.None);
            return new DiscardResult(DiscardOutcome.Ok, termId, counts);
        }
    }

    // Trimmed, inner spaces collapsed, case- and accent-insensitive.
    public static bool NameMatches(string? typed, string firstName, string lastName) =>
        !string.IsNullOrWhiteSpace(typed) && Normalize(typed) == Normalize($"{firstName} {lastName}");

    private static string Normalize(string value)
    {
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(collapsed.Length);
        foreach (var c in collapsed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    // Stored objects of the rows about to be deleted: note and document PDFs, external documents, dossiers.
    private static Task<List<string>> ObjectKeysAsync(AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct) =>
        db.Database.SqlQuery<string>($"""
            SELECT "PdfObjectKey" AS "Value" FROM "Notes" WHERE "PatientId" = {patientId} AND "ProviderId" = {providerId} AND "PdfObjectKey" IS NOT NULL
            UNION
            SELECT "PdfObjectKey" FROM "ClinicalDocuments" WHERE "PatientId" = {patientId} AND "ProviderId" = {providerId} AND "PdfObjectKey" IS NOT NULL
            UNION
            SELECT "ObjectKey" FROM "ExternalDocuments" WHERE "PatientId" = {patientId} AND "ProviderId" = {providerId} AND "ObjectKey" IS NOT NULL
            UNION
            SELECT "ObjectKey" FROM "Dossiers" WHERE "PatientId" = {patientId} AND "ObjectKey" IS NOT NULL
            """).ToListAsync(ct);

    // Children first, so no RESTRICT foreign key stops a parent; each count goes into the term. Rows that only
    // cascade (billing codes, conformity checks, share accesses, consent events, denials, speech markers, audio
    // destruction logs, appointment confirmations and tokens, reminders, room sessions) go with their parent.
    private static async Task<Dictionary<string, int>> DeleteContentAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid? accountId, CancellationToken ct)
    {
        var p = patientId;
        var v = providerId;
        var counts = new Dictionary<string, int>();
        async Task Run(string kind, FormattableString sql) =>
            counts[kind] = counts.GetValueOrDefault(kind) + await db.Database.ExecuteSqlAsync(sql, ct);

        await Run("notifications", $"""
            DELETE FROM "Notifications" WHERE "ProviderId" = {v} AND (
                ("SubjectType" = 'patient' AND "SubjectId" = {p})
                OR ("SubjectType" = 'note' AND "SubjectId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v}))
                OR ("SubjectType" = 'appointment' AND "SubjectId" IN (SELECT "Id" FROM "Appointments" WHERE "PatientId" = {p} AND "ProviderId" = {v})))
            """);
        await Run("excerptRefs", $"""
            DELETE FROM "ExcerptRefs" WHERE "ProviderId" = {v} AND (
                ("SourceType" = 'note' AND "SourceId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v}))
                OR ("SourceType" = 'document' AND "SourceId" IN (SELECT "Id" FROM "ExternalDocuments" WHERE "PatientId" = {p} AND "ProviderId" = {v}))
                OR ("SourceType" = 'transcript' AND ("SourceId" IN (SELECT "Id" FROM "Sessions" WHERE "PatientId" = {p} AND "ProviderId" = {v})
                    OR "SourceId" IN (SELECT t."Id" FROM "Transcripts" t JOIN "Sessions" s ON s."Id" = t."SessionId" WHERE s."PatientId" = {p} AND s."ProviderId" = {v}))))
            """);
        await Run("patientQuotes", $"""DELETE FROM "PatientQuotes" WHERE "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v})""");
        await Run("themeTags", $"""DELETE FROM "ThemeTags" WHERE "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v})""");
        await Run("followUpItems", $"""
            DELETE FROM "FollowUpItems" WHERE "PatientId" = {p}
                OR "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v})
                OR "ResolvedNoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v})
            """);
        await Run("tissGuides", $"""
            DELETE FROM "TissGuides" WHERE "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v})
                OR "SessionId" IN (SELECT "Id" FROM "Sessions" WHERE "PatientId" = {p} AND "ProviderId" = {v})
            """);
        // The per-note and per-document trail holds clinical text (edited sections, generated content) and its
        // RESTRICT keys would block the delete; the discard itself is audited below with ids and counts only.
        await Run("auditEntries", $"""
            DELETE FROM "AuditEntries" WHERE "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v})
                OR "DocumentId" IN (SELECT "Id" FROM "ClinicalDocuments" WHERE "PatientId" = {p} AND "ProviderId" = {v})
            """);
        await Run("transcripts", $"""
            DELETE FROM "Transcripts" WHERE "SessionId" IN (SELECT "Id" FROM "Sessions" WHERE "PatientId" = {p} AND "ProviderId" = {v})
                OR "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v})
            """);
        await Run("noteSections", $"""DELETE FROM "NoteSections" WHERE "NoteId" IN (SELECT "Id" FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v})""");
        await Run("shareLinks", $"""DELETE FROM "ShareLinks" WHERE "DocumentId" IN (SELECT "Id" FROM "ClinicalDocuments" WHERE "PatientId" = {p} AND "ProviderId" = {v})""");
        await Run("disclosureAuthorizations", $"""
            DELETE FROM "DisclosureAuthorizations" WHERE "PatientId" = {p}
                OR "DocumentId" IN (SELECT "Id" FROM "ClinicalDocuments" WHERE "PatientId" = {p} AND "ProviderId" = {v})
            """);
        await Run("documentHashes", $"""DELETE FROM "DocumentHashes" WHERE "DocumentId" IN (SELECT "Id" FROM "ClinicalDocuments" WHERE "PatientId" = {p} AND "ProviderId" = {v})""");
        await Run("clinicalDocuments", $"""DELETE FROM "ClinicalDocuments" WHERE "PatientId" = {p} AND "ProviderId" = {v}""");
        await Run("notes", $"""DELETE FROM "Notes" WHERE "PatientId" = {p} AND "ProviderId" = {v}""");
        await Run("medications", $"""DELETE FROM "MedicationEntries" WHERE "PatientId" = {p}""");
        await Run("extractedFacts", $"""DELETE FROM "ExtractedFacts" WHERE "ExternalDocumentId" IN (SELECT "Id" FROM "ExternalDocuments" WHERE "PatientId" = {p} AND "ProviderId" = {v})""");
        await Run("externalDocumentPages", $"""DELETE FROM "ExternalDocumentPages" WHERE "ExternalDocumentId" IN (SELECT "Id" FROM "ExternalDocuments" WHERE "PatientId" = {p} AND "ProviderId" = {v})""");
        await Run("externalDocuments", $"""DELETE FROM "ExternalDocuments" WHERE "PatientId" = {p} AND "ProviderId" = {v}""");
        await Run("scaleApplications", $"""DELETE FROM "ScaleApplications" WHERE "PatientId" = {p}""");
        await Run("diagnoses", $"""DELETE FROM "PatientDiagnoses" WHERE "PatientId" = {p}""");
        await Run("planObjectives", $"""DELETE FROM "PlanObjectives" WHERE "PlanId" IN (SELECT "Id" FROM "TreatmentPlans" WHERE "PatientId" = {p})""");
        await Run("recordingConsents", $"""DELETE FROM "RecordingConsents" WHERE "PatientId" = {p}""");
        await Run("treatmentPlans", $"""DELETE FROM "TreatmentPlans" WHERE "PatientId" = {p}""");
        await Run("intakeResponses", $"""DELETE FROM "IntakeResponses" WHERE "PatientId" = {p}""");
        await Run("dossiers", $"""DELETE FROM "Dossiers" WHERE "PatientId" = {p}""");
        await Run("insurerAuthorizations", $"""DELETE FROM "InsurerAuthorizations" WHERE "PatientId" = {p}""");
        await Run("bookingHolds", $"""DELETE FROM "BookingHolds" WHERE "PatientId" = {p}""");
        await Run("appointments", $"""DELETE FROM "Appointments" WHERE "PatientId" = {p} AND "ProviderId" = {v}""");
        await Run("appointmentSeries", $"""DELETE FROM "AppointmentSeries" WHERE "PatientId" = {p} AND "ProviderId" = {v}""");
        await Run("sessions", $"""DELETE FROM "Sessions" WHERE "PatientId" = {p} AND "ProviderId" = {v}""");
        await Run("invitations", $"""DELETE FROM "PatientInvitations" WHERE "PatientId" = {p}""");
        // The portal login goes with the record when no other provider's record uses it; otherwise only the link goes.
        await db.Database.ExecuteSqlAsync($"""UPDATE "Patients" SET "AccountId" = NULL WHERE "Id" = {p} AND "ProviderId" = {v}""", ct);
        await Run("portalAccounts", $"""
            DELETE FROM "PatientAccounts" a
             WHERE a."Id" = {accountId} AND NOT EXISTS (SELECT 1 FROM "Patients" o WHERE o."AccountId" = a."Id")
            """);
        return counts;
    }

    private static async Task DeleteWithRetriesAsync(INoteFileStore store, ILogger logger, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await store.DeleteAsync(key, CancellationToken.None);
                    break;
                }
                catch (Exception) when (attempt < DeleteAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
                }
                catch (Exception e)
                {
                    logger.LogWarning("Discard: object delete failed for key {Key} ({ErrorType}).", key, e.GetType().Name);
                    break;
                }
            }
        }
    }
}
