using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The Auto model on the grain (ADR 0137): the router decides, the grain applies it through the ordinary model switch,
/// and the reserved wire value <c>auto</c> is never persisted as a model.
/// </summary>
public sealed class ModelRouterGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string Blind = TestSiloConfigurator.NoVisionModel;

    private static int _conversation;

    private static string NewKey() => $"router-user/conv-{Interlocked.Increment(ref _conversation)}-{Guid.NewGuid():N}";

    // A grain may activate on any silo, so the router goes on all of them.
    private FakeModelRouter UseRouter(RouterDecision? decision, Exception? throws = null)
    {
        var router = new FakeModelRouter { Decision = decision, Throws = throws };
        SetRouter(router);
        return router;
    }

    private void SetRouter(IModelRouter? router)
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<FakeModelRouterProvider>().Router = router;
        }
    }

    private static async Task<List<AgentStreamEvent>> Collect(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    private static RouterDecision To(string model, bool changed = true) => new(model, model, changed);

    [Fact]
    public async Task AutoTurn_PersistsRealModel_AndRestoresToAuto()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        var routed = Assert.Single(events, e => e.Type == AgentStreamEventType.Routed);
        Assert.Equal((Blind, Blind), (routed.Model, routed.Text));
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        Assert.Single(router.Calls);
        // The picker restores to Auto, and the REAL model is what the conversation runs on.
        Assert.Equal(AutoModelId.Value, await agent.GetSelectedModelAsync());
        SetRouter(null);
        Assert.Equal(Blind, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task RouterFailure_KeepsModel_AndTheTurnCompletes()
    {
        UseRouter(null, throws: new InvalidOperationException("router down"));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Routed);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task RouterReturnsNothing_KeepsModel()
    {
        UseRouter(null);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Routed);
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task Sticky_LabelsEveryAutoTurnWithTheModelInUse()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var first = await Collect(agent.ChatAsync("one", AutoModelId.Value));
        router.Decision = To(Blind, changed: false);
        var second = await Collect(agent.ChatAsync("two", AutoModelId.Value));
        router.Decision = To(TestSiloConfigurator.DefaultModel);
        var third = await Collect(agent.ChatAsync("three", AutoModelId.Value));

        Assert.Contains(first, e => e.Type == AgentStreamEventType.Routed);
        Assert.Equal(Blind, Assert.Single(second, e => e.Type == AgentStreamEventType.Routed).Model);
        Assert.Equal(TestSiloConfigurator.DefaultModel, Assert.Single(third, e => e.Type == AgentStreamEventType.Routed).Model);
        Assert.Equal(3, router.Calls.Count);
        // The router is told the model the conversation is on, so it can be sticky.
        Assert.Equal([TestSiloConfigurator.DefaultModel, Blind, Blind], router.Calls.Select(c => c.CurrentModelId));
    }

    // ADR 0018: a grain resolves its providers once per activation, so a model added after it woke is unknown to it. Switching onto what
    // the router chose must reload them first, or the turn falls back to the default model while the label names the new one.
    private int FactoryResolutions() => Cluster.Silos.Sum(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
        .GetRequiredService<CountingChatClientFactoryProvider>().Calls);

    [Fact]
    public async Task AutoSwitch_ReloadsTheProviders_ButAKeptModelDoesNot()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("warm up", TestSiloConfigurator.DefaultModel));
        var afterActivation = FactoryResolutions();

        await Collect(agent.ChatAsync("route me", AutoModelId.Value));
        Assert.Equal(afterActivation + 1, FactoryResolutions());

        // Same model again, a null decision, and an explicit pick never reload.
        router.Decision = To(Blind, changed: false);
        await Collect(agent.ChatAsync("again", AutoModelId.Value));
        router.Decision = null;
        await Collect(agent.ChatAsync("nothing", AutoModelId.Value));
        await Collect(agent.ChatAsync("by hand", TestSiloConfigurator.DefaultModel));
        Assert.Equal(afterActivation + 1, FactoryResolutions());
    }

    [Fact]
    public async Task ExplicitModel_ClearsAuto()
    {
        UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("hello", AutoModelId.Value));
        Assert.Equal(AutoModelId.Value, await agent.GetSelectedModelAsync());

        await Collect(agent.ChatAsync("hello again", TestSiloConfigurator.DefaultModel));

        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task NullModel_FromABackgroundRun_KeepsAuto()
    {
        UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        await Collect(agent.ChatAsync("follow up", null));

        Assert.Equal(AutoModelId.Value, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task NoRouter_AutoIsIgnored_NeverPersisted()
    {
        SetRouter(null);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Routed);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task StaleAuto_AfterTheRouterIsGone_ReportsTheRealModelAndRunsOnIt()
    {
        UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        SetRouter(null);
        var events = await Collect(agent.ChatAsync("still sending auto", AutoModelId.Value));

        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
        Assert.Equal(Blind, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task Auto_SkipsEphemeralTurns()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("incognito", AutoModelId.Value, ephemeral: true));

        Assert.Empty(router.Calls);
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Routed);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    // A turn that overlaps another run would swap the agent and session under it (ADR 0106): it never routes.
    [Fact]
    public async Task Auto_SkipsATurnWhileAnotherRunIsInFlight()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        await using var slow = agent.ChatAsync(FakeChatClient.SlowStreamMarker, TestSiloConfigurator.DefaultModel).GetAsyncEnumerator();
        Assert.True(await slow.MoveNextAsync());
        var second = await Collect(agent.ChatAsync("route me", AutoModelId.Value)).WaitAsync(TimeSpan.FromSeconds(20));
        while (await slow.MoveNextAsync())
        {
        }

        Assert.Empty(router.Calls);
        Assert.DoesNotContain(second, e => e.Type == AgentStreamEventType.Routed);
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    // The switch below an explicit pick still runs on an incognito turn, so Auto must not outlive it.
    [Fact]
    public async Task ExplicitModel_OnAnIncognitoTurn_ClearsAuto()
    {
        UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        await Collect(agent.ChatAsync("hidden", TestSiloConfigurator.DefaultModel, ephemeral: true));

        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task Auto_DoesNotRouteAnAgentPinnedChat()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await agent.BindAgentAsync(new AgentBinding("agent-1", "Pinned", "prompt", "pinned-model"));

        await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        Assert.Empty(router.Calls);
        Assert.Equal("pinned-model", await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task Router_IsToldTheTurnFacts()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        await Collect(agent.ChatAsync("a hard question", AutoModelId.Value));

        var call = Assert.Single(router.Calls);
        Assert.Equal(("a hard question", false, true), (call.Message, call.HasImages, call.ToolsEnabled));
    }

    // ---- Auto routes a picture request to an image model (ADR 0138) -------------------------------------------------------

    private const string Maker = TestSiloConfigurator.ImageModel;

    private static RouterDecision Pic(bool changed = true) => new(Maker, "Picture maker", changed, ImageGeneration: true);

    private IEnumerable<ProbeImageGenerator> Probes() => Cluster.Silos
        .Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<ProbeImageGenerator>());

    private void ProbeAll(Action<ProbeImageGenerator> configure)
    {
        foreach (var probe in Probes())
        {
            configure(probe);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for a condition");
            await Task.Delay(20);
        }
    }

    private static async Task<byte[]> StoredBytesAsync(string key, AgentStreamEvent image)
        => (await new InMemoryAttachmentStore().OpenReadAsync(key, image.AttachmentId!))!.Bytes;

    private static async Task<AgentStreamEvent> DrawAsync(IAgentGrain agent, string prompt, params AttachmentRef[] attachments)
        => Assert.Single(await Collect(agent.ChatAsync(prompt, AutoModelId.Value, attachments)), e => e.Type == AgentStreamEventType.Image);

    [Fact]
    public async Task AutoImageTurn_DoesNotSwitchTheChatModel()
    {
        UseRouter(Pic());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("draw a cat", AutoModelId.Value));

        Assert.Equal([AgentStreamEventType.Routed, AgentStreamEventType.Image, AgentStreamEventType.Done], events.Select(e => e.Type));
        Assert.Equal((Maker, "Picture maker"), (events[0].Model, events[0].Text));
        Assert.Equal(AutoModelId.Value, await agent.GetSelectedModelAsync());
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task AutoImageTurn_NextTextTurnUsesTheChatModel()
    {
        var router = UseRouter(Pic());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await DrawAsync(agent, "draw a cat");

        router.Decision = null;
        var autoText = await Collect(agent.ChatAsync("thanks", AutoModelId.Value));
        var explicitText = await Collect(agent.ChatAsync("and again", TestSiloConfigurator.DefaultModel));

        Assert.DoesNotContain(autoText, e => e.Type is AgentStreamEventType.Image or AgentStreamEventType.Routed);
        Assert.Contains(autoText, e => e.Type == AgentStreamEventType.TextDelta);
        Assert.Contains(explicitText, e => e.Type == AgentStreamEventType.TextDelta);
    }

    [Fact]
    public async Task AutoImageTurn_EditInput_ThisTurnsAttachmentWins()
    {
        UseRouter(Pic());
        var key = NewKey();
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var photo = await new InMemoryAttachmentStore().SaveAsync(key, "in.png", "image/png", [10, 20, 30]);

        var image = await DrawAsync(agent, "make it blue", photo);

        Assert.Equal(new byte[] { 10, 20, 30 }, await StoredBytesAsync(key, image));
    }

    [Fact]
    public async Task AutoImageTurn_EditInput_IsThePictureThePreviousTurnMade()
    {
        UseRouter(Pic());
        var key = NewKey();
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var photo = await new InMemoryAttachmentStore().SaveAsync(key, "in.png", "image/png", [4, 5, 6]);
        await DrawAsync(agent, "make it blue", photo);

        // The probe echoes its input, so these bytes can only come from an edit of the previous turn's picture.
        var image = await DrawAsync(agent, "now make it bluer");

        Assert.Equal(new byte[] { 4, 5, 6 }, await StoredBytesAsync(key, image));
    }

    [Fact]
    public async Task AutoImageTurn_EditInput_NeverUsesAnOlderImage()
    {
        UseRouter(Pic());
        var key = NewKey();
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var photo = await new InMemoryAttachmentStore().SaveAsync(key, "old.png", "image/png", [7, 7, 7]);
        await Collect(agent.ChatAsync("here is a photo", TestSiloConfigurator.DefaultModel, [photo]));

        var image = await DrawAsync(agent, "draw a dog");

        // Text-to-image, not an edit of the photo two turns back.
        Assert.Equal(ProbeImageGenerator.TextToImageBytes, await StoredBytesAsync(key, image));
    }

    [Fact]
    public async Task AutoImageTurn_Regenerate_UsesThePreviousImage()
    {
        UseRouter(Pic());
        var key = NewKey();
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var photo = await new InMemoryAttachmentStore().SaveAsync(key, "in.png", "image/png", [8, 9]);
        await DrawAsync(agent, "make it blue", photo);
        await DrawAsync(agent, "make it red");

        var users = (await agent.GetHistoryAsync()).Where(h => h.Role == "user").Select(h => h.Text).ToList();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(users[^1])));
        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(users.Count, hash, ephemeral: false));
        var again = Assert.Single(
            await Collect(agent.ChatAsync("make it red", AutoModelId.Value, replacesLastTurn: true)),
            e => e.Type == AgentStreamEventType.Image);

        // The removed turn's picture is gone, so the re-run edits the one before it.
        Assert.Equal(new byte[] { 8, 9 }, await StoredBytesAsync(key, again));
    }

    [Fact]
    public async Task AutoImageTurn_AlwaysLabelled()
    {
        UseRouter(Pic());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var first = await Collect(agent.ChatAsync("draw a cat", AutoModelId.Value));
        var second = await Collect(agent.ChatAsync("draw a dog", AutoModelId.Value));

        foreach (var events in new[] { first, second })
        {
            var routed = Assert.Single(events, e => e.Type == AgentStreamEventType.Routed);
            Assert.Equal((Maker, "Picture maker"), (routed.Model, routed.Text));
        }
    }

    [Fact]
    public async Task AutoImageTurn_HistoryImages_StillOffersImageModel()
    {
        var router = UseRouter(Pic());
        var key = NewKey();
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var photo = await new InMemoryAttachmentStore().SaveAsync(key, "up.png", "image/png", [1, 2, 3]);
        await Collect(agent.ChatAsync("a photo", TestSiloConfigurator.DefaultModel, [photo]));

        var events = await Collect(agent.ChatAsync("draw a cat", AutoModelId.Value));

        // A USER image restricts chat candidates to vision models, but the picture option is still offered and used.
        Assert.True(router.Calls.Single().HasImages);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.Image);

        // A generated image alone never sets HasImages.
        var fresh = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await DrawAsync(fresh, "draw a cat");
        await DrawAsync(fresh, "draw a dog");
        Assert.False(router.Calls.Last().HasImages);
    }

    [Fact]
    public async Task PreviousReplyWasImage_TrueOnlyRightAfterAPicture()
    {
        var router = UseRouter(To(TestSiloConfigurator.DefaultModel, changed: false));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        await Collect(agent.ChatAsync("hello", AutoModelId.Value));
        router.Decision = Pic();
        await Collect(agent.ChatAsync("draw a cat", AutoModelId.Value));
        router.Decision = To(TestSiloConfigurator.DefaultModel, changed: false);
        await Collect(agent.ChatAsync("make it bluer", AutoModelId.Value));
        await Collect(agent.ChatAsync("thanks", AutoModelId.Value));

        Assert.Equal([false, false, true, false], router.Calls.Select(c => c.PreviousReplyWasImage));
    }

    [Fact]
    public async Task PreviousReplyWasImage_AlsoAfterAPictureChosenByHand()
    {
        var router = UseRouter(To(TestSiloConfigurator.DefaultModel));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("draw a cat", Maker));

        await Collect(agent.ChatAsync("make it bluer", AutoModelId.Value));

        Assert.True(router.Calls.Single().PreviousReplyWasImage);
    }

    [Fact]
    public async Task AutoFromAnImageModel_AChatWinnerSwitchesOffIt_AnImageWinnerDoesNot()
    {
        var router = UseRouter(To(TestSiloConfigurator.DefaultModel));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("draw a cat", Maker));

        var chat = await Collect(agent.ChatAsync("hello there", AutoModelId.Value));

        // An image model is never the "current" model the router is told about, so the chat winner counts as a change.
        Assert.Null(router.Calls.Single().CurrentModelId);
        Assert.Contains(chat, e => e.Type == AgentStreamEventType.TextDelta);
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());

        // And a conversation that stays on the image model keeps it when the router wants another picture.
        var other = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(other.ChatAsync("draw a cat", Maker));
        UseRouter(Pic());
        Assert.Contains(await Collect(other.ChatAsync("and a dog", AutoModelId.Value)), e => e.Type == AgentStreamEventType.Image);
        SetRouter(null);
        Assert.Equal(Maker, await other.GetSelectedModelAsync());
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("throws")]
    [InlineData("no-router")]
    [InlineData("incognito")]
    public async Task AutoFromAnImageModel_Unrouted_UsesTheDefaultChatModel(string how)
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("draw a cat", Maker));
        switch (how)
        {
            case "nothing":
                UseRouter(null);
                break;
            case "throws":
                UseRouter(null, throws: new InvalidOperationException("router down"));
                break;
            case "no-router":
                SetRouter(null);
                break;
            default:
                UseRouter(Pic());
                break;
        }

        var events = await Collect(agent.ChatAsync("what is two plus two", AutoModelId.Value, ephemeral: how == "incognito"));

        // A text turn is never sent to the picture endpoint.
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Image);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task AutoFromAnImageModel_InFlight_DoesNotSwitch()
    {
        var router = UseRouter(To(TestSiloConfigurator.DefaultModel));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        var gate = new TaskCompletionSource();
        ProbeAll(p => p.Gate = gate.Task);

        // A by-hand picture parked at the generator, then an Auto turn that overlaps it.
        var first = Collect(agent.ChatAsync("draw a cat", Maker));
        await WaitUntil(() => Probes().Sum(p => p.Entered) == 1);
        var second = Collect(agent.ChatAsync("and a dog", AutoModelId.Value));
        await WaitUntil(() => Probes().Sum(p => p.Entered) == 2);
        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        // The overlapping turn cannot switch (ADR 0106): it neither routed nor left the image model.
        Assert.Empty(router.Calls);
        SetRouter(null);
        Assert.Equal(Maker, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task AutoImage_SkipsIncognitoAgentPinnedAndInFlightTurns()
    {
        var router = UseRouter(Pic());

        var incognito = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        var ephemeral = await Collect(incognito.ChatAsync("draw a cat", AutoModelId.Value, ephemeral: true));
        Assert.DoesNotContain(ephemeral, e => e.Type == AgentStreamEventType.Image);

        var pinned = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await pinned.BindAgentAsync(new AgentBinding("agent-1", "Pinned", "prompt", "pinned-model"));
        var agentChat = await Collect(pinned.ChatAsync("draw a cat", AutoModelId.Value));
        Assert.DoesNotContain(agentChat, e => e.Type == AgentStreamEventType.Image);

        var busy = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await using var slow = busy.ChatAsync(FakeChatClient.SlowStreamMarker, TestSiloConfigurator.DefaultModel).GetAsyncEnumerator();
        Assert.True(await slow.MoveNextAsync());
        var overlapping = await Collect(busy.ChatAsync("draw a cat", AutoModelId.Value)).WaitAsync(TimeSpan.FromSeconds(20));
        while (await slow.MoveNextAsync())
        {
        }

        Assert.DoesNotContain(overlapping, e => e.Type == AgentStreamEventType.Image);
        Assert.Empty(router.Calls);
    }

    [Fact]
    public async Task AutoImage_DroppedWhenARunStartsDuringTheRouterCall()
    {
        var router = UseRouter(Pic());
        var gate = new TaskCompletionSource();
        router.Gate = gate.Task;
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var auto = Collect(agent.ChatAsync("draw a cat", AutoModelId.Value));
        await WaitUntil(() => router.Calls.Count == 1);
        await using var slow = agent.ChatAsync(FakeChatClient.SlowStreamMarker, TestSiloConfigurator.DefaultModel).GetAsyncEnumerator();
        Assert.True(await slow.MoveNextAsync());
        gate.SetResult();
        var events = await auto.WaitAsync(TimeSpan.FromSeconds(20));
        while (await slow.MoveNextAsync())
        {
        }

        // The decision arrived after another run began: dropped, so no picture and no label.
        Assert.DoesNotContain(events, e => e.Type is AgentStreamEventType.Image or AgentStreamEventType.Routed);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
    }

    [Fact]
    public async Task AutoImageTurn_GenerationFailure_Surfaces()
    {
        UseRouter(Pic());
        ProbeAll(p => p.Failure = new InvalidOperationException("image provider down"));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in agent.ChatAsync("draw a cat", AutoModelId.Value))
            {
            }
        });

        // Nothing was recorded, and there was no silent fall back to a chat reply.
        Assert.DoesNotContain(await agent.GetHistoryAsync(), t => t.Text == "draw a cat");
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task AutoImageTurn_ReloadsTheProviders()
    {
        var router = UseRouter(Pic());
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("warm up", TestSiloConfigurator.DefaultModel));
        var afterActivation = FactoryResolutions();

        await DrawAsync(agent, "draw a cat");
        Assert.Equal(afterActivation + 1, FactoryResolutions());

        // A kept decision and a null decision do not reload.
        router.Decision = Pic(changed: false);
        await DrawAsync(agent, "draw a dog");
        router.Decision = null;
        await Collect(agent.ChatAsync("nothing", AutoModelId.Value));
        Assert.Equal(afterActivation + 1, FactoryResolutions());
    }

    [Fact]
    public async Task AutoOutsideChat_BindAgent_IsRefused_AndNothingIsPersisted()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        await Assert.ThrowsAsync<ArgumentException>(
            () => agent.BindAgentAsync(new AgentBinding("agent-1", "Pinned", "prompt", AutoModelId.Value)));

        Assert.Null(await agent.GetAgentBindingAsync());
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }
}
