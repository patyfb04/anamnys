using Anamnys.Server.Data.Entities;
using Anamnys.Server.Patients;

namespace Anamnys.Server.RecordingConsents;

// The consent state machine and the capture rule (design/specs/2026-11-15-recording-consent-design.md §4). Pure: no
// database. RecordingConsentService persists what DueExpiry finds.
public static class ConsentRules
{
    public const string NotRequested = "NOT_REQUESTED";
    public const string Sent = "SENT";
    public const string Signed = "SIGNED";
    public const string Revoked = "REVOKED";
    public const string Expired = "EXPIRED";

    public const string RolePatient = "patient";
    public const string RoleGuardian = "guardian";

    public const string ExpiredLink = "link";
    public const string ExpiredDischarge = "discharge";
    public const string ExpiredAdulthood = "adulthood";
    public const string ExpiredNewVersion = "new_version";

    public const string ReasonOk = "ok";
    public const string ReasonNone = "none";
    public const string ReasonSent = "sent";
    public const string ReasonRevoked = "revoked";
    public const string ReasonExpired = "expired";
    public const string ReasonAssentMissing = "assent_missing";
    public const string ReasonArchived = "archived";

    public const int MaxFailedBirthDates = 5;
    public static readonly TimeSpan LinkValidity = TimeSpan.FromDays(7);

    // A minor is a patient whose age today (practice day, from the record's date of birth) is under 18. Without a date
    // of birth the patient is not treated as a minor.
    public static bool IsMinor(Patient patient, DateTimeOffset now) =>
        patient.DateOfBirth is { } born && born.AddYears(18) > PatientSearchQuery.PracticeToday(now);

    // The expiry a read must apply now, or null: a SENT link past its deadline; a SENT or SIGNED consent of an archived
    // patient (discharge); a consent a guardian signed for a patient who is now an adult.
    public static string? DueExpiry(RecordingConsent consent, Patient patient, DateTimeOffset now)
    {
        if (consent.State is not (Sent or Signed))
        {
            return null;
        }
        if (patient.ArchivedAt is not null)
        {
            return ExpiredDischarge;
        }
        if (consent.State == Sent && consent.SignTokenExpiresAt is { } expires && expires <= now)
        {
            return ExpiredLink;
        }
        if (consent.State == Signed && consent.SignerRole == RoleGuardian && !IsMinor(patient, now))
        {
            return ExpiredAdulthood;
        }
        return null;
    }

    // When a due expiry actually happened (the event's time, so the history reads in order even when the expiry is
    // applied later, by the read that found it): the link's deadline, the archive, the 18th birthday; never after now.
    public static DateTimeOffset ExpiredAt(RecordingConsent consent, Patient patient, string reason, DateTimeOffset now)
    {
        DateTimeOffset? at = reason switch
        {
            ExpiredLink => consent.SignTokenExpiresAt,
            ExpiredDischarge => patient.ArchivedAt,
            ExpiredAdulthood when patient.DateOfBirth is { } born => PracticeMidnight(born.AddYears(18)),
            _ => null,
        };
        return at is { } value && value < now ? value : now;
    }

    public static DateTimeOffset PracticeMidnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Practice.GetUtcOffset(local)).ToUniversalTime();
    }

    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    // True only for a SIGNED consent (after due expiries) of a patient in care, with the assent recorded when the patient
    // is a minor today.
    public static (bool Allowed, string Reason) CanRecordSession(RecordingConsent? consent, Patient patient, DateTimeOffset now)
    {
        if (patient.ArchivedAt is not null)
        {
            return (false, ReasonArchived);
        }
        if (consent is null || consent.State == NotRequested)
        {
            return (false, ReasonNone);
        }
        if (DueExpiry(consent, patient, now) is not null)
        {
            return (false, ReasonExpired);
        }
        return consent.State switch
        {
            Sent => (false, ReasonSent),
            Revoked => (false, ReasonRevoked),
            Expired => (false, ReasonExpired),
            Signed when IsMinor(patient, now) && consent.AssentRecordedAt is null => (false, ReasonAssentMissing),
            Signed => (true, ReasonOk),
            _ => (false, ReasonNone),
        };
    }

    public static bool CanResend(RecordingConsent consent) =>
        consent.State == Sent || (consent.State == Expired && consent.ExpiredReason == ExpiredLink);

    public static bool CanRevokeByProvider(RecordingConsent consent) => consent.State is Sent or Signed;

    public static bool CanRecordAssent(RecordingConsent consent) => consent.State is Sent or Signed;

    // "Pedir de novo": a consent of an older term version, still SENT or SIGNED.
    public static bool CanRenew(RecordingConsent consent) =>
        consent.State is Sent or Signed && consent.TemplateVersion != ConsentTerm.CurrentVersion;

    // The API's lower-case state.
    public static string ApiState(string state) => state.ToLowerInvariant();
}
