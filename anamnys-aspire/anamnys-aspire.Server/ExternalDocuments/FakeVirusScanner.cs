using System.Text;

namespace Anamnys.Server.ExternalDocuments;

// Development and test fixture only (ExternalDocuments:Scanner = fake): flags the EICAR test string, the
// same marker the real scanner is checked with by hand.
public sealed class FakeVirusScanner : IVirusScanner
{
    public const string Marker = "EICAR-STANDARD-ANTIVIRUS-TEST-FILE";
    private static readonly byte[] MarkerBytes = Encoding.ASCII.GetBytes(Marker);

    public Task<ScanVerdict> ScanAsync(byte[] content, CancellationToken ct) =>
        Task.FromResult(content.AsSpan().IndexOf(MarkerBytes) >= 0
            ? new ScanVerdict(false, "Eicar-Test-Signature")
            : new ScanVerdict(true));
}
