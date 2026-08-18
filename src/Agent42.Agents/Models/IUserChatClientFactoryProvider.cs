namespace Agent42.Agents.Models;

/// <summary>
/// Resolves the <see cref="IChatClientFactory"/> a specific user's conversations run on (ADR 0018).
///
/// <para>A user can add/override model providers, so the effective provider set is per-user, not a
/// process singleton. The agent grain resolves this <b>once</b> in <c>OnActivateAsync</c> (async) from
/// its own userId and stores the result — so the sync <see cref="IChatClientFactory"/> interface, and
/// its ~8 in-grain call sites, are unchanged. A user's saved change takes effect on the next activation
/// of each of their conversation grains (the version-stamped cache behind the backend implementation).</para>
/// </summary>
public interface IUserChatClientFactoryProvider
{
    /// <summary>The chat-client factory for <paramref name="userId"/>'s effective (baseline ⊕ overlay) providers.</summary>
    Task<IChatClientFactory> GetForUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="IUserChatClientFactoryProvider"/> that hands back the SAME factory for every user —
/// DevHost and tests, where a single canned/fake <see cref="IChatClientFactory"/> stands in and there is
/// no per-user overlay store. Wraps any <see cref="IChatClientFactory"/> so those hosts change by one line.
/// </summary>
public sealed class SingleUserChatClientFactoryProvider(IChatClientFactory factory) : IUserChatClientFactoryProvider
{
    public Task<IChatClientFactory> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(factory);
}
