namespace Agent42.Backend;

/// <summary>
/// The paths that need no sign-in in <c>entra</c> mode: the same list as <c>excludedPaths</c> in
/// <c>infra/modules/container-app.bicep</c> (ADR 0042), with the same measured matcher. <c>PublicPathsTests</c> parses the
/// Bicep and asserts set-equality, so the two cannot drift (the drift that shipped a dead feature in ADR 0035).
/// An entry ending in <c>/*</c> is a prefix match that keeps the trailing slash and spans any number of segments,
/// including an empty remainder; every other entry matches exactly. Every public endpoint self-enforces and takes its
/// user id from a directory grain, never <c>GetUserId()</c>.
/// </summary>
public static class PublicPaths
{
    public static readonly IReadOnlyList<string> Entries =
    [
        "/api/agent-run",
        "/api/share/*",
        "/shared",
        "/shared/*",
        "/api/agent-mcp",
        "/api/agent-mcp/*",
        "/api/local-bridge",
        "/api/email-in",
        "/legal/*",
    ];

    public static bool IsPublic(PathString path) => IsPublic(path.Value);

    public static bool IsPublic(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || HasDotSegment(path))
        {
            return false;
        }

        foreach (var entry in Entries)
        {
            var hit = entry.EndsWith("/*", StringComparison.Ordinal)
                ? path.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase)
                : path.Equals(entry, StringComparison.OrdinalIgnoreCase);
            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The sign-in surface itself (<c>/.auth/*</c>); never gated, so a signed-out user can reach the login.</summary>
    public static bool IsAuthSurface(PathString path)
        => path.StartsWithSegments("/.auth", StringComparison.OrdinalIgnoreCase);

    // Fail closed on traversal: /api/share/../models is gated even though it starts with a public prefix. A percent-encoded
    // dot segment is decoded first, so %2e%2e cannot slip through either.
    private static bool HasDotSegment(string path)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return true;
        }

        return decoded.Replace('\\', '/').Split('/').Any(s => s is "." or "..");
    }
}
