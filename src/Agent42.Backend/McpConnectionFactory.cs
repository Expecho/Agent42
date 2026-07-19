using Agent42.Agents;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Agent42.Backend;

/// <summary>
/// One connected MCP server: its raw tools and a handle to dispose the live connection later.
/// <see cref="UserMcpToolProvider"/> wraps the tools for truncation, then keeps the <see cref="Connection"/>
/// alive for as long as any grain may hold them, disposing it on cache eviction (the bounded-lifetime policy).
/// </summary>
public sealed record McpServerConnection(IReadOnlyList<AITool> Tools, IAsyncDisposable Connection);

/// <summary>
/// Connects to a single remote MCP server and returns its tools. A seam (not the concrete SDK types) so the
/// per-user tool provider's orchestration — version-cached, best-effort, bounded lifetime — is testable with
/// a stub, while the real SDK path (proven by the Step 0 spike) is exercised in prod/DevHost.
/// </summary>
public interface IMcpConnectionFactory
{
    /// <summary>
    /// Connects to <paramref name="server"/> and lists its tools. <paramref name="decryptedHeaderValue"/> is
    /// the already-decrypted Header-mode secret, or null. Throws on any connect/list failure — the caller
    /// treats a throw as "skip this server" (best-effort).
    /// </summary>
    Task<McpServerConnection> ConnectAsync(StoredMcpServer server, string? decryptedHeaderValue, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="IMcpConnectionFactory"/> over the <c>ModelContextProtocol</c> SDK (Streamable HTTP).
///
/// <para>Phase 1 handles None and Header auth. Header uses the transport's <c>AdditionalHeaders</c> (Step 0
/// spike) — a STATIC header needs no <c>DelegatingHandler</c> and no custom <see cref="HttpClient"/>, so the
/// transport builds its own client and the <c>AddServiceDefaults</c> resilience pipeline is never inherited
/// (the "named HttpClient inherits the retry pipeline" gotcha is avoided by construction). Phases 2/3
/// (ClientCredentials/OAuth) will need a plain <c>new HttpClient</c> + auth handler for dynamic tokens.</para>
/// </summary>
public sealed class McpConnectionFactory : IMcpConnectionFactory
{
    // The transport's own connect timeout — a genuine second layer under the provider's linked-CTS bound,
    // so a server that accepts the socket but stalls the handshake still fails fast.
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    private readonly ILoggerFactory _loggerFactory;

    public McpConnectionFactory(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public async Task<McpServerConnection> ConnectAsync(
        StoredMcpServer server, string? decryptedHeaderValue, CancellationToken cancellationToken)
    {
        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Endpoint),
            ConnectionTimeout = ConnectionTimeout,
        };

        // Header mode: stamp the static header via AdditionalHeaders. None mode adds nothing. Other modes
        // never reach here in Phase 1 (the API rejects them and the provider skips them defensively).
        if (Enum.TryParse<McpAuthMode>(server.Auth, ignoreCase: true, out var mode)
            && mode == McpAuthMode.Header
            && !string.IsNullOrWhiteSpace(server.HeaderName)
            && !string.IsNullOrEmpty(decryptedHeaderValue))
        {
            options.AdditionalHeaders = new Dictionary<string, string>
            {
                [server.HeaderName] = decryptedHeaderValue,
            };
        }

        var transport = new HttpClientTransport(options, _loggerFactory);
        var client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            // McpClientTool : AIFunction : AITool (Step 0 spike). The provider wraps each for truncation.
            IReadOnlyList<AITool> raw = [.. tools.Cast<AITool>()];
            return new McpServerConnection(raw, client);
        }
        catch
        {
            // Connected but listing failed — don't leak the socket before rethrowing to the best-effort caller.
            await client.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// Wraps an MCP tool (an <see cref="AIFunction"/>) so its serialized result is truncated to
/// <see cref="McpToolLimits.MaxToolResultLength"/> before it is persisted into the 2 MB session (ADR 0004).
/// <see cref="DelegatingAIFunction"/> forwards Name/Description/JsonSchema unchanged (Step 0 spike), so the
/// model's view of the tool is identical — only the return value is bounded.
/// </summary>
internal sealed class TruncatingMcpTool(AIFunction inner) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var raw = await base.InvokeCoreAsync(arguments, cancellationToken);

        // MCP results surface as a JsonElement (Step 0 spike); ToString() yields its compact JSON. Under
        // budget, return the raw value so structure is preserved; over budget, return a truncated string.
        var text = raw as string ?? raw?.ToString() ?? "";
        return text.Length <= McpToolLimits.MaxToolResultLength
            ? raw
            : text[..McpToolLimits.MaxToolResultLength] + McpToolLimits.TruncationMarker;
    }
}
