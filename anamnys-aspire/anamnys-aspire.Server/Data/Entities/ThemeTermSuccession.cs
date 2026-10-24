namespace Anamnys.Server.Data.Entities;

public class ThemeTermSuccession
{
    public Guid Id { get; set; }
    public Guid FromTermId { get; set; }
    public Guid ToTermId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public Guid StaffId { get; set; }
}
