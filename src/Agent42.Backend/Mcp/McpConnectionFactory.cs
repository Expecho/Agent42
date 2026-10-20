using Agent42.Agents;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Agent42.Backend;

/// <summary>
/// One connected MCP server: its raw tools and a handle to dispose the live connection later.
/// <see cref="UserMcpToolProvider"/> wraps the tools for truncation, then keeps the <see cref="Connection"/>
/// alive for as long as any grain may hold them, disposing it on cache eviction (the bounded-lifetime policy).
/// <see cref="Elicitation"/> is the connection's elicitation router (ADR 0095). It is copied onto the group so the
/// grain can open a scope around each call, and it is null for a connection that declared no elicitation.
/// </summary>
public sealed record McpServerConnection(
    IReadOnlyList<AITool> Tools, IAsyncDisposable Connection, McpElicitationRouter? Elicitation = null);

/// <summary>
/// The already-decrypted secrets a connection may need, unprotected by the caller (never the ciphertext).
/// Phase 1: <see cref="HeaderValue"/>. Phase 2: <see cref="ClientSecret"/> (MSAL). InteractiveOAuth:
/// <see cref="OAuthTokenProvider"/> — the per-request bearer the caller built over the resolver (ADR 0081). It
/// REPLACED a static access-token field: two ways to pass one credential is how the connect path and the test path
/// drift apart (ADR 0064).
/// </summary>
public sealed record McpConnectionSecrets(string? HeaderValue, string? ClientSecret, IAuthHeaderProvider? OAuthTokenProvider = null);

/// <summary>
/// Connects to a single remote MCP server and returns its tools. A seam (not the concrete SDK types) so the
/// per-user tool provider's orchestration — version-cached, best-effort, bounded lifetime — is testable with
/// a stub, while the real SDK path (proven by the Step 0 spike) is exercised in prod/DevHost.
/// </summary>
public interface IMcpConnectionFactory
{
    /// <summary>
    /// Connects to <paramref name="server"/> and lists its tools. <paramref name="secrets"/> carries the
    /// already-decrypted values for the server's auth mode (never ciphertext). Throws on any connect/list
    /// failure — the caller treats a throw as "skip this server" (best-effort).
    /// </summary>
    Task<McpServerConnection> ConnectAsync(StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="IMcpConnectionFactory"/> over the <c>ModelContextProtocol</c> SDK (Streamable HTTP).
///
/// <para>None and Header (Phase 1): Header uses the transport's <c>AdditionalHeaders</c> (Step 0 spike) — a
/// STATIC header needs no <c>DelegatingHandler</c> and no custom <see cref="HttpClient"/>, so the transport
/// builds its own client and the <c>AddServiceDefaults</c> resilience pipeline is never inherited (the "named
/// HttpClient inherits the retry pipeline" gotcha is avoided by construction). ClientCredentials (Phase 2):
/// the token is dynamic (MSAL refreshes per request), so it needs an <see cref="AuthHeaderHandler"/> over a
/// <b>plain <c>new HttpClient</c></b> (not DI-resolved — so again no resilience inheritance). InteractiveOAuth
/// (ADR 0081) takes the same handler path with the caller-built <see cref="InteractiveOAuthTokenProvider"/>.</para>
/// </summary>
public sealed class McpConnectionFactory : IMcpConnectionFactory
{
    // The transport's own connect timeout — a genuine second layer under the provider's linked-CTS bound,
    // so a server that accepts the socket but stalls the handshake still fails fast.
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    private readonly ILoggerFactory _loggerFactory;

    public McpConnectionFactory(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public async Task<McpServerConnection> ConnectAsync(
        StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken)
    {
        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Endpoint),
            ConnectionTimeout = ConnectionTimeout,
        };

        Enum.TryParse<McpAuthMode>(server.Auth, ignoreCase: true, out var mode);

        // Header mode: stamp the static header via AdditionalHeaders. None mode adds nothing.
        if (mode == McpAuthMode.Header
            && !string.IsNullOrWhiteSpace(server.HeaderName)
            && !string.IsNullOrEmpty(secrets.HeaderValue))
        {
            options.AdditionalHeaders = new Dictionary<string, string>
            {
                [server.HeaderName] = secrets.HeaderValue,
            };
        }

        // Dynamic auth, stamped per request by AuthHeaderHandler over a plain HttpClient the transport owns. The
        // plain client is never DI-resolved, so no resilience inheritance. ClientCredentials: an MSAL token.
        // InteractiveOAuth (ADR 0081): the caller-built resolver-backed provider — which is what keeps this factory
        // grain-free; a connection outlives its access token, so a static bearer here would go stale in the cache.
        IAuthHeaderProvider? dynamicAuth = mode switch
        {
            McpAuthMode.ClientCredentials => new ClientCredentialsTokenProvider(
                server.Authority!, server.ClientId!, secrets.ClientSecret!, [.. server.Scopes ?? []]),
            McpAuthMode.InteractiveOAuth => secrets.OAuthTokenProvider,
            _ => null,
        };
        var transport = dynamicAuth is null
            ? new HttpClientTransport(options, _loggerFactory)
            : new HttpClientTransport(
                options,
                new HttpClient(new AuthHeaderHandler(dynamicAuth) { InnerHandler = new SocketsHttpHandler() }),
                _loggerFactory,
                ownsHttpClient: true);

        var router = new McpElicitationRouter();
        var client = await McpClient.CreateAsync(
            transport, McpElicitationClient.Options(router), _loggerFactory, cancellationToken);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            // McpClientTool : AIFunction : AITool (Step 0 spike). The provider wraps each for truncation.
            IReadOnlyList<AITool> raw = [.. tools.Cast<AITool>()];
            return new McpServerConnection(raw, client, router);
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
/// Wraps an MCP tool (an <see cref="AIFunction"/>) so its result is bounded to
/// <see cref="McpToolLimits.MaxToolResultLength"/> before it is persisted into the 2 MB session (ADR 0004), with any
/// image taken out and shown to the model for this turn only (<see cref="McpToolResultShaper"/>, ADR 0109).
/// <see cref="DelegatingAIFunction"/> forwards Name/Description/JsonSchema unchanged (Step 0 spike), so the
/// model's view of the tool is identical — only the return value is bounded.
///
/// <para>It is also where the SDK's -32042 <see cref="ModelContextProtocol.UrlElicitationRequiredException"/>
/// becomes the Agents layer's <see cref="McpUrlElicitationRequiredException"/> (ADR 0095). Every MCP tool passes
/// through here, and the grain-side wrapper that handles the exception has no MCP package reference.</para>
/// </summary>
internal sealed class TruncatingMcpTool(AIFunction inner) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        object? raw;
        try
        {
            raw = await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        catch (ModelContextProtocol.UrlElicitationRequiredException ex)
        {
            throw McpElicitationClient.Translate(ex);
        }

        return McpToolResultShaper.Shape(
            raw, TurnContext.CurrentOrClosed, FunctionInvokingChatClient.CurrentContext?.CallContent.CallId);
    }
}
