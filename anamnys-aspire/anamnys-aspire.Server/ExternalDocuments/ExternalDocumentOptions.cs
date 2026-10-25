namespace Anamnys.Server.ExternalDocuments;

// Scanner: "clamav" (clamd over TCP) or "fake" (Development only: the test fixture).
public sealed class ExternalDocumentOptions
{
    public string Scanner { get; set; } = "clamav";
    public string ClamAvHost { get; set; } = "localhost";
    public int ClamAvPort { get; set; } = 3310;
}
