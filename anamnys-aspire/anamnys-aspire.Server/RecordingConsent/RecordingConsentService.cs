using System.Text.Json;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Email;
using Anamnys.Server.Patients;
using Anamnys.Server.Retention;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.RecordingConsents;

public enum ConsentOutcome
{
    Ok, NotFound, ProfileIncomplete, PatientEmailMissing, PatientBirthDateMissing, PatientArchived, ConsentActive, InvalidState,
    NotOutdated, OutdatedVersion, NotMinor, InvalidDate, InvalidNote, EmailNotConfigured, SendFailed, StorageUnavailable,
}

public sealed record ConsentResult(ConsentOutcome Outcome, ConsentView? View = null);

public sealed record ConsentDocument(ConsentOutcome Outcome, byte[]? Content = null, string? FileName = null);

// Gone: every dead, unknown, used or expired link (one answer for all). Invalid: the body is incomplete (Errors).
public enum PublicConsentOutcome { Ok, Gone, Invalid, BirthDateMismatch, StorageUnavailable }

public sealed record PublicConsentResult(PublicConsentOutcome Outcome, IReadOnlyList<string>? Errors = null);

// The consent lifecycle (design/specs/2026-11-15-recording-consent-design.md §3–§5). Every provider query filters by the
// provider; the public calls find the consent by the SHA-256 of the link's token only. Reading a consent applies its due
// expiries (persisted, under a row lock). Audit values are ids and codes; no names, dates of birth, tokens or text.
public static class RecordingConsentService
{
    public const string SubjectType = "recording_consent";
    public const string SignedKind = "consent_signed";
    public const string RevokedKind = "consent_revoked";

    public const string SentAction = "consent_sent";
    public const string SignedAction = "consent_signed";
    public const string RevokedAction = "consent_revoked";
    public const string ExpiredAction = "consent_expired";
    public const string AssentAction = "consent_assent_recorded";
    public const string DocumentDownloadedAction = "consent_document_downloaded";
    public const string DictationAction ="dictation_confirmed";
    public const string AuditField = "recording_consent";

    public const string ActorProvider = "provider";
    public const string ActorSigner = "signer";
    public const string ActorSystem = "system";

    public const int MaxProviderNote = 1000;
    public const int MaxSignerName = 200;
    public const int MaxRelationship = 100;
    public const int MaxAssentNote = 500;
    public const int MaxRevokeReason = 500;

    private const string ActiveKey = "RecordingConsents_Patient_active_key";

    // ---- Reading -------------------------------------------------------------------------------------------------

