using System.Diagnostics;
using System.Threading.Channels;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Agent42.Tests;

/// <summary>
/// ADR 0121: a non-owner's prompt and completion text never reaches the OTLP collector or the <c>/logs</c> ring,
/// and an owner's does. Built on the real composition (<c>ConfigureOpenTelemetry</c> with the redactor as its
/// before-exporters hook, then <c>AddBackendTelemetry</c>), which is what proves the ORDER: a redactor registered
/// after the exporters would leave the collector holding every stranger's prompt.
/// </summary>
[Collection("LiveTelemetry")]
public sealed class TelemetryRedactionTests
{
    private const string OwnerOid = "oid-owner";
    private const string StrangerOid = "oid-stranger";
    private const string OwnerContent = "SENTINEL_OWNER_CONTENT_7a1f";
    private const string StrangerContent = "SENTINEL_STRANGER_CONTENT_3c9d";
    private const string AnonymousContent = "SENTINEL_ANONYMOUS_CONTENT_5e2b";

    private static string NewSourceName() => "Agent42.Tests.Redaction." + Guid.NewGuid().ToString("N");

    private static IHost BuildHost(Dictionary<string, string?> config, string sourceName, bool registerPolicy = true)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(config);
        if (registerPolicy)
        {
            builder.Services.AddSingleton<IOwnerPolicy>(new OwnerPolicy([OwnerOid]));
        }

