using System.Collections.Concurrent;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// In-memory <see cref="IWebPushClient"/> shared by the push sender and push test tests: records every
/// send (endpoint + payload) and can be told to fail, throw, or stall per subscription.
/// </summary>
internal sealed class FakeWebPushClient : IWebPushClient
{
    public ConcurrentBag<string> Sent { get; } = [];

    /// <summary>The payload of the last send to each endpoint.</summary>
    public ConcurrentDictionary<string, string> Payloads { get; } = new();

    /// <summary>Returns an HTTP status to throw as <see cref="PushDeliveryException"/> for a sub, else null.</summary>
    public Func<PushSubscription, int?> FailStatusFor { get; init; } = _ => null;

    /// <summary>When true for a sub, throws a non-delivery exception to prove nothing escapes.</summary>
    public Func<PushSubscription, bool> ThrowGenericFor { get; init; } = _ => false;

    /// <summary>When set, the exception thrown for every send (e.g. an HttpClient timeout).</summary>
    public Exception? ThrowAlways { get; init; }

    /// <summary>When set, each send waits this long first, honouring the token (the budget-timeout case).</summary>
    public TimeSpan? Delay { get; init; }

    public async Task SendAsync(PushSubscription subscription, string payload, CancellationToken cancellationToken)
    {
        Sent.Add(subscription.Endpoint);
        Payloads[subscription.Endpoint] = payload;
        if (Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }
        if (ThrowAlways is not null)
        {
            throw ThrowAlways;
        }
        if (ThrowGenericFor(subscription))
        {
            throw new InvalidOperationException("boom");
        }
        if (FailStatusFor(subscription) is int status)
        {
            throw new PushDeliveryException(status, new HttpRequestException());
        }
    }
}
