using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent42.Tests;

/// <summary>
/// A loopback HTTP server that stands in for every telemetry sink the App Insights tests export to: the
/// Application Insights ingestion endpoint (JSON lines, possibly gzipped), an OTLP/HTTP collector
/// (<c>/v1/traces</c>) and, with <paramref name="http2"/>, an OTLP/gRPC collector. It records each request
/// body as text, which is enough to find a sentinel: OTLP protobuf carries strings as raw UTF-8.
///
/// <para>Constructing one also switches Azure Monitor Statsbeat off for the process, so no test sends
/// telemetry to Microsoft's own ingestion keys. The exporter reads the variable when it is built, and its
/// option for this is internal.</para>
/// </summary>
internal sealed class TelemetryLoopback : IAsyncDisposable
{
    public const string StatsbeatDisabledVariable = "APPLICATIONINSIGHTS_STATSBEAT_DISABLED";

    private readonly WebApplication app;
    private readonly ConcurrentQueue<Received> requests = new();

    private TelemetryLoopback(bool http2)
    {
        Environment.SetEnvironmentVariable(StatsbeatDisabledVariable, "true");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(
            IPAddress.Loopback, 0, listen => listen.Protocols = http2 ? HttpProtocols.Http2 : HttpProtocols.Http1));

        app = builder.Build();
        app.Run(HandleAsync);
    }

    /// <summary>The base address, with a trailing slash.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>An App Insights connection string whose ingestion endpoint is this server.</summary>
    public string ConnectionString =>
        $"InstrumentationKey=00000000-0000-0000-0000-000000000042;IngestionEndpoint={BaseAddress}";

    public IReadOnlyList<Received> Requests => [.. requests];

    /// <summary>Every Application Insights telemetry item received, one JSON document per item.</summary>
    public IReadOnlyList<string> AppInsightsItems =>
        [.. Requests.Where(r => r.Path.Contains("/track", StringComparison.Ordinal))
            .SelectMany(r => r.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];

    /// <summary>Every OTLP trace export body received, over HTTP or gRPC.</summary>
    public IReadOnlyList<string> OtlpTraceBodies =>
        [.. Requests.Where(r => r.Path.EndsWith("/v1/traces", StringComparison.Ordinal)
                || r.Path.EndsWith("TraceService/Export", StringComparison.Ordinal))
            .Select(r => r.Body)];

    public static async Task<TelemetryLoopback> StartAsync(bool http2 = false)
    {
        var loopback = new TelemetryLoopback(http2);
        await loopback.app.StartAsync();

        var address = loopback.app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        loopback.BaseAddress = new Uri(address.TrimEnd('/') + "/");
        return loopback;
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    private async Task HandleAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        if (context.Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            await gzip.CopyToAsync(inflated);
            bytes = inflated.ToArray();
        }

        var body = Encoding.UTF8.GetString(bytes);
        var headers = context.Request.Headers.ToDictionary(
            header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        requests.Enqueue(new Received(context.Request.Path.Value ?? "", body, headers));

        if (context.Request.ContentType?.StartsWith("application/grpc", StringComparison.Ordinal) == true)
        {
            // An empty ExportTraceServiceResponse, length-prefixed, and an OK status in the trailers.
            context.Response.ContentType = "application/grpc";
            await context.Response.Body.WriteAsync(new byte[5]);
            context.Response.AppendTrailer("grpc-status", "0");
            return;
        }

        if (context.Request.Path.Value?.Contains("/track", StringComparison.Ordinal) == true)
        {
            var items = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($$"""{"itemsReceived":{{items}},"itemsAccepted":{{items}},"errors":[]}""");
            return;
        }

        context.Response.ContentType = "application/x-protobuf";
    }

    public sealed record Received(string Path, string Body, IReadOnlyDictionary<string, string> Headers);
}
