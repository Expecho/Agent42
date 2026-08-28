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
/// <para><b>Scope every assertion to your own bank.</b> Other tests in the class share this recorder and
/// a reminder fired by one of them can drain <c>ChatCoreAsync</c> and retain. Assert "no call for bank
/// <c>X</c>", never a cluster-wide total.</para>
/// </remarks>
public sealed class RecordingHindsightClient : IHindsightClient
{
    private readonly List<HindsightMemory> _memories = [];

    /// <summary>Every recall, as (bank, query).</summary>
    public List<(string BankId, string Query)> Recalls { get; } = [];

    /// <summary>Every retain, as (bank, content).</summary>
    public List<(string BankId, string Content)> Retains { get; } = [];

    /// <summary>When set, every call throws it — the "the memory service is down" case.</summary>
    public Exception? Throws { get; set; }

    /// <summary>What the next recall returns. Empty by default, so a test opts in to injection.</summary>
    public void ReturnsMemories(params string[] texts)
    {
        _memories.Clear();
        _memories.AddRange(texts.Select((t, i) => new HindsightMemory($"m{i}", t, "world")));
    }

    /// <summary>Recalls for one bank only — the correctly-scoped read (see the type's remarks).</summary>
    public IReadOnlyList<string> RecallsFor(string bankId) =>
        [.. Recalls.Where(r => r.BankId == bankId).Select(r => r.Query)];

    /// <summary>Retains for one bank only — the correctly-scoped read (see the type's remarks).</summary>
    public IReadOnlyList<string> RetainsFor(string bankId) =>
        [.. Retains.Where(r => r.BankId == bankId).Select(r => r.Content)];

    public void Reset()
    {
        Recalls.Clear();
        Retains.Clear();
        _memories.Clear();
        Throws = null;
    }

    public Task<IReadOnlyList<HindsightMemory>> RecallAsync(string bankId, string query, CancellationToken cancellationToken = default)
    {
        lock (Recalls)
        {
            Recalls.Add((bankId, query));
        }

        return Throws is not null
            ? Task.FromException<IReadOnlyList<HindsightMemory>>(Throws)
            : Task.FromResult<IReadOnlyList<HindsightMemory>>([.. _memories]);
    }

    public Task RetainAsync(string bankId, string content, CancellationToken cancellationToken = default)
    {
        lock (Retains)
        {
            Retains.Add((bankId, content));
        }

        return Throws is not null ? Task.FromException(Throws) : Task.CompletedTask;
    }

    public Task EnsureBankAsync(string bankId, string mission, CancellationToken cancellationToken = default)
        => Throws is not null ? Task.FromException(Throws) : Task.CompletedTask;
}
