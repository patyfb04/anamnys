namespace Anamnys.Server.ExternalDocuments;

// Signature is the scanner's name for the threat (not patient data); null when clean.
public sealed record ScanVerdict(bool Clean, string? Signature = null);

// A scanner that cannot give a verdict throws (ScanException for a refusal, anything else for transport
// failures): the caller retries and, after the last attempt, fails the document.
public interface IVirusScanner
{
    Task<ScanVerdict> ScanAsync(byte[] content, CancellationToken ct);
}

public sealed class ScanException(string message) : Exception(message);

// The scanner could not be reached (not started, still loading signatures, reloading): not a verdict on the
// document, so the worker keeps the document pending and tries again without counting an attempt.
// ConnectionLost: clamd accepted the connection and then dropped it while the file was being scanned. Repeated on
// the same file for too long it points at that file, not at clamd (see ScanWorker.MaxConnectionLostDuration).
public sealed class ScannerUnavailableException(string message, Exception? inner = null, bool connectionLost = false)
    : Exception(message, inner)
{
    public bool ConnectionLost { get; } = connectionLost;
}