    // The patient's current consent (the active one, else the latest), after its due expiries; null when the patient
    // is not the provider's (or was discarded) or never had one.
    public static async Task<RecordingConsent?> GetEffectiveAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        var patient = await PatientAsync(db, providerId, patientId, ct);
        return patient is null ? null : await CurrentAsync(db, providerId, patient, now, ct);
    }

    public static async Task<ConsentView?> GetViewAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        var patient = await PatientAsync(db, providerId, patientId, ct);
        if (patient is null)
        {
            return null;
        }
        var consent = await CurrentAsync(db, providerId, patient, now, ct);
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
        return await ViewOfAsync(db, consent, patient, provider, now, ct);
    }

    public static async Task<ConsentPreview?> PreviewAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        var patient = await PatientAsync(db, providerId, patientId, ct);
        if (patient is null)
        {
            return null;
        }
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
        var text = ConsentTerm.Render(ConsentTerm.CurrentVersion, TermData(patient, provider, null, now));
        return new ConsentPreview(ConsentTerm.CurrentVersion, text, SendBlockedBy(provider, patient));
    }

    // ---- Provider actions ----------------------------------------------------------------------------------------

    public static async Task<ConsentResult> SendAsync(
        AnamnysDbContext db, IEmailSender? sender, Uri portalBaseUrl, Guid providerId, Guid patientId, string? providerNote,
        DateTimeOffset now, CancellationToken ct)
    {
        var note = Clean(providerNote);
        if (note is { Length: > MaxProviderNote })
        {
            return new ConsentResult(ConsentOutcome.InvalidNote);
        }
        var patient = await PatientAsync(db, providerId, patientId, ct);
        if (patient is null)
        {
            return new ConsentResult(ConsentOutcome.NotFound);
        }
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
        if (Blocked(SendBlockedBy(provider, patient)) is { } blocked)
        {
            return new ConsentResult(blocked);
        }
        var current = await CurrentAsync(db, providerId, patient, now, ct);
        if (current?.State is ConsentRules.Sent or ConsentRules.Signed)
        {
            return new ConsentResult(ConsentOutcome.ConsentActive);
        }
        if (sender is null)
        {
            return new ConsentResult(ConsentOutcome.EmailNotConfigured);
        }

        var consentId = Guid.NewGuid();
        var token = ConsentTokens.NewToken();
        var outcome = await WriteThenSendAsync(db, sender, async ct =>
        {
            var p = await PatientAsync(db, providerId, patientId, ct);
            if (p is null)
            {
                return (ConsentOutcome.NotFound, null);
            }
            var consent = NewConsent(consentId, p, provider, note, token, now);
            db.RecordingConsents.Add(consent);
            AddEvent(db, consent.Id, ConsentRules.NotRequested, ConsentRules.Sent, ActorProvider, providerId, null, now);
            AddAudit(db, providerId, SentAction, $"{consent.Id} sent", now);
            if (await SaveAsync(db, ct) is { } refused)
            {
                return (refused, null);
            }
            return (ConsentOutcome.Ok, RequestMessage(p, provider, token, consent.SignTokenExpiresAt!.Value, portalBaseUrl, now));
        }, ct);
        return outcome == ConsentOutcome.Ok
            ? new ConsentResult(ConsentOutcome.Ok, await GetViewAsync(db, providerId, patientId, now, ct))
            : new ConsentResult(outcome);
    }

    // A SENT consent or one whose link expired: a new link (the old one stops working) and a new e-mail.
    public static async Task<ConsentResult> ResendAsync(
        AnamnysDbContext db, IEmailSender? sender, Uri portalBaseUrl, Guid providerId, Guid consentId, DateTimeOffset now,
        CancellationToken ct)
    {
        var (consent, patient, provider) = await LoadAsync(db, providerId, consentId, now, ct);
        if (consent is null)
        {
            return new ConsentResult(ConsentOutcome.NotFound);
        }
        if (!ConsentRules.CanResend(consent))
        {
            return new ConsentResult(ConsentOutcome.InvalidState);
        }
        // A consent of an older term version is never resent as that version: send a new term (or renew) instead.
        if (consent.TemplateVersion != ConsentTerm.CurrentVersion)
        {
            return new ConsentResult(ConsentOutcome.OutdatedVersion);
        }
        if (Blocked(SendBlockedBy(provider!, patient!)) is { } blocked)
        {
            return new ConsentResult(blocked);
        }
        if (sender is null)
        {
            return new ConsentResult(ConsentOutcome.EmailNotConfigured);
        }

        var token = ConsentTokens.NewToken();
        var outcome = await WriteThenSendAsync(db, sender, async ct =>
        {
            var locked = await LockAsync(db, consentId, ct);
            var p = await PatientAsync(db, providerId, consent.PatientId, ct);
            if (locked is null || p is null || locked.ProviderId != providerId)
            {
                return (ConsentOutcome.NotFound, null);
            }
            if (!ConsentRules.CanResend(locked) || ConsentRules.DueExpiry(locked, p, now) == ConsentRules.ExpiredDischarge)
            {
                return (ConsentOutcome.InvalidState, null);
            }
            if (locked.TemplateVersion != ConsentTerm.CurrentVersion)
            {
                return (ConsentOutcome.OutdatedVersion, null);
            }
            var from = locked.State;
            locked.State = ConsentRules.Sent;
            locked.ExpiredReason = null;
            locked.IssuedAt = now;
            locked.SignTokenHash = ConsentTokens.Hash(token);
            locked.SignTokenExpiresAt = now + ConsentRules.LinkValidity;
            locked.FailedBirthDateCount = 0;
            locked.TermText = ConsentTerm.Render(locked.TemplateVersion ?? ConsentTerm.CurrentVersion, TermData(p, provider!, locked.ProviderNote, now));
            locked.TermSha256 = ConsentTerm.Sha256(locked.TermText);
            locked.UpdatedAt = now;
            AddEvent(db, locked.Id, from, ConsentRules.Sent, ActorProvider, providerId, "resent", now);
            AddAudit(db, providerId, SentAction, $"{locked.Id} resend", now);
            if (await SaveAsync(db, ct) is { } refused)
            {
                return (refused, null);
            }
            return (ConsentOutcome.Ok, RequestMessage(p, provider!, token, locked.SignTokenExpiresAt.Value, portalBaseUrl, now));
        }, ct);
        return outcome == ConsentOutcome.Ok
            ? new ConsentResult(ConsentOutcome.Ok, await GetViewAsync(db, providerId, consent.PatientId, now, ct))
            : new ConsentResult(outcome);
    }

    // "Pedir de novo" after a new term version: the old consent expires (new_version) and a new one is sent, all or nothing.
    public static async Task<ConsentResult> RenewAsync(
        AnamnysDbContext db, IEmailSender? sender, Uri portalBaseUrl, Guid providerId, Guid consentId, DateTimeOffset now,
        CancellationToken ct)
    {
        var (consent, patient, provider) = await LoadAsync(db, providerId, consentId, now, ct);
        if (consent is null)
        {
            return new ConsentResult(ConsentOutcome.NotFound);
        }
        if (consent.State is not (ConsentRules.Sent or ConsentRules.Signed))
        {
            return new ConsentResult(ConsentOutcome.InvalidState);
        }
        if (!ConsentRules.CanRenew(consent))
        {
            return new ConsentResult(ConsentOutcome.NotOutdated);
        }
        if (Blocked(SendBlockedBy(provider!, patient!)) is { } blocked)
        {
            return new ConsentResult(blocked);
        }
        if (sender is null)
        {
            return new ConsentResult(ConsentOutcome.EmailNotConfigured);
        }

        var newId = Guid.NewGuid();
        var token = ConsentTokens.NewToken();
        var outcome = await WriteThenSendAsync(db, sender, async ct =>
        {
            var old = await LockAsync(db, consentId, ct);
            var p = await PatientAsync(db, providerId, consent.PatientId, ct);
            if (old is null || p is null || old.ProviderId != providerId)
            {
                return (ConsentOutcome.NotFound, null);
            }
            if (!ConsentRules.CanRenew(old))
            {
                return (ConsentOutcome.InvalidState, null);
            }
            MarkExpired(db, old, ConsentRules.ExpiredNewVersion, ActorProvider, providerId, now);
            if (await SaveAsync(db, ct) is { } refusedOld)
            {
                return (refusedOld, null);
            }
            var renewed = NewConsent(newId, p, provider!, old.ProviderNote, token, now);
            db.RecordingConsents.Add(renewed);
            AddEvent(db, renewed.Id, ConsentRules.NotRequested, ConsentRules.Sent, ActorProvider, providerId, ConsentRules.ExpiredNewVersion, now);
            AddAudit(db, providerId, SentAction, $"{renewed.Id} renew {old.Id}", now);
            if (await SaveAsync(db, ct) is { } refused)
            {
                return (refused, null);
            }
            return (ConsentOutcome.Ok, RequestMessage(p, provider!, token, renewed.SignTokenExpiresAt!.Value, portalBaseUrl, now));
        }, ct);
        return outcome == ConsentOutcome.Ok
            ? new ConsentResult(ConsentOutcome.Ok, await GetViewAsync(db, providerId, consent.PatientId, now, ct))
            : new ConsentResult(outcome);
    }

    // The minor's assent, recorded by the provider in session: a day from the consent's request to today, and an optional
    // note.
    public static async Task<ConsentResult> RecordAssentAsync(
        AnamnysDbContext db, Guid providerId, Guid consentId, ConsentAssentRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        var today = PatientSearchQuery.PracticeToday(now);
        if (body?.Date is not { } date || date > today)
        {
            return new ConsentResult(ConsentOutcome.InvalidDate);
        }
        var note = Clean(body.Note);
        if (note is { Length: > MaxAssentNote })
        {
            return new ConsentResult(ConsentOutcome.InvalidNote);
        }
        var (consent, patient, _) = await LoadAsync(db, providerId, consentId, now, ct);
        if (consent is null)
        {
            return new ConsentResult(ConsentOutcome.NotFound);
        }
        if (!ConsentRules.CanRecordAssent(consent))
        {
            return new ConsentResult(ConsentOutcome.InvalidState);
        }
        if (!ConsentRules.IsMinor(patient!, now))
        {
            return new ConsentResult(ConsentOutcome.NotMinor);
        }
        // The assent belongs to this consent: not before the day it was first requested.
        if (date < PatientSearchQuery.PracticeToday(consent.CreatedAt))
        {
            return new ConsentResult(ConsentOutcome.InvalidDate);
        }

        var outcome = await db.InTransactionAsync(async ct =>
        {
            var locked = await LockAsync(db, consentId, ct);
            if (locked is null || locked.ProviderId != providerId)
            {
                return ConsentOutcome.NotFound;
            }
            if (!ConsentRules.CanRecordAssent(locked))
            {
                return ConsentOutcome.InvalidState;
            }
            locked.AssentRecordedAt = ConsentRules.PracticeMidnight(date);
            locked.AssentNote = note;
            locked.UpdatedAt = now;
            AddEvent(db, locked.Id, locked.State, locked.State, ActorProvider, providerId, "assent", now);
            AddAudit(db, providerId, AssentAction, locked.Id.ToString(), now);
            await db.SaveChangesAsync(ct);
            return ConsentOutcome.Ok;
        }, ct);
        return outcome == ConsentOutcome.Ok
            ? new ConsentResult(ConsentOutcome.Ok, await GetViewAsync(db, providerId, consent.PatientId, now, ct))
            : new ConsentResult(outcome);
    }

    // The provider records a revocation (or cancels a pending link). The reason is kept on the consent only.
    public static async Task<ConsentResult> RevokeAsync(
        AnamnysDbContext db, Guid providerId, Guid consentId, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        var cleaned = Clean(reason);
        if (cleaned is { Length: > MaxRevokeReason })
        {
            return new ConsentResult(ConsentOutcome.InvalidNote);
        }
        var (consent, _, _) = await LoadAsync(db, providerId, consentId, now, ct);
        if (consent is null)
        {
            return new ConsentResult(ConsentOutcome.NotFound);
        }
        if (!ConsentRules.CanRevokeByProvider(consent))
        {
            return new ConsentResult(ConsentOutcome.InvalidState);
        }

        var outcome = await db.InTransactionAsync(async ct =>
        {
            var locked = await LockAsync(db, consentId, ct);
            if (locked is null || locked.ProviderId != providerId)
            {
                return ConsentOutcome.NotFound;
            }
            if (!ConsentRules.CanRevokeByProvider(locked))
            {
                return ConsentOutcome.InvalidState;
            }
            MarkRevoked(db, locked, ActorProvider, providerId, cleaned, now);
            await db.SaveChangesAsync(ct);
            return ConsentOutcome.Ok;
        }, ct);
        return outcome == ConsentOutcome.Ok
            ? new ConsentResult(ConsentOutcome.Ok, await GetViewAsync(db, providerId, consent.PatientId, now, ct))
            : new ConsentResult(outcome);
    }

    public static async Task<ConsentDocument> DocumentAsync(
        AnamnysDbContext db, INoteFileStore store, ILogger logger, Guid providerId, Guid consentId, CancellationToken ct)
    {
        var key = await db.RecordingConsents.AsNoTracking()
            .Where(c => c.Id == consentId && c.ProviderId == providerId)
            .Join(db.Patients.Where(p => p.ProviderId == providerId && p.DiscardedAt == null), c => c.PatientId, p => p.Id, (c, _) => c.DocumentObjectKey)
            .SingleOrDefaultAsync(ct);
        if (key is null)
        {
            return new ConsentDocument(ConsentOutcome.NotFound);
        }
        byte[]? content;
        try
        {
            content = await store.GetAsync(key, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Recording consent {ConsentId}: reading the signed PDF failed ({ErrorType}).", consentId, e.GetType().Name);
            return new ConsentDocument(ConsentOutcome.StorageUnavailable);
        }
        if (content is null)
        {
            return new ConsentDocument(ConsentOutcome.NotFound);
        }
        // The signed term is ePHI: audited (the consent id only) before the bytes are returned.
        AddAudit(db, providerId, DocumentDownloadedAction, consentId.ToString(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return new ConsentDocument(ConsentOutcome.Ok, content, ConsentPdf.FileNameFor(consentId));
    }

    // Archiving (discharge) expires the SENT or SIGNED consent. Call inside the archive's transaction: the rows are locked
    // FOR UPDATE (a signature in flight finishes first and is then expired with its real from-state), and the changes go to
    // the caller's change tracker, committed by the caller's SaveChanges with the archive.
    public static async Task ExpireOnArchiveAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RecordingConsents.FromSql($"""
            SELECT * FROM "RecordingConsents"
             WHERE "ProviderId" = {providerId} AND "PatientId" = {patientId} AND "State" IN ('SENT', 'SIGNED')
             FOR UPDATE
            """).ToListAsync(ct);
        foreach (var consent in active)
        {
            MarkExpired(db, consent, ConsentRules.ExpiredDischarge, ActorProvider, providerId, now);
        }
    }

    // ---- Public links --------------------------------------------------------------------------------------------

    public static async Task<PublicConsentView?> ViewSignAsync(AnamnysDbContext db, string token, DateTimeOffset now, CancellationToken ct)
    {
        var found = await FindByTokenAsync(db, token, sign: true, now, ct);
        if (found is null)
        {
            return null;
        }
        var (consent, patient, provider) = found.Value;
        // The text that will be signed, without the date of birth the signer must type.
        var text = ConsentTerm.MaskBirthDate(SentText(consent, patient, provider, now));
        return new PublicConsentView(text, ConsentRules.IsMinor(patient, now), provider.Name);
    }

    public static async Task<PublicConsentResult> SignAsync(
        AnamnysDbContext db, INoteFileStore store, IEmailSender? sender, Uri? portalBaseUrl, ILogger logger, string token,
        PublicConsentSignRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        var found = await FindByTokenAsync(db, token, sign: true, now, ct);
        if (found is null)
        {
            return new PublicConsentResult(PublicConsentOutcome.Gone);
        }
        var minor = ConsentRules.IsMinor(found.Value.Patient, now);
        var signerName = Clean(body?.SignerName);
        var relationship = Clean(body?.Relationship);
        List<string> errors = [];
        if (body?.Accepted != true)
        {
            errors.Add("accepted_required");
        }
        if (body?.BirthDate is null)
        {
            errors.Add("birthdate_required");
        }
        if (signerName is null || signerName.Length > MaxSignerName)
        {
            errors.Add("signer_name_required");
        }
        else if (!IsSingleLine(signerName))
        {
            errors.Add("signer_name_single_line");
        }
        if (minor && (relationship is null || relationship.Length > MaxRelationship))
        {
            errors.Add("relationship_required");
        }
        else if (minor && !IsSingleLine(relationship!))
        {
            errors.Add("relationship_single_line");
        }
        if (errors.Count > 0)
        {
            return new PublicConsentResult(PublicConsentOutcome.Invalid, errors);
        }

        var consentId = found.Value.Consent.Id;
        var hash = ConsentTokens.Hash(token);
        var revokeToken = ConsentTokens.NewToken();
        string? email = null;
        var outcome = await db.InTransactionAsync(async ct =>
        {
            var c = await LockAsync(db, consentId, ct);
            if (c is null || c.State != ConsentRules.Sent || c.SignTokenHash is null || !c.SignTokenHash.SequenceEqual(hash)
                || c.FailedBirthDateCount >= ConsentRules.MaxFailedBirthDates || c.SignTokenExpiresAt <= now)
            {
                return PublicConsentOutcome.Gone;
            }
            var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(
                p => p.Id == c.PatientId && p.ProviderId == c.ProviderId && p.DiscardedAt == null && p.ArchivedAt == null, ct);
            if (patient is null)
            {
                return PublicConsentOutcome.Gone;
            }
            if (patient.DateOfBirth != body!.BirthDate)
            {
                c.FailedBirthDateCount++;
                c.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                return c.FailedBirthDateCount >= ConsentRules.MaxFailedBirthDates ? PublicConsentOutcome.Gone : PublicConsentOutcome.BirthDateMismatch;
            }

            var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == c.ProviderId, ct);
            var isMinor = ConsentRules.IsMinor(patient, now);
            var role = isMinor ? ConsentRules.RoleGuardian : ConsentRules.RolePatient;
            var signature = new ConsentSignature(signerName!, role, isMinor ? relationship : null, now);
            var version = c.TemplateVersion ?? ConsentTerm.CurrentVersion;
            // The signed text is the text the signing page shows with its open lines filled. A fresh rendering only when
            // they no longer fit (the patient came of age since sending).
            var text = ConsentTerm.Complete(SentText(c, patient, provider, now), signature, provider.PracticeCity)
                ?? ConsentTerm.Render(version, TermData(patient, provider, c.ProviderNote, now) with { Signature = signature });
            var sha = ConsentTerm.Sha256(text);
            var key = ConsentPdf.ObjectKeyFor(c.ProviderId, c.Id);
            var pdf = ConsentPdf.Render(new ConsentPdfModel(
                c.Id, version, text, sha, signature.SignerName, role, signature.Relationship, now, provider.Name,
                ConsentTerm.Crp(provider.CrpNumber, provider.CrpRegion)));
            try
            {
                await store.PutAsync(key, pdf, "application/pdf", ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Recording consent {ConsentId}: storing the signed PDF failed ({ErrorType}).", c.Id, e.GetType().Name);
                return PublicConsentOutcome.StorageUnavailable;
            }

            c.State = ConsentRules.Signed;
            c.SignedAt = now;
            c.SignerRole = role;
            c.SignerName = signature.SignerName;
            c.GuardianName = isMinor ? signature.SignerName : null;
            c.GuardianRelationship = signature.Relationship;
            c.TermText = text;
            c.TermSha256 = sha;
            c.SignatureEvidence = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["birthDateMatched"] = true, ["termSha256"] = sha, ["termVersion"] = version, ["signerRole"] = role,
            });
            c.SignTokenHash = null;
            c.SignTokenExpiresAt = null;
            c.FailedBirthDateCount = 0;
            c.RevokeTokenHash = ConsentTokens.Hash(revokeToken);
            c.DocumentObjectKey = key;
            c.UpdatedAt = now;
            AddEvent(db, c.Id, ConsentRules.Sent, ConsentRules.Signed, ActorSigner, null, role, now);
            AddAudit(db, null, SignedAction, $"{c.Id} {role}", now);
            AddBell(db, c.ProviderId, SignedKind, c.Id, now);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                // The row will not point at the PDF: remove it rather than leave an orphan in the private bucket.
                await DeleteQuietlyAsync(store, logger, key, c.Id);
                throw;
            }
            email = patient.ContactEmail;
            return PublicConsentOutcome.Ok;
        }, ct);

        if (outcome == PublicConsentOutcome.Ok && (sender is null || portalBaseUrl is null))
        {
            logger.LogWarning("Recording consent {ConsentId}: signed, but no signed-copy e-mail (sender or portal URL not configured).", consentId);
        }
        else if (outcome == PublicConsentOutcome.Ok && string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning("Recording consent {ConsentId}: signed, but no signed-copy e-mail (the patient's contact e-mail is empty).", consentId);
        }
        else if (outcome == PublicConsentOutcome.Ok)
        {
            var providerName = found.Value.Provider.Name;
            try
            {
                await sender!.SendAsync(
                    ConsentEmails.Signed(email, signerName!, providerName, ConsentTokens.RevokeLink(portalBaseUrl!, revokeToken), now),
                    CancellationToken.None);
            }
            catch (EmailSendException e)
            {
                logger.LogWarning("Recording consent {ConsentId}: the signed-copy e-mail failed ({ErrorType}).", consentId, e.GetType().Name);
            }
        }
        return new PublicConsentResult(outcome);
    }

    public static async Task<PublicRevokeView?> ViewRevokeAsync(AnamnysDbContext db, string token, DateTimeOffset now, CancellationToken ct)
    {
        var found = await FindByTokenAsync(db, token, sign: false, now, ct);
        if (found is null)
        {
            return null;
        }
        var (consent, _, provider) = found.Value;
        return new PublicRevokeView(
            ConsentTerm.MaskBirthDate(consent.TermText ?? ""), consent.SignedAt!.Value, consent.SignerName ?? "", consent.TermSha256 ?? "",
            provider.Name);
    }

    public static async Task<PublicConsentResult> RevokeByLinkAsync(
        AnamnysDbContext db, string token, PublicConsentRevokeRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        var found = await FindByTokenAsync(db, token, sign: false, now, ct);
        if (found is null)
        {
            return new PublicConsentResult(PublicConsentOutcome.Gone);
        }
        if (body?.BirthDate is not { } birthDate)
        {
            return new PublicConsentResult(PublicConsentOutcome.Invalid, ["birthdate_required"]);
        }

        var consentId = found.Value.Consent.Id;
        var hash = ConsentTokens.Hash(token);
        var outcome = await db.InTransactionAsync(async ct =>
        {
            var c = await LockAsync(db, consentId, ct);
            if (c is null || c.State != ConsentRules.Signed || c.RevokeTokenHash is null || !c.RevokeTokenHash.SequenceEqual(hash)
                || c.FailedBirthDateCount >= ConsentRules.MaxFailedBirthDates)
            {
                return PublicConsentOutcome.Gone;
            }
            var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(
                p => p.Id == c.PatientId && p.ProviderId == c.ProviderId && p.DiscardedAt == null, ct);
            if (patient is null)
            {
                return PublicConsentOutcome.Gone;
            }
            if (patient.DateOfBirth != birthDate)
            {
                c.FailedBirthDateCount++;
                c.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                return c.FailedBirthDateCount >= ConsentRules.MaxFailedBirthDates ? PublicConsentOutcome.Gone : PublicConsentOutcome.BirthDateMismatch;
            }
            MarkRevoked(db, c, ConsentRules.RolePatient, null, null, now);
            AddBell(db, c.ProviderId, RevokedKind, c.Id, now);
            await db.SaveChangesAsync(ct);
            return PublicConsentOutcome.Ok;
        }, ct);
        return new PublicConsentResult(outcome);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    // The first sending precondition that fails, as its API code; null when a term can be sent.
    public static string? SendBlockedBy(Provider provider, Patient patient) =>
        string.IsNullOrWhiteSpace(provider.Name) || string.IsNullOrWhiteSpace(provider.CrpNumber) || string.IsNullOrWhiteSpace(provider.CrpRegion)
            ? "profile_incomplete"
        : string.IsNullOrWhiteSpace(patient.ContactEmail) ? "patient_email_missing"
        : patient.DateOfBirth is null ? "patient_birthdate_missing"
        : patient.ArchivedAt is not null ? "patient_archived"
        : null;

    private static ConsentOutcome? Blocked(string? code) => code switch
    {
        "profile_incomplete" => ConsentOutcome.ProfileIncomplete,
        "patient_email_missing" => ConsentOutcome.PatientEmailMissing,
        "patient_birthdate_missing" => ConsentOutcome.PatientBirthDateMissing,
        "patient_archived" => ConsentOutcome.PatientArchived,
        _ => null,
    };

    private static Task<Patient?> PatientAsync(AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct) =>
        db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct);

    private static async Task<RecordingConsent?> CurrentAsync(
        AnamnysDbContext db, Guid providerId, Patient patient, DateTimeOffset now, CancellationToken ct)
    {
        var consent = await db.RecordingConsents.AsNoTracking()
            .Where(c => c.ProviderId == providerId && c.PatientId == patient.Id)
            .OrderByDescending(c => c.State == ConsentRules.Sent || c.State == ConsentRules.Signed)
            .ThenByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);
        return consent is null ? null : await ApplyDueExpiryAsync(db, consent, patient, now, ct);
    }

    // A provider-scoped consent with its patient and provider, after its due expiries; all null when not found.
    private static async Task<(RecordingConsent? Consent, Patient? Patient, Provider? Provider)> LoadAsync(
        AnamnysDbContext db, Guid providerId, Guid consentId, DateTimeOffset now, CancellationToken ct)
    {
        var consent = await db.RecordingConsents.AsNoTracking().SingleOrDefaultAsync(c => c.Id == consentId && c.ProviderId == providerId, ct);
        var patient = consent is null ? null : await PatientAsync(db, providerId, consent.PatientId, ct);
        if (consent is null || patient is null)
        {
            return (null, null, null);
        }
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
        return (await ApplyDueExpiryAsync(db, consent, patient, now, ct), patient, provider);
    }

    // Persists a due expiry in one statement (state change, event and audit together, guarded by the state and UpdatedAt
    // read, so two readers never expire it twice and a concurrent resend is never undone) and returns the consent as it now
    // is. It leaves the caller's change tracker alone.
    private static async Task<RecordingConsent> ApplyDueExpiryAsync(
        AnamnysDbContext db, RecordingConsent consent, Patient patient, DateTimeOffset now, CancellationToken ct)
    {
        if (ConsentRules.DueExpiry(consent, patient, now) is not { } reason)
        {
            return consent;
        }
        var at = now.ToUniversalTime();
        var expiredAt = ConsentRules.ExpiredAt(consent, patient, reason, at).ToUniversalTime();
        var from = consent.State;
        // Guarded by the row version that was evaluated: a resend or signature committed since then wins.
        var updatedAt = consent.UpdatedAt.ToUniversalTime();
        await db.Database.ExecuteSqlAsync($"""
            WITH done AS (
                UPDATE "RecordingConsents"
                   SET "State" = 'EXPIRED', "ExpiredReason" = {reason}, "SignTokenHash" = NULL, "RevokeTokenHash" = NULL, "UpdatedAt" = {at}
                 WHERE "Id" = {consent.Id} AND "State" = {from} AND "UpdatedAt" = {updatedAt}
                RETURNING "Id"),
            ev AS (
                INSERT INTO "ConsentEvents" ("Id", "ConsentId", "FromState", "ToState", "At", "ActorKind", "Reason")
                SELECT gen_random_uuid(), "Id", {from}, 'EXPIRED', {expiredAt}, 'system', {reason} FROM done)
            INSERT INTO "AuditEntries" ("Id", "ActorId", "Action", "FieldChanged", "NewValue", "At")
            SELECT gen_random_uuid(), NULL, {ExpiredAction}, {AuditField}, "Id"::text || ' ' || {reason}, {at} FROM done
            """, ct);
        return await db.RecordingConsents.AsNoTracking().SingleAsync(c => c.Id == consent.Id, ct);
    }

    // The consent behind a public link, after its due expiries; null for every link that no longer works.
    private static async Task<(RecordingConsent Consent, Patient Patient, Provider Provider)?> FindByTokenAsync(
        AnamnysDbContext db, string token, bool sign, DateTimeOffset now, CancellationToken ct)
    {
        if (!ConsentTokens.IsWellFormed(token))
        {
            return null;
        }
        var hash = ConsentTokens.Hash(token);
        var consent = sign
            ? await db.RecordingConsents.AsNoTracking().SingleOrDefaultAsync(c => c.SignTokenHash == hash, ct)
            : await db.RecordingConsents.AsNoTracking().SingleOrDefaultAsync(c => c.RevokeTokenHash == hash, ct);
        if (consent is null)
        {
            return null;
        }
        var patient = await PatientAsync(db, consent.ProviderId, consent.PatientId, ct);
        if (patient is null)
        {
            return null;
        }
        consent = await ApplyDueExpiryAsync(db, consent, patient, now, ct);
        if (consent.State != (sign ? ConsentRules.Sent : ConsentRules.Signed)
            || consent.FailedBirthDateCount >= ConsentRules.MaxFailedBirthDates)
        {
            return null;
        }
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == consent.ProviderId, ct);
        return (consent, patient, provider);
    }

    private static async Task DeleteQuietlyAsync(INoteFileStore store, ILogger logger, string key, Guid consentId)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning("Recording consent {ConsentId}: removing the unsaved signed PDF failed ({ErrorType}).", consentId, e.GetType().Name);
        }
    }

    private static async Task<RecordingConsent?> LockAsync(AnamnysDbContext db, Guid consentId, CancellationToken ct) =>
        (await db.RecordingConsents.FromSql($"""
            SELECT * FROM "RecordingConsents" WHERE "Id" = {consentId} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();

    // Runs the write and the e-mail in one transaction: a send that fails rolls the write back, so no consent is left
    // SENT without its e-mail. The token is made by the caller, so a retried attempt sends the same link.
    private static async Task<ConsentOutcome> WriteThenSendAsync(
        AnamnysDbContext db, IEmailSender sender, Func<CancellationToken, Task<(ConsentOutcome, EmailMessage?)>> work,
        CancellationToken ct)
    {
        var sent = false;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var (outcome, message) = await work(ct);
            if (outcome != ConsentOutcome.Ok || message is null)
            {
                return outcome;
            }
            if (!sent)
            {
                try
                {
                    await sender.SendAsync(message, ct);
                }
                catch (EmailSendException)
                {
                    return ConsentOutcome.SendFailed;
                }
                sent = true;
            }
            await tx.CommitAsync(ct);
            return ConsentOutcome.Ok;
        }, ct);
    }

    // Null when saved; the refusal when the patient already has an active consent or was discarded meanwhile.
    private static async Task<ConsentOutcome?> SaveAsync(AnamnysDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: ActiveKey })
        {
            return ConsentOutcome.ConsentActive;
        }
        catch (DbUpdateException e) when (DiscardedPatient.Is(e))
        {
            return ConsentOutcome.NotFound;
        }
    }

    private static RecordingConsent NewConsent(Guid id, Patient patient, Provider provider, string? note, string token, DateTimeOffset now)
    {
        var text = ConsentTerm.Render(ConsentTerm.CurrentVersion, TermData(patient, provider, note, now));
        return new RecordingConsent
        {
            Id = id,
            ProviderId = provider.Id,
            PatientId = patient.Id,
            State = ConsentRules.Sent,
            TemplateVersion = ConsentTerm.CurrentVersion,
            IssuedAt = now,
            ProviderNote = note,
            TermText = text,
            TermSha256 = ConsentTerm.Sha256(text),
            SignTokenHash = ConsentTokens.Hash(token),
            SignTokenExpiresAt = now + ConsentRules.LinkValidity,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static EmailMessage RequestMessage(
        Patient patient, Provider provider, string token, DateTimeOffset expiresAt, Uri portalBaseUrl, DateTimeOffset now) =>
        ConsentEmails.Request(patient.ContactEmail!, patient.FirstName, provider.Name, ConsentRules.IsMinor(patient, now),
            ConsentTokens.SignLink(portalBaseUrl, token), expiresAt);

    // The unsigned text the signing page shows and the signature completes: the stored sent text while it still names the
    // patient as the record does (name and date of birth), otherwise a fresh rendering from the record, so the signed
    // text never states a date of birth other than the one the signer had to type.
    private static string SentText(RecordingConsent consent, Patient patient, Provider provider, DateTimeOffset now) =>
        consent.TermText is { } sent && ConsentTerm.NamesPatient(sent, PatientName(patient), patient.DateOfBirth)
            ? sent
            : ConsentTerm.Render(consent.TemplateVersion ?? ConsentTerm.CurrentVersion, TermData(patient, provider, consent.ProviderNote, now));

    private static string PatientName(Patient patient) => $"{patient.FirstName} {patient.LastName}".Trim();

    private static ConsentTermData TermData(Patient patient, Provider provider, string? note, DateTimeOffset now) =>
        new(PatientName(patient), patient.DateOfBirth, ConsentRules.IsMinor(patient, now), provider.Name,
            provider.CrpNumber, provider.CrpRegion, provider.PracticeCity, note);

    private static void MarkExpired(AnamnysDbContext db, RecordingConsent consent, string reason, string actorKind, Guid? actorId, DateTimeOffset now)
    {
        var from = consent.State;
        consent.State = ConsentRules.Expired;
        consent.ExpiredReason = reason;
        consent.SignTokenHash = null;
        consent.RevokeTokenHash = null;
        consent.UpdatedAt = now;
        AddEvent(db, consent.Id, from, ConsentRules.Expired, actorKind, actorId, reason, now);
        AddAudit(db, actorId, ExpiredAction, $"{consent.Id} {reason}", now);
    }

    // By the patient (link) or the provider (route); the provider's reason stays on the consent, never in the event.
    private static void MarkRevoked(AnamnysDbContext db, RecordingConsent consent, string by, Guid? actorId, string? reason, DateTimeOffset now)
    {
        var from = consent.State;
        consent.State = ConsentRules.Revoked;
        consent.RevokedAt = now;
        consent.RevokedBy = by;
        consent.RevokeReason = by == ActorProvider ? reason : null;
        consent.RevokeTokenHash = null;
        consent.SignTokenHash = null;
        consent.UpdatedAt = now;
        AddEvent(db, consent.Id, from, ConsentRules.Revoked, by == ActorProvider ? ActorProvider : ActorSigner, actorId, by, now);
        AddAudit(db, actorId, RevokedAction, $"{consent.Id} {by}", now);
    }

    private static void AddEvent(
        AnamnysDbContext db, Guid consentId, string from, string to, string actorKind, Guid? actorId, string? reason, DateTimeOffset now) =>
        db.ConsentEvents.Add(new ConsentEvent
        {
            Id = Guid.NewGuid(),
            ConsentId = consentId,
            FromState = from,
            ToState = to,
            At = now,
            ActorId = actorId,
            ActorKind = actorKind,
            Reason = reason,
        });

    private static void AddAudit(AnamnysDbContext db, Guid? actorId, string action, string value, DateTimeOffset now) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Action = action,
            FieldChanged = AuditField,
            NewValue = value,
            At = now,
        });

    private static void AddBell(AnamnysDbContext db, Guid providerId, string kind, Guid consentId, DateTimeOffset now) =>
        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            Kind = kind,
            SubjectType = SubjectType,
            SubjectId = consentId,
            Channel = "in_app",
            ScheduledFor = now,
            SentAt = now,
            DeliveryStatus = "sent",
        });

    private static async Task<ConsentView> ViewOfAsync(
        AnamnysDbContext db, RecordingConsent? consent, Patient patient, Provider provider, DateTimeOffset now, CancellationToken ct)
    {
        var minor = ConsentRules.IsMinor(patient, now);
        var (canRecord, reason) = ConsentRules.CanRecordSession(consent, patient, now);
        var blocked = SendBlockedBy(provider, patient);
        if (consent is null)
        {
            return new ConsentView(null, "none", null, ConsentTerm.CurrentVersion, null, null, null, null, null, null, null, null, null,
                null, null, null, null, false, minor, false, reason, blocked, []);
        }
        var events = await db.ConsentEvents.AsNoTracking()
            .Where(e => e.ConsentId == consent.Id)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => new ConsentEventView(e.FromState.ToLower(), e.ToState.ToLower(), e.At, e.ActorKind, e.Reason))
            .ToListAsync(ct);
        return new ConsentView(
            consent.Id,
            ConsentRules.ApiState(consent.State),
            consent.TemplateVersion,
            ConsentTerm.CurrentVersion,
            consent.IssuedAt,
            consent.SignedAt,
            consent.SignerRole,
            consent.SignerName,
            consent.GuardianRelationship,
            consent.AssentRecordedAt is { } assent ? PatientSearchQuery.PracticeToday(assent) : null,
            consent.AssentNote,
            consent.RevokedAt,
            consent.RevokedBy,
            consent.RevokeReason,
            consent.ExpiredReason,
            consent.State == ConsentRules.Sent ? consent.SignTokenExpiresAt : null,
            consent.ProviderNote,
            consent.DocumentObjectKey is not null,
            minor,
            canRecord,
            reason,
            blocked,
            events);
    }

    // Plain text on one line (as the sharing recipient name): no control, format or line/paragraph separator characters,
    // so a signer cannot add lines to the signed term.
    private static bool IsSingleLine(string value) =>
        !value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
