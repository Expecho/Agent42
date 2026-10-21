using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IChatDeliverySender"/> that records every delivery instead of posting to a chat (ADR 0111), the
/// shape of <see cref="RecordingAgentWebhookSender"/>. Registered as a concrete singleton in
/// <see cref="TestSiloConfigurator"/>; the other silo hosts register <see cref="NullChatDeliverySender"/>.
/// </summary>
public sealed class RecordingChatDeliverySender : IChatDeliverySender
{
    private readonly ConcurrentQueue<(string UserId, ChatDelivery Delivery)> _sends = new();

    /// <summary>Every recorded delivery, in order.</summary>
    public IReadOnlyList<(string UserId, ChatDelivery Delivery)> Sends => _sends.ToArray();

    /// <summary>Deliveries of one conversation.</summary>
    public IReadOnlyList<ChatDelivery> For(string conversationId)
        => [.. Sends.Select(s => s.Delivery).Where(d => d.ConversationId == conversationId)];

    /// <summary>Drops every recorded delivery. The cluster outlives a single test.</summary>
    public void Reset() => _sends.Clear();

    public Task DeliverAsync(string userId, ChatDelivery delivery, CancellationToken cancellationToken = default)
    {
        _sends.Enqueue((userId, delivery));
        return Task.CompletedTask;
    }
}
