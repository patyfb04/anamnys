namespace Anamnys.Server.Auth;

public static class AuthSchemes
{
    public const string ProviderCookie = "provider-cookie";
    public const string PatientCookie = "patient-cookie";
    public const string OwnerCookie = "owner-cookie";

    public const string ProviderOidc = "provider-oidc";
    public const string PatientOidc = "patient-oidc";
    public const string OwnerOidc = "owner-oidc";

    public const string ProviderBearer = "provider-bearer";
    public const string PatientBearer = "patient-bearer";
    public const string OwnerBearer = "owner-bearer";

    // For [Authorize(AuthenticationSchemes = ...)] on controllers or endpoint
    // filters, which take one comma-joined string. Route groups use
    // AddAuthenticationSchemes(params string[]) instead and pass the individual
    // constants above.
    //
    // A credential from a realm not in the list is not an authenticated
    // principal on that endpoint at all, so the request fails with 401 rather
    // than 403 — a missing scheme, not a failed policy.
    //
    // Cookie schemes only, matching the groups in Program.cs. The bearer
    // schemes are registered but not mounted: LocalId is minted on the OIDC
    // handler alone, so CurrentUser.LocalId throws for a bearer principal.
    // They come back — here and in Program.cs — together with provisioning on
    // JwtBearerEvents.OnTokenValidated, when the spec §10.3 mobile client
    // ships.
    public const string ProviderSchemes = ProviderCookie;
    public const string PatientSchemes = PatientCookie;
    public const string PhiSchemes = $"{ProviderCookie},{PatientCookie}";
    public const string AdminSchemes = OwnerCookie;
}
