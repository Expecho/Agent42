using System.Security.Cryptography;
using System.Text;
using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Storage;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// Edit the last message, regenerate the last reply, and fork a conversation (ADR 0092), through a real grain:
/// truncation removes whole session turns and re-anchors everything that indexes off them, a fork copies a prefix
/// without touching its source, and every refusal changes nothing.
/// </summary>
public sealed class EditRegenerateForkTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private readonly InMemoryChatSearchIndex _search = new();

    // AC1: regenerate removes the whole last exchange, and the re-run leaves exactly one copy of it.
    [Fact]
    public async Task ReplaceLastTurn_ReplacesTheWholeLastExchange()
    {
        var agent = Agent("erf-regen-user/conv");
        await ChatAsync(agent, "first question");
        await ChatAsync(agent, "say hello");
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        Assert.Equal(["user", "assistant"], (await agent.GetHistoryAsync()).Select(t => t.Role));

        await ChatAsync(agent, "say hello");
        var history = await agent.GetHistoryAsync();
        Assert.Equal(["user", "assistant", "user", "assistant"], history.Select(t => t.Role));
        Assert.Single(history, t => t.Text == "say hello");
    }

    // AC2: an edit re-runs with the new text, and the old text is gone from history AND the search index.
    [Fact]
    public async Task EditLastTurn_RemovesOldTextFromHistoryAndIndex()
    {
        const string user = "erf-edit-user";
        var agent = Agent($"{user}/conv");
        await ChatAsync(agent, "the zebra question");
        Assert.NotEmpty(await _search.SearchAsync(user, "zebra", 10));
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        await ChatAsync(agent, "the giraffe question");

        Assert.DoesNotContain(await agent.GetHistoryAsync(), t => t.Text.Contains("zebra", StringComparison.Ordinal));
        Assert.Empty(await _search.SearchAsync(user, "zebra", 10));
        Assert.Contains(await _search.SearchAsync(user, "giraffe", 10), hit => hit.Ordinal == 0);
        Assert.DoesNotContain("zebra", (await StoredAsync(agent)).SerializedSession, StringComparison.Ordinal);
    }

    // AC3: a stale token changes nothing.
    [Fact]
    public async Task Truncate_StaleCount_ChangesNothing()
    {
        var agent = Agent("erf-stale-user/conv");
        await ChatAsync(agent, "only question");
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Stale, await agent.TruncateLastTurnAsync(count + 1, hash, ephemeral: false));
        Assert.Equal(HistoryEditOutcome.Stale, await agent.TruncateLastTurnAsync(count, Hash("other"), ephemeral: false));
        Assert.Equal(2, (await agent.GetHistoryAsync()).Count);
    }

    // AC3: the replayed second-tab case. After an edit restored the count, the old hash no longer matches.
    [Fact]
    public async Task Truncate_ReplayedCountWithOldHash_IsStale()
    {
        var agent = Agent("erf-replay-user/conv");
        await ChatAsync(agent, "first");
        await ChatAsync(agent, "second");
        var staleTab = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(staleTab.Count, staleTab.Hash, false));
        await ChatAsync(agent, "edited second");
        Assert.Equal(staleTab.Count, (await TokenOf(agent)).Count);

        Assert.Equal(HistoryEditOutcome.Stale, await agent.TruncateLastTurnAsync(staleTab.Count, staleTab.Hash, false));
        Assert.Contains(await agent.GetHistoryAsync(), t => t.Text == "edited second");
    }

    // AC3a: a blank last session user turn does not project, so the client cannot see what it would replace.
    [Fact]
    public async Task Truncate_BlankLastUserTurn_IsNothing()
    {
        var agent = Agent("erf-blank-last-user/conv");
        await ChatAsync(agent, "visible");
        await ChatAsync(agent, "");
        var (count, hash) = await TokenOf(agent);
        var before = await agent.GetHistoryAsync();

        Assert.Equal(HistoryEditOutcome.Nothing, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        Assert.Equal(before.Count, (await agent.GetHistoryAsync()).Count);
    }

    // AC3a: a blank user turn between visible ones never shifts the fork boundary.
    [Fact]
    public async Task Fork_SkipsBlankUserTurns_InMapping()
    {
        var source = Agent("erf-blank-fork-user/src");
        await ChatAsync(source, "alpha");
        await ChatAsync(source, "");
        await ChatAsync(source, "beta");
        var (count, hash) = await TokenOf(source);

        var first = await ForkAsync(source, 0, count, hash, "erf-blank-fork-user/f0");
        Assert.Equal(["alpha"], UserTexts(await first.GetHistoryAsync()));
        Assert.Single(await SessionUserMessagesOf(first));

        var second = await ForkAsync(source, 1, count, hash, "erf-blank-fork-user/f1");
        Assert.Equal(["alpha", "beta"], UserTexts(await second.GetHistoryAsync()));
    }

    // AC3b: no configured model refuses before anything is removed.
    [Fact]
    public async Task Truncate_NoModel_IsRefused()
    {
        await using var cluster = await NewClusterAsync<NoModelSiloConfigurator>();
        var provider = cluster.GetSiloServiceProvider(cluster.Primary!.SiloAddress)
            .GetRequiredService<SwitchableChatClientFactoryProvider>();
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>("erf-nomodel-user/conv");

        provider.HasModel = true;
        await ChatAsync(agent, "question");
        provider.HasModel = false;
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.NoModel, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        Assert.Equal(2, (await agent.GetHistoryAsync()).Count);
    }

    // AC3c: an edit on a retention-windowed continuous chat truncates the tail and re-indexes contiguously.
    [Fact]
    public async Task Truncate_RetentionWindowedChat_ReindexesCorrectly()
    {
        const string user = "erf-retention-user";
        var agent = Agent($"{user}/agent-erf-retention");
        await ChatAsync(agent, "the ancient walrus");
        Fixture.AdvanceClock(RetentionLimits.Window + TimeSpan.FromHours(1));
        await ChatAsync(agent, "the fresh walrus");
        await ChatAsync(agent, "the doomed walrus");
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));

        Assert.Equal(["the fresh walrus"], UserTexts(await agent.GetHistoryAsync()));
        var hits = (await _search.SearchAsync(user, "walrus", 50)).OrderBy(h => h.Ordinal).ToList();
        var fresh = Assert.Single(hits);
        Assert.Equal((0, "the fresh walrus"), (fresh.Ordinal, fresh.Text));
    }

    // AC4: a truncate that lands while a chat run is suspended is refused, and that run's turn persists intact.
    [Fact]
    public async Task Truncate_DuringAChatRun_IsRefused()
    {
        var agent = Agent("erf-busy-chat-user/conv");
        await ChatAsync(agent, "earlier");
        var (count, hash) = await TokenOf(agent);

        await using var stream = agent.ChatAsync(
            FakeChatClient.CallToolPrompt(ClientToolLimits.ConfirmAction, new { action = "Hold the turn" }), null,
            clientTools: [ClientToolLimits.ConfirmAction]).GetAsyncEnumerator();
        string? callId = null;
        while (callId is null && await stream.MoveNextAsync())
        {
            callId = stream.Current.ClientTool == true ? stream.Current.ToolCallId : null;
        }

        Assert.Equal(HistoryEditOutcome.Busy, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        Assert.Equal(HistoryEditOutcome.Busy, (await agent.CreateForkAsync(0, count, hash)).Outcome);

        await agent.SubmitClientToolResultAsync(callId!, """{"approved":true}""");
        while (await stream.MoveNextAsync())
        {
        }

        var users = UserTexts(await agent.GetHistoryAsync());
        Assert.Equal(2, users.Count);
        Assert.Equal("earlier", users[0]);
    }

    // AC4: a fired reminder holds the (non-reentrant) grain, so a truncate queues behind it and is then refused as
    // stale: the reminder's turn is never the one removed.
    [Fact]
    public async Task Truncate_DuringAReminderRun_IsRefused()
    {
        var agent = Agent("erf-busy-reminder-user/conv");
        await ChatAsync(agent, "please remind me to drink water");
        var (count, hash) = await TokenOf(agent);

        var gates = Cluster.Silos.Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<FakeChatClient>().OpenStreamGate(afterChunks: 0)).ToList();
        var reminder = agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);
        await Task.WhenAny(gates.Select(g => g.Reached.Task));

        var truncate = agent.TruncateLastTurnAsync(count, hash, ephemeral: false);
        CloseStreamGates();
        await reminder;

        Assert.Equal(HistoryEditOutcome.Stale, await truncate);
        var history = await agent.GetHistoryAsync();
        Assert.Equal("Tell the user to drink some water.", UserTexts(history)[^1]);
        Assert.Equal("assistant", history[^1].Role);
    }

    // AC5: TurnAttachments stays aligned — an earlier turn's image still renders after the truncation.
    [Fact]
    public async Task Truncate_KeepsEarlierTurnImagesAligned()
    {
        const string key = "erf-images-user/conv";
        var agent = Agent(key);
        var image = await Store().SaveAsync(key, "a.png", "image/png", [1, 2, 3]);
        await ChatAsync(agent, "look at this", [image]);
        await ChatAsync(agent, "and now this");
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        await ChatAsync(agent, "edited follow-up");

        var users = (await agent.GetHistoryAsync()).Where(t => t.Role == "user").ToList();
        Assert.Equal(image.Id, Assert.Single(users[0].Attachments!).Id);
        Assert.Null(users[1].Attachments);
        Assert.Equal(2, (await StoredAsync(agent)).TurnAttachments.Count);
    }

    // AC5: a failed run left an orphan trailing TurnAttachments entry; the truncation still removes the REAL last
    // turn's refs, so the re-run does not inherit them.
    [Fact]
    public async Task Truncate_WithOrphanTurnAttachment_StaysAligned()
    {
        const string key = "erf-orphan-user/conv";
        var agent = Agent(key);
        var first = await Store().SaveAsync(key, "a.png", "image/png", [1]);
        var second = await Store().SaveAsync(key, "b.png", "image/png", [2]);
        var orphan = await Store().SaveAsync(key, "c.png", "image/png", [3]);
        await ChatAsync(agent, "first image", [first]);
        await ChatAsync(agent, "second image", [second]);
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await MutateStoredAsync(agent, state => state.TurnAttachments.Add([orphan]));
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        await ChatAsync(agent, "no image this time");

        var users = (await agent.GetHistoryAsync()).Where(t => t.Role == "user").ToList();
        Assert.Equal(first.Id, Assert.Single(users[0].Attachments!).Id);
        Assert.Null(users[1].Attachments);
    }

    // AC6: the removed turn's artifact version goes; the earlier version stays servable.
    [Fact]
    public async Task Truncate_DropsOnlyTheRemovedTurnsArtifactVersions()
    {
        var agent = Agent("erf-artifact-user/conv");
        await ChatAsync(agent, FakeChatClient.CallToolPrompt(
            "create_artifact", new { kind = "html", title = "Page", content = "<p>v1</p>" }));
        var artifactId = Assert.Single(await agent.GetArtifactsAsync()).Id;
        await ChatAsync(agent, FakeChatClient.CallToolPrompt(
            "update_artifact", new { artifactId, content = "<p>v2</p>" }));
        Assert.Equal(2, Assert.Single(await agent.GetArtifactsAsync()).Versions.Count);
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));

        var version = Assert.Single(Assert.Single(await agent.GetArtifactsAsync()).Versions);
        Assert.Equal(1, version.Version);
        Assert.NotNull(await Store().OpenReadAsync(agent.GetPrimaryKeyString(), version.AttachmentId));

        // The next update numbers after the last version kept, so it never collides.
        await ChatAsync(agent, FakeChatClient.CallToolPrompt(
            "update_artifact", new { artifactId, content = "<p>v2 again</p>" }));
        Assert.Equal([1, 2], Assert.Single(await agent.GetArtifactsAsync()).Versions.Select(v => v.Version));
    }

    // AC6: removing the turn that created an artifact removes the artifact.
    [Fact]
    public async Task Truncate_OfTheCreatingTurn_RemovesTheArtifact()
    {
        var agent = Agent("erf-artifact-create-user/conv");
        await ChatAsync(agent, FakeChatClient.CallToolPrompt(
            "create_artifact", new { kind = "svg", title = "Logo", content = "<svg/>" }));
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));

        Assert.Empty(await agent.GetArtifactsAsync());
        Assert.Empty((await StoredAsync(agent)).Artifacts);
    }

    // AC6: the removed turn's generated image goes from state; the earlier one still renders.
    [Fact]
    public async Task Truncate_DropsOnlyTheRemovedTurnsGeneratedImage()
    {
        var agent = Agent("erf-genimage-user/conv");
        await ChatAsync(agent, "draw a cat", model: TestSiloConfigurator.ImageModel);
        await ChatAsync(agent, "draw a dog", model: TestSiloConfigurator.ImageModel);
        Assert.Equal(2, (await StoredAsync(agent)).GeneratedImages.Count);
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));

        var stored = Assert.Single((await StoredAsync(agent)).GeneratedImages);
        var assistant = Assert.Single(await agent.GetHistoryAsync(), t => t.Role == "assistant");
        Assert.Equal(stored.Value.Id, Assert.Single(assistant.Attachments!).Id);
    }

    // AC7: an incognito truncation writes nothing, and the removed incognito text is never indexed later.
    [Fact]
    public async Task EphemeralTruncate_WritesNothing_AndOrdinalsStayCorrect()
    {
        const string user = "erf-ephemeral-user";
        var agent = Agent($"{user}/conv");
        await ChatAsync(agent, "secret otter", ephemeral: true);
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: true));
        Assert.Null((await StoredAsync(agent)).SerializedSession);

        await ChatAsync(agent, "public otter");

        var hits = await _search.SearchAsync(user, "otter", 10);
        var hit = Assert.Single(hits);
        Assert.Equal((0, "public otter"), (hit.Ordinal, hit.Text));
        Assert.DoesNotContain("secret", (await StoredAsync(agent)).SerializedSession, StringComparison.Ordinal);
    }

    // An incognito flag on a conversation that HAS a stored record cannot leave the removed turn in Cosmos or in
    // the index: the stored record decides, not the client's flag.
    [Fact]
    public async Task EphemeralFlag_OnAPersistedChat_StillPersistsTheTruncation()
    {
        const string user = "erf-ephemeral-persisted-user";
        var agent = Agent($"{user}/conv");
        await ChatAsync(agent, "kept heron");
        await ChatAsync(agent, "removed heron");
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: true));

        Assert.DoesNotContain("removed heron", (await StoredAsync(agent)).SerializedSession, StringComparison.Ordinal);
        Assert.DoesNotContain(await _search.SearchAsync(user, "heron", 10), hit => hit.Text.Contains("removed"));
    }

    // A listed conversation that also served an incognito turn on this activation is never forked: the fork would
    // persist and index the incognito text.
    [Fact]
    public async Task Fork_OfAConversationHoldingIncognitoTurns_IsRefused()
    {
        var source = Agent("erf-fork-incognito-user/src");
        await ChatAsync(source, "on the record");
        await ChatAsync(source, "off the record", ephemeral: true);
        var (count, hash) = await TokenOf(source);

        Assert.Equal(HistoryEditOutcome.Unsupported, (await source.CreateForkAsync(0, count, hash)).Outcome);
    }

    // AC8: the fork holds exactly the chosen exchanges, the source is unchanged, and the fork carries on.
    [Fact]
    public async Task Fork_CopiesThroughTheChosenExchange_AndLeavesSourceUntouched()
    {
        var source = Agent("erf-fork-user/src");
        await ChatAsync(source, "alpha");
        await ChatAsync(source, "beta");
        await ChatAsync(source, "gamma");
        var (count, hash) = await TokenOf(source);
        var sourceSession = (await StoredAsync(source)).SerializedSession;

        var fork = await ForkAsync(source, 1, count, hash, "erf-fork-user/fork");

        Assert.Equal(["alpha", "beta"], UserTexts(await fork.GetHistoryAsync()));
        Assert.Equal(4, (await fork.GetHistoryAsync()).Count);
        Assert.Equal(["alpha", "beta", "gamma"], UserTexts(await source.GetHistoryAsync()));
        Assert.Equal(sourceSession, (await StoredAsync(source)).SerializedSession);

        await ChatAsync(fork, "delta");
        Assert.Equal(["alpha", "beta", "delta"], UserTexts(await fork.GetHistoryAsync()));
        Assert.Equal(["alpha", "beta", "gamma"], UserTexts(await source.GetHistoryAsync()));
    }

    // AC8: a fork's user image, generated image and artifact are readable under the FORK's owner key.
    [Fact]
    public async Task Fork_CopiesBlobsUnderTheForkKey()
    {
        const string sourceKey = "erf-fork-blobs-user/src";
        const string forkKey = "erf-fork-blobs-user/fork";
        var source = Agent(sourceKey);
        var image = await Store().SaveAsync(sourceKey, "a.png", "image/png", [4, 2]);
        await ChatAsync(source, "with an image", [image]);
        await ChatAsync(source, FakeChatClient.CallToolPrompt(
            "create_artifact", new { kind = "markdown", title = "Notes", content = "# hi" }));
        await ChatAsync(source, "draw a cat", model: TestSiloConfigurator.ImageModel);
        var (count, hash) = await TokenOf(source);

        var fork = await ForkAsync(source, 2, count, hash, forkKey);

        Assert.NotNull(await Store().OpenReadAsync(forkKey, image.Id));
        var version = Assert.Single(Assert.Single(await fork.GetArtifactsAsync()).Versions);
        Assert.NotNull(await Store().OpenReadAsync(forkKey, version.AttachmentId));
        var generated = Assert.Single((await StoredAsync(fork)).GeneratedImages).Value;
        Assert.Equal($"{forkKey}/{generated.Id}", generated.BlobPath);
        Assert.NotNull(await Store().OpenReadAsync(forkKey, generated.Id));
        Assert.Equal($"{forkKey}/{image.Id}", (await StoredAsync(fork)).TurnAttachments[0][0].BlobPath);
    }

    // AC8: a fork before the exchange that created an artifact version leaves that version out.
    [Fact]
    public async Task Fork_LeavesOutArtifactVersionsOfExcludedExchanges()
    {
        var source = Agent("erf-fork-artifacts-user/src");
        await ChatAsync(source, FakeChatClient.CallToolPrompt(
            "create_artifact", new { kind = "html", title = "Page", content = "<p>v1</p>" }));
        var artifactId = Assert.Single(await source.GetArtifactsAsync()).Id;
        await ChatAsync(source, FakeChatClient.CallToolPrompt(
            "update_artifact", new { artifactId, content = "<p>v2</p>" }));
        var (count, hash) = await TokenOf(source);

        var fork = await ForkAsync(source, 0, count, hash, "erf-fork-artifacts-user/fork");

        Assert.Equal([1], Assert.Single(await fork.GetArtifactsAsync()).Versions.Select(v => v.Version));
        Assert.Equal(2, Assert.Single(await source.GetArtifactsAsync()).Versions.Count);
    }

    // AC10: the fork copies the persona snapshot verbatim from the source grain.
    [Fact]
    public async Task Fork_CopiesThePersonaSnapshot_NotTheCurrentAgent()
    {
        var source = Agent("erf-persona-user/src");
        var binding = new AgentBinding(
            "agent-p", "Pascal", "You are Pascal.", TestSiloConfigurator.DefaultModel, ["web_search"]);
        await source.BindAgentAsync(binding);
        await ChatAsync(source, "hi Pascal");
        var (count, hash) = await TokenOf(source);

        var fork = await ForkAsync(source, 0, count, hash, "erf-persona-user/fork");

        Assert.Equal(await source.GetAgentBindingAsync(), await fork.GetAgentBindingAsync(), new BindingComparer());
        Assert.Equal(await source.GetEnabledToolsAsync(), await fork.GetEnabledToolsAsync());
    }

    // AC11: a Copilot chat refuses both truncate and fork.
    [Fact]
    public async Task CopilotChat_RefusesTruncateAndFork()
    {
        var agent = Agent("erf-copilot-user/conv");
        await agent.BindAgentAsync(new AgentBinding(
            "copilot-agent", "Pilot", "You fly.", TestSiloConfigurator.DefaultModel, null, AgentKinds.Copilot));
        await ChatAsync(agent, "hello pilot");
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Unsupported, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        Assert.Equal(HistoryEditOutcome.Unsupported, (await agent.CreateForkAsync(0, count, hash)).Outcome);
        Assert.Equal(2, (await agent.GetHistoryAsync()).Count);
    }

    // AC12: a finished research turn truncates whole: no iteration message is left behind.
    [Fact]
    public async Task ResearchTurn_TruncatesWhole()
    {
        var agent = Agent("erf-research-user/conv");
        await agent.BindAgentAsync(new AgentBinding(
            "research-agent", ResearchAgentDefaults.Name, ResearchAgentDefaults.SystemPrompt,
            TestSiloConfigurator.DefaultModel, ResearchAgentDefaults.EnabledTools, AgentKinds.Research));
        await ChatAsync(agent, "warm up");
        await ChatAsync(agent, FakeChatClient.ResearchTrigger);
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));

        Assert.Equal(["user", "assistant"], (await agent.GetHistoryAsync()).Select(t => t.Role));
        Assert.DoesNotContain(FakeChatClient.ResearchTrigger, (await StoredAsync(agent)).SerializedSession, StringComparison.Ordinal);
    }

    // AC13: a fork of a continuous agent chat is an ordinary per-chat conversation, never retention-pruned.
    [Fact]
    public async Task Fork_OfAContinuousChat_IsAPerChatConversation()
    {
        var source = Agent("erf-continuous-user/agent-erf-continuous");
        await ChatAsync(source, "old penguin");
        var (count, hash) = await TokenOf(source);

        var fork = await ForkAsync(source, 0, count, hash, $"erf-continuous-user/{Guid.NewGuid()}");
        Fixture.AdvanceClock(RetentionLimits.Window + TimeSpan.FromHours(1));
        await ChatAsync(fork, "new penguin");

        Assert.Equal(["old penguin", "new penguin"], UserTexts(await fork.GetHistoryAsync()));
    }

    // AC14: a target that already has a session or a persona is refused and left as it is.
    [Fact]
    public async Task InitializeFromFork_RefusesANonEmptyTarget()
    {
        var source = Agent("erf-nonempty-user/src");
        await ChatAsync(source, "source text");
        var (count, hash) = await TokenOf(source);
        var payload = (await source.CreateForkAsync(0, count, hash)).Fork!;

        var used = Agent("erf-nonempty-user/used");
        await ChatAsync(used, "already here");
        Assert.False(await used.InitializeFromForkAsync(payload));
        Assert.Equal(["already here"], UserTexts(await used.GetHistoryAsync()));

        var bound = Agent("erf-nonempty-user/bound");
        await bound.BindAgentAsync(new AgentBinding("agent-b", "B", "You are B.", TestSiloConfigurator.DefaultModel));
        Assert.False(await bound.InitializeFromForkAsync(payload));
        Assert.Empty(await bound.GetHistoryAsync());
    }

    // AC14: a fork onto a model that takes no tools strips the tool history, as a model switch would.
    [Fact]
    public async Task Fork_IntoNoToolsModel_StripsToolHistory()
    {
        var source = Agent("erf-notools-user/src");
        await ChatAsync(source, "please remind me to drink water");
        Assert.Contains(await source.GetHistoryAsync(), t => t.ToolCalls is { Count: > 0 });
        var (count, hash) = await TokenOf(source);
        var payload = (await source.CreateForkAsync(0, count, hash)).Fork!;

        var fork = Agent("erf-notools-user/fork");
        Assert.True(await fork.InitializeFromForkAsync(
            payload with { DeploymentName = TestSiloConfigurator.NoToolsNoVisionModel }));

        Assert.DoesNotContain(await fork.GetHistoryAsync(), t => t.ToolCalls is { Count: > 0 });
        Assert.Equal(TestSiloConfigurator.NoToolsNoVisionModel, await fork.GetSelectedModelAsync());
    }

    // AC14: an artifact version whose blob is missing is dropped from the fork, never fatal.
    [Fact]
    public async Task Fork_DanglingArtifactVersion_IsDropped()
    {
        var source = Agent("erf-dangling-user/src");
        await ChatAsync(source, FakeChatClient.CallToolPrompt(
            "create_artifact", new { kind = "html", title = "Page", content = "<p>v1</p>" }));
        var (count, hash) = await TokenOf(source);
        var payload = (await source.CreateForkAsync(0, count, hash)).Fork!;
        var record = Assert.Single(payload.Artifacts);
        record.Versions.Add(new ArtifactVersionRef(2, "missing-blob", 1, DateTimeOffset.UtcNow));

        var fork = Agent("erf-dangling-user/fork");
        Assert.True(await fork.InitializeFromForkAsync(payload));

        Assert.Equal([1], Assert.Single(await fork.GetArtifactsAsync()).Versions.Select(v => v.Version));
    }

    // A fork position out of range is refused.
    [Fact]
    public async Task Fork_OutOfRangePosition_IsNothing()
    {
        var source = Agent("erf-range-user/src");
        await ChatAsync(source, "only");
        var (count, hash) = await TokenOf(source);

        Assert.Equal(HistoryEditOutcome.Nothing, (await source.CreateForkAsync(1, count, hash)).Outcome);
        Assert.Equal(HistoryEditOutcome.Nothing, (await source.CreateForkAsync(-1, count, hash)).Outcome);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private IAgentGrain Agent(string key) => Cluster.GrainFactory.GetGrain<IAgentGrain>(key);

    private IAttachmentStore Store() =>
        Cluster.GetSiloServiceProvider(Cluster.Primary!.SiloAddress).GetRequiredService<IAttachmentStore>();

    private static async Task ChatAsync(
        IAgentGrain agent, string message, IReadOnlyList<AttachmentRef>? attachments = null, string? model = null,
        bool ephemeral = false)
    {
        await foreach (var _ in agent.ChatAsync(message, model, attachments, null, ephemeral))
        {
        }
    }

    private async Task<IAgentGrain> ForkAsync(
        IAgentGrain source, int throughUserTurn, int count, string hash, string forkKey)
    {
        var result = await source.CreateForkAsync(throughUserTurn, count, hash);
        Assert.Equal(HistoryEditOutcome.Ok, result.Outcome);
        var fork = Agent(forkKey);
        Assert.True(await fork.InitializeFromForkAsync(result.Fork!));
        return fork;
    }

    // The token the browser sends: the projected user-turn count and the SHA-256 of the last user text, computed
    // here independently of the grain so the two cannot share a bug.
    private static async Task<(int Count, string Hash)> TokenOf(IAgentGrain agent)
    {
        var users = UserTexts(await agent.GetHistoryAsync());
        return (users.Count, Hash(users.Count == 0 ? "" : users[^1]));
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static List<string> UserTexts(IEnumerable<ChatTurn> history)
        => [.. history.Where(t => t.Role == "user").Select(t => t.Text)];

    private async Task<List<string>> SessionUserMessagesOf(IAgentGrain agent)
    {
        var session = (await StoredAsync(agent)).SerializedSession ?? "";
        return [.. System.Text.RegularExpressions.Regex.Matches(session, "\"role\":\"user\"").Select(m => m.Value)];
    }

    private async Task<AgentGrainState> StoredAsync(IAgentGrain agent)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary!.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", agent.GetGrainId(), state);
        return state.State!;
    }

    private async Task MutateStoredAsync(IAgentGrain agent, Action<AgentGrainState> mutate)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary!.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", agent.GetGrainId(), state);
        mutate(state.State!);
        await storage.WriteStateAsync("agent", agent.GetGrainId(), state);
    }

    private void CloseStreamGates()
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<FakeChatClient>().CloseStreamGate();
        }
    }

    private static async Task<TestCluster> NewClusterAsync<TConfigurator>()
        where TConfigurator : ISiloConfigurator, new()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<TConfigurator>();
        var cluster = builder.Build();
        await cluster.DeployAsync();
        return cluster;
    }

    // AgentBinding carries a list, so record equality compares it by reference.
    private sealed class BindingComparer : IEqualityComparer<AgentBinding?>
    {
        public bool Equals(AgentBinding? x, AgentBinding? y)
            => x is not null && y is not null
                && (x.AgentId, x.Name, x.SystemPrompt, x.Model, x.Kind) == (y.AgentId, y.Name, y.SystemPrompt, y.Model, y.Kind)
                && (x.EnabledTools ?? []).SequenceEqual(y.EnabledTools ?? []);

        public int GetHashCode(AgentBinding? obj) => obj?.AgentId.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
