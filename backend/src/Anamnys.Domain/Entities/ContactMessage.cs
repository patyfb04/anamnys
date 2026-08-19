namespace Anamnys.Domain.Entities;

/// <summary>
/// A submission from the public "Contact / Support" marketing page (unauthenticated — anyone
/// can submit, no ProviderId). Purely a persisted inbox entry for now: no outbound email is
/// sent on creation, since this project has no email-sending service configured yet.
/// </summary>
public class ContactMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
