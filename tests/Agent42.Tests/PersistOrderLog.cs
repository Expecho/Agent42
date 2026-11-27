using System.Collections.Concurrent;
using Orleans.Runtime;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// Records the order of the persist-site side effects the grain performs for one user (cc-08): the durable
/// state write, the search reconcile (a conversation delete) and the search projection (an index call).
///
/// <para>Keyed by userId — the part of the grain key before the first '/' — for the same reason
/// <see cref="TestChatSearchIndex"/> keys its failures by user: the stores are process-global and test classes run in
/// parallel, so a unique userId per test is what keeps one test's calls out of another's log.</para>
/// </summary>
public static class PersistOrderLog
{
    public const string Write = "write";
    public const string Index = "index";
    public const string Reconcile = "reconcile";

    private static readonly ConcurrentDictionary<string, List<string>> Logs = new();

    public static void Record(string userId, string step)
    {
        var log = Logs.GetOrAdd(userId, _ => []);
        lock (log)
        {
            log.Add(step);
        }
    }

    public static IReadOnlyList<string> Of(string userId)
    {
        if (!Logs.TryGetValue(userId, out var log))
        {
            return [];
        }

        lock (log)
        {
            return [.. log];
        }
    }

    /// <summary>Forgets what was recorded so far, so a test can assert on one operation in isolation.</summary>
    public static void Clear(string userId) => Logs.TryRemove(userId, out _);

    /// <summary>The user a grain key belongs to: <c>user/conversation</c> → <c>user</c>.</summary>
    public static string UserOf(string grainKey)
    {
        var slash = grainKey.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? grainKey : grainKey[..slash];
    }
}

/// <summary>
/// Wraps the memory grain storage so every state write is recorded in <see cref="PersistOrderLog"/> before it is
/// forwarded. Reads and clears pass straight through.
/// </summary>
public sealed class RecordingGrainStorage(IGrainStorage inner) : IGrainStorage
{
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => inner.ReadStateAsync(stateName, grainId, grainState);

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        PersistOrderLog.Record(PersistOrderLog.UserOf(grainId.Key.ToString() ?? ""), PersistOrderLog.Write);
        return inner.WriteStateAsync(stateName, grainId, grainState);
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => inner.ClearStateAsync(stateName, grainId, grainState);
}