        // Exactly as Program.cs does it: the redactor rides the before-exporters hook.
        builder.ConfigureOpenTelemetry(NonOwnerContentRedactor.Register);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(sourceName));
        builder.AddBackendTelemetry(Channel.CreateUnbounded<UsageEvent>().Writer);
        return builder.Build();
    }

    private static void EmitChatSpan(string sourceName, string label, string? userId, string content)
    {
        using var source = new ActivitySource(sourceName);
        using var activity = source.StartActivity("chat " + label)!;
        activity.SetTag("gen_ai.operation.name", "chat");
        activity.SetTag("gen_ai.request.model", "gpt-test");
        activity.SetTag("gen_ai.usage.input_tokens", 11);
        activity.SetTag(UsageSpanProjector.ProviderKindTag, "AzureOpenAI");
        activity.SetTag("gen_ai.input.messages", content);
        activity.SetTag("gen_ai.output.messages", content);
        activity.SetTag("gen_ai.system_instructions", content);
        activity.SetTag("gen_ai.tool.call.arguments", content);
        activity.SetTag("gen_ai.tool.call.result", content);
        if (userId is not null)
        {
            activity.SetTag(UserTelemetryContext.UserIdTag, userId);
        }
    }

    private static void EmitAll(IHost host, string sourceName)
    {
        var provider = host.Services.GetRequiredService<TracerProvider>();
        EmitChatSpan(sourceName, "owner", OwnerOid, OwnerContent);
        EmitChatSpan(sourceName, "stranger", StrangerOid, StrangerContent);
        EmitChatSpan(sourceName, "anonymous", userId: null, AnonymousContent);
        provider.ForceFlush();
    }

    /// <summary>
    /// AC14 on the OTLP sink: the owner's content is present, a non-owner's is absent, and so is a span with no
    /// <c>user.id</c> at all (a reminder or gateway run: "has an id and it is not an owner" would leak exactly those).
    /// The redacted span still arrives, with its metadata.
    /// </summary>
    [Fact]
    public async Task OtlpCollector_GetsOwnerContent_ButNeverNonOwnerOrAnonymousContent()
    {
        await using var otlp = await TelemetryLoopback.StartAsync();
        var sourceName = NewSourceName();
        using var host = BuildHost(new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlp.BaseAddress.ToString(),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        }, sourceName);

        EmitAll(host, sourceName);

        Assert.Contains(otlp.OtlpTraceBodies, body => body.Contains(OwnerContent, StringComparison.Ordinal));
        Assert.DoesNotContain(otlp.OtlpTraceBodies, body => body.Contains(StrangerContent, StringComparison.Ordinal));
        Assert.DoesNotContain(otlp.OtlpTraceBodies, body => body.Contains(AnonymousContent, StringComparison.Ordinal));

        // The span itself (its name and metadata) is still exported: redaction removes content, not spans.
        Assert.Contains(otlp.OtlpTraceBodies, body => body.Contains("chat stranger", StringComparison.Ordinal));
        Assert.Contains(otlp.OtlpTraceBodies, body => body.Contains("gpt-test", StringComparison.Ordinal));
    }

    /// <summary>The same property on the <c>/logs</c> ring, plus that the usage rollup's provider-kind tag survives.</summary>
    [Fact]
    public void LiveBuffer_KeepsOwnerContent_DropsNonOwnerContent_AndKeepsTheUsageTags()
    {
        var sourceName = NewSourceName();
        using var host = BuildHost([], sourceName);

        EmitAll(host, sourceName);

        var spans = host.Services.GetRequiredService<LiveTelemetryBuffer>().SpansSince(0, null).Spans
            .Where(span => span.Source == sourceName)
            .ToList();
        Assert.Equal(3, spans.Count);

        var owner = spans.Single(s => s.Name.Contains("owner", StringComparison.Ordinal));
        Assert.Contains(owner.Tags, tag => tag.Name == "gen_ai.input.messages" && tag.Value.Contains(OwnerContent, StringComparison.Ordinal));

        foreach (var redacted in spans.Where(s => !s.Name.Contains("owner", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(redacted.Tags, tag => tag.Value.Contains("SENTINEL_", StringComparison.Ordinal));
            Assert.DoesNotContain(redacted.Tags, tag => tag.Name is "gen_ai.input.messages" or "gen_ai.output.messages"
                or "gen_ai.system_instructions" or "gen_ai.tool.call.arguments" or "gen_ai.tool.call.result");

            // Metadata the usage dashboard needs is kept. The provider kind is read by UsageSpanProcessor AFTER
            // this processor runs, so stripping it would silently make every non-owner call unpriced.
            Assert.Contains(redacted.Tags, tag => tag.Name == UsageSpanProjector.ProviderKindTag);
            Assert.Contains(redacted.Tags, tag => tag.Name == "gen_ai.usage.input_tokens");
        }
    }

    /// <summary>Fail closed: a host with no <see cref="IOwnerPolicy"/> redacts everyone, the owner's id included.</summary>
    [Fact]
    public void WithNoPolicyRegistered_EverySpanIsRedacted()
    {
        var sourceName = NewSourceName();
        using var host = BuildHost([], sourceName, registerPolicy: false);

        EmitAll(host, sourceName);

        var spans = host.Services.GetRequiredService<LiveTelemetryBuffer>().SpansSince(0, null).Spans
            .Where(span => span.Source == sourceName);
        Assert.All(spans, span => Assert.DoesNotContain(span.Tags, tag => tag.Value.Contains("SENTINEL_", StringComparison.Ordinal)));
    }

    /// <summary>Only gen_ai spans are touched: an HTTP or Orleans span keeps its tags whoever it belongs to.</summary>
    [Fact]
    public void NonGenAiSpans_AreLeftAlone()
    {
        var sourceName = NewSourceName();
        using var host = BuildHost([], sourceName);
        var provider = host.Services.GetRequiredService<TracerProvider>();

        using (var source = new ActivitySource(sourceName))
        using (var activity = source.StartActivity("not-gen-ai")!)
        {
            activity.SetTag("custom.tag", "KEEP_ME");
            activity.SetTag(UserTelemetryContext.UserIdTag, StrangerOid);
        }

        provider.ForceFlush();

        var span = host.Services.GetRequiredService<LiveTelemetryBuffer>().SpansSince(0, null).Spans
            .Single(s => s.Source == sourceName);
        Assert.Contains(span.Tags, tag => tag.Name == "custom.tag" && tag.Value == "KEEP_ME");
    }

    /// <summary>
    /// AC15, the order pin. The redactor must run before every trace exporter: ServiceDefaults has to invoke the
    /// hook before it adds the exporters, and Program.cs has to hand the redactor to that hook. Either edit alone
    /// leaves the OTLP collector holding strangers' prompts, and nothing in the suite runs Program.cs.
    /// </summary>
    [Fact]
    public void RedactorHook_RunsBeforeTheExporters_AndProgramPassesIt()
    {
        var root = RepoRoot();
        var defaults = File.ReadAllText(Path.Combine(root, "src", "Agent42.ServiceDefaults", "Extensions.cs"));
        var hook = defaults.IndexOf("beforeTraceExporters?.Invoke(tracing)", StringComparison.Ordinal);
        var exporters = defaults.IndexOf("builder.AddOpenTelemetryExporters();", StringComparison.Ordinal);
        Assert.True(hook >= 0, "ServiceDefaults no longer invokes the before-exporters hook");
        Assert.True(hook < exporters, "The before-exporters hook runs after the exporters are added: too late to redact");

        var program = File.ReadAllText(Path.Combine(root, "src", "Agent42.Backend", "Program.cs"));
        Assert.Contains("builder.AddServiceDefaults(NonOwnerContentRedactor.Register)", program, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
