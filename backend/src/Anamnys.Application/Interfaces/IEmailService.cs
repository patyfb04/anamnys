namespace Anamnys.Application.Interfaces;

public interface IEmailService
{
    /// <summary>Sends a single HTML email. Throws on failure — callers decide whether that
    /// should fail their own request or just be logged (e.g. a notification email failing
    /// shouldn't roll back the record it's notifying about).</summary>
    Task SendAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default);
}
