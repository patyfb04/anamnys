namespace Anamnys.Server.Data.Entities;

// A provider's national holiday preferences (design/specs/2026-11-09-availability-design.md §2, §3). No row means the
// defaults: does not work on holidays, none switched off. DisabledHolidays holds holiday keys such as "tiradentes".
public class ProviderHolidaySettings
{
    public Guid ProviderId { get; set; }
    public bool WorksOnHolidays { get; set; }
    public string[] DisabledHolidays { get; set; } = [];
}
