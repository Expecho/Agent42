namespace Agent42.Backend;

/// <summary>Who authenticates the caller: the Azure ingress (Easy Auth), or this process (native Entra OIDC, ADR 0145).</summary>
public enum AuthMode
{
    /// <summary>Easy Auth in front of the app injects the <c>X-MS-CLIENT-PRINCIPAL*</c> headers. The default; what production runs.</summary>
    EasyAuth,

    /// <summary>The Backend runs the Entra OIDC sign-in itself and synthesizes the same headers.</summary>
    Entra,
}

/// <summary>
/// Reads <c>Auth:Mode</c>. Absent means <see cref="AuthMode.EasyAuth"/>, so production's Bicep needs no change; a value that
/// is present but unknown fails startup, because falling back to Easy Auth on a typo would silently run with no sign-in at
/// all outside Azure (every caller becomes <c>local-dev</c>).
/// </summary>
public static class AuthModeSetting
{
    public const string Key = "Auth:Mode";

    public static AuthMode Read(IConfiguration configuration)
    {
        var value = configuration[Key]?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals("easyauth", StringComparison.OrdinalIgnoreCase))
        {
            return AuthMode.EasyAuth;
        }

        if (value.Equals("entra", StringComparison.OrdinalIgnoreCase))
        {
            return AuthMode.Entra;
        }

        throw new InvalidOperationException($"{Key} is '{value}'. Use 'easyauth' or 'entra'.");
    }
}

/// <summary>The settings <c>Auth:Mode=entra</c> needs. Missing tenant, client id or secret fails startup (ADR 0145).</summary>
public sealed record EntraAuthSettings(string TenantId, string ClientId, string ClientSecret, string? PublicBaseUrl)
{
    public static EntraAuthSettings Read(IConfiguration configuration)
    {
        var tenant = configuration["Auth:Entra:TenantId"]?.Trim();
        var client = configuration["Auth:Entra:ClientId"]?.Trim();
        var secret = configuration["Auth:Entra:ClientSecret"];

        var missing = new List<string>();
        if (string.IsNullOrEmpty(tenant))
        {
            missing.Add("Auth:Entra:TenantId");
        }

        if (string.IsNullOrEmpty(client))
        {
            missing.Add("Auth:Entra:ClientId");
        }

        if (string.IsNullOrEmpty(secret))
        {
            missing.Add("Auth:Entra:ClientSecret");
        }

        if (missing.Count > 0)
        {
            var verb = missing.Count == 1 ? "is" : "are";
            throw new InvalidOperationException(
                $"{AuthModeSetting.Key} is 'entra' but {string.Join(", ", missing)} {verb} not set. " +
                "Refusing to start: without sign-in every caller would be the user 'local-dev'.");
        }

        var baseUrl = configuration["Auth:PublicBaseUrl"]?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(baseUrl) && !IsHttpUrl(baseUrl))
        {
            throw new InvalidOperationException("Auth:PublicBaseUrl must be an absolute http(s) URL.");
        }

        return new EntraAuthSettings(tenant!, client!, secret!, string.IsNullOrEmpty(baseUrl) ? null : baseUrl);
    }

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
}
