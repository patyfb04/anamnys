using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Sharing;

public enum ShareOutcome
{
    Ok, NotFound, NotSigned, Superseded, Archived, AuthorizationRequired, AuthorizationExists,
    TermEmpty, TermTooLarge, TermUnsupportedType, TermTypeMismatch, TermInfected, ScannerUnavailable, StorageUnavailable, Busy,
}

public sealed record TermFile(ShareOutcome Outcome, byte[]? Content = null, string? ContentType = null);

public sealed record AuthorizationResult(ShareOutcome Outcome, Guid? Id = null);

public sealed record CreatedShareLink(Guid Id, string Token, string Password, DateTimeOffset ExpiresAt, int? ViewCap, bool DownloadEnabled);

public sealed record LinkResult(ShareOutcome Outcome, CreatedShareLink? Link = null);

public sealed record TermUpload(string FileName, byte[] Content);

public sealed record RecordAuthorizationRequest(
    string? RecipientName, string? RecipientRole, string? Purpose, DateTimeOffset? AuthorizedAt, string? ObtainedVia, bool Confirmed)
{
    public const int MaxName = 200;
    public const int MaxRole = 100;
    public const int MaxPurpose = 500;

    public Dictionary<string, string[]> Validate(DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, "recipientName", RecipientName, MaxName);
        Required(errors, "recipientRole", RecipientRole, MaxRole);
        Required(errors, "purpose", Purpose, MaxPurpose);
        // The name goes on the watermark: no line breaks, and no invisible or direction-changing characters to disguise it.
        SingleLine(errors, "recipientName", RecipientName);
        SingleLine(errors, "recipientRole", RecipientRole);
        if (AuthorizedAt is not { } at || at > now)
        {
            errors["authorizedAt"] = ["Informe a data da autorização, que não pode estar no futuro."];
        }
        if (ObtainedVia is null || !DisclosureAuthorization.ObtainedVias.Contains(ObtainedVia))
        {
            errors["obtainedVia"] = ["Informe como a autorização foi obtida."];
        }
        if (!Confirmed)
        {
            errors["confirmed"] = ["Confirme que a autorização do paciente foi obtida."];
        }
        return errors;
    }

    private static void SingleLine(Dictionary<string, string[]> errors, string key, string? value)
    {
        if (value is not null && value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator))
        {
            errors[key] = ["Use apenas texto simples, em uma linha."];
        }
    }

    private static void Required(Dictionary<string, string[]> errors, string key, string? value, int max)
    {
        var length = value?.Trim().Length ?? 0;
        if (length == 0 || length > max)
        {
            errors[key] = [$"Preencha com até {max} caracteres."];
        }
    }
}

public sealed record CreateShareLinkRequest(Guid? AuthorizationId, int? ExpiresInDays, int? ViewCap, bool? DownloadEnabled)
{
    public const int DefaultDays = 7;
    public const int MaxDays = 30;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (ExpiresInDays is < 1 or > MaxDays)
        {
            errors["expiresInDays"] = [$"Escolha de 1 a {MaxDays} dias."];
        }
        if (ViewCap is < 1)
        {
            errors["viewCap"] = ["O limite de visualizações deve ser maior que zero."];
        }
        return errors;
    }
}

public sealed record ShareAccessItem(DateTimeOffset At, string Outcome, string? UserAgent);

public sealed record ShareLinkItem(
    Guid Id, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int? ViewCap, int ViewsUsed, bool DownloadEnabled,
    DateTimeOffset? RevokedAt, DateTimeOffset? BlockedAt, IReadOnlyList<ShareAccessItem> Accesses, int AccessCount);

public sealed record AuthorizationItem(
    Guid Id, string RecipientName, string RecipientRole, string Purpose, DateTimeOffset AuthorizedAt, string ObtainedVia,
    bool HasTerm, IReadOnlyList<ShareLinkItem> Links);

// CanShare is false with BlockedReason "not_signed", "document_superseded" or "archived". SupersededBy is the newer version.
public sealed record DocumentShares(
    Guid DocumentId, bool CanShare, string? BlockedReason, Guid? SupersededBy, IReadOnlyList<AuthorizationItem> Authorizations);

public sealed record PublicShareCheckRequest(string? Token);

public sealed record PublicShareOpenRequest(string? Token, string? Password);

public enum PublicOpenOutcome { Opened, WrongPassword, Unavailable, Busy }

public sealed record PublicOpenResult(PublicOpenOutcome Outcome, byte[]? Pdf = null, string? FileName = null, bool DownloadEnabled = false);
