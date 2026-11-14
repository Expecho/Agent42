using System.Diagnostics;

namespace Agent42.Agents;

/// <summary>
/// Pure projection of an OpenTelemetry gen_ai span into a <see cref="UsageEvent"/> (ADR 0031). The
/// attribute names and the operation-name whitelist are spike-verified against Microsoft.Agents.AI
/// 1.13.0 / Microsoft.Extensions.AI 10.6.0 (2026-07-22): all spans emit on the wrapped
/// <c>Agent42.Backend</c> source, tokens ride <c>chat</c> spans, tool calls ride <c>execute_tool</c>
/// spans, and <c>orchestrate_tools</c> re-reports the SUM of its child chat spans — so counting it
/// double-counts. This projector therefore keeps <c>chat</c> (tokens) and <c>execute_tool</c> (tool)
/// and drops everything else.
///
/// <para>The core <see cref="Project"/> takes a plain tag bag so it is unit-testable without an
/// <see cref="Activity"/>; <see cref="FromActivity"/> is the thin adapter the processor uses.</para>
/// </summary>
public static class UsageSpanProjector
{
    // gen_ai semantic-convention attribute keys actually emitted (spike-verified).
    public const string OperationNameTag = "gen_ai.operation.name";
    public const string InputTokensTag = "gen_ai.usage.input_tokens";
    public const string OutputTokensTag = "gen_ai.usage.output_tokens";
    // Emitted by NEITHER version tested (kept for forward-compat; resolve to 0) — see ADR 0031.
    public const string CacheReadTokensTag = "gen_ai.usage.cache_read.input_tokens";
    public const string ReasoningTokensTag = "gen_ai.usage.reasoning.output_tokens";
    public const string RequestModelTag = "gen_ai.request.model";
    public const string ResponseModelTag = "gen_ai.response.model";
    public const string ProviderNameTag = "gen_ai.provider.name";
    public const string ToolNameTag = "gen_ai.tool.name";
    public const string ErrorTypeTag = "error.type";

    public const string ChatOperation = "chat";
    public const string ExecuteToolOperation = "execute_tool";

    // The per-agent "application" dimension (ADR 0031). The grain publishes the agent name into Orleans
    // RequestContext under <see cref="ApplicationContextKey"/> for the turn; the span processor stamps it
    // onto the in-grain spans as <see cref="ApplicationTag"/> (the ADR 0010 identity mechanism); this
    // projector reads the tag, falling back to the caller's default (the service constant) when a turn is
    // not bound to an agent.
    public const string ApplicationContextKey = "agent42.usage.application";
    public const string ApplicationTag = "agent42.application";

    // The serving provider's kind (ADR 0110), stamped on the chat span by the Backend's ProviderKindTagChatClient.
    // gen_ai.provider.name cannot stand in for it: it reads "openai" for Azure AND every OpenAI-compatible endpoint.
    public const string ProviderKindTag = "agent42.provider.kind";

    /// <summary>
    /// Projects a span's tags into a <see cref="UsageEvent"/>, or <c>null</c> when the span is
    /// neither a chat completion nor a tool execution (dropped, including
    /// <c>orchestrate_tools</c>). A span is treated as errored when the OTel activity status is
    /// error OR an <c>error.type</c> tag is present.
    /// </summary>
    public static UsageEvent? Project(
        IReadOnlyDictionary<string, object?> tags, DateTimeOffset timestamp, bool activityErrored, string application)
    {
        var operation = Str(tags, OperationNameTag);
        var errored = activityErrored || !string.IsNullOrEmpty(Str(tags, ErrorTypeTag));
        var user = UsageUserKey.From(
            Str(tags, UserTelemetryContext.UserEmailTag),
            Str(tags, UserTelemetryContext.UserNameTag),
            Str(tags, UserTelemetryContext.UserIdTag));
        var userId = Str(tags, UserTelemetryContext.UserIdTag) ?? "";

        // Per-agent application if the grain stamped one this turn, else the caller's default constant.
        var app = FirstNonEmpty(Str(tags, ApplicationTag));
        if (app.Length == 0)
        {
            app = application;
        }

        switch (operation)
        {
            case ChatOperation:
                var model = FirstNonEmpty(Str(tags, ResponseModelTag), Str(tags, RequestModelTag));
                return new UsageEvent(
                    UsageEventKind.Token, timestamp, app,
                    Provider: Str(tags, ProviderNameTag) ?? "",
                    Model: model,
                    UserKey: user,
                    ToolName: "",
                    InputTokens: Long(tags, InputTokensTag),
                    OutputTokens: Long(tags, OutputTokensTag),
                    CachedInputTokens: Long(tags, CacheReadTokensTag),
                    ReasoningOutputTokens: Long(tags, ReasoningTokensTag),
                    IsError: errored,
                    ProviderKind: Str(tags, ProviderKindTag) ?? "",
                    UserId: userId);

            case ExecuteToolOperation:
                return new UsageEvent(
                    UsageEventKind.Tool, timestamp, app,
                    Provider: "", Model: "", UserKey: user,
                    ToolName: Str(tags, ToolNameTag) ?? "",
                    InputTokens: 0, OutputTokens: 0, CachedInputTokens: 0, ReasoningOutputTokens: 0,
                    IsError: errored,
                    UserId: userId);

            default:
                return null;
        }
    }

    /// <summary>Adapter: builds the tag bag from a finished <see cref="Activity"/> and projects it.</summary>
    public static UsageEvent? FromActivity(Activity activity, string application)
    {
        var tags = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in activity.TagObjects)
        {
            tags[tag.Key] = tag.Value;
        }

        var start = activity.StartTimeUtc == default
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero);
        var timestamp = start + activity.Duration;

        return Project(tags, timestamp, activity.Status == ActivityStatusCode.Error, application);
    }

    private static string? Str(IReadOnlyDictionary<string, object?> tags, string key)
        => tags.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static long Long(IReadOnlyDictionary<string, object?> tags, string key)
    {
        if (tags.TryGetValue(key, out var value) && value is not null)
        {
            try
            {
                return Convert.ToInt64(value);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return 0;
            }
        }

        return 0;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate))
            {
                return candidate;
            }
        }

        return "";
    }
}
