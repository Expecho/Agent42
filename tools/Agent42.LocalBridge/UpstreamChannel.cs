using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.LocalBridge;

/// <summary>
/// Where a channel's MCP messages go on this machine (ADR 0076). v1 has one implementation,
/// <see cref="HttpUpstream"/>; an adapter for a CLI-shaped server such as <c>agent-browser</c> is the
/// planned second, and this interface is the seam for it.
/// </summary>
public interface IUpstream
{
    /// <summary>Opens a fresh MCP session. One per bridge channel.</summary>
    Task<ITransport> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A streamable-HTTP MCP server on a local URL — BrowserOS neo, measured at <c>127.0.0.1:9210</c>.
///
/// <para><b>Never add an <c>Origin</c> header.</b> Neo answers any request carrying <c>Origin</c> (or
/// <c>Sec-Fetch-*</c>) with 403 — that is what keeps a web page from driving it — and a .NET
/// <see cref="HttpClient"/> sends none by default. <c>Upstream_SendsNoOrigin</c> pins it.</para>
/// </summary>
public sealed class HttpUpstream : IUpstream
{
    private readonly Uri _endpoint;
    private readonly Func<HttpMessageHandler>? _handler;

    public HttpUpstream(Uri endpoint, Func<HttpMessageHandler>? handler = null)
    {
        _endpoint = endpoint;
        _handler = handler;
    }

    public Task<ITransport> ConnectAsync(CancellationToken cancellationToken)
    {
        var options = new HttpClientTransportOptions { Endpoint = _endpoint, Name = "upstream" };
        var transport = _handler is null
            ? new HttpClientTransport(options, NullLoggerFactory.Instance)
            : new HttpClientTransport(options, new HttpClient(_handler()), NullLoggerFactory.Instance, ownsHttpClient: true);
        return transport.ConnectAsync(cancellationToken);
    }
}

/// <summary>
/// One bridge channel (ADR 0076): its own upstream MCP session, messages relayed verbatim in both directions,
/// with every <c>tools/call</c> from the backend passing <see cref="ConfirmationGate"/> first and every
/// <c>tools/list</c> answer feeding the gate's annotations on the way back.
///
/// <para>Backend → upstream messages are processed <b>in order</b> through a queue, so an <c>open</c>
/// immediately followed by <c>initialize</c> cannot race the upstream connect, and a call waiting on the user
/// holds back only its own channel.</para>
/// </summary>
public sealed class UpstreamChannel : IAsyncDisposable
{
    private readonly int _id;
    private readonly IUpstream _upstream;
    private readonly ConfirmationGate _gate;
    private readonly Func<BridgeEnvelope, Task> _send;
    private readonly Action<string> _log;
    private readonly Channel<JsonElement> _inbound = Channel.CreateUnbounded<JsonElement>(new() { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<RequestId> _pendingToolLists = [];
    private readonly Lock _lock = new();
    private readonly Task _run;
    private ITransport? _transport;
    private int _closed;

    public UpstreamChannel(int id, IUpstream upstream, ConfirmationGate gate, Func<BridgeEnvelope, Task> send, Action<string> log)
    {
        _id = id;
        _upstream = upstream;
        _gate = gate;
        _send = send;
        _log = log;
        _run = Task.Run(RunAsync);
    }

    /// <summary>Queues one JSON-RPC message from the backend.</summary>
    public void Enqueue(JsonElement message) => _inbound.Writer.TryWrite(message);

    private async Task RunAsync()
    {
        try
        {
            _transport = await _upstream.ConnectAsync(_stop.Token);
            var pump = PumpUpstreamAsync(_transport);
            await foreach (var message in _inbound.Reader.ReadAllAsync(_stop.Token))
            {
                await ForwardAsync(_transport, message);
            }

            await pump;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // disposed
        }
        catch (Exception ex)
        {
            _log($"channel {_id}: upstream failed ({ex.GetType().Name}); closing the channel.");
            await CloseTowardBackendAsync();
        }
    }

    private async Task ForwardAsync(ITransport transport, JsonElement raw)
    {
        var message = raw.Deserialize<JsonRpcMessage>(McpJsonUtilities.DefaultOptions);
        switch (message)
        {
            case JsonRpcRequest { Method: RequestMethods.ToolsCall } call:
                var name = call.Params?["name"]?.GetValue<string>() ?? "";
                var decision = await _gate.ReviewAsync(name, call.Params?["arguments"], _stop.Token);
                if (!decision.Allowed)
                {
                    _log($"channel {_id}: declined {ConfirmationGate.Sanitize(name, keepNewlines: false)}.");
                    await SendToBackendAsync(new JsonRpcResponse { Id = call.Id, Result = ConfirmationGate.DenialResult(decision.DenialText!) });
                    return;
                }

                break;
            case JsonRpcRequest { Method: RequestMethods.ToolsList } list:
                lock (_lock)
                {
                    _pendingToolLists.Add(list.Id);
                }

                break;
            case null:
                return;
        }

        await transport.SendMessageAsync(message, _stop.Token);
    }

    private async Task PumpUpstreamAsync(ITransport transport)
    {
        try
        {
            await foreach (var message in transport.MessageReader.ReadAllAsync(_stop.Token))
            {
                if (message is JsonRpcResponse response && IsToolListAnswer(response))
                {
                    _gate.ObserveToolsList(response.Result);
                }

                await SendToBackendAsync(message);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log($"channel {_id}: upstream stream failed ({ex.GetType().Name}).");
        }

        // The upstream session ended on its own (or broke): the backend's MCP client must not wait on it.
        await CloseTowardBackendAsync();
    }

    private bool IsToolListAnswer(JsonRpcResponse response)
    {
        lock (_lock)
        {
            return _pendingToolLists.Remove(response.Id);
        }
    }

    private Task SendToBackendAsync(JsonRpcMessage message) =>
        _send(new BridgeEnvelope(
            _id, BridgeEnvelope.MessageType, JsonSerializer.SerializeToElement(message, McpJsonUtilities.DefaultOptions)));

    private async Task CloseTowardBackendAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            try
            {
                await _send(new BridgeEnvelope(_id, BridgeEnvelope.Close));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
            {
                // the bridge is going away anyway
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        _inbound.Writer.TryComplete();
        await _stop.CancelAsync();
        try
        {
            await _run;
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }
    }
}
