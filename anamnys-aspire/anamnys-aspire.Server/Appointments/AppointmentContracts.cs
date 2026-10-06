namespace Anamnys.Server.Appointments;

// See design/specs/2026-10-05-provider-calendar-design.md §3. Each request validates
// itself; the outcome rules that need the database live in Appointments.

public static class AppointmentStatus
{
    public const string Scheduled = "scheduled";
    public const string Confirmed = "confirmed";
    public const string Attended = "attended";
    public const string Cancelled = "cancelled";
    public const string NoShow = "no_show";

    public static readonly string[] All = [Scheduled, Confirmed, Attended, Cancelled, NoShow];
}

public static class AppointmentTransitions
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [AppointmentStatus.Scheduled] = [AppointmentStatus.Confirmed, AppointmentStatus.Attended, AppointmentStatus.NoShow, AppointmentStatus.Cancelled],
        [AppointmentStatus.Confirmed] = [AppointmentStatus.Scheduled, AppointmentStatus.Attended, AppointmentStatus.NoShow, AppointmentStatus.Cancelled],
        [AppointmentStatus.Attended] = [AppointmentStatus.Scheduled],
        [AppointmentStatus.NoShow] = [AppointmentStatus.Scheduled],
        [AppointmentStatus.Cancelled] = [],
    };

    public static bool IsAllowed(string from, string to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
}

public static class AppointmentRules
{
    public const int MinDuration = 5;
    public const int MaxDuration = 480;
    public const int MaxReason = 500;
    public const int MaxRangeDays = 42;
    public static readonly string[] Modalities = ["online", "presencial"];

    internal static void ValidateSlot(Dictionary<string, string[]> errors, DateTimeOffset? startsAt, int? durationMinutes, string? modality)
    {
        if (startsAt is null)
        {
            errors["startsAt"] = ["Informe a data e o horário."];
        }
        if (durationMinutes is null or < MinDuration or > MaxDuration)
        {
            errors["durationMinutes"] = [$"A duração deve ficar entre {MinDuration} e {MaxDuration} minutos."];
        }
        if (modality is null || !Modalities.Contains(modality))
        {
            errors["modality"] = ["Escolha online ou presencial."];
        }
    }
}

public sealed record CreateAppointmentRequest(Guid? PatientId, DateTimeOffset? StartsAt, int? DurationMinutes, string? Modality)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (PatientId is null || PatientId == Guid.Empty)
        {
            errors["patientId"] = ["Escolha o paciente."];
        }
        AppointmentRules.ValidateSlot(errors, StartsAt, DurationMinutes, Modality);
        return errors;
    }
}

public sealed record UpdateAppointmentRequest(DateTimeOffset? StartsAt, int? DurationMinutes, string? Modality)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        AppointmentRules.ValidateSlot(errors, StartsAt, DurationMinutes, Modality);
        return errors;
    }
}

public sealed record ChangeAppointmentStatusRequest(string? Status, string? Reason)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Status is null || !AppointmentStatus.All.Contains(Status))
        {
            errors["status"] = ["Status inválido."];
        }
        if (!string.IsNullOrWhiteSpace(Reason))
        {
            if (Status != AppointmentStatus.Cancelled)
            {
                errors["reason"] = ["O motivo só se aplica ao cancelamento."];
            }
            else if (Reason.Trim().Length > AppointmentRules.MaxReason)
            {
                errors["reason"] = [$"Use no máximo {AppointmentRules.MaxReason} caracteres."];
            }
        }
        return errors;
    }
}

public static class AppointmentRange
{
    public static Dictionary<string, string[]> Validate(DateTimeOffset? from, DateTimeOffset? to, string[]? statuses)
    {
        var errors = new Dictionary<string, string[]>();
        if (from is null)
        {
            errors["from"] = ["Informe o início do período."];
        }
        if (to is null)
        {
            errors["to"] = ["Informe o fim do período."];
        }
        else if (from is not null && (to <= from || to - from > TimeSpan.FromDays(AppointmentRules.MaxRangeDays)))
        {
            errors["to"] = [$"O período deve ter entre 1 minuto e {AppointmentRules.MaxRangeDays} dias."];
        }
        if (statuses is not null && statuses.Any(s => !AppointmentStatus.All.Contains(s)))
        {
            errors["status"] = ["Status inválido."];
        }
        return errors;
    }
}

public sealed record AppointmentItem(
    Guid Id,
    Guid PatientId,
    string PatientName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Timezone,
    string Modality,
    string Status,
    string? CancellationReason);

public sealed record AppointmentListResponse(IReadOnlyList<AppointmentItem> Items);
