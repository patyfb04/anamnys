namespace Anamnys.Server.Data.Entities;

public class Instrument
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? ItemCount { get; set; }
    public int MinScore { get; set; }
    public int MaxScore { get; set; }
    public string SatepsiStatus { get; set; } = "";
    public DateOnly? SatepsiCheckedOn { get; set; }
    public string LicenseMode { get; set; } = "";
    public bool MayRenderItems { get; set; }
    public string? SourceRef { get; set; }
    public string StatusExplanation { get; set; } = "";
    public bool Enabled { get; set; }
    public string Version { get; set; } = "";
}
