using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// Shared agent↔UI state (ADR 0089) through a real grain: the model's tool changes it and streams a snapshot, the
/// user's edit reaches the next run, nothing is ever written mid-run, and incognito never gets any.
///
/// <para>Two holds keep a run in flight while a test acts: a client tool waiting for its browser
/// (<c>confirm_action</c>), and the fake provider's stream gate. Storage is read directly, because "not written
/// mid-run" is a statement about what is persisted, not about what the grain answers.</para>
/// </summary>
public sealed class SharedStateTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string Checklist = """{"checklist":[{"id":"a","title":"Book the venue","done":true}]}""";

    private IAgentGrain Agent(string key) => Cluster.GrainFactory.GetGrain<IAgentGrain>(key);

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    private static string Answer(IEnumerable<AgentStreamEvent> events) =>
        string.Concat(events.Where(e => e.Type == AgentStreamEventType.TextDelta).Select(e => e.Text));

    private static string UpdatePrompt(object operations)
        => FakeChatClient.CallToolPrompt(SharedStateLimits.UpdateToolName, new { operations });

    private static readonly object[] AddChecklist =
    [
        new { op = "add", path = "/checklist", value = new[] { new { id = "a", title = "Book the venue", done = false } } },
    ];

    // A conversation with a saved record, which a user edit needs.
    private async Task<IAgentGrain> SavedConversationAsync(string key)
    {
        var agent = Agent(key);
        await CollectAsync(agent.ChatAsync("hi", null));
        return agent;
    }

    private async Task<AgentGrainState> StoredAsync(IAgentGrain agent)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary!.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", agent.GetGrainId(), state);
        return state.State!;
    }

    // AC13: the tool changes the state, the run streams the whole new state once, and it survives deactivation.
    [Fact]
    public async Task UpdateSharedState_EmitsSnapshot_AndPersists()
    {
        var agent = Agent("ss-update-user/conv");

        var events = await CollectAsync(agent.ChatAsync(UpdatePrompt(AddChecklist), null));

        var snapshot = Assert.Single(events, e => e.Type == AgentStreamEventType.StateSnapshot);
        Assert.Equal(1, snapshot.StateVersion);
        Assert.Contains("Book the venue", snapshot.State, StringComparison.Ordinal);
        Assert.Contains("\"version\":1", Answer(events), StringComparison.Ordinal);

        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reopened = await Agent("ss-update-user/conv").GetSharedStateAsync();
        Assert.Equal((snapshot.State, 1L), (reopened.Json, reopened.Version));
    }

    // A refused patch changes nothing and streams no snapshot.
    [Fact]
    public async Task UpdateSharedState_InvalidPatch_ChangesNothing()
    {
        var agent = Agent("ss-invalid-user/conv");

        var events = await CollectAsync(agent.ChatAsync(UpdatePrompt(new[] { new { op = "remove", path = "/missing" } }), null));

        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.StateSnapshot);
        Assert.Contains("nothing changed", Answer(events), StringComparison.Ordinal);
        Assert.Equal((null, 0L), ((await agent.GetSharedStateAsync()).Json, (await agent.GetSharedStateAsync()).Version));
    }

    // AC14: the user's edit is what the model sees on the next run; with the key deselected it sees nothing.
    [Fact]
    public async Task UserEdit_IsVisibleToTheNextTurn()
    {
        var agent = await SavedConversationAsync("ss-visible-user/conv");
        Assert.Equal(SharedStateWriteStatus.Ok, (await agent.SetSharedStateAsync(Checklist, 0)).Status);

        var answer = Answer(await CollectAsync(agent.ChatAsync("what is done?", null)));
        Assert.Contains("## Shared state", answer, StringComparison.Ordinal);
        Assert.Contains("\"done\":true", answer, StringComparison.Ordinal);

        await agent.SetEnabledToolsAsync([ToolCatalog.WebSearch]);
        Assert.DoesNotContain("## Shared state", Answer(await CollectAsync(agent.ChatAsync("and now?", null))), StringComparison.Ordinal);
    }

    // AC14: the injection is a context message for one run (ADR 0104), never a stored message.
    [Fact]
    public async Task InjectedState_IsNotStoredInHistory()
    {
        var agent = await SavedConversationAsync("ss-history-user/conv");
        await agent.SetSharedStateAsync(Checklist, 0);
        await CollectAsync(agent.ChatAsync("what is done?", null));

        var history = await agent.GetHistoryAsync();
        Assert.Equal(["user", "assistant", "user", "assistant"], history.Select(t => t.Role));
        Assert.DoesNotContain(history.Where(t => t.Role == "user"), t => t.Text.Contains("Shared state", StringComparison.Ordinal));
    }

    // AC15 + AC16a: an edit during a run returns at once, a stale base is a conflict, and NOTHING is written while
    // the run is in flight (the stored document still has the previous turn's attachments entry count and no
    // state); the run's own end-of-turn write then persists the edit.
    [Fact]
    public async Task UserEdit_DuringATurn_IsNotWrittenMidTurn_AndPersistsAtTurnEnd()
    {
        var agent = await SavedConversationAsync("ss-midturn-user/conv");
        var before = await StoredAsync(agent);

        await using var stream = agent.ChatAsync(
            FakeChatClient.CallToolPrompt(ClientToolLimits.ConfirmAction, new { action = "Hold the turn" }), null,
            clientTools: [ClientToolLimits.ConfirmAction]).GetAsyncEnumerator();
        string? callId = null;
        while (callId is null && await stream.MoveNextAsync())
        {
            callId = stream.Current.ClientTool == true ? stream.Current.ToolCallId : null;
        }

        var edit = await agent.SetSharedStateAsync(Checklist, 0);
        Assert.Equal((SharedStateWriteStatus.Ok, 1L), (edit.Status, edit.Version));
        Assert.Equal(SharedStateWriteStatus.VersionConflict, (await agent.SetSharedStateAsync("""{"b":1}""", 0)).Status);
        Assert.Single(await agent.GetPendingClientToolCallsAsync()); // the turn is still waiting

        var mid = await StoredAsync(agent);
        Assert.Null(mid.SharedStateJson);
        Assert.Equal(before.TurnAttachments.Count, mid.TurnAttachments.Count);
        Assert.Equal(before.SerializedSession, mid.SerializedSession);

        await agent.SubmitClientToolResultAsync(callId!, """{"approved":true}""");
        while (await stream.MoveNextAsync())
        {
        }

        var after = await StoredAsync(agent);
        Assert.Equal((Checklist, 1L), (after.SharedStateJson, after.SharedStateVersion));
        Assert.Equal(before.TurnAttachments.Count + 1, after.TurnAttachments.Count);
    }

    // AC16a: the run in flight FAILS, so it never writes; being the last run out, it flushes the edit.
    [Fact]
    public async Task UserEdit_DuringAFailedTurn_IsFlushedByTheLastRun()
    {
        var agent = await SavedConversationAsync("ss-failed-user/conv");
        var gates = Cluster.Silos.Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<FakeChatClient>().OpenStreamGate()).ToList();

        var turn = CollectAsync(agent.ChatAsync($"hi, {FakeChatClient.FaultingStreamMarker}", null));
        await Task.WhenAny(gates.Select(g => g.Reached.Task));
        Assert.Equal(SharedStateWriteStatus.Ok, (await agent.SetSharedStateAsync(Checklist, 0)).Status);
        Assert.Null((await StoredAsync(agent)).SharedStateJson);

        foreach (var gate in gates)
        {
            gate.Release.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => turn);
        Assert.Equal(Checklist, (await StoredAsync(agent)).SharedStateJson);
    }

    // AC16: an incognito turn's tool call is refused and changes nothing, and the next normal turn has no state to
    // inherit or persist.
    [Fact]
    public async Task IncognitoTurn_UpdateSharedState_Refuses_AndNothingLeaksIntoTheNextNormalTurn()
    {
        var agent = Agent("ss-incognito-user/conv");

        var events = await CollectAsync(agent.ChatAsync(UpdatePrompt(AddChecklist), null, ephemeral: true));

        Assert.Contains("incognito", Answer(events), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.StateSnapshot);
        Assert.Null((await agent.GetSharedStateAsync()).Json);

        await CollectAsync(agent.ChatAsync("hi", null));
        Assert.Null((await StoredAsync(agent)).SharedStateJson);
    }

    // AC16: a conversation with no saved record (never saved, or incognito) refuses a user edit.
    [Fact]
    public async Task Put_OnAConversationWithNoSavedRecord_Is409()
    {
        var fresh = Agent("ss-norecord-user/conv");
        Assert.Equal(SharedStateWriteStatus.NoSavedRecord, (await fresh.SetSharedStateAsync(Checklist, 0)).Status);

        var incognito = Agent("ss-norecord-incognito-user/conv");
        await CollectAsync(incognito.ChatAsync("hi", null, ephemeral: true));
        Assert.Equal(SharedStateWriteStatus.NoSavedRecord, (await incognito.SetSharedStateAsync(Checklist, 0)).Status);
    }

    // A user edit with no run in flight is written at once.
    [Fact]
    public async Task UserEdit_BetweenTurns_IsWrittenAtOnce()
    {
        var agent = await SavedConversationAsync("ss-between-user/conv");

        await agent.SetSharedStateAsync(Checklist, 0);

        Assert.Equal(Checklist, (await StoredAsync(agent)).SharedStateJson);
    }
}
