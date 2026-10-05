using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The agent a copilot-kind chat runs: the Copilot loop (<paramref name="inner"/>), plus a mirror of each finished
/// run into the session's in-memory chat history.
///
/// <para><b>Two records of one chat.</b> The Copilot runtime keeps its own transcript in SessionFs (Blob), and that
/// is what the MODEL sees. The AF session it hands back is only a pointer (<c>{"sessionId":...}</c>, measured). So
/// every grain feature that reads history (<c>GetHistoryAsync</c>, attachments, retention, artifact compaction,
/// search, share links, follow-ups, the stopped-turn hand-append) reads this mirror instead, through the same
/// <c>TryGetInMemoryChatHistory</c> it uses for an ordinary chat. The two may diverge. Pruning or compacting the
/// mirror never reaches Copilot's transcript, and the ADR lists each case.</para>
///
/// <para><b>One assistant turn per user turn.</b> Copilot narrates around its tool calls ("Calling get_word to ...",
/// then the answer), and that arrives as several assistant messages. Stored as-is, history would show several
/// bubbles for one question, and stream recovery (ADR 0046) and tool-call replay (ADR 0080) both rest on there being
/// one. So the mirror strips the text from every message of the run, keeps the tool calls and results in order, and
/// stores the run's whole text, concatenated, as ONE final assistant message. That is exactly what the live
/// stream showed.</para>
///
/// <para>Only <see cref="TextContent"/>, <see cref="FunctionCallContent"/> and <see cref="FunctionResultContent"/>
/// are kept. A Copilot run also yields dozens of bare <see cref="AIContent"/> parts, which serialize as <c>{}</c>
/// (measured), and reasoning, which is never stored (ADR 0071).</para>
///
/// <para>A cancelled or failed run mirrors NOTHING, the same as a cancelled <c>ChatClientAgent</c> run, so the
/// grain's stopped-turn hand-append (ADR 0078) works unchanged.</para>
/// </summary>
public sealed class CopilotChatAgent(AIAgent inner, string authorName) : DelegatingAIAgent(inner)
{
    // Every session this agent hands out has a mirror, empty if need be. A ChatClientAgent's history provider
    // creates its state when a run starts, so the grain's stopped-turn hand-append always finds a list. Nothing
    // creates this one until a run completes, and a first turn that is stopped would otherwise find no history and
    // be dropped (measured: the stop test's history came back empty).
    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => WithMirror(await base.CreateSessionCoreAsync(cancellationToken));

    protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => WithMirror(await base.DeserializeSessionCoreAsync(serializedState, jsonSerializerOptions, cancellationToken));

    private static AgentSession WithMirror(AgentSession session)
    {
        if (!session.TryGetInMemoryChatHistory(out List<ChatMessage>? _))
        {
            session.SetInMemoryChatHistory([]);
        }

        return session;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        CancellationToken cancellationToken = default)
    {
        var input = messages.ToList();
        var response = await base.RunCoreAsync(input, session, options, cancellationToken);
        Mirror(session, input, response.Messages);
        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = messages.ToList();
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in base.RunCoreStreamingAsync(input, session, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        Mirror(session, input, updates.ToAgentResponse().Messages);
    }

    // Appends one finished run to the session's in-memory history: the input as sent, the run's tool calls and
    // results in order, then the run's text as one assistant message.
    private void Mirror(AgentSession? session, List<ChatMessage> input, IList<ChatMessage> response)
    {
        if (session is null)
        {
            return;
        }

        var history = session.TryGetInMemoryChatHistory(out List<ChatMessage>? stored) && stored is not null
            ? stored
            : [];
        history.AddRange(input);

        var text = new StringBuilder();
        foreach (var message in response)
        {
            var kept = new List<AIContent>();
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent t:
                        text.Append(t.Text);
                        break;
                    case FunctionCallContent:
                        kept.Add(content);
                        break;
                    case FunctionResultContent result:
                        kept.Add(Bounded(result));
                        break;
                }
            }

            if (kept.Count > 0)
            {
                history.Add(new ChatMessage(message.Role, kept) { AuthorName = message.AuthorName ?? authorName });
            }
        }

        // No assistant message when nothing was said: an empty one would be replayed and rendered as a blank bubble.
        if (text.Length > 0)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, text.ToString()) { AuthorName = authorName });
        }

        session.SetInMemoryChatHistory(history);
    }

    // A tool result as the mirror may store it: unchanged when it fits, else its text cut at the cap and marked.
    private static FunctionResultContent Bounded(FunctionResultContent result)
    {
        var raw = result.Result as string ?? (result.Result is null ? null : JsonSerializer.Serialize(result.Result));
        return raw is null || raw.Length <= CopilotLimits.MaxMirroredToolResultLength
            ? result
            : new FunctionResultContent(
                result.CallId,
                raw[..CopilotLimits.MaxMirroredToolResultLength] + CopilotLimits.TruncationMarker);
    }
}
