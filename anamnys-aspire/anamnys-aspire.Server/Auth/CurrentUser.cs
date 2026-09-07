using System.Security.Claims;

namespace Anamnys.Server.Auth;

// The only sanctioned source of a local row id. The id comes from the
// session, which came from a token this server obtained itself — never from
// anything the client supplied. Every provider-scoped query resolves through
// this.
public static class CurrentUser
{
    public static Guid LocalId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(AnamnysClaims.LocalId)
            ?? throw new InvalidOperationException("Principal has no local id claim.");
        return Guid.Parse(raw);
    }

    public static Guid? LocalIdOrNull(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(AnamnysClaims.LocalId);
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
