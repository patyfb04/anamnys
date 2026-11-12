namespace Anamnys.Server.Appointments.Series;

// Wire contract of design/specs/2026-11-09-appointment-series-design.md §4 (JSON camelCase). Dates are "yyyy-MM-dd",
// times "HH:mm", instants ISO 8601 with offset.

public sealed record CreateSeriesRequest(
    Guid? PatientId,
    DateTimeOffset? StartsAt,
    int? DurationMinutes,
    string? Modality,
    int? Interval,
    DateOnly? EndsOn,
    int? OccurrenceCount,
    bool AcknowledgeOutsideAvailability = false)
{
    // Field-level problems (400 validation problem, like the appointment form). Interval and end have their own codes.
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

// Skipped: the occurrences whose slot was taken by another appointment (not created), [{ seriesIndex, startsAt }].
public sealed record SeriesCreatedResponse(Guid SeriesId, Guid FirstAppointmentId, int Generated, IReadOnlyList<SkippedOccurrence> Skipped);

public sealed record SeriesOccurrenceItem(Guid Id, DateTimeOffset StartsAt, string Status, int? SeriesIndex, bool Detached);

public sealed record SeriesResponse(
    Guid Id,
    Guid PatientId,
    int Interval,
    int Weekday,
    string StartTime,
    string Timezone,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    int? OccurrenceCount,
    bool Active,
    string Description,
    IReadOnlyList<SeriesOccurrenceItem> Upcoming);

public sealed record SeriesSummaryResponse(
    Guid Id,
    Guid PatientId,
    int Interval,
    int Weekday,
    string StartTime,
    string Timezone,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    int? OccurrenceCount,
    bool Active,
    string Description);

public sealed record SplitSeriesRequest(
    Guid? FromAppointmentId,
    int? Weekday,
    string? StartTime,
    int? DurationMinutes,
    string? Modality,
    bool AcknowledgeOutsideAvailability = false)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (FromAppointmentId is null || FromAppointmentId == Guid.Empty)
        {
            errors["fromAppointmentId"] = ["Escolha a sessão."];
        }
        if (Weekday is null or < 0 or > 6)
        {
            errors["weekday"] = ["Escolha o dia da semana."];
        }
        if (SeriesPattern.ParseTime(StartTime) is null)
        {
            errors["startTime"] = ["Informe o horário (HH:mm)."];
        }
        if (DurationMinutes is null or < AppointmentRules.MinDuration or > AppointmentRules.MaxDuration)
        {
            errors["durationMinutes"] = [$"A duração deve ficar entre {AppointmentRules.MinDuration} e {AppointmentRules.MaxDuration} minutos."];
        }
        if (Modality is null || !AppointmentRules.Modalities.Contains(Modality))
        {
            errors["modality"] = ["Escolha online ou presencial."];
        }
        return errors;
    }
}

public static class StayReasons
{
    public const string Detached = "detached";
    public const string Confirmed = "confirmed";
}

public sealed record SplitStayItem(Guid Id, DateTimeOffset StartsAt, string Reason);

public sealed record SplitPreviewResponse(DateTimeOffset NewFirstStartsAt, int Moving, IReadOnlyList<SplitStayItem> Staying);

public sealed record SplitResponse(Guid NewSeriesId, IReadOnlyList<SkippedOccurrence> Skipped);

public sealed record EndSeriesRequest(DateOnly? FromDate);

public sealed record EndSeriesResponse(int Cancelled);
