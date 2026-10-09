using Anamnys.Server.Email;

namespace Anamnys.Tests.Notifications;

public sealed class RecordingEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];
    public int FailNext { get; set; }
    public Queue<Exception> ThrowNext { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (ThrowNext.TryDequeue(out var toThrow))
        {
            throw toThrow;
        }
        if (FailNext > 0)
        {
            FailNext--;
            throw new EmailSendException("simulated failure");
        }
        Sent.Add(message);
        return Task.CompletedTask;
    }
}
