using System.Net.Sockets;

namespace Anamnys.Server.ExternalDocuments;

// One connection per scan to clamd (design/specs/2026-10-25-external-documents-design.md §3).
// Failing to connect, or losing the connection, means clamd is not ready (starting, reloading): that is
// ScannerUnavailableException. A clamd that connects but answers too slowly or with an error is a real
// scan failure.
public sealed class ClamAvScanner(ExternalDocumentOptions options) : IVirusScanner
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(2);

    public async Task<ScanVerdict> ScanAsync(byte[] content, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(options.ClamAvHost, options.ClamAvPort, connect.Token);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new ScannerUnavailableException("clamd is not reachable.", e);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ScanTimeout);
        try
        {
            await using var stream = client.GetStream();
            await ClamdProtocol.WriteInstreamAsync(stream, content, timeout.Token);
            return ClamdProtocol.ParseVerdict(await ClamdProtocol.ReadReplyAsync(stream, timeout.Token));
        }
        catch (IOException e)
        {
            throw new ScannerUnavailableException("clamd closed the connection.", e, connectionLost: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("clamd did not answer in time.");
        }
    }
}
