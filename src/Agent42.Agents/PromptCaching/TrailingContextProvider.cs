using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Wraps an <see cref="AIContextProvider"/> whose output changes every turn so that output reaches the model as a
/// <b>trailing user-role message</b> rather than as instructions (ADR 0104).
///
/// <para><b>Why this exists.</b> Every provider caches the longest identical request <i>prefix</i>, and the
/// instructions sit at the very front of it. A provider that appends per-turn text to them (Hindsight's
/// <c>## Memories</c> recall, measured) changes the first segment of every request, so nothing after the persona
/// (tools, history) can ever be a cache hit. Moved behind the new user turn, the same text costs only itself.
/// Document RAG already injects there; this makes any other provider do the same.</para>
///
/// <para><b>Trust is a side effect worth naming:</b> recalled memories are untrusted (ADR 0056), and this moves
/// them from system authority down to user-role context.</para>
///
/// <para>The message is tagged <c>AIContextProvider</c>-sourced by the framework, so the history provider's
/// storage filter keeps it out of the session (it never accumulates) and Hindsight neither recalls with it nor
/// retains it (measured, plan-prompt-caching Step 0c). <see cref="StoreAIContextAsync"/> therefore forwards
/// unfiltered.</para>
///
/// <para>Public for the same reason as <see cref="TruncatingSkillsProvider"/>: a test must be able to build it.</para>
/// </summary>
public sealed class TrailingContextProvider(AIContextProvider inner) : AIContextProvider
{
    public override IReadOnlyList<string> StateKeys => inner.StateKeys;

    public override object? GetService(Type serviceType, object? serviceKey = null)
        => inner.GetService(serviceType, serviceKey) ?? base.GetService(serviceType, serviceKey);

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        // An ISOLATED context, as TruncatingSkillsProvider does, because InvokingAsync accumulates onto the context
        // it is given. But NOT an empty one: Hindsight builds its recall query from the run's messages, so an empty
        // context would make recall silently return nothing. It gets a copy of the messages and nothing else, and
        // only what it ADDS after them is kept (Wrapper_DoesNotEchoInput).
        var input = context.AIContext.Messages?.ToList() ?? [];
#pragma warning disable MAAI001 // InvokingContext's ctor is [Experimental]; see TruncatingSkillsProvider.
        var isolated = new InvokingContext(context.Agent, context.Session, new AIContext { Messages = input });
#pragma warning restore MAAI001

        var produced = await inner.InvokingAsync(isolated, cancellationToken);

        // The framework appends a provider's messages after the input ones, so the inner provider's own are the tail.
        List<ChatMessage> messages = [.. (produced.Messages ?? []).Skip(input.Count)];
        if (!string.IsNullOrWhiteSpace(produced.Instructions))
        {
            messages.Add(new ChatMessage(ChatRole.User, produced.Instructions));
        }

        return new AIContext { Messages = messages, Tools = produced.Tools };
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
        => inner.InvokedAsync(context, cancellationToken);
}
