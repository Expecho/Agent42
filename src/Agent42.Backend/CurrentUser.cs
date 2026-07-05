namespace Agent42.Backend;

/// <summary>
/// Resolves the signed-in user from the header that Container Apps built-in
/// authentication (Easy Auth) injects on every authenticated request. The
/// ingress strips X-MS-CLIENT-* headers from incoming traffic, so inside the
/// app the header can only originate from Easy Auth itself.
/// </summary>
public static class CurrentUser
{
    private const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    // Local development (DevHost, standalone runs) has no Easy Auth in front,
    // so every request maps to this single pseudo-user.
    private const string LocalDevelopmentUserId = "local-dev";

    /// <summary>The Entra ID object id of the signed-in user.</summary>
    public static string GetUserId(this HttpContext http)
        => http.Request.Headers.TryGetValue(PrincipalIdHeader, out var id)
            && !string.IsNullOrWhiteSpace(id)
            ? id.ToString()
            : LocalDevelopmentUserId;
}
