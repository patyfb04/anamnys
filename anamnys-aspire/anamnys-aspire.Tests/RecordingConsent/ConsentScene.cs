using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Email;
using Anamnys.Server.RecordingConsents;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Notifications;
using Anamnys.Tests.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anamnys.Tests.RecordingConsents;

// A scratch database from the bootstrap script (which carries design/migrations/2026-11-15-recording-consent.sql), so
// these tests do not depend on the dev database having the migration applied. The provider has a CRP; the adult
// patient (Benedita, born 1990-05-04) and the minor (Caio, born 2012-03-10) have a contact e-mail; a second provider owns
// the stranger. Now is Sunday 2026-11-15, 12:00 in São Paulo.
internal sealed class ConsentScene : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);
    // Signing happens a minute after sending, and later actions a minute after that, so the history has one order.
    public static readonly DateTimeOffset SignedAt = Now.AddMinutes(1);
    public static readonly DateTimeOffset Later = Now.AddMinutes(2);
    public static readonly DateOnly AdultBirth = new(1990, 5, 4);
    public static readonly DateOnly MinorBirth = new(2012, 3, 10);
    public static readonly Uri Portal = new("https://portal.test/patient");

    private ConsentScene(RetentionScratch db, Guid provider, Guid adult, Guid minor, Guid otherProvider, Guid stranger)
    {
        Db = db;
        Provider = provider;
        Adult = adult;
        Minor = minor;
        OtherProvider = otherProvider;
        Stranger = stranger;
    }

    public RetentionScratch Db { get; }
    public Guid Provider { get; }
    public Guid Adult { get; }
    public Guid Minor { get; }
    public Guid OtherProvider { get; }
    public Guid Stranger { get; }
    public RecordingEmailSender Sender { get; } = new();
    public FakeNoteFileStore Store { get; } = new();

    public static async Task<ConsentScene> CreateAsync(SharedAppHostFixture fixture, CancellationToken ct)
    {
        var db = await RetentionScratch.CreateAsync(fixture, ct);
        var provider = await db.AddProviderAsync(ct, withCrp: true);
        var adult = await db.AddPatientAsync(provider, "Benedita", ct);
        var minor = await db.AddPatientAsync(provider, "Caio", ct);
        var other = await db.AddProviderAsync(ct, withCrp: true);
        var stranger = await db.AddPatientAsync(other, "Estranha", ct);
        var scene = new ConsentScene(db, provider, adult, minor, other, stranger);
        await scene.SetContactAsync(adult, "benedita@example.test", AdultBirth, ct);
        await scene.SetContactAsync(minor, "familia.caio@example.test", MinorBirth, ct);
        await scene.SetContactAsync(stranger, "estranha@example.test", AdultBirth, ct);
        return scene;
    }

    public AnamnysDbContext CreateDbContext() => Db.CreateDbContext();

    public Task SetContactAsync(Guid patient, string? email, DateOnly? birth, CancellationToken ct) =>
        Db.ExecuteAsync("""UPDATE "Patients" SET "ContactEmail" = @e, "DateOfBirth" = @b WHERE "Id" = @pa""", ct,
            ("pa", patient), ("e", (object?)email ?? DBNull.Value), ("b", (object?)birth ?? DBNull.Value));

    public async Task<ConsentResult> SendAsync(Guid patient, CancellationToken ct, string? note = null, IEmailSender? sender = null,
        DateTimeOffset? now = null, Guid? provider = null)
    {
        await using var db = CreateDbContext();
        return await RecordingConsentService.SendAsync(db, sender ?? Sender, Portal, provider ?? Provider, patient, note, now ?? Now, ct);
    }

    public async Task<PublicConsentResult> SignAsync(string token, PublicConsentSignRequest body, CancellationToken ct, DateTimeOffset? now = null)
    {
        await using var db = CreateDbContext();
        return await RecordingConsentService.SignAsync(db, Store, Sender, Portal, NullLogger.Instance, token, body, now ?? SignedAt, ct);
    }

    public async Task<PublicConsentResult> RevokeByLinkAsync(string token, DateOnly? birth, CancellationToken ct)
    {
        await using var db = CreateDbContext();
        return await RecordingConsentService.RevokeByLinkAsync(db, token, new PublicConsentRevokeRequest(birth), Later, ct);
    }

    public async Task<RecordingConsent?> EffectiveAsync(Guid patient, CancellationToken ct, DateTimeOffset? now = null)
    {
        await using var db = CreateDbContext();
        return await RecordingConsentService.GetEffectiveAsync(db, Provider, patient, now ?? Now, ct);
    }

    public async Task<ConsentView?> ViewAsync(Guid patient, CancellationToken ct, DateTimeOffset? now = null, Guid? provider = null)
    {
        await using var db = CreateDbContext();
        return await RecordingConsentService.GetViewAsync(db, provider ?? Provider, patient, now ?? Now, ct);
    }

    public async Task<Patient> PatientAsync(Guid patient, CancellationToken ct)
    {
        await using var db = CreateDbContext();
        return await db.Patients.AsNoTracking().SingleAsync(p => p.Id == patient, ct);
    }

    // Sends a term to the patient and signs it through the e-mailed link; returns the consent id and the revocation token.
    public async Task<(Guid Consent, string RevokeToken)> SignedAsync(Guid patient, CancellationToken ct)
    {
        var sent = await SendAsync(patient, ct);
        if (sent.Outcome != ConsentOutcome.Ok)
        {
            throw new InvalidOperationException(sent.Outcome.ToString());
        }
        var minor = patient == Minor;
        var signed = await SignAsync(SignToken(), new PublicConsentSignRequest(
            minor ? MinorBirth : AdultBirth, minor ? "Joana Souza" : "Benedita Silva", minor ? "mãe" : null, true), ct);
        if (signed.Outcome != PublicConsentOutcome.Ok)
        {
            throw new InvalidOperationException(signed.Outcome.ToString());
        }
        return (sent.View!.Id!.Value, RevokeToken());
    }

    // The token of the latest e-mailed signing (or revocation) link.
    public string SignToken() => TokenFrom(Sender.Sent.Last(m => m.Subject == ConsentEmails.RequestSubject), "/consentimento/");

    public string RevokeToken() => TokenFrom(Sender.Sent.Last(m => m.Subject == ConsentEmails.SignedSubject), "/consentimento/revogar/");

    private static string TokenFrom(EmailMessage message, string marker)
    {
        var start = message.Text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = message.Text.IndexOfAny([' ', '\n'], start);
        return message.Text[start..end];
    }

    public async Task<List<string>> EventsAsync(Guid consent, CancellationToken ct)
    {
        await using var db = CreateDbContext();
        return await db.ConsentEvents.AsNoTracking()
            .Where(e => e.ConsentId == consent)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => e.FromState + ">" + e.ToState + " " + e.ActorKind + " " + (e.Reason ?? "-"))
            .ToListAsync(ct);
    }

    public async Task<List<string>> AuditAsync(CancellationToken ct)
    {
        await using var db = CreateDbContext();
        return await db.AuditEntries.AsNoTracking()
            .Where(a => a.FieldChanged == RecordingConsentService.AuditField)
            .OrderBy(a => a.At).ThenBy(a => a.Action)
            .Select(a => a.Action + " " + a.NewValue)
            .ToListAsync(ct);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
