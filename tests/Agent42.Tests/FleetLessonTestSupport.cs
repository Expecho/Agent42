using System.Text.RegularExpressions;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>A catalog with two tool-capable models, one without tools and one that only a Copilot agent can use.</summary>
public sealed class LessonCatalogs : IUserModelCatalogProvider, IModelCatalog
{
    public const string ModelA = "fake:model-a";
    public const string ModelB = "fake:model-b";
    public const string NoToolsModel = "fake:text-only";
    public const string CopilotModel = "copilot:gpt";

    /// <summary>The catalog default; a test sets it to a model that "vanished".</summary>
    public string Default { get; set; } = ModelA;

    public Task<IModelCatalog> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IModelCatalog>(this);

    public Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelCatalogResult(Default,
        [
            new ModelInfo(ModelA, "fake", "Model A", "a", null, SupportsTools: true, SupportsVision: false),
            new ModelInfo(ModelB, "fake", null, "b", null, SupportsTools: true, SupportsVision: false),
            new ModelInfo(NoToolsModel, "fake", null, "t", null, SupportsTools: false, SupportsVision: false),
            new ModelInfo(CopilotModel, "copilot", null, "c", null, SupportsTools: true, SupportsVision: false, CopilotOnly: true),
        ]));
}

/// <summary>What a <see cref="ScriptedDreamClient"/> script sees on each model call.</summary>
public sealed record DreamCall(int Number, string Digest, string? LastToolResult)
{
    private static readonly Regex TaskLine = new(@"^Task (\S+) \(workspace (\S+)\)", RegexOptions.Multiline);

    /// <summary>The task ids in the digest, newest last, and their workspaces.</summary>
    public IReadOnlyList<(string Id, string Workspace)> Tasks => [.. TaskLine.Matches(Digest).Select(m => (m.Groups[1].Value, m.Groups[2].Value))];
}

/// <summary>
/// The dream's model, scripted: each call runs <see cref="Script"/>, which replies with <see cref="Submit"/> (a
/// <c>submit_lessons</c> call whose evidence ids it builds from the digest it was given) or <see cref="Say"/>. Records the
/// call count, the instructions and user text, and the telemetry context and application name in force at call time.
/// </summary>
public sealed class ScriptedDreamClient(Func<DreamCall, ChatResponse> script) : IChatClient
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);
    public string? Instructions { get; private set; }
    public string? UserText { get; private set; }
    public object? SeenUser { get; private set; }
    public object? SeenApplication { get; private set; }

    /// <summary>When set, a call waits for it before answering (a gated dream).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public static ChatResponse Say(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse Submit(params object[] lessons)
        => new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), "submit_lessons", new Dictionary<string, object?> { ["lessons"] = lessons })]));

    public static object Proposed(string text, IEnumerable<string> evidence, string topic = FleetLessonTopics.Staging, string confidence = FleetLessonConfidence.Medium)
        => new { text, topic, confidence, evidence = evidence.ToArray() };

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var number = Interlocked.Increment(ref _calls);
        Instructions = options?.Instructions;
        UserText = string.Join("\n", list.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        SeenUser = Orleans.Runtime.RequestContext.Get(UserTelemetryContext.RequestContextKey);
        SeenApplication = Orleans.Runtime.RequestContext.Get(UsageSpanProjector.ApplicationContextKey);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        var lastResult = list.LastOrDefault()?.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.Result?.ToString();
        return script(new DreamCall(number, UserText, lastResult));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
