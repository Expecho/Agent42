using Orleans.Core.Internal;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// Hosts the minimal-API endpoints in-memory (TestServer) against a real Orleans test
/// cluster, so the HTTP contract the browser depends on is exercised end to end.
/// </summary>
public sealed class AgentApiTests(AgentApiFixture fixture) : SharedClusterTests<AgentApiFixture>(fixture)
{
    // The host, the cluster and the client are all the fixture's, deployed once for the class.
    private HttpClient Client => Fixture.Client;

    [Fact]
    public async Task SelectedModelEndpoint_RespondsWithJson_NotPlainText()
    {
        // Regression: the endpoint used to return the model as a bare string, which the
        // framework serves as text/plain. The UI parses this response as JSON, so a
        // text/plain body threw in response.json() and silently aborted rendering the
        // conversation history alongside it.
        var response = await Client.GetAsync("/api/conversations/model-contract-conversation/model");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var model = await response.Content.ReadFromJsonAsync<string>();
        Assert.False(string.IsNullOrWhiteSpace(model));
    }

    // ADR 0089 (step 1): the endpoint passes the browser's advertised client tools through, narrowed to the ones
    // the server defines. An unknown name is dropped, so a client advertising only that gets no broker: the call
    // answers "unavailable" and nothing is flagged for the browser.
    [Theory]
    [InlineData(new[] { "no_such_tool", ClientToolLimits.GetBrowserContext }, true)]
    [InlineData(new[] { "no_such_tool" }, false)]
    public async Task ChatEndpoint_PassesClientToolsThrough(string[] advertised, bool flagged)
    {
        var id = $"api-client-tools-{flagged}";
        var prompt = FakeChatClient.CallToolPrompt(ClientToolLimits.GetBrowserContext, new { });

        using var chat = await Client.PostAsJsonAsync(
            $"/api/chat/{id}", new ChatRequest(prompt, null, ClientTools: advertised));
        var ndjson = await chat.Content.ReadAsStringAsync();

        Assert.Equal(flagged, ndjson.Contains("\"clientTool\":true", StringComparison.Ordinal));
        Assert.Contains(flagged ? "timeout" : "unavailable", ndjson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessagesEndpoint_ReturnsTheConversationHistoryAsJson()
    {
        const string id = "api-history-conversation";

        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{id}", new ChatRequest("hi", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain the NDJSON stream so the turn is persisted
        }

        var response = await Client.GetAsync($"/api/conversations/{id}/messages");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var turns = await response.Content.ReadFromJsonAsync<List<ChatTurn>>();
        Assert.NotNull(turns);
        Assert.Contains(turns!, t => t.Role == "user" && t.Text == "hi");
        Assert.Contains(turns!, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    // AC13 (ADR 0093) — a PWA still running the old cached shell posts a `location` member that ChatRequest no
    // longer has. System.Text.Json ignores an unknown member by default, so the turn still binds and streams; and
    // the posted position is NOT used — that old client never advertised the location client tool, so the tool
    // answers that no browser can read the location, until the service worker swaps the shell.
    [Fact]
    public async Task Chat_OldClientBodyWithLocation_StillBinds()
    {
        using var body = new StringContent(
            """{"message":"where am i","model":null,"location":{"latitude":52.3676,"longitude":4.9041,"accuracyMetres":12}}""",
            System.Text.Encoding.UTF8, "application/json");

        using var chat = await Client.PostAsync("/api/chat/api-old-client-location-conversation", body);

        chat.EnsureSuccessStatusCode();
        var stream = await chat.Content.ReadAsStringAsync();

        Assert.Contains("No browser is connected to this chat", stream);
        Assert.DoesNotContain(FakeGeocoder.Address, stream);
    }

    [Fact]
    public async Task DeleteConversation_RemovesItFromTheListAndClearsHistory()
    {
        const string id = "api-delete-conversation";

        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{id}", new ChatRequest("hi", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain the NDJSON stream so the conversation is persisted
        }

        var before = await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations");
        Assert.Contains(before!, c => c.Id == id);

        var delete = await Client.DeleteAsync($"/api/conversations/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var after = await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations");
        Assert.DoesNotContain(after!, c => c.Id == id);

        var history = await Client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{id}/messages");
        Assert.Empty(history!);
    }

    // Pinned conversations (ADR 0045). The listing is shared across this class's tests, so these assert the
    // RELATIVE order of their own two ids rather than an absolute index — an unrelated pinned chat left by a
    // sibling test must not decide the outcome.
    [Fact]
    public async Task PinConversation_HoistsItInTheListing()
    {
        const string olderId = "api-pin-older";
        const string newerId = "api-pin-newer";

        await StartConversationAsync(olderId, "the older chat");
        await StartConversationAsync(newerId, "the newer chat");

        var pin = await Client.PutAsJsonAsync($"/api/conversations/{olderId}/pin", new PinConversationRequest(true));
        Assert.Equal(HttpStatusCode.NoContent, pin.StatusCode);

        var conversations = (await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!;

        Assert.True(conversations.FindIndex(c => c.Id == olderId) < conversations.FindIndex(c => c.Id == newerId));
        Assert.True(Assert.Single(conversations, c => c.Id == olderId).Pinned);
        Assert.False(Assert.Single(conversations, c => c.Id == newerId).Pinned);
    }

    [Fact]
    public async Task PinConversation_Unpin_RestoresRecencyOrder()
    {
        const string olderId = "api-unpin-older";
        const string newerId = "api-unpin-newer";

        await StartConversationAsync(olderId, "the older chat");
        // UtcNow is coarse on Windows and OrderByDescending is stable, so two touches inside one clock
        // tick would keep insertion order — the opposite of the recency claim this test makes.
        await Task.Delay(20);
        await StartConversationAsync(newerId, "the newer chat");

        await Client.PutAsJsonAsync($"/api/conversations/{olderId}/pin", new PinConversationRequest(true));
        var unpin = await Client.PutAsJsonAsync($"/api/conversations/{olderId}/pin", new PinConversationRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, unpin.StatusCode);

        var conversations = (await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!;

        Assert.True(conversations.FindIndex(c => c.Id == newerId) < conversations.FindIndex(c => c.Id == olderId));
        Assert.False(Assert.Single(conversations, c => c.Id == olderId).Pinned);
    }

    [Fact]
    public async Task PinConversation_UnknownId_Returns204()
    {
        // Idempotent silent no-op: an unlisted id must not be distinguishable from a listed one.
        var pin = await Client.PutAsJsonAsync(
            "/api/conversations/api-pin-does-not-exist/pin", new PinConversationRequest(true));

        Assert.Equal(HttpStatusCode.NoContent, pin.StatusCode);

        var conversations = (await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!;
        Assert.DoesNotContain(conversations, c => c.Id == "api-pin-does-not-exist");
    }

    // Routing and idempotence only — this one stays green even if the handler never calls the grain, and
    // that is all it claims. The stop itself is proved by the test below (over HTTP) and by
    // StopGenerationTests (at the grain).
    [Fact]
    public async Task StopChat_Returns204()
    {
        // Silent for the same reason the pin route is (ADR 0078): by the time a press arrives the turn may
        // have finished on its own, so "there was nothing to stop" is not an error the client can act on.
        var stop = await Client.PostAsync("/api/chat/api-stop-nothing-in-flight/stop", content: null);

        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
    }

    // ADR 0078 on the wire, which is the half no C# test above reaches: `stopped` is the field send.js
    // dispatches on to mark the bubble, and nothing in the endpoint spells it — the record is serialized
    // directly, so renaming the property would break the button with every other test still green. It also
    // pins the nullable shape: a completed turn must OMIT the field, not send `"stopped":false`, or every
    // per-token delta of every turn carries it.
    [Fact]
    public async Task ChatEndpoint_StreamsStoppedFlagOnlyWhenStopped()
    {
        const string conversationId = "api-stop-midstream-conversation";
        var gates = Fixture.Cluster.Silos
            .Select(silo => Fixture.Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<FakeChatClient>().OpenStreamGate())
            .ToList();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/chat/{conversationId}")
        {
            Content = JsonContent.Create(new ChatRequest("tell me a long story", null)),
        };

        // ResponseHeadersRead, or the client would buffer the whole turn before we could stop it.
        using var chat = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        chat.EnsureSuccessStatusCode();

        await Task.WhenAny(gates.Select(g => g.Reached.Task));
        var stop = await Client.PostAsync($"/api/chat/{conversationId}/stop", content: null);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        foreach (var gate in gates)
        {
            gate.Release.TrySetResult();
        }

        var lines = (await chat.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var done = lines[^1];
        Assert.Equal("done", done.GetProperty("type").GetString());
        Assert.True(done.GetProperty("stopped").GetBoolean());

        // Every other event omits it entirely — including the deltas, which is the reason the property is
        // nullable rather than a plain bool.
        Assert.DoesNotContain(lines[..^1], e => e.TryGetProperty("stopped", out _));

        // And the partial reply is on the server, not just on the wire.
        var history = await Client.GetFromJsonAsync<List<ChatTurn>>(
            $"/api/conversations/{conversationId}/messages");
        Assert.Equal(2, history!.Count);
        Assert.DoesNotContain("help you today?", history[1].Text ?? "", StringComparison.Ordinal);
    }

    // AC4 (ADR 0071): reasoning reaches the browser as its own NDJSON event, typed "reasoning".
    //
    // The WIRE SHAPE is the contract send.js dispatches on, and nothing in the endpoint spells it — the
    // event record is serialized directly and the enum member's camelCase name becomes the type string. So
    // renaming the member would silently change the wire type and break the client with every C# test still
    // green. This asserts the literal the client actually compares against.
    [Fact]
    public async Task ChatEndpoint_StreamsReasoningAsNdjson()
    {
        using var chat = await Client.PostAsJsonAsync(
            "/api/chat/api-reasoning-conversation",
            new ChatRequest($"hi, {FakeChatClient.ReasoningStreamMarker}", null));
        chat.EnsureSuccessStatusCode();

        var lines = (await chat.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var reasoning = Assert.Single(lines, e => e.GetProperty("type").GetString() == "reasoning");
        Assert.Equal(FakeChatClient.ReasoningText, reasoning.GetProperty("text").GetString());

        // It rides the SAME `text` property as a textDelta rather than a field of its own, so a client that
        // read chunk.text on the wrong arm would still see content — the type string is the discriminator.
        Assert.Contains(lines, e => e.GetProperty("type").GetString() == "textDelta");
        Assert.Equal("done", lines[^1].GetProperty("type").GetString());
    }

    // AC18/19 (ADR 0082) on the wire: send.js dispatches on "todos" and "textReset", and reads id/title/done off each
    // todo. Nothing in the endpoint spells any of them, so a renamed enum member or record property would break the
    // checklist with every grain test still green.
    [Fact]
    public async Task ChatEndpoint_StreamsResearchEventsAsNdjson()
    {
        var research = (await ListAgentsAsAsync("local-dev")).Single(a => a.Kind == AgentKinds.Research);

        using var chat = await Client.PostAsJsonAsync(
            "/api/chat/api-research-conversation",
            new ChatRequest(FakeChatClient.ResearchTrigger, null, AgentId: research.Id));
        chat.EnsureSuccessStatusCode();

        var lines = (await chat.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var todos = lines.Last(e => e.GetProperty("type").GetString() == "todos").GetProperty("todos");
        var first = todos.EnumerateArray().First();
        Assert.Equal(1, first.GetProperty("id").GetInt32());
        Assert.Equal("Find sources", first.GetProperty("title").GetString());
        Assert.True(first.GetProperty("done").GetBoolean());
        Assert.Single(lines, e => e.GetProperty("type").GetString() == "textReset");
    }

    // Runs one turn so the conversation is listed in the index, draining the NDJSON stream.
    private async Task StartConversationAsync(string conversationId, string prompt)
    {
        using var chat = await Client.PostAsJsonAsync($"/api/chat/{conversationId}", new ChatRequest(prompt, null));
        chat.EnsureSuccessStatusCode();
        await chat.Content.ReadAsStringAsync();
    }

    // Ephemeral / incognito chat (ADR 0034): a POST with ephemeral:true is not listed in the sidebar —
    // the endpoint skips TouchAsync, so the conversation never enters /api/conversations. (Non-persistence
    // of the session itself is the grain's job — see AgentGrainTests.EphemeralChat_LeavesNoHistory_AfterReactivation.)
    // Contrast with a normal chat on the same run, which IS listed, so this proves the skip, not an empty index.
    [Fact]
    public async Task EphemeralChat_IsNotListedInTheSidebar()
    {
        const string ephemeralId = "api-ephemeral-conversation";
        const string normalId = "api-normal-conversation";

        using (var chat = await Client.PostAsJsonAsync(
            $"/api/chat/{ephemeralId}", new ChatRequest("secret", null, null, null, Ephemeral: true)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain the NDJSON stream
        }

        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{normalId}", new ChatRequest("hi", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        var conversations = await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations");
        Assert.DoesNotContain(conversations!, c => c.Id == ephemeralId);
        Assert.Contains(conversations!, c => c.Id == normalId);
    }

    // ADR 0134: an incognito chat with a per-chat agent is not listed and the bind leaves no stored record.
    [Fact]
    public async Task IncognitoAgentChat_IsNotListed_AndLeavesNoRecord()
    {
        const string convId = "api-incognito-agent";
        var (agentId, _) = await CreateAgentAsync("Hidden");

        using (var chat = await Client.PostAsJsonAsync(
            $"/api/chat/{convId}", new ChatRequest("secret", null, agentId, null, Ephemeral: true)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        var conversations = await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations");
        Assert.DoesNotContain(conversations!, c => c.Id == convId);
        var grain = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{LocalDevUser}/{convId}");
        Assert.NotNull(await grain.GetAgentBindingAsync()); // bound in memory for this activation
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reactivated = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{LocalDevUser}/{convId}");
        Assert.Null(await reactivated.GetAgentBindingAsync());
        Assert.Empty(await reactivated.GetHistoryAsync());
    }

    // ADR 0134: the client resends agentId every incognito turn, so a grain that deactivated in between rebinds.
    [Fact]
    public async Task IncognitoAgentChat_RebindsAfterDeactivation()
    {
        const string convId = "api-incognito-rebind";
        var (agentId, _) = await CreateAgentAsync("Rebinder");
        var request = new ChatRequest("secret", null, agentId, null, Ephemeral: true);

        using (var first = await Client.PostAsJsonAsync($"/api/chat/{convId}", request))
        {
            first.EnsureSuccessStatusCode();
            await first.Content.ReadAsStringAsync();
        }

        var grain = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{LocalDevUser}/{convId}");
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        using (var second = await Client.PostAsJsonAsync($"/api/chat/{convId}", request))
        {
            second.EnsureSuccessStatusCode();
            await second.Content.ReadAsStringAsync();
        }

        var binding = await Cluster.GrainFactory.GetGrain<IAgentGrain>($"{LocalDevUser}/{convId}").GetAgentBindingAsync();
        Assert.Equal(agentId, binding!.AgentId);
    }

    // ADR 0134: a continuous agent's conversation is one shared persisted grain, so an incognito turn is refused
    // before any touch, bind or turn, and nothing is written.
    [Fact]
    public async Task IncognitoTurn_OnContinuousConversation_Is400()
    {
        var (agentId, _) = await CreateAgentAsync("Continuous");
        var convId = $"{RetentionLimits.ContinuousConversationIdPrefix}{agentId}";

        using var res = await Client.PostAsJsonAsync(
            $"/api/chat/{convId}", new ChatRequest("secret", null, agentId, null, Ephemeral: true));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var grain = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{LocalDevUser}/{convId}");
        Assert.Null(await grain.GetAgentBindingAsync());
        Assert.Empty(await grain.GetHistoryAsync());
    }

    // AC5: agents CRUD is reachable over HTTP and returns JSON.
    [Fact]
    public async Task AgentsEndpoints_CrudRoundTrip_AsJson()
    {
        var createRes = await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Marvin", "Paranoid android", "gpt-4o", "You are perpetually depressed.",
                Continuous: true));
        createRes.EnsureSuccessStatusCode();
        Assert.Equal("application/json", createRes.Content.Headers.ContentType?.MediaType);
        // The create/update body is now AgentSaveResponse { agent, apiKey } (ADR 0022), not a bare
        // AgentDefinition — read the wrapper and use .Agent.
        var createBody = await createRes.Content.ReadFromJsonAsync<AgentSaveResponse>();
        Assert.NotNull(createBody);
        var created = createBody!.Agent;
        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.Equal("Marvin", created.Name);
        // AC1: creating an agent returns a raw key ONCE.
        Assert.False(string.IsNullOrEmpty(createBody.ApiKey), "create should mint an API key.");
        // AC2: POST echoes the history-mode flag.
        Assert.True(created.Continuous);

        var list = await Client.GetFromJsonAsync<List<AgentDefinition>>("/api/agents");
        // AC2: GET carries it too.
        Assert.Contains(list!, a => a.Id == created.Id && a.Name == "Marvin" && a.Continuous);

        // AC2: PUT accepts and echoes a flipped flag.
        var updateRes = await Client.PutAsJsonAsync($"/api/agents/{created.Id}",
            new AgentUpsertRequest("Marvin II", "Still paranoid", "gpt-4o", "You are brilliant but morose.",
                Continuous: false));
        updateRes.EnsureSuccessStatusCode();
        var updateBody = await updateRes.Content.ReadFromJsonAsync<AgentSaveResponse>();
        var updated = updateBody!.Agent;
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Marvin II", updated.Name);
        Assert.False(updated.Continuous);
        // A plain update (no regenerate) mints no key.
        Assert.Null(updateBody.ApiKey);

        // AC3: an old-shape client that OMITS `continuous` entirely stores false (per-chat) — the
        // real "old data defaults to per-chat" property, asserted at the JSON layer (not the ctor).
        // An anonymous payload without the field, so it is genuinely absent on the wire.
        var oldShapeRes = await Client.PostAsJsonAsync("/api/agents",
            new { name = "Legacy", description = "no flag", model = "gpt-4o", systemPrompt = "You are old." });
        oldShapeRes.EnsureSuccessStatusCode();
        var legacy = (await oldShapeRes.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;
        Assert.NotNull(legacy);
        Assert.False(legacy.Continuous, "an omitted `continuous` must default to false (per-chat).");
        await Client.DeleteAsync($"/api/agents/{legacy.Id}");

        var deleteRes = await Client.DeleteAsync($"/api/agents/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        var after = await Client.GetFromJsonAsync<List<AgentDefinition>>("/api/agents");
        Assert.DoesNotContain(after!, a => a.Id == created.Id);
    }

    // A create may claim the copilot kind (forced per-chat). Research stays seed-only and an unknown kind is refused
    // with a 400 instead of being stored as an ordinary agent the caller didn't ask for.
    [Fact]
    public async Task CreateAgent_Kind_CopilotAcceptedOthersRejected()
    {
        using var copilotRes = await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Pilot", "", "fake-model", "sys", Continuous: true, Kind: AgentKinds.Copilot));
        copilotRes.EnsureSuccessStatusCode();
        var copilot = (await copilotRes.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;
        Assert.Equal(AgentKinds.Copilot, copilot.Kind);
        Assert.False(copilot.Continuous);

        foreach (var kind in new[] { AgentKinds.Research, "made-up" })
        {
            using var res = await Client.PostAsJsonAsync("/api/agents",
                new AgentUpsertRequest("Impostor", "", "fake-model", "sys", Kind: kind));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    // AC2 (ADR 0140): a preset is stored by POST and PUT, and an unknown value is a 400 on both, with nothing stored.
    [Fact]
    public async Task SaveAgent_Retention_PresetStored_UnknownRejected()
    {
        using var createRes = await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Keeper", "", "fake-model", "sys", RetentionMinutes: 1440));
        createRes.EnsureSuccessStatusCode();
        var created = (await createRes.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;
        Assert.Equal(1440, created.RetentionMinutes);

        using var putRes = await Client.PutAsJsonAsync($"/api/agents/{created.Id}",
            new AgentUpsertRequest("Keeper", "", "fake-model", "sys", RetentionMinutes: 43200));
        putRes.EnsureSuccessStatusCode();
        Assert.Equal(43200, (await putRes.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent.RetentionMinutes);

        using var badPost = await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Bad", "", "fake-model", "sys", RetentionMinutes: 7));
        Assert.Equal(HttpStatusCode.BadRequest, badPost.StatusCode);

        using var badPut = await Client.PutAsJsonAsync($"/api/agents/{created.Id}",
            new AgentUpsertRequest("Keeper", "", "fake-model", "sys", RetentionMinutes: 0));
        Assert.Equal(HttpStatusCode.BadRequest, badPut.StatusCode);

        var stored = (await Client.GetFromJsonAsync<List<AgentDefinition>>("/api/agents"))!.Single(a => a.Id == created.Id);
        Assert.Equal(43200, stored.RetentionMinutes);
    }

    // PUT ignores Kind: an agent's kind is fixed at create, in both directions.
    [Fact]
    public async Task UpdateAgent_IgnoresKind()
    {
        var plain = (await (await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Plain", "", "fake-model", "sys")))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;
        var copilot = (await (await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Pilot", "", "fake-model", "sys", Kind: AgentKinds.Copilot)))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

        var updatedPlain = (await (await Client.PutAsJsonAsync($"/api/agents/{plain.Id}",
            new AgentUpsertRequest("Plain", "", "fake-model", "sys", Kind: AgentKinds.Copilot)))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;
        var updatedCopilot = (await (await Client.PutAsJsonAsync($"/api/agents/{copilot.Id}",
            new AgentUpsertRequest("Pilot", "", "fake-model", "sys", Continuous: true)))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

        Assert.Null(updatedPlain.Kind);
        Assert.Equal(AgentKinds.Copilot, updatedCopilot.Kind);
        Assert.False(updatedCopilot.Continuous);
    }

    // AC6: starting a chat with an agent id binds it; the conversation reports its agent and
    // the agent's pinned model.
    [Fact]
    public async Task ChatWithAgentId_BindsAgent_AndReportsIt()
    {
        const string convId = "api-agent-chat";
        var created = (await (await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Zaphod", "Two heads", "zaphod-model", "You are Zaphod.")))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{convId}",
            new ChatRequest("hi", null, created!.Id)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain the NDJSON stream so the turn is persisted
        }

        var bindingRes = await Client.GetAsync($"/api/conversations/{convId}/agent");
        bindingRes.EnsureSuccessStatusCode();
        Assert.Equal("application/json", bindingRes.Content.Headers.ContentType?.MediaType);
        var binding = await bindingRes.Content.ReadFromJsonAsync<AgentBinding>();
        Assert.NotNull(binding);
        Assert.Equal(created.Id, binding!.AgentId);
        Assert.Equal("You are Zaphod.", binding.SystemPrompt);

        // The model endpoint reports the agent's pinned model, not the server default.
        var model = await Client.GetFromJsonAsync<string>($"/api/conversations/{convId}/model");
        Assert.Equal("zaphod-model", model);
    }

    // The conversation index records the producing agent so the sidebar can group agent chats
    // under a collapsible header; a plain chat lists with no agent identity.
    [Fact]
    public async Task ChatWithAgentId_ListsConversationUnderAgent()
    {
        var created = (await (await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Slartibartfast", "Fjords", "fjord-model", "You design coastlines.")))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

        using (var chat = await Client.PostAsJsonAsync("/api/chat/api-agent-grouped",
            new ChatRequest("hi", null, created!.Id)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }
        using (var chat = await Client.PostAsJsonAsync("/api/chat/api-plain-grouped", new ChatRequest("hi", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        var conversations = (await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!;

        var agentChat = Assert.Single(conversations, c => c.Id == "api-agent-grouped");
        Assert.Equal(created.Id, agentChat.AgentId);
        Assert.Equal("Slartibartfast", agentChat.AgentName);

        var plain = Assert.Single(conversations, c => c.Id == "api-plain-grouped");
        Assert.Null(plain.AgentId);
    }

    // AC6 (negative): a normal chat reports no agent, as JSON null (honors the text/plain gotcha).
    [Fact]
    public async Task NormalChat_HasNoAgentBinding()
    {
        const string convId = "api-normal-chat";
        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{convId}", new ChatRequest("hi", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        var bindingRes = await Client.GetAsync($"/api/conversations/{convId}/agent");
        bindingRes.EnsureSuccessStatusCode();
        Assert.Equal("application/json", bindingRes.Content.Headers.ContentType?.MediaType);
        var binding = await bindingRes.Content.ReadFromJsonAsync<AgentBinding>();
        Assert.Null(binding);
    }

    // AC7: resuming an agent chat after its agent is deleted still uses the pinned persona and
    // does not error — the persona is snapshotted into the grain, not referenced live (ADR 0003).
    [Fact]
    public async Task ChatAfterAgentDeleted_StillUsesPinnedPersona_NoError()
    {
        const string convId = "api-agent-deleted-chat";
        var created = (await (await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Trillian", "", "trillian-model", "You are Trillian.")))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{convId}",
            new ChatRequest("hi", null, created!.Id)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        (await Client.DeleteAsync($"/api/agents/{created.Id}")).EnsureSuccessStatusCode();

        // Another turn still carrying the now-deleted agent id must succeed, not 500.
        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{convId}",
            new ChatRequest("hello again", null, created.Id)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        var binding = await (await Client.GetAsync($"/api/conversations/{convId}/agent"))
            .Content.ReadFromJsonAsync<AgentBinding>();
        Assert.NotNull(binding);
        Assert.Equal(created.Id, binding!.AgentId);
        Assert.Equal("You are Trillian.", binding.SystemPrompt);
        Assert.Equal("trillian-model", binding.Model);
    }

    // AC2: GET /api/profile is application/json and returns the empty default when never set
    // (honors the JSON-contract gotcha — a non-null object via Results.Json, never a bare string).
    //
    // The principal is named here, and written by no other test, so "never set" is a property of THIS
    // request. With no header the endpoint falls back to the shared local-dev user, whose profile the
    // neighbouring PUT tests set — which the per-test cluster used to hide.
    [Fact]
    public async Task ProfileEndpoint_ReturnsJson_WithDefaultWhenUnset()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "profile-never-set-user");

        using var response = await Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var profile = await response.Content.ReadFromJsonAsync<UserProfile>();
        Assert.NotNull(profile);
        Assert.Equal("", profile!.PreferredLanguage);
    }

    // AC3: PUT persists the language; the next GET reflects it.
    [Fact]
    public async Task ProfileEndpoint_PersistsPreferredLanguage()
    {
        var putRes = await Client.PutAsJsonAsync("/api/profile", new ProfileUpdateRequest("nl-NL"));
        putRes.EnsureSuccessStatusCode();
        Assert.Equal("application/json", putRes.Content.Headers.ContentType?.MediaType);
        var saved = await putRes.Content.ReadFromJsonAsync<UserProfile>();
        Assert.Equal("nl-NL", saved!.PreferredLanguage);

        var getBack = await Client.GetFromJsonAsync<UserProfile>("/api/profile");
        Assert.Equal("nl-NL", getBack!.PreferredLanguage);
    }

    // Theme (ADR 0005 field #2) persists through PUT and is reflected by the next GET.
    [Fact]
    public async Task ProfileEndpoint_PersistsTheme()
    {
        var putRes = await Client.PutAsJsonAsync("/api/profile", new ProfileUpdateRequest("en-US", "dark"));
        putRes.EnsureSuccessStatusCode();
        Assert.Equal("dark", (await putRes.Content.ReadFromJsonAsync<UserProfile>())!.Theme);

        var getBack = await Client.GetFromJsonAsync<UserProfile>("/api/profile");
        Assert.Equal("dark", getBack!.Theme);
    }

    // Regression: a stale client (old cached app.js) that PUTs a language-only body — the Theme
    // field entirely absent — must NOT reset a previously saved theme. The endpoint merges onto
    // the stored profile, so an omitted field keeps its stored value.
    [Fact]
    public async Task ProfileEndpoint_OmittedTheme_KeepsStoredTheme()
    {
        var seed = await Client.PutAsJsonAsync("/api/profile", new ProfileUpdateRequest("en-US", "dark"));
        seed.EnsureSuccessStatusCode();

        // Body without any Theme key, as a pre-theme client would send.
        var staleSave = await Client.PutAsJsonAsync("/api/profile", new { preferredLanguage = "fr-FR" });
        staleSave.EnsureSuccessStatusCode();

        var saved = await staleSave.Content.ReadFromJsonAsync<UserProfile>();
        Assert.Equal("fr-FR", saved!.PreferredLanguage);
        Assert.Equal("dark", saved.Theme); // kept, not silently reset to "system"
    }

    // A stale cached client that PUTs without `palette` must not reset the stored palette (ADR 0129).
    [Fact]
    public async Task ProfileEndpoint_OmittedPalette_KeepsStoredPalette()
    {
        var seed = await Client.PutAsJsonAsync("/api/profile", new ProfileUpdateRequest("en-US", "dark", Palette: "teal"));
        seed.EnsureSuccessStatusCode();

        var staleSave = await Client.PutAsJsonAsync("/api/profile", new { preferredLanguage = "fr-FR", theme = "light" });
        staleSave.EnsureSuccessStatusCode();

        var saved = await staleSave.Content.ReadFromJsonAsync<UserProfile>();
        Assert.Equal("teal", saved!.Palette);
        Assert.Equal("light", saved.Theme);
    }

    // Follow-up suggestions preference persists through PUT and is reflected by the next GET.
    [Fact]
    public async Task ProfileEndpoint_PersistsSuggestFollowUps()
    {
        var putRes = await Client.PutAsJsonAsync(
            "/api/profile", new ProfileUpdateRequest(PreferredLanguage: "en-US", SuggestFollowUps: false));
        putRes.EnsureSuccessStatusCode();
        Assert.False((await putRes.Content.ReadFromJsonAsync<UserProfile>())!.SuggestFollowUps);

        var getBack = await Client.GetFromJsonAsync<UserProfile>("/api/profile");
        Assert.False(getBack!.SuggestFollowUps);
    }

    // Regression: a body that omits suggestFollowUps must NOT reset a previously saved choice — the
    // endpoint merges onto the stored profile (absent-means-keep), like Theme/EnabledTools.
    [Fact]
    public async Task ProfileEndpoint_OmittedSuggestFollowUps_KeepsStored()
    {
        var seed = await Client.PutAsJsonAsync(
            "/api/profile", new ProfileUpdateRequest(PreferredLanguage: "en-US", SuggestFollowUps: false));
        seed.EnsureSuccessStatusCode();

        // Body without any suggestFollowUps key.
        var staleSave = await Client.PutAsJsonAsync("/api/profile", new { preferredLanguage = "fr-FR" });
        staleSave.EnsureSuccessStatusCode();

        var saved = await staleSave.Content.ReadFromJsonAsync<UserProfile>();
        Assert.Equal("fr-FR", saved!.PreferredLanguage);
        Assert.False(saved.SuggestFollowUps); // kept, not reset to the null default (ON)
    }

    // AC4: the profile is isolated per user (the Entra object id). The shared Client sends no
    // principal header (→ "local-dev"), so this builds explicit requests carrying distinct
    // X-MS-CLIENT-PRINCIPAL-ID values; TestServer has no ingress stripping X-MS-CLIENT-* headers.
    [Fact]
    public async Task ProfileEndpoint_IsScopedPerUser()
    {
        await PutProfileAsAsync("user-alice", "de-DE");

        var bobProfile = await GetProfileAsAsync("user-bob");
        Assert.Equal("", bobProfile.PreferredLanguage);

        var aliceProfile = await GetProfileAsAsync("user-alice");
        Assert.Equal("de-DE", aliceProfile.PreferredLanguage);
    }

    // AC1 + AC (Step 5): a document upload is accepted (not rejected as non-image), returns a
    // document-kind marker with NO id (so the client never sends it back as an attachmentId), and
    // its text is indexed and searchable via the process-global in-memory index the grain also sees.
    [Fact]
    public async Task Upload_Document_IsAccepted_IndexedAndNotAnAttachment()
    {
        const string convId = "doc-upload-conversation";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(TestDocuments.SampleTwoPagePdf());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(DocumentMediaTypes.Pdf);
        form.Add(fileContent, "file", "guide.pdf");

        var response = await Client.PostAsync($"/api/conversations/{convId}/attachments", form);

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        // A document marker, not an AttachmentMetadata — there is no id for the client to send back.
        Assert.DoesNotContain("\"id\"", raw);
        var result = JsonSerializer.Deserialize<DocumentUploadResult>(raw, JsonSerializerOptions.Web);
        Assert.NotNull(result);
        Assert.Equal("document", result!.Kind);
        Assert.Equal("guide.pdf", result.FileName);
        Assert.True(result.ChunkCount > 0, "the PDF should have produced at least one indexed chunk");

        // The chunk text is retrievable from the same process-global index the grain queries,
        // scoped to this conversation's owner key. The default (headerless) user id is "local-dev".
        var index = new InMemoryDocumentIndex();
        var embedder = new DeterministicEmbeddingGenerator();
        var query = (await embedder.GenerateAsync(["magrathea"]))[0].Vector;
        var hits = await index.SearchAsync($"local-dev/{convId}", query, topK: 3);
        Assert.Contains(hits, h => h.Text.Contains("magrathea", StringComparison.OrdinalIgnoreCase));
        Assert.All(hits, h => Assert.Equal("guide.pdf", h.FileName));
    }

    // Regression: Windows browsers frequently send application/octet-stream (or no type) for a
    // document, which used to be rejected as "unsupported" — the upload 400'd, the frontend silently
    // dropped it, no chip appeared and the grain never learned the document existed. The handler now
    // resolves the type from the file extension, so the same bytes ingest as a document.
    [Fact]
    public async Task Upload_Document_WithGenericContentType_IsResolvedByExtensionAndIngested()
    {
        const string convId = "doc-octet-stream-conversation";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(TestDocuments.SampleTwoPagePdf());
        // The generic type a browser sends when the OS has no MIME registration for the extension.
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "guide.pdf");

        var response = await Client.PostAsync($"/api/conversations/{convId}/attachments", form);

        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<DocumentUploadResult>(
            await response.Content.ReadAsStringAsync(), JsonSerializerOptions.Web);
        Assert.NotNull(result);
        Assert.Equal("document", result!.Kind);
        Assert.True(result.ChunkCount > 0, "the PDF should have produced at least one indexed chunk");
    }

    // AC14 (ADR 0074): two Entra principals, one dataset. Written as the profile because it is the
    // smallest user-keyed store with a round-trip endpoint; the alias is applied in ResolveUserId, above
    // every grain key, so proving it here proves it for conversations, agents and the rest.
    [Fact]
    public async Task ProfileEndpoint_IsSharedAcrossAnAliasedPrincipal()
    {
        await PutProfileAsAsync(AgentApiFixture.CanonicalPrincipalOid, "nl-NL");

        // The second account reads the first account's profile.
        var viaAlias = await GetProfileAsAsync(AgentApiFixture.AliasPrincipalOid);
        Assert.Equal("nl-NL", viaAlias.PreferredLanguage);

        // ...and writes back into the same one, rather than shadowing it.
        await PutProfileAsAsync(AgentApiFixture.AliasPrincipalOid, "en-GB");
        var viaCanonical = await GetProfileAsAsync(AgentApiFixture.CanonicalPrincipalOid);
        Assert.Equal("en-GB", viaCanonical.PreferredLanguage);
    }

    // AC15 (ADR 0074, security set): an alias grants exactly the one principal named in it. A third
    // account signing in while aliases are configured stays as isolated as it was before the feature —
    // if this ever fails, the map is widening access beyond its entries.
    [Fact]
    public async Task ProfileEndpoint_KeepsAnUnlistedPrincipalIsolated_WhenAliasesAreConfigured()
    {
        await PutProfileAsAsync(AgentApiFixture.CanonicalPrincipalOid, "sv-SE");

        var stranger = await GetProfileAsAsync("cccccccc-0000-0000-0000-00000000cccc");
        Assert.Equal("", stranger.PreferredLanguage);
    }

    private async Task PutProfileAsAsync(string principalId, string language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/profile")
        {
            Content = JsonContent.Create(new ProfileUpdateRequest(language)),
        };
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", principalId);
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<UserProfile> GetProfileAsAsync(string principalId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", principalId);
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserProfile>())!;
    }

    // AC13: the picker reads this payload, so the wire contract is the qualified id plus the provider
    // and the capability flags — in camelCase, which is what the frontend indexes by (ADR 0013).
    [Fact]
    public async Task ModelsEndpoint_ReturnsQualifiedIdsWithProvider()
    {
        var json = await Client.GetStringAsync("/api/models");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("foundry:gpt-4o", doc.RootElement.GetProperty("default").GetString());

        var models = doc.RootElement.GetProperty("models").EnumerateArray().ToList();

        var github = models.Single(m => m.GetProperty("provider").GetString() == "github");
        Assert.Equal("github:openai/gpt-4o-mini", github.GetProperty("id").GetString());
        Assert.Equal("GPT-4o mini", github.GetProperty("displayName").GetString());
        Assert.True(github.GetProperty("supportsTools").GetBoolean());
        Assert.False(github.GetProperty("supportsVision").GetBoolean());

        var foundry = models.Single(m => m.GetProperty("provider").GetString() == "foundry");
        Assert.Equal("foundry:gpt-4o", foundry.GetProperty("id").GetString());
    }

    // ————— per-agent API key + async run endpoint (ADR 0022) —————

    // AC1 (ADR 0082): a user's first list seeds exactly one research agent on their concrete default model, and the
    // second list does not add another. A unique principal, so no other test's seed or delete can interfere.
    [Fact]
    public async Task FirstList_SeedsOneResearchAgent()
    {
        var principal = $"research-seed-{Guid.NewGuid():N}";

        var first = await ListAgentsAsAsync(principal);
        var second = await ListAgentsAsAsync(principal);

        var research = Assert.Single(first, a => a.Kind == AgentKinds.Research);
        Assert.Equal(TestSiloConfigurator.DefaultModel, research.Model);
        Assert.Single(second, a => a.Kind == AgentKinds.Research);
        Assert.Equal(research.Id, second.Single(a => a.Kind == AgentKinds.Research).Id);
    }

    private async Task<List<AgentDefinition>> ListAgentsAsAsync(string principalId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", principalId);
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<AgentDefinition>>())!;
    }

    // AC2: GET /api/agents never returns a raw key (its prefix must not appear anywhere in the body).
    [Fact]
    public async Task GetAgents_NeverReturnsARawKey()
    {
        var (_, apiKey) = await CreateAgentAsync("SecretHolder");

        var listRaw = await Client.GetStringAsync("/api/agents");
        Assert.DoesNotContain(apiKey, listRaw);
        Assert.DoesNotContain("sk_agent_", listRaw);
    }

    // AC3 + AC4: a valid key → 202 with a Location + conversationId; the background run persists the
    // prompt + an assistant reply into a new conversation bound to the agent's persona.
    [Fact]
    public async Task AgentRun_ValidKey_Returns202_AndRunsInBackground()
    {
        var (agentId, apiKey) = await CreateAgentAsync();

        using var res = await AgentRunAsync(apiKey, "hello agent");
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var convId = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conversationId").GetString();
        Assert.False(string.IsNullOrEmpty(convId));
        Assert.Equal($"/api/conversations/{convId}/messages", res.Headers.Location?.ToString());

        var turns = await PollMessagesAsync(convId!, t => t.Any(x => x.Role == "assistant"));
        Assert.Contains(turns, t => t.Role == "user" && t.Text == "hello agent");
        Assert.Contains(turns, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));

        // The conversation is bound to the agent persona (ADR 0003).
        var binding = await (await Client.GetAsync($"/api/conversations/{convId}/agent"))
            .Content.ReadFromJsonAsync<AgentBinding>();
        Assert.NotNull(binding);
        Assert.Equal(agentId, binding!.AgentId);
    }

    // AC6 (ADR 0082): the agent-run bind carries the research kind, so the background turn runs the loop and lands
    // as ONE folded assistant turn — the drain path goes through the same ChatCoreAsync as the browser.
    [Fact]
    public async Task AgentRun_ResearchAgent_BindsItsKind()
    {
        var research = (await ListAgentsAsAsync("local-dev")).Single(a => a.Kind == AgentKinds.Research);
        var apiKey = (await Fixture.Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("local-dev")
            .SaveAsync(research, regenerateKey: true)).PlaintextApiKey!;

        using var res = await AgentRunAsync(apiKey, FakeChatClient.ResearchTrigger);
        var convId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetString();

        var turns = await PollMessagesAsync(convId!, t => t.Any(x => x.Role == "assistant"));
        var answer = Assert.Single(turns, t => t.Role == "assistant");
        Assert.StartsWith("[loopfeedback:1]", answer.Text, StringComparison.Ordinal);
        var binding = await (await Client.GetAsync($"/api/conversations/{convId}/agent"))
            .Content.ReadFromJsonAsync<AgentBinding>();
        Assert.Equal(AgentKinds.Research, binding!.Kind);
    }

    // AC5: missing or unknown X-Api-Key → 401 (fail closed).
    [Fact]
    public async Task AgentRun_MissingOrUnknownKey_Returns401()
    {
        using var noKey = await AgentRunAsync(apiKey: null, "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        using var badKey = await AgentRunAsync("sk_agent_not-a-real-key", "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, badKey.StatusCode);
    }

    // AC6: regenerating a key invalidates the old one — the new key runs (202), the old key → 401.
    [Fact]
    public async Task AgentRun_RegeneratedKey_Works_OldKeyRejected()
    {
        var (agentId, oldKey) = await CreateAgentAsync();

        var regenerated = (await (await Client.PutAsJsonAsync($"/api/agents/{agentId}",
            new AgentUpsertRequest("Runner", "", "gpt-4o", "You are a runner.", RegenerateKey: true)))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!;
        var newKey = regenerated.ApiKey;
        Assert.False(string.IsNullOrEmpty(newKey));
        Assert.NotEqual(oldKey, newKey);

        using var withNew = await AgentRunAsync(newKey!, "hi");
        Assert.Equal(HttpStatusCode.Accepted, withNew.StatusCode);

        using var withOld = await AgentRunAsync(oldKey, "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
    }

    // AC9: an image handed to agent-run rides the run and lands on the user turn in the new conversation.
    [Fact]
    public async Task AgentRun_WithImage_AttachesToTurn()
    {
        var (_, apiKey) = await CreateAgentAsync();

        using var res = await AgentRunAsync(apiKey, "describe this",
            (new byte[] { 1, 2, 3, 4 }, "image/png", "pic.png"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var convId = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conversationId").GetString()!;

        var turns = await PollMessagesAsync(convId, t => t.Any(x => x.Role == "assistant"));
        var userTurn = Assert.Single(turns, t => t.Role == "user");
        Assert.NotNull(userTurn.Attachments);
        Assert.Contains(userTurn.Attachments!, a => a.FileName == "pic.png");
    }

    // AC10: a supported document handed to agent-run → 202, and its text is ingested for retrieval
    // under the run's conversation owner key (the same process-global in-memory index the grain queries).
    [Fact]
    public async Task AgentRun_WithDocument_RegistersForRag()
    {
        var (_, apiKey) = await CreateAgentAsync();

        using var res = await AgentRunAsync(apiKey, "what does the doc say",
            (TestDocuments.SampleTwoPagePdf(), DocumentMediaTypes.Pdf, "guide.pdf"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var convId = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conversationId").GetString()!;

        // Ingestion runs synchronously in the request (before the 202), so the chunk is indexed now.
        var index = new InMemoryDocumentIndex();
        var query = (await new DeterministicEmbeddingGenerator().GenerateAsync(["magrathea"]))[0].Vector;
        var hits = await index.SearchAsync($"local-dev/{convId}", query, topK: 3);
        Assert.Contains(hits, h => h.FileName == "guide.pdf");
    }

    // AC3 (agent-run path): a copilot agent's kind reaches the conversation through the agent-run bind, and its turn
    // runs. A document is refused before anything is ingested, because a copilot chat has no retrieval.
    [Fact]
    public async Task AgentRun_CopilotAgent_BindsItsKind_AndRefusesDocuments()
    {
        var (_, apiKey) = await CreateAgentAsync("Pilot", AgentKinds.Copilot);

        using (var doc = await AgentRunAsync(apiKey, "what does the doc say",
            (TestDocuments.SampleTwoPagePdf(), DocumentMediaTypes.Pdf, "guide.pdf")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, doc.StatusCode);
        }

        using var res = await AgentRunAsync(apiKey, "hello");
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var convId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetString()!;
        await PollMessagesAsync(convId, t => t.Any(x => x.Role == "assistant"));
        var binding = await (await Client.GetAsync($"/api/conversations/{convId}/agent"))
            .Content.ReadFromJsonAsync<AgentBinding>();
        Assert.Equal(AgentKinds.Copilot, binding!.Kind);
    }

    // A document uploaded into a copilot chat is refused with a 400 before ingestion (no embedding call is made).
    [Fact]
    public async Task CopilotChat_DocumentUpload_IsRefused()
    {
        const string convId = "api-copilot-upload";
        var (agentId, _) = await CreateAgentAsync("Pilot", AgentKinds.Copilot);
        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{convId}", new ChatRequest("hi", null, agentId)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(TestDocuments.SampleTwoPagePdf());
        part.Headers.ContentType = new MediaTypeHeaderValue(DocumentMediaTypes.Pdf);
        form.Add(part, "file", "guide.pdf");
        using var upload = await Client.PostAsync($"/api/conversations/{convId}/attachments", form);

        Assert.Equal(HttpStatusCode.BadRequest, upload.StatusCode);
        var index = new InMemoryDocumentIndex();
        var query = (await new DeterministicEmbeddingGenerator().GenerateAsync(["magrathea"]))[0].Vector;
        Assert.Empty(await index.SearchAsync($"local-dev/{convId}", query, topK: 3));
    }

    // AC11: a batch with an unsupported (or oversize) file is rejected WHOLE (400) and no conversation
    // is created (constraint 13) — automation retries cleanly rather than getting a half-processed run.
    [Fact]
    public async Task AgentRun_BadFile_Rejects400_NoConversation()
    {
        var (_, apiKey) = await CreateAgentAsync();
        var before = (await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!.Count;

        using var res = await AgentRunAsync(apiKey, "hi",
            (new byte[] { 1, 2, 3 }, "application/octet-stream", "malware.exe"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var after = (await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!.Count;
        Assert.Equal(before, after);
    }

    // Creates an agent and returns its id + the once-shown raw API key.
    private async Task<(string Id, string ApiKey)> CreateAgentAsync(string name = "Runner", string? kind = null)
    {
        var body = (await (await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest(name, "", "gpt-4o", "You are a runner.", Kind: kind)))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!;
        return (body.Agent.Id, body.ApiKey!);
    }

    // POSTs multipart/form-data to /api/agent-run: a 'prompt' field, an optional X-Api-Key header, and
    // zero or more 'file' parts. Mirrors how external automation calls the endpoint.
    private async Task<HttpResponseMessage> AgentRunAsync(
        string? apiKey, string prompt, params (byte[] Bytes, string ContentType, string FileName)[] files)
    {
        var form = new MultipartFormDataContent { { new StringContent(prompt), "prompt" } };
        foreach (var (bytes, contentType, fileName) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, "file", fileName);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent-run") { Content = form };
        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        return await Client.SendAsync(request);
    }

    // Polls a conversation's history until <paramref name="until"/> holds or a bound elapses — the
    // agent-run turn runs in the background (OneWay), so the reply is not present the instant 202 returns.
    private async Task<List<ChatTurn>> PollMessagesAsync(string convId, Func<List<ChatTurn>, bool> until)
    {
        List<ChatTurn> turns = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            turns = (await Client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{convId}/messages"))!;
            if (until(turns))
            {
                return turns;
            }

            await Task.Delay(100);
        }

        return turns;
    }

    // ---- Web Push subscription + config endpoints (ADR 0028) -------------------------------------

    // The key the fixture registers, so GET /api/push/config is assertable against a known value.
    private const string TestVapidPublicKey = AgentApiFixture.TestVapidPublicKey;

    // With no principal header GetUserId falls back to this — the key the push grain is stored under.
    private const string LocalDevUser = "local-dev";

    // AC7: GET /api/push/config reflects the configured enabled state + public key.
    [Fact]
    public async Task PushConfig_ReflectsEnabled()
    {
        var response = await Client.GetAsync("/api/push/config");

        response.EnsureSuccessStatusCode();
        var config = await response.Content.ReadFromJsonAsync<PushConfigResponse>();
        Assert.NotNull(config);
        Assert.True(config!.Enabled);
        Assert.Equal(TestVapidPublicKey, config.PublicKey);
    }

    // AC7: POST persists a subscription (visible via the grain) and DELETE removes it.
    [Fact]
    public async Task PushSubscriptionEndpoints_Crud()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserPushGrain>(LocalDevUser);

        var post = await Client.PostAsJsonAsync(
            "/api/push/subscriptions", new PushSubscriptionRequest("https://push/dev", "p256", "auth"));
        Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);
        var stored = Assert.Single(await grain.GetAllAsync());
        Assert.Equal("https://push/dev", stored.Endpoint);

        var delete = await Client.DeleteAsync(
            "/api/push/subscriptions?endpoint=" + Uri.EscapeDataString("https://push/dev"));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await grain.GetAllAsync());
    }

    // A subscription missing a required field is rejected 400 (never persisted).
    //
    // Its own principal, not the shared local-dev fallback: PushSubscriptionEndpoints_Crud writes that
    // grain, and "never persisted" must be a property of THIS request rather than of that test having
    // already deleted what it added (ADR 0055, check 1 — one grain key per test).
    [Fact]
    public async Task PushSubscription_Post_RejectsIncomplete()
    {
        const string user = "push-reject-user";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/push/subscriptions")
        {
            Content = JsonContent.Create(new PushSubscriptionRequest("https://push/x", "", "auth")),
        };
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", user);

        using var post = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Empty(await Cluster.GrainFactory.GetGrain<IUserPushGrain>(user).GetAllAsync());
    }

    // AC8: with no VAPID private key configured the app still serves, and config reports disabled.
    [Fact]
    public async Task PushConfig_Disabled_WhenNoKey()
    {
        await using var app = await StartPushDisabledHostAsync();
        using var client = app.GetTestClient();

        var config = await client.GetFromJsonAsync<PushConfigResponse>("/api/push/config");

        Assert.NotNull(config);
        Assert.False(config!.Enabled);
        Assert.Equal("", config.PublicKey);
    }

    // AC7 (test push): the caller's stored endpoint gets the test push and the verdict comes back as JSON.
    // Its own principal, not the shared local-dev fallback that PushSubscriptionEndpoints_Crud writes.
    [Fact]
    public async Task PushTest_StoredEndpoint_ReturnsSent()
    {
        const string user = "push-test-api-user";
        const string endpoint = "https://push/api-test";
        await Cluster.GrainFactory.GetGrain<IUserPushGrain>(user)
            .AddAsync(new PushSubscription(endpoint, "p256", "auth"));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/push/test")
        {
            Content = JsonContent.Create(new PushTestRequest(endpoint)),
        };
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", user);
        using var response = await Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PushTestResponse>();
        Assert.Equal(new PushTestResponse(PushTestOutcomes.Sent), result);
        Assert.Contains(endpoint, Fixture.PushClient.Sent);
    }

    // AC8 (test push): a blank endpoint is rejected before anything is looked up or sent.
    [Fact]
    public async Task PushTest_BlankEndpoint_Returns400()
    {
        using var response = await Client.PostAsJsonAsync("/api/push/test", new PushTestRequest(" "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC9 (test push): a host with push off registers no notifier; the endpoint 409s and routing still works.
    [Fact]
    public async Task PushTest_PushDisabled_Returns409()
    {
        await using var app = await StartPushDisabledHostAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/api/push/test", new PushTestRequest("https://push/x"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await client.GetAsync("/api/push/config")).IsSuccessStatusCode);
    }

    // A separate minimal host with push DISABLED (no notifier, no client) — the shared host registers it enabled.
    private async Task<WebApplication> StartPushDisabledHostAsync()
    {
        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseTestServer();
        appBuilder.Services.AddSingleton<IGrainFactory>(Cluster.GrainFactory);
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        appBuilder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        appBuilder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        appBuilder.Services.AddSingleton<IMcpSecretProtector>(
            new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        appBuilder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
        appBuilder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        appBuilder.Services.AddDocumentRagFakes();
        appBuilder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));
        var app = appBuilder.Build();
        app.MapAgentApi();
        await app.StartAsync();
        return app;
    }

    // ---- read-only share links (ADR 0035) ----------------------------------------------------------
    //
    // NOTE: these run behind NO Easy Auth (TestServer has none), so GetUserId is local-dev on every call —
    // owner and public routes share one identity here. The excludedPaths ingress carve-out that makes the
    // public routes reachable WITHOUT a login is a deploy-time property, not unit-testable.

    // Extracts the raw token from a share URL (…/shared/#<token>).
    private static string TokenFromShareUrl(string url) => url.Split('#', 2)[1];

    // Create → the public endpoint resolves the same history; revoke → it 404s and the owner sees no link.
    [Fact]
    public async Task ShareLink_CreateResolveRevoke()
    {
        const string id = "share-lifecycle-conversation";
        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{id}", new ChatRequest("share me", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain so the turn persists
        }

        // Not shared yet.
        var before = await Client.GetFromJsonAsync<ShareLinkResponse>($"/api/conversations/{id}/share");
        Assert.Null(before!.Url);

        // Create → a URL carrying the token in the fragment.
        var created = await (await Client.PostAsync($"/api/conversations/{id}/share", null))
            .Content.ReadFromJsonAsync<ShareLinkResponse>();
        Assert.False(string.IsNullOrEmpty(created!.Url));
        Assert.Contains("/shared/#shr_", created.Url);
        var token = TokenFromShareUrl(created.Url!);

        // Idempotent — a second create returns the same stable, re-copyable link.
        var again = await (await Client.PostAsync($"/api/conversations/{id}/share", null))
            .Content.ReadFromJsonAsync<ShareLinkResponse>();
        Assert.Equal(created.Url, again!.Url);

        // The public endpoint resolves the conversation's history.
        var shared = await Client.GetFromJsonAsync<SharedConversationResponse>($"/api/share/{token}");
        Assert.NotNull(shared);
        Assert.Contains(shared!.Turns, t => t.Role == "user" && t.Text == "share me");

        // Revoke → the public endpoint 404s and the owner sees no link.
        var del = await Client.DeleteAsync($"/api/conversations/{id}/share");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterPublic = await Client.GetAsync($"/api/share/{token}");
        Assert.Equal(HttpStatusCode.NotFound, afterPublic.StatusCode);

        var afterOwner = await Client.GetFromJsonAsync<ShareLinkResponse>($"/api/conversations/{id}/share");
        Assert.Null(afterOwner!.Url);
    }

    // The conversation both tool-call share tests below drive. They share it deliberately: AC4's
    // non-disclosure claim is only worth something about a history that demonstrably HAS tool calls,
    // and AC5 is that precondition promoted to its own criterion.
    private const string ToolCallShareConversation = "share-toolcalls-conversation";

    // Drives one web_search turn through the HTTP chat route and returns the OWNER's view of it.
    private async Task<List<ChatTurn>> ChatWithAToolCallAsync(string id)
    {
        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{id}", new ChatRequest("look up the answer", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain so the turn persists
        }

        return (await Client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{id}/messages"))!;
    }

    // ADR 0080 AC5: the owner's own /messages route carries the turn's tool calls — this is what the
    // reopened chat replays its chips from.
    [Fact]
    public async Task Messages_CarryToolCalls()
    {
        var turns = await ChatWithAToolCallAsync(ToolCallShareConversation);

        Assert.NotEmpty(turns);
        // Not Single: this conversation is shared with ShareLink_DisclosesNoToolCalls, which drives a turn
        // of its own, so the count depends on execution order — the claim under test does not.
        var calls = turns.Where(t => t.ToolCalls is { Count: > 0 }).SelectMany(t => t.ToolCalls!).ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, call =>
        {
            Assert.Equal(ToolCatalog.WebSearch, call.ToolName);
            Assert.False(string.IsNullOrWhiteSpace(call.ToolInput));
            Assert.False(string.IsNullOrWhiteSpace(call.ToolResult));
        });

        // One assistant turn per tool-calling turn — the projection attaches, it never appends.
        Assert.All(turns.Where(t => t.ToolCalls is not null), t => Assert.Equal("assistant", t.Role));
    }

    // ADR 0080 AC4: a PUBLIC share link discloses no tool calls. Tool arguments and results carry search
    // queries, fetched URLs and MCP payloads; widening a public link to include them is an ADR 0013 egress
    // extension that was declined (and it makes ADR 0035's "anything it contains" no longer literal).
    //
    // The owner's view is asserted FIRST, on the same conversation, so this cannot pass vacuously: over a
    // tool-less history every Assert.Null below holds even with the strip deleted.
    [Fact]
    public async Task ShareLink_DisclosesNoToolCalls()
    {
        var owned = await ChatWithAToolCallAsync(ToolCallShareConversation);
        Assert.Contains(owned, t => t.ToolCalls is { Count: > 0 });

        var created = await (await Client.PostAsync($"/api/conversations/{ToolCallShareConversation}/share", null))
            .Content.ReadFromJsonAsync<ShareLinkResponse>();
        var shared = await Client.GetFromJsonAsync<SharedConversationResponse>(
            $"/api/share/{TokenFromShareUrl(created!.Url!)}");

        // Same turns — the strip removes a FIELD, never a turn — and not one tool call among them.
        Assert.NotNull(shared);
        Assert.Equal(owned.Count, shared!.Turns.Count);
        Assert.All(shared.Turns, t => Assert.Null(t.ToolCalls));
    }

    // An unknown/garbage token fails closed → 404 (never discloses existence), on both public routes.
    [Fact]
    public async Task ShareLink_UnknownToken_Returns404()
    {
        var view = await Client.GetAsync("/api/share/shr_not-a-real-token");
        Assert.Equal(HttpStatusCode.NotFound, view.StatusCode);

        var att = await Client.GetAsync("/api/share/shr_not-a-real-token/attachments/whatever");
        Assert.Equal(HttpStatusCode.NotFound, att.StatusCode);
    }

    // Deleting a shared conversation also revokes its link (a still-live link must not resurrect it).
    [Fact]
    public async Task ShareLink_RevokedWhenConversationDeleted()
    {
        const string id = "share-then-delete-conversation";
        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{id}", new ChatRequest("bye", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        var token = TokenFromShareUrl((await (await Client.PostAsync($"/api/conversations/{id}/share", null))
            .Content.ReadFromJsonAsync<ShareLinkResponse>())!.Url!);

        var del = await Client.DeleteAsync($"/api/conversations/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var gone = await Client.GetAsync($"/api/share/{token}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    // A shared conversation's images are readable through the token-scoped proxy — and stop resolving on revoke.
    [Fact]
    public async Task ShareLink_ServesImageThroughTokenScopedProxy()
    {
        const string id = "share-image-conversation";
        var attId = await UploadImageAsync(id, [1, 2, 3, 4], "pic.png");
        using (var chat = await Client.PostAsJsonAsync($"/api/chat/{id}",
            new ChatRequest("look", null, null, [attId])))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        var token = TokenFromShareUrl((await (await Client.PostAsync($"/api/conversations/{id}/share", null))
            .Content.ReadFromJsonAsync<ShareLinkResponse>())!.Url!);

        // The public history carries the attachment id (never a blob path).
        var shared = await Client.GetFromJsonAsync<SharedConversationResponse>($"/api/share/{token}");
        var userTurn = Assert.Single(shared!.Turns, t => t.Role == "user");
        var att = Assert.Single(userTurn.Attachments!);
        Assert.Equal("pic.png", att.FileName);

        // The token-scoped byte proxy returns the image without any auth.
        var img = await Client.GetAsync($"/api/share/{token}/attachments/{att.Id}");
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await img.Content.ReadAsByteArrayAsync());

        // After revoke, the image proxy 404s too.
        await Client.DeleteAsync($"/api/conversations/{id}/share");
        var goneImg = await Client.GetAsync($"/api/share/{token}/attachments/{att.Id}");
        Assert.Equal(HttpStatusCode.NotFound, goneImg.StatusCode);
    }

    // Uploads an image to a conversation and returns its attachment id (mirrors the browser two-phase upload).
    private async Task<string> UploadImageAsync(string convId, byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(part, "file", fileName);
        var res = await Client.PostAsync($"/api/conversations/{convId}/attachments", form);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<AttachmentMetadata>())!.Id;
    }

    // ── Plain-name inbound address (ADR 0141) ──
    private async Task<AgentDefinition> CreateSafeAgentAsync(string name)
    {
        var res = await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest(name, "", "gpt-4o", "sys", EnabledTools: new List<string> { "web_search" }));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;
    }

    private Task<HttpResponseMessage> PutAgentAsync(AgentDefinition agent, bool? plainEmail = null, bool regenerateEmail = false)
        => Client.PutAsJsonAsync($"/api/agents/{agent.Id}",
            new AgentUpsertRequest(agent.Name, "", "gpt-4o", "sys", EnabledTools: new List<string> { "web_search" },
                RegenerateEmail: regenerateEmail, PlainEmail: plainEmail));

    [Fact]
    public async Task PutAgent_PlainEmail_OptsInAndOmittingItKeepsIt()
    {
        var name = "Api Plain " + Guid.NewGuid().ToString("N")[..8];
        var agent = await CreateSafeAgentAsync(name);

        var optIn = await PutAgentAsync(agent, plainEmail: true);
        optIn.EnsureSuccessStatusCode();
        var plain = (await optIn.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;
        Assert.True(plain.EmailPlain);
        Assert.Equal(PlainEmailName.Slugify(name), plain.EmailToken);

        var kept = (await (await PutAgentAsync(agent)).Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;
        Assert.True(kept.EmailPlain);
        Assert.Equal(plain.EmailToken, kept.EmailToken);
    }

    [Fact]
    public async Task PutAgent_PlainEmailTaken_Returns409()
    {
        var name = "Api Taken " + Guid.NewGuid().ToString("N")[..8];
        (await PutAgentAsync(await CreateSafeAgentAsync(name), plainEmail: true)).EnsureSuccessStatusCode();
        var second = await CreateSafeAgentAsync(name);

        var res = await PutAgentAsync(second, plainEmail: true);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task PutAgent_RegenerateOnPlain_Returns400()
    {
        var agent = await CreateSafeAgentAsync("Api Regen " + Guid.NewGuid().ToString("N")[..8]);
        (await PutAgentAsync(agent, plainEmail: true)).EnsureSuccessStatusCode();

        var res = await PutAgentAsync(agent, regenerateEmail: true);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task PutAgent_PlainEmailWithAllTools_Returns400()
    {
        var created = await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Api Unsafe " + Guid.NewGuid().ToString("N")[..8], "", "gpt-4o", "sys"));
        var agent = (await created.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

        var res = await Client.PutAsJsonAsync($"/api/agents/{agent.Id}",
            new AgentUpsertRequest(agent.Name, "", "gpt-4o", "sys", PlainEmail: true));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
