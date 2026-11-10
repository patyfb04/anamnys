using System.Text.Json.Serialization;

namespace Anamnys.Server.Availability;

// Wire shapes of design/specs/2026-11-09-availability-design.md §4 (JSON camelCase). Times of day are "HH:mm" in the
// provider's zone; dates are "yyyy-MM-dd" local dates; instants are ISO 8601 with offset.

public sealed record AvailabilityInterval(string Start, string End);

public sealed record AvailabilityDay(int Weekday, IReadOnlyList<AvailabilityInterval> Intervals);

public sealed record AvailabilityVersion(DateOnly EffectiveFrom, IReadOnlyList<AvailabilityDay> Days);

public sealed record AvailabilityResponse(AvailabilityVersion? Current, IReadOnlyList<AvailabilityVersion> Upcoming);

public sealed record SaveAvailabilityRequest(DateOnly? EffectiveFrom, IReadOnlyList<SaveAvailabilityDay?>? Days);

public sealed record SaveAvailabilityDay(int? Weekday, IReadOnlyList<SaveAvailabilityInterval?>? Intervals);

public sealed record SaveAvailabilityInterval(string? Start, string? End);

public sealed record HolidayItem(string Key, string Name, DateOnly Date);

public sealed record HolidaySettingsResponse(bool WorksOnHolidays, IReadOnlyList<string> DisabledHolidays, IReadOnlyList<HolidayItem> Holidays);

public sealed record SaveHolidaySettingsRequest(bool? WorksOnHolidays, IReadOnlyList<string?>? DisabledHolidays);

public sealed record BlockRequest(
    string? Kind, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool AllDay = false, string? Reason = null, bool CancelAffected = false,
    IReadOnlyList<Guid>? ExpectedAppointmentIds = null);

public sealed record BlockItem(Guid Id, string Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool AllDay, string? Reason);

public sealed record AffectedAppointment(Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid PatientId, string PatientName);

public sealed record BlockCreatedResponse(Guid Id, IReadOnlyList<Guid> CancelledAppointmentIds);

public sealed record OverlayWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record OverlayHoliday(string Key, string Name);

public sealed record OverlayDay(DateOnly Date, IReadOnlyList<OverlayWindow> Windows, OverlayHoliday? Holiday);

public sealed record CalendarOverlayResponse(IReadOnlyList<OverlayDay> Days, IReadOnlyList<BlockItem> Blocks);

// One reason an appointment falls outside the provider's availability. Kind is outside_hours, block or holiday; From/To
// are the block's (or the holiday's day) bounds, absent for outside_hours.
public sealed record OutsideAvailabilityReason(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? From = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? To = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlockKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HolidayName = null)
{
    public const string OutsideHours = "outside_hours";
    public const string Block = "block";
    public const string Holiday = "holiday";
}

public static class BlockKinds
{
    public const string Block = "block";
    public const string Vacation = "vacation";
    public const string Holiday = "holiday";

    public static readonly string[] All = [Block, Vacation, Holiday];
}
