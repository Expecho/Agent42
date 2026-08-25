using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IChatClient"/> that records the <see cref="ChatOptions"/> one agent run produced and
/// answers nothing useful. Where <see cref="FakeChatClient"/> echoes tool NAMES into its reply (enough for
/// the grain tests, which only ask which tools were registered), this hands back the live
/// <see cref="AIFunction"/> objects — so a test can actually <b>invoke</b> a tool and assert on what it
/// returns, which is the only way to prove a skill's resources are readable rather than merely on disk.
/// </summary>
public sealed class ToolCapturingChatClient : IChatClient
{
    /// <summary>The tools the last run registered.</summary>
    public IReadOnlyList<AITool> Tools { get; private set; } = [];

    /// <summary>The instructions the last run sent — where <c>AgentSkillsProvider</c> advertises each skill.</summary>
    public string? Instructions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Capture(options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Capture(options);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to release.
    }

    private void Capture(ChatOptions? options)
    {
        Tools = options?.Tools?.ToList() ?? [];
        Instructions = options?.Instructions;
    }
}
