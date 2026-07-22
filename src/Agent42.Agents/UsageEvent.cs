namespace Agent42.Agents;

/// <summary>
/// What a projected span contributes to the usage rollups. Token-bearing <c>chat</c> spans and
/// tool <c>execute_tool</c> spans are the only two the projector keeps (ADR 0031); everything else
/// — notably <c>orchestrate_tools</c>, whose usage is a SUM over its child chat spans — is dropped
/// to avoid double counting.
/// </summary>
[GenerateSerializer]
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
/// <para>Cached/reasoning tokens are carried for wire-contract parity with the reference dashboard
/// but are always 0 in practice: the Microsoft.Extensions.AI 10.6.0 OTel emitter emits only
/// input/output token attributes (spike-verified 2026-07-22) — see ADR 0031.</para>
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
