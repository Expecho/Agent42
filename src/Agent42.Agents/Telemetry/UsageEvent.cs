namespace Agent42.Agents;

/// <summary>
/// What a projected span contributes to the usage rollups. Token-bearing <c>chat</c> spans and
/// tool <c>execute_tool</c> spans are the only two the projector keeps (ADR 0031); everything else
/// — notably <c>orchestrate_tools</c>, whose usage is a SUM over its child chat spans — is dropped
/// to avoid double counting.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UsageEventKind")]
public enum UsageEventKind
{
    /// <summary>A <c>chat</c> completion span: carries input/output tokens, model, provider.</summary>
    Token,

    /// <summary>An <c>execute_tool</c> span: carries a tool name, no tokens.</summary>
    Tool,
}

/// <summary>
/// One normalized usage contribution projected from a single OpenTelemetry gen_ai span
/// (<see cref="UsageSpanProjector"/>). Crosses the grain boundary to
/// <see cref="IUsageRollupGrain.ApplyAsync"/>, so it is Orleans-serializable.
///
/// <para>Cached/reasoning tokens are whatever the provider reported: the Microsoft.Extensions.AI OTel emitter tags
/// them when the response's <c>UsageDetails</c> carry them (ADR 0104, <c>ChatSpan_CarriesCachedAndReasoningTokens</c>).
/// ADR 0031's "always 0" came from its spike provider reporting none, not from the emitter.</para>
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UsageEvent")]
public sealed record UsageEvent(
    [property: Id(0)] UsageEventKind Kind,
    [property: Id(1)] DateTimeOffset Timestamp,
    [property: Id(2)] string Application,
    [property: Id(3)] string Provider,
    [property: Id(4)] string Model,
    [property: Id(5)] string UserKey,
    [property: Id(6)] string ToolName,
    [property: Id(7)] long InputTokens,
    [property: Id(8)] long OutputTokens,
    [property: Id(9)] long CachedInputTokens,
    [property: Id(10)] long ReasoningOutputTokens,
    [property: Id(11)] bool IsError);
