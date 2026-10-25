using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// clamd answers PING with PONG once the signature database is loaded; until then the port refuses
// connections, so the resource stays unhealthy during the first download.
internal sealed class ClamdHealthCheck(EndpointReference endpoint) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("zPING\0"), timeout.Token);
            var buffer = new byte[16];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            return Encoding.ASCII.GetString(buffer, 0, read).Trim('\0', '\n', ' ') == "PONG"
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("clamd did not answer PONG.");
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("clamd is not reachable.", e);
        }
    }
}
