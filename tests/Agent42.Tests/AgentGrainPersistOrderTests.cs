using System.Security.Cryptography;
using System.Text;
using Agent42.Agents;
using Orleans;

namespace Agent42.Tests;

/// <summary>
/// cc-08 characterisation: the ORDER of the durable write, the search reconcile and the search projection at every
/// persist site that shares the grain's persist tail (ADR 0016/0061/0106). Ordering is behaviour — the write must come
/// before the index so ADR 0046's stream recovery never reads a half-persisted chat.
///
/// <para>Sites that share the tail: a chat turn, a fired reminder, an image turn, a history truncation and the retention
/// sweep. Deliberately excluded: the fork's first initialisation (<c>AgentGrain.EditFork.cs</c>), which registers the retention
/// sweep BETWEEN the write and the index and has no prune to reconcile, and the ephemeral paths, which never write.</para>
/// </summary>
public sealed class AgentGrainPersistOrderTests(SharedClusterFixture fixture) : SharedClusterTests<SharedClusterFixture>(fixture)
{
    private IAgentGrain Grain(string user, string conversation)
        => Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/{conversation}");

    // The model turn writes once, then indexes — never the other way round.
    [Fact]
    public async Task PersistOrder_ChatTurn_WritesThenIndexes()
    {
        const string user = "persist-order-chat-user";
        var agent = Grain(user, "conv");
        PersistOrderLog.Clear(user);

        await CollectAsync(agent.ChatAsync("hello order", null));

        AssertWriteThenIndexAtTheEnd(user);
    }

    // The incognito turn runs the model and writes nothing, indexes nothing.
    [Fact]
    public async Task PersistOrder_EphemeralChatTurn_NeitherWritesNorIndexes()
    {
        const string user = "persist-order-chat-ephemeral-user";
        var agent = Grain(user, "conv");
        PersistOrderLog.Clear(user);

        await CollectAsync(agent.ChatAsync("off the record", null, ephemeral: true));

        Assert.DoesNotContain(PersistOrderLog.Index, PersistOrderLog.Of(user));
        Assert.DoesNotContain(PersistOrderLog.Reconcile, PersistOrderLog.Of(user));
        var writesBefore = PersistOrderLog.Of(user).Count(s => s == PersistOrderLog.Write);

        // A following normal turn writes again: the ephemeral turn did not poison the sequence.
        await CollectAsync(agent.ChatAsync("on the record", null));
        var log = PersistOrderLog.Of(user);
        Assert.True(log.Count(s => s == PersistOrderLog.Write) > writesBefore);
        AssertWriteThenIndexAtTheEnd(user);
    }

    // A fired reminder does its own persist: write, then index.
    [Fact]
    public async Task PersistOrder_Reminder_WritesThenIndexes()
    {
        const string user = "persist-order-reminder-user";
        var agent = Grain(user, "conv");
        await CollectAsync(agent.ChatAsync("please remind me to drink water", null));
        PersistOrderLog.Clear(user);

        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);

        AssertWriteThenIndexAtTheEnd(user);
    }

    // An image turn persists through its own block: write, then index.
    [Fact]
    public async Task PersistOrder_ImageTurn_WritesThenIndexes()
    {
        const string user = "persist-order-image-user";
        var agent = Grain(user, "conv");
        PersistOrderLog.Clear(user);

        await CollectAsync(agent.ChatAsync("draw a cat", TestSiloConfigurator.ImageModel));

        AssertWriteThenIndexAtTheEnd(user);
    }

    // The ephemeral image turn is the else branch of that block: nothing written, nothing indexed.
    [Fact]
    public async Task PersistOrder_EphemeralImageTurn_NeitherWritesNorIndexes()
    {
        const string user = "persist-order-image-ephemeral-user";
        var agent = Grain(user, "conv");
        PersistOrderLog.Clear(user);

        await CollectAsync(agent.ChatAsync("draw a ghost", TestSiloConfigurator.ImageModel, ephemeral: true));

        Assert.DoesNotContain(PersistOrderLog.Index, PersistOrderLog.Of(user));
        Assert.DoesNotContain(PersistOrderLog.Reconcile, PersistOrderLog.Of(user));
    }

    // The truncation forces the reconcile: write, then the conversation's index documents are dropped, then they are
    // re-projected — in exactly that order.
    [Fact]
    public async Task PersistOrder_Truncation_WritesThenReconcilesThenIndexes()
    {
        const string user = "persist-order-truncate-user";
        var agent = Grain(user, "conv");
        await CollectAsync(agent.ChatAsync("first", null));
        await CollectAsync(agent.ChatAsync("second", null));
        var (count, hash) = await TokenOf(agent);
        PersistOrderLog.Clear(user);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));

        Assert.Equal(
            [PersistOrderLog.Write, PersistOrderLog.Reconcile, PersistOrderLog.Index],
            PersistOrderLog.Of(user));
    }

    private static void AssertWriteThenIndexAtTheEnd(string user)
    {
        var log = PersistOrderLog.Of(user);
        var lastIndex = log.ToList().LastIndexOf(PersistOrderLog.Index);
        Assert.True(lastIndex >= 0, $"the turn never indexed; log: {string.Join(",", log)}");
        var lastWrite = log.ToList().LastIndexOf(PersistOrderLog.Write);
        Assert.True(lastWrite >= 0, $"the turn never wrote; log: {string.Join(",", log)}");
        Assert.True(lastWrite < lastIndex, $"the index must follow the durable write; log: {string.Join(",", log)}");
        // Nothing is indexed before a write has happened in the same operation.
        Assert.True(log.ToList().IndexOf(PersistOrderLog.Write) < log.ToList().IndexOf(PersistOrderLog.Index));
    }

    private static async Task<(int Count, string Hash)> TokenOf(IAgentGrain agent)
    {
        var users = (await agent.GetHistoryAsync()).Where(t => t.Role == "user").Select(t => t.Text).ToList();
        return (users.Count, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(users.Count == 0 ? "" : users[^1]))));
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var e in stream)
        {
            events.Add(e);
        }

        return events;
    }
}
