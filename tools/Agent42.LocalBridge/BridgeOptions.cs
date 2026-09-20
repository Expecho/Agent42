namespace Agent42.LocalBridge;

/// <summary>
/// The helper's configuration (ADR 0076). <c>--server</c> is required, <c>--upstream</c> defaults to where
/// BrowserOS neo was measured listening, and <c>--auto-approve</c> pre-approves named tools — there is no
/// "approve everything" switch, and answering <c>a</c> at a prompt pre-approves only the tool being asked
/// about. The key comes from <see cref="KeyVariable"/> <b>only</b>: an argument would be visible to every
/// process that can list this one's command line.
/// </summary>
public sealed record BridgeOptions(Uri Server, Uri Upstream, string Key, IReadOnlySet<string> AutoApprove)
{
    public const string KeyVariable = "AGENT42_BRIDGE_KEY";

    /// <summary>
    /// Measured default for BrowserOS neo 0.0.53. Its docs say 9239; the live server listened on 9210 —
    /// check <c>chrome://browseros/mcp</c> and pass <c>--upstream</c> if yours differs.
    /// </summary>
    public static readonly Uri DefaultUpstream = new("http://127.0.0.1:9210/mcp");

    public const string Usage = """
        Agent42 local bridge — lets your Agent42 chats use an MCP server that listens only on this machine.

        Usage: Agent42.LocalBridge --server <https://your-agent42> [--upstream <url>] [--auto-approve <tool,tool>]
               Agent42.LocalBridge --version

          --server        Agent42 base URL, e.g. https://chat.agent42.nl (required)
          --upstream      Local MCP endpoint (default http://127.0.0.1:9210/mcp — see chrome://browseros/mcp)
          --auto-approve  Comma-separated tool names that run without asking (default: none)

        The bridge key is read from the AGENT42_BRIDGE_KEY environment variable — never from the command line.
        Every tool call not marked read-only by the server asks for confirmation here first. Answer [y]es for
        that one call, [n]o to decline it, or [a]lways to stop being asked about that tool until this
        bridge stops.
        """;

    /// <summary>Parses the command line, or returns an error message.</summary>
    public static (BridgeOptions? Options, string? Error) Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        Uri? server = null;
        var upstream = DefaultUpstream;
        var autoApprove = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--server" when TryHttpUri(value, out var parsed):
                    server = parsed;
                    i++;
                    break;
                case "--upstream" when TryHttpUri(value, out var parsed):
                    upstream = parsed;
                    i++;
                    break;
                case "--auto-approve" when value is not null:
                    autoApprove.UnionWith(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    i++;
                    break;
                case "--key" or "--api-key":
                    return (null, $"Do not pass the key as an argument. Set {KeyVariable} instead.");
                default:
                    return (null, $"Unrecognised or incomplete argument '{args[i]}'.");
            }
        }

        if (server is null)
        {
            return (null, "--server is required (an absolute http or https URL).");
        }

        var key = environment(KeyVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            return (null, $"Set {KeyVariable} to the bridge key shown when you added the server in Agent42.");
        }

        return (new BridgeOptions(server, upstream, key.Trim(), autoApprove), null);
    }

    private static bool TryHttpUri(string? value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
