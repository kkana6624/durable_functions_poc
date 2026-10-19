namespace Poc.AppService.Authentication;

public sealed class AppServiceAuthenticationOptions
{
    public const string SectionName = "AppServiceAuth";

    /// <summary>
    /// Authentication mode: "Stub" (for local/testing), "ClaimsPrincipal" (Entra ID bearer tokens), or "EasyAuth" (App Service built-in auth).
    /// </summary>
    public string Mode { get; set; } = "Stub";

    /// <summary>
    /// Optional default owner ID when running in Stub mode without headers.
    /// </summary>
    public string? DefaultOwnerId { get; set; }

    /// <summary>
    /// Optional default org ID when running in Stub mode without headers.
    /// </summary>
    public string? DefaultOrgId { get; set; }

    /// <summary>
    /// Whether to allow requests without an authenticated caller in Stub mode.
    /// Default is false (unauthenticated requests return 401).
    /// </summary>
    public bool AllowAnonymousInStub { get; set; }

    /// <summary>
    /// OpenID Connect / Entra ID authority URL (e.g. https://login.microsoftonline.com/{tenantId}/v2.0).
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Expected audience (aud) for JWT bearer tokens.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Whether HTTPS is required for metadata addresses or authority. Defaults to true.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
