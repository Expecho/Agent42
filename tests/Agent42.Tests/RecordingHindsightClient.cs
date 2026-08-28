using System.Collections.Concurrent;
using Hindsight.AgentFramework;

namespace Agent42.Tests;

/// <summary>
/// The long-term-memory stand-in (ADR 0056): records which bank each recall and retain was for, so a
/// test can assert what memory did — including that it did <em>nothing</em>.
/// </summary>
/// <remarks>
/// <para>A recorder rather than a stateless fake, because the interesting assertions here are negative:
/// an incognito turn must neither recall nor retain, and an unconfigured deployment must make no call at
/// all. Neither is visible in the agent's reply, and neither is distinguishable in
/// <c>FakeChatClient.InstructionsEcho</c> — "no provider attached" and "attached, recalled nothing" both
/// produce instructions with no <c>## Memories</c> block.</para>
/// <para>Registered as its concrete type in each silo configurator so a test can fetch it from the silo
/// container, which is the <see cref="RecordingPushNotificationSender"/> shape — not
/// <c>AddWebToolFakes()</c>, which is for stateless fakes. <see cref="Reset"/> is called from
/// <c>SharedClusterFixture.ResetSiloState</c>: the cluster is shared per test class (ADR 0055), so
/// without it the recorder accumulates across tests and every count assertion silently rots.</para>
/// <para><b>Concurrent by construction, exactly like the exemplar.</b> Writes really do race with reads
/// here — the remarks below say so — and a lock around the writers alone would leave a reader walking a
/// <c>List&lt;T&gt;</c> mid-<c>Add</c> and throwing "Collection was modified", while <em>reading</em> as
/// thread-safe. <see cref="ConcurrentQueue{T}"/> plus snapshot-returning properties is what
/// <see cref="RecordingPushNotificationSender"/> does and it is the whole defence.</para>
/// <para><b>Scope every assertion to your own bank.</b> Other tests in the class share this recorder and
/// a reminder fired by one of them can drain <c>ChatCoreAsync</c> and retain. Assert "no call for bank
/// <c>X</c>", never a cluster-wide total.</para>
/// </remarks>
public sealed class RecordingHindsightClient : IHindsightClient
{
    private readonly ConcurrentQueue<(string BankId, string Query)> _recalls = new();
    private readonly ConcurrentQueue<(string BankId, string Content)> _retains = new();

    // Replaced wholesale rather than mutated, so a recall reading it never sees a half-filled list.
    private volatile HindsightMemory[] _memories = [];

    /// <summary>Every recall, as (bank, query), in order.</summary>
    public IReadOnlyList<(string BankId, string Query)> Recalls => _recalls.ToArray();

    /// <summary>Every retain, as (bank, content), in order.</summary>
    public IReadOnlyList<(string BankId, string Content)> Retains => _retains.ToArray();

    /// <summary>When set, every call throws it — the "the memory service is down" case.</summary>
    public Exception? Throws { get; set; }

    /// <summary>What the next recall returns. Empty by default, so a test opts in to injection.</summary>
    public void ReturnsMemories(params string[] texts)
        => _memories = [.. texts.Select((t, i) => new HindsightMemory($"m{i}", t, "world"))];

    /// <summary>Recalls for one bank only — the correctly-scoped read (see the type's remarks).</summary>
    public IReadOnlyList<string> RecallsFor(string bankId) =>
        [.. Recalls.Where(r => r.BankId == bankId).Select(r => r.Query)];

    /// <summary>Retains for one bank only — the correctly-scoped read (see the type's remarks).</summary>
    public IReadOnlyList<string> RetainsFor(string bankId) =>
        [.. Retains.Where(r => r.BankId == bankId).Select(r => r.Content)];

    public void Reset()
    {
        _recalls.Clear();
        _retains.Clear();
        _memories = [];
        Throws = null;
    }

    public Task<IReadOnlyList<HindsightMemory>> RecallAsync(string bankId, string query, CancellationToken cancellationToken = default)
    {
        _recalls.Enqueue((bankId, query));
        return Throws is not null
            ? Task.FromException<IReadOnlyList<HindsightMemory>>(Throws)
            : Task.FromResult<IReadOnlyList<HindsightMemory>>(_memories);
    }

    public Task RetainAsync(string bankId, string content, CancellationToken cancellationToken = default)
    {
        _retains.Enqueue((bankId, content));
        return Throws is not null ? Task.FromException(Throws) : Task.CompletedTask;
    }

    public Task EnsureBankAsync(string bankId, string mission, CancellationToken cancellationToken = default)
        => Throws is not null ? Task.FromException(Throws) : Task.CompletedTask;
}
