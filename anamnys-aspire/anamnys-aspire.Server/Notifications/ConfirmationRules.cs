namespace Anamnys.Server.Notifications;

public static class ReminderTemplates
{
    public const string Confirmation = "confirmation";
    public const string Reminder1h = "reminder_1h";
    public const string Cancellation = "cancellation";
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
