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
    public const string ProviderSchemes = $"{ProviderCookie},{ProviderBearer}";
    public const string PatientSchemes = $"{PatientCookie},{PatientBearer}";
    public const string PhiSchemes = $"{ProviderCookie},{PatientCookie},{ProviderBearer},{PatientBearer}";
    public const string AdminSchemes = $"{OwnerCookie},{OwnerBearer}";
}
