using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// A scripted signal-cli daemon for the Signal tests (ADR 0111, phase 3): JSON-RPC calls are recorded and answered per
/// method, and each account's SSE stream is a pipe the test writes events into. It stands in at the
/// <see cref="HttpMessageHandler"/> level, under the real <see cref="SignalRpcClient"/>.
/// </summary>
public sealed class SignalDaemonStub : HttpMessageHandler, ISignalDaemon
{
    public static readonly Uri BaseUrl = new("http://127.0.0.1:8086/");

    private readonly ConcurrentDictionary<string, Pipe> _streams = new(StringComparer.Ordinal);

    /// <summary>Every JSON-RPC call, in order: the method and its params.</summary>
    public ConcurrentQueue<(string Method, JsonElement Params)> Calls { get; } = new();

    /// <summary>The accounts <c>listAccounts</c> answers with.</summary>
    public ConcurrentDictionary<string, string> Accounts { get; } = new(StringComparer.Ordinal);

    /// <summary>A JSON-RPC error to answer a method with: <c>(code, message)</c>.</summary>
    public ConcurrentDictionary<string, (int Code, string Message)> Errors { get; } = new(StringComparer.Ordinal);

    /// <summary>A raw <c>result</c> JSON to answer a method with, instead of the default.</summary>
    public ConcurrentDictionary<string, string> Results { get; } = new(StringComparer.Ordinal);

    /// <summary>Accounts whose event stream answers 400 (the daemon doesn't know them).</summary>
    public ConcurrentDictionary<string, bool> UnknownStreams { get; } = new(StringComparer.Ordinal);

    /// <summary>How many times each account's event stream was opened.</summary>
    public ConcurrentDictionary<string, int> StreamOpens { get; } = new(StringComparer.Ordinal);

    public IEnumerable<string> Methods => Calls.Select(c => c.Method);

    public Task<Uri> EnsureRunningAsync(CancellationToken cancellationToken) => Task.FromResult(BaseUrl);

    public SignalRpcClient Client() => new(new Factory(this), this);

    /// <summary>Writes one <c>receive</c> event into the account's open stream.</summary>
    public async Task PushAsync(string account, string envelopeJson)
    {
        var pipe = await StreamAsync(account);
        var frame = $"event:receive\ndata:{{\"account\":\"{account}\",\"envelope\":{envelopeJson}}}\n\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(frame));
    }

    /// <summary>Ends the account's current stream, as a daemon restart would.</summary>
    public async Task EndStreamAsync(string account)
    {
        if (_streams.TryRemove(account, out var pipe))
        {
            await pipe.Writer.CompleteAsync();
        }
    }

    private async Task<Pipe> StreamAsync(string account)
    {
        await TestWait.WaitUntilAsync(() => _streams.ContainsKey(account), $"the stream for {account}");

        return _streams[account];
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/v1/events")
        {
            return OpenStream(Uri.UnescapeDataString(request.RequestUri.Query.Split("account=")[1]));
        }

        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
        var method = body.GetProperty("method").GetString()!;
        var parameters = body.TryGetProperty("params", out var p) ? p.Clone() : default;
        Calls.Enqueue((method, parameters));
        return Json(Answer(method));
    }

    private HttpResponseMessage OpenStream(string account)
    {
        StreamOpens.AddOrUpdate(account, 1, (_, n) => n + 1);
        if (UnknownStreams.ContainsKey(account))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var pipe = new Pipe();
        _streams[account] = pipe;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) };
    }

    private string Answer(string method)
    {
        if (Errors.TryGetValue(method, out var error))
        {
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", error = new { code = error.Code, message = error.Message }, id = "1" });
        }

        var result = Results.TryGetValue(method, out var scripted) ? scripted
            : method == "listAccounts" ? JsonSerializer.Serialize(Accounts.Select(a => new { number = a.Key, aci = a.Value }))
            : method == "send" ? """{"timestamp":1700000000999,"results":[{"type":"SUCCESS"}]}"""
            : "{}";
        return $$"""{"jsonrpc":"2.0","result":{{result}},"id":"1"}""";
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
