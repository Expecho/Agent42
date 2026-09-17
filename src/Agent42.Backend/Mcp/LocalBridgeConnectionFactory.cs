using Agent42.Agents;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agent42.Backend;

/// <summary>
/// Thrown when a <see cref="McpAuthMode.LocalBridge"/> server has no live helper socket (ADR 0076). Thrown
/// <b>immediately</b>, so the tool provider skips the server without spending its connect budget, and
/// curated by <see cref="McpConnectionTester"/> into "local bridge not connected" — the user's next step
/// is to start the helper, not to fix a URL.
/// </summary>
public sealed class LocalBridgeNotConnectedException()
    : InvalidOperationException("The local bridge for this MCP server is not connected.");

/// <summary>
/// <see cref="IMcpConnectionFactory"/> decorator (ADR 0076): a <see cref="McpAuthMode.LocalBridge"/> server
/// is reached over a channel of its live bridge socket; every other mode goes to the inner factory
/// unchanged. The backend stays the MCP client — the channel is just a transport, so
/// <c>McpClient.CreateAsync</c> + <c>ListToolsAsync</c> run exactly as in <see cref="McpConnectionFactory"/>.
///
/// <para>Registered <b>after</b> the inner factory in both hosts (it resolves the previous registration);
/// <c>Hosts_RegisterLocalBridge</c> pins that order.</para>
/// </summary>
public sealed class LocalBridgeConnectionFactory : IMcpConnectionFactory
{
    private readonly IMcpConnectionFactory _inner;
    private readonly LocalBridgeRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;

    public LocalBridgeConnectionFactory(IMcpConnectionFactory inner, LocalBridgeRegistry registry, ILoggerFactory loggerFactory)
    {
        _inner = inner;
        _registry = registry;
        _loggerFactory = loggerFactory;
    }

    public async Task<McpServerConnection> ConnectAsync(
        StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<McpAuthMode>(server.Auth, ignoreCase: true, out var mode) || mode != McpAuthMode.LocalBridge)
        {
            return await _inner.ConnectAsync(server, secrets, cancellationToken);
        }

        // No user id reaches this seam, so the socket is found by the key hash on the owner's own stored record
        // (see LocalBridgeRegistry). Not connected → throw at once, never wait out the caller's budget.
        var connection = FindFor(server) ?? throw new LocalBridgeNotConnectedException();
        var channel = await connection.OpenChannelAsync(cancellationToken);

        var client = await McpClient.CreateAsync(
            new ChannelClientTransport(server.Name, channel), loggerFactory: _loggerFactory, cancellationToken: cancellationToken);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            IReadOnlyList<AITool> raw = [.. tools.Cast<AITool>()];
            return new McpServerConnection(raw, client);
        }
        catch
        {
            // Mirrors McpConnectionFactory: connected but listing failed — close the channel before rethrowing.
            await client.DisposeAsync();
            throw;
        }
    }

    private ILocalBridgeConnection? FindFor(StoredMcpServer server) =>
        server.BridgeKeyHash is { Length: > 0 } hash ? _registry.FindByKeyHash(hash) : null;

    // McpClient.CreateAsync takes an IClientTransport; the channel is already open, so "connect" hands it over.
    private sealed class ChannelClientTransport(string name, ITransport channel) : IClientTransport
    {
        public string Name => name;

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(channel);
    }
}
