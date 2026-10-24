namespace Anamnys.Server.Data.Entities;

public class ThemeTermChange
{
    public Guid Id { get; set; }
    public Guid TermId { get; set; }
    public string Kind { get; set; } = "";
    public string? PreviousLabel { get; set; }
    public string? NewLabel { get; set; }
    public Guid StaffId { get; set; }
    public DateTimeOffset At { get; set; }
}
