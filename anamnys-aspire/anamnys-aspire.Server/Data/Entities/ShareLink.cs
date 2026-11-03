namespace Anamnys.Server.Data.Entities;

// A tokenized link to one signed document (design/specs/2026-11-01-protected-sharing-design.md §2). Only hashes are
// stored: SHA-256 of the token, Argon2id of the password. No ProviderId: the document scopes it.
public class ShareLink
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid AuthorizationId { get; set; }
    public string TokenHash { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public int? ViewCap { get; set; }
    public int ViewsUsed { get; set; }
    public bool DownloadEnabled { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int WrongPasswordCount { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
}

// One password attempt against a link. CoarseGeo stays unused and no IP is kept (spec §1, access log).
public class ShareAccess
{
    public const string Opened = "opened";
    public const string BadPassword = "bad_password";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
    public const string CapReached = "cap_reached";
    public const string Blocked = "blocked";

    public Guid Id { get; set; }
    public Guid ShareLinkId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Outcome { get; set; } = "";
    public string? UserAgent { get; set; }
}
