using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// The agent fleet's side of the fake (ADR 0097). A fleet task carries <see cref="FleetMarker"/> plus a token naming a
/// <see cref="FleetScript"/>, which says what the planner submits (in order, one per planner call or rejection) and
/// what the judge scores (in order; none left means the judge gives no verdict). The group chat moderator always
/// finishes. Agents fall through to the ordinary canned reply, which is a non-empty result.
/// </summary>
public sealed partial class FakeChatClient
{
    public const string FleetMarker = "[[fleet]]";

    private static readonly ConcurrentDictionary<string, FleetScript> FleetScripts = new();

    /// <summary>A fleet task script. Plans are <c>submit_team_plan</c> arguments as anonymous objects.</summary>
    public sealed class FleetScript(
        IReadOnlyList<object> plans, IReadOnlyList<int>? scores = null, long usagePerCall = 0, IReadOnlyList<string>? agentReplies = null,
        IReadOnlyList<(string Name, Dictionary<string, object?> Arguments)>? agentCalls = null)
    {
        private int _plans;
        private int _scores;
        private int _agentReplies;
        private int _agentCalls;

        /// <summary>Function calls the team's agents make, one per agent turn that doesn't follow a function result, in
        /// order; once they are used up agents just reply.</summary>
        public IReadOnlyList<(string Name, Dictionary<string, object?> Arguments)> AgentCalls { get; } = agentCalls ?? [];

        /// <summary>The user text of every judge call, in order.</summary>
        public ConcurrentQueue<string> JudgeInputs { get; } = new();

        /// <summary>Replies of the team's agents, one per agent call, in order; none means the canned reply.</summary>
        public IReadOnlyList<string> AgentReplies { get; } = agentReplies ?? [];

        public IReadOnlyList<object> Plans { get; } = plans;
        public IReadOnlyList<int> Scores { get; } = scores ?? [];

        /// <summary>Input tokens every planner, judge and moderator reply reports, for the token-cap tests.</summary>
        public long UsagePerCall { get; } = usagePerCall;

        public int PlannerCalls => Volatile.Read(ref _plans);
        public int JudgeCalls => Volatile.Read(ref _scores);

        internal object? NextPlan() => Interlocked.Increment(ref _plans) - 1 is var i && i < Plans.Count ? Plans[i] : null;

        internal string? NextAgentReply()
            => AgentReplies.Count > 0 ? AgentReplies[Math.Min(Interlocked.Increment(ref _agentReplies) - 1, AgentReplies.Count - 1)] : null;

        internal (string Name, Dictionary<string, object?> Arguments)? NextAgentCall()
            => Interlocked.Increment(ref _agentCalls) - 1 is var i && i < AgentCalls.Count ? AgentCalls[i] : null;

        internal int? NextScore() => Interlocked.Increment(ref _scores) - 1 is var i && i < Scores.Count ? Scores[i] : null;
    }

    /// <summary>The task text that makes this fake follow <paramref name="script"/>.</summary>
    public static string FleetTask(string text, FleetScript script)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        FleetScripts[token] = script;
        return $"{text} {FleetMarker}{token}";
    }

    /// <summary>A Prompt agent's YAML, as a planner writes it.</summary>
    public static string FleetAgentYaml(string name, string? model = null, params string[] tools)
    {
        var yaml = $"kind: Prompt\nname: {name}\ndescription: {name} does its part\ninstructions: |\n  You are {name}.\n";
        if (model is not null)
        {
            yaml += $"model:\n  id: {model}\n";
        }

        if (tools.Length > 0)
        {
            yaml += "tools:\n" + string.Concat(tools.Select(t => $"  - kind: function\n    name: {t}\n    description: use {t}\n"));
        }

        return yaml;
    }

    // The scripted reply of a fleet planner, judge or moderator call, or null when this is not one.
    private static ChatResponse? FleetReply(IReadOnlyList<ChatMessage> list, ChatOptions? options)
    {
        var tools = options?.Tools?.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        if (tools.Contains("finish") && tools.Contains("next_speaker"))
        {
            return Fleet(new FunctionCallContent(FleetCallId(), "finish", new Dictionary<string, object?> { ["reason"] = "done" }), FindScript(list));
        }

        if (FindScript(list) is not { } script)
        {
            return null;
        }

        var lastResult = list.LastOrDefault()?.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.Result?.ToString();
        if (!tools.Contains("submit_team_plan") && !tools.Contains("submit_verdict"))
        {
            if (lastResult is null && script.NextAgentCall() is { } call)
            {
                return Fleet(new FunctionCallContent(FleetCallId(), call.Name, new Dictionary<string, object?>(call.Arguments)), script);
            }

            return script.NextAgentReply() is { } reply ? Fleet(new TextContent(reply), script) : null;
        }

        if (tools.Contains("submit_verdict") && lastResult is null)
        {
            script.JudgeInputs.Enqueue(string.Join("\n", list.Where(m => m.Role == ChatRole.User).Select(m => m.Text)));
        }

        return tools.Contains("submit_team_plan") ? PlannerReply(script, lastResult) : JudgeReply(script, lastResult);
    }

    private static ChatResponse PlannerReply(FleetScript script, string? lastResult)
    {
        // After an accepted plan, or a refusal with nothing left to try, the planner just confirms in text.
        if (lastResult is not null && !lastResult.Contains("rejected", StringComparison.OrdinalIgnoreCase))
        {
            return Fleet(new TextContent("The team is ready."), script);
        }

        if (script.NextPlan() is not { } plan)
        {
            return Fleet(new TextContent("I cannot design a team for this."), script);
        }

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(plan));
        var args = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return Fleet(new FunctionCallContent(FleetCallId(), "submit_team_plan", args), script);
    }

    private static ChatResponse JudgeReply(FleetScript script, string? lastResult)
    {
        if (lastResult is not null || script.NextScore() is not { } score)
        {
            return Fleet(new TextContent("Reviewed."), script);
        }

        return Fleet(new FunctionCallContent(FleetCallId(), "submit_verdict", new Dictionary<string, object?>
        {
            ["score"] = score,
            ["feedback"] = $"scored {score}",
        }), script);
    }

    private static ChatResponse Fleet(AIContent content, FleetScript? script)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [content]));
        if (script is { UsagePerCall: > 0 })
        {
            response.Usage = new UsageDetails { InputTokenCount = script.UsagePerCall, OutputTokenCount = 0 };
        }

        return response;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FleetStream(ChatResponse response)
    {
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
            await Task.Yield();
        }
    }

    private static string FleetCallId() => "fleet-call-" + Guid.NewGuid().ToString("N")[..8];

    private static FleetScript? FindScript(IReadOnlyList<ChatMessage> list)
    {
        foreach (var message in list)
        {
            var text = message.Text ?? "";
            var at = text.IndexOf(FleetMarker, StringComparison.Ordinal);
            if (at >= 0 && text.Length >= at + FleetMarker.Length + 12
                && FleetScripts.TryGetValue(text.Substring(at + FleetMarker.Length, 12), out var script))
            {
                return script;
            }
        }

        return null;
    }
}
