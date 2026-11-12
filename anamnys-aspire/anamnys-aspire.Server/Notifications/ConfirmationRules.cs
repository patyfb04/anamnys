namespace Anamnys.Server.Notifications;

public static class ReminderTemplates
{
    public const string Confirmation = "confirmation";
    public const string Reminder1h = "reminder_1h";
    public const string Cancellation = "cancellation";
    // F-29 delivery 3 (design/specs/2026-11-09-appointment-series-design.md §2, §3): one summary when a series is
    // created or split, one notice when it is ended. Attached to an occurrence of the series.
    public const string SeriesScheduled = "series_scheduled";
    public const string SeriesEnded = "series_ended";

    public static readonly string[] Series = [SeriesScheduled, SeriesEnded];
}

public static class AutoCancelModes
{
    public const string Off = "off";
    public const string AfterEmail = "after_email";
    public const string BeforeSession = "before_session";
    public static readonly string[] All = [Off, AfterEmail, BeforeSession];
}

// design/specs/2026-10-07-appointment-notifications-design.md §2 (edge cases) and §4.
public static class ConfirmationRules
{
    public const string DefaultMode = AutoCancelModes.AfterEmail;
    public const int DefaultHours = 1;
    public const int MaxHours = 168;
    public const int MaxAttempts = 3;
    public const string SystemCancellationReason = "Não confirmada no prazo";
    public static readonly TimeSpan ReminderLead = TimeSpan.FromHours(1);
    // A series occurrence's confirmation request goes out at least this long before it (or at once, if closer).
    public static readonly TimeSpan SeriesConfirmationLead = TimeSpan.FromHours(48);
    public static readonly TimeSpan SeriesDeadlineMargin = TimeSpan.FromHours(24);

    // max(48 h, auto-cancel hours + 24 h): in after_email mode the deadline (send + hours) then falls 24 h before the
    // session, in before_session mode (start - hours) 24 h after the send. With auto-cancel off, 48 h.
    public static TimeSpan SeriesConfirmationLeadFor(string mode, int hours)
    {
        if (mode == AutoCancelModes.Off)
        {
            return SeriesConfirmationLead;
        }
        var needed = TimeSpan.FromHours(hours) + SeriesDeadlineMargin;
        return needed > SeriesConfirmationLead ? needed : SeriesConfirmationLead;
    }

    public static DateTimeOffset? DeadlineAt(string mode, int hours, DateTimeOffset sentAt, DateTimeOffset startsAt)
    {
        DateTimeOffset? raw = mode switch
        {
            AutoCancelModes.AfterEmail => sentAt.AddHours(hours),
            AutoCancelModes.BeforeSession => startsAt.AddHours(-hours),
            _ => null,
        };
        if (raw is not { } deadline)
        {
            return null;
        }
        // Never cancel at or after the session start: such a deadline means no automatic cancellation.
        return deadline > sentAt && deadline < startsAt ? deadline : null;
    }

    public static DateTimeOffset? ReminderAt(DateTimeOffset startsAt, DateTimeOffset queuedAt)
    {
        var at = startsAt - ReminderLead;
        return at > queuedAt ? at : null;
    }
}
