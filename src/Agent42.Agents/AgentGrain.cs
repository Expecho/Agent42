using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Agent42.Agents;

/// <summary>
/// An Orleans grain wrapping a Microsoft Agent Framework <see cref="AIAgent"/>.
/// Streams replies and persists the conversation session in the "agent-state"
/// storage provider (Cosmos DB in production). Implements <see cref="IRemindable"/>
/// so the agent can schedule durable reminders via the <c>schedule_prompt</c> tool.
/// </summary>
public sealed class AgentGrain : Grain, IAgentGrain, IRemindable
{
    public const string StorageProviderName = "agent-state";

    private const string Instructions = """
        You are Agent 42, a friendly and helpful assistant.
        Answer the user's questions concisely and accurately.
        When a question may be answered by a document the user uploaded to this conversation,
        call the search_documents tool and ground your answer in the returned excerpts, citing
        the source file and page (or chunk index) you used.
        """;

    private readonly IPersistentState<AgentGrainState> _state;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IAttachmentStore _attachments;
    private readonly IDocumentIndex _documentIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly ILogger<AgentGrain> _logger;

    private AIAgent _agent = null!;
    private AgentSession _session = null!;
    private string _deployment = null!;

    public AgentGrain(
        [PersistentState("agent", StorageProviderName)] IPersistentState<AgentGrainState> state,
        IChatClientFactory chatClientFactory,
        IAttachmentStore attachments,
        IDocumentIndex documentIndex,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        ILogger<AgentGrain> logger)
    {
        _state = state;
        _chatClientFactory = chatClientFactory;
        _attachments = attachments;
        _documentIndex = documentIndex;
        _embeddings = embeddings;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _deployment = _state.State.DeploymentName ?? _chatClientFactory.DefaultDeployment;
        _agent = BuildAgent(_deployment);

        _session = _state.State.SerializedSession is { Length: > 0 } serialized
            ? await _agent.DeserializeSessionAsync(
                JsonSerializer.Deserialize<JsonElement>(serialized), cancellationToken: cancellationToken)
            : await _agent.CreateSessionAsync(cancellationToken);

        _logger.LogInformation("Agent grain {GrainKey} activated on model {Model} (existing session: {HasSession})",
            this.GetPrimaryKeyString(), _deployment, _state.State.SerializedSession is not null);

        await base.OnActivateAsync(cancellationToken);
    }

    // Builds the Agent Framework agent bound to a specific model deployment. Called on
    // activation and again whenever the conversation switches models.
    private AIAgent BuildAgent(string deployment)
    {
        // The agent's tools. Each is bound to this grain instance, so a tool can reach
        // Orleans (grain factory, reminders, state) directly through `this`.
        var getDateTime = AIFunctionFactory.Create(
            GetDate,
            name: "get_date",
            description: "Get the current date and time. Returns a string in the local timezone.");

        var schedulePrompt = AIFunctionFactory.Create(
            SchedulePromptAsync,
            name: "schedule_prompt",
            description: "Schedule a durable reminder for this conversation. When it fires, " +
                "its 'prompt' is sent to you as a user message so you can act on it. It first " +
                "fires after 'dueInMinutes' and then repeats every 'repeatEveryMinutes'. Orleans " +
                "reminders fire at most once per minute, so both values must be at least 1.");

        var cancelScheduledPrompt = AIFunctionFactory.Create(
            CancelScheduledPromptAsync,
            name: "cancel_scheduled_prompt",
            description: "Cancel a single scheduled reminder for this conversation by its " +
                "'name' — the same short identifier used when it was scheduled. Use this when " +
                "the user wants to stop one specific reminder.");

        var cancelAllScheduledPrompts = AIFunctionFactory.Create(
            CancelAllScheduledPromptsAsync,
            name: "cancel_all_scheduled_prompts",
            description: "Cancel every scheduled reminder for this conversation at once. Use " +
                "this only when the user wants to clear all of their reminders, not a single one.");

        var searchDocuments = AIFunctionFactory.Create(
            SearchDocumentsAsync,
            name: "search_documents",
            description: "Search the documents the user uploaded to THIS conversation for passages " +
                "relevant to a question. Returns matching excerpts, each with its source file name " +
                "and page number (or chunk index when the format has no pages). Use it whenever a " +
                "question might be answered by an uploaded document, then cite the file and page.");

        // Agent Skills: markdown skills discovered on disk from Skills/<name>/SKILL.md.
        // The provider advertises each skill's name + description in the system prompt and
        // exposes a `load_skill` tool so the agent pulls the full guidance only when a task
        // matches (progressive disclosure). This grain has no human-in-the-loop approval
        // step, so load_skill is auto-approved; the skills ship no scripts or resources.
        var skillsProvider = new AgentSkillsProvider(
            Path.Combine(AppContext.BaseDirectory, "Skills"),
            options: new AgentSkillsProviderOptions { DisableLoadSkillApproval = true });

        // Persona: an agent chat drives Name/Instructions from the snapshot pinned in
        // state at bind time; a normal chat falls back to the default Agent 42 persona.
        // Old Cosmos state has null persona fields, so existing chats fall back too.
        return new ChatClientAgent(
            _chatClientFactory.GetClient(deployment),
            new ChatClientAgentOptions
            {
                Name = _state.State.AgentName ?? "Agent42",
                ChatOptions = new ChatOptions
                {
                    Instructions = _state.State.SystemPrompt ?? Instructions,
                    Tools = [schedulePrompt, cancelScheduledPrompt, cancelAllScheduledPrompts, getDateTime, searchDocuments]
                },
                AIContextProviders = [skillsProvider],
            });
    }

    public Task<string> GetSelectedModelAsync() => Task.FromResult(_deployment);

    public async Task BindAgentAsync(AgentBinding binding)
    {
        // Immutable once bound: a conversation keeps the first persona it was given, so a
        // repeat bind (e.g. a retried first turn) can never flip it mid-conversation.
        if (!string.IsNullOrEmpty(_state.State.AgentId))
        {
            return;
        }

        _state.State.AgentId = binding.AgentId;
        _state.State.AgentName = binding.Name;
        _state.State.SystemPrompt = binding.SystemPrompt;
        _state.State.DeploymentName = binding.Model;

        // Rebuild the agent so the pinned persona + model take effect, mirroring the
        // model-switch path in ChatAsync: snapshot the session, rebuild, restore. Binding
        // runs on a brand-new empty session, but copying that shape keeps the rebuild path
        // uniform and avoids relying on an empty session being portable across instances.
        var snapshot = await _agent.SerializeSessionAsync(_session);
        _deployment = binding.Model;
        _agent = BuildAgent(binding.Model);
        _session = await _agent.DeserializeSessionAsync(snapshot);

        await _state.WriteStateAsync();

        _logger.LogInformation("Agent grain {GrainKey} bound to agent {AgentId} ({AgentName}) on model {Model}",
            this.GetPrimaryKeyString(), binding.AgentId, binding.Name, binding.Model);
    }

    public Task<AgentBinding?> GetAgentBindingAsync()
        => Task.FromResult(_state.State.AgentId is { Length: > 0 } id
            ? new AgentBinding(id, _state.State.AgentName ?? "", _state.State.SystemPrompt ?? "", _deployment)
            : null);

    public async IAsyncEnumerable<AgentStreamEvent> ChatAsync(
        string message, string? model, IReadOnlyList<AttachmentRef>? attachments = null)
    {
        _logger.LogInformation("Agent grain {GrainKey} received a message ({Length} chars, {Attachments} attachment(s))",
            this.GetPrimaryKeyString(), message.Length, attachments?.Count ?? 0);

        if (!string.IsNullOrWhiteSpace(model) && model != _deployment)
        {
            // Rehost the existing conversation on the newly picked model: snapshot the
            // session, rebuild the agent against the new client, then restore the session
            // so history carries over.
            var snapshot = await _agent.SerializeSessionAsync(_session);
            _deployment = model;
            _state.State.DeploymentName = model;
            _agent = BuildAgent(model);
            _session = await _agent.DeserializeSessionAsync(snapshot);

            _logger.LogInformation("Agent grain {GrainKey} switched to model {Model}",
                this.GetPrimaryKeyString(), model);
        }

        // Refresh the image URLs on PRIOR turns before this model call. Azure OpenAI re-fetches
        // a prior turn's image URL on every subsequent turn (Step 1 spike), and a blob SAS is
        // short-lived — but a warm grain serves many turns without reactivating, so this must run
        // per turn, not only on activation, or a long-lived conversation 400s once a SAS expires.
        await RehydrateAttachmentUrlsAsync();

        // Build the user turn. Images ride alongside the text as UriContent parts pointing at
        // a store-provided URL (a blob SAS in prod, a placeholder offline) — never raw bytes,
        // so the serialized session stays small (ADR 0004). Record this turn's refs so history
        // can re-render them.
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(message))
        {
            contents.Add(new TextContent(message));
        }

        var turnRefs = new List<AttachmentRef>();
        if (attachments is { Count: > 0 })
        {
            foreach (var attachment in attachments)
            {
                var url = await _attachments.GetReadUrlAsync(attachment);
                contents.Add(new UriContent(url, attachment.MediaType));
                turnRefs.Add(attachment);
            }
        }

        // Never send a message with no content parts (an empty text-only turn keeps a TextContent).
        if (contents.Count == 0)
        {
            contents.Add(new TextContent(string.Empty));
        }

        // Keep TurnAttachments 1:1 with the user turns already in history before appending this
        // turn's entry, so the index join holds even for conversations created before this list
        // existed (old Cosmos state deserializes it empty).
        AlignTurnAttachmentsToHistory();
        _state.State.TurnAttachments.Add(turnRefs);

        var userMessage = new ChatMessage(ChatRole.User, contents);

        // Token usage arrives as UsageContent updates, one per LLM turn (a tool-calling
        // run has several); accumulate them and report the totals on the Done event.
        long inputTokens = 0, outputTokens = 0, cachedTokens = 0;

        await foreach (AgentResponseUpdate update in _agent.RunStreamingAsync([userMessage], _session))
        {
            foreach (AIContent content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        yield return new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: text.Text);
                        break;

                    case FunctionCallContent call:
                        yield return new AgentStreamEvent(AgentStreamEventType.ToolCallStart,
                            ToolName: call.Name, ToolCallId: call.CallId, ToolInput: SerializeOrNull(call.Arguments));
                        break;

                    case FunctionResultContent result:
                        yield return new AgentStreamEvent(AgentStreamEventType.ToolCallEnd,
                            ToolCallId: result.CallId, ToolResult: SerializeOrNull(result.Result));
                        break;

                    case UsageContent usage:
                        inputTokens += usage.Details.InputTokenCount.GetValueOrDefault();
                        outputTokens += usage.Details.OutputTokenCount.GetValueOrDefault();
                        cachedTokens += usage.Details.CachedInputTokenCount.GetValueOrDefault();
                        break;
                }
            }
        }

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        await _state.WriteStateAsync();

        _logger.LogInformation("Agent grain {GrainKey} completed a turn and persisted its session",
            this.GetPrimaryKeyString());

        yield return new AgentStreamEvent(AgentStreamEventType.Done,
            InputTokens: inputTokens > 0 ? inputTokens : null,
            OutputTokens: outputTokens > 0 ? outputTokens : null,
            CachedTokens: cachedTokens > 0 ? cachedTokens : null);
    }

    private static string? SerializeOrNull(object? value) =>
        value is not null ? JsonSerializer.Serialize(value) : null;

    public Task<IReadOnlyList<ChatTurn>> GetHistoryAsync()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return Task.FromResult<IReadOnlyList<ChatTurn>>([]);
        }

        var turns = new List<ChatTurn>();
        var userTurnIndex = 0;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                // Join this user turn to its attachments by index (one TurnAttachments entry per
                // user turn, in order — see ChatAsync/ReceiveReminder).
                var refs = userTurnIndex < _state.State.TurnAttachments.Count
                    ? _state.State.TurnAttachments[userTurnIndex]
                    : null;
                userTurnIndex++;

                var hasAttachments = refs is { Count: > 0 };
                // Retain an image-only turn (blank text but an image attached) — the old filter
                // dropped any blank-text message, which would hide an image-only prompt.
                if (string.IsNullOrWhiteSpace(message.Text) && !hasAttachments)
                {
                    continue;
                }

                turns.Add(new ChatTurn(
                    message.Role.Value, message.Text ?? "", hasAttachments ? refs : null));
            }
            else if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
            {
                turns.Add(new ChatTurn(message.Role.Value, message.Text));
            }
        }

        return Task.FromResult<IReadOnlyList<ChatTurn>>(turns);
    }

    public Task<IReadOnlyList<ReminderInfo>> GetScheduledPromptsAsync()
        => Task.FromResult<IReadOnlyList<ReminderInfo>>(
            [.. _state.State.ScheduledPrompts.Values.OrderBy(r => r.CreatedAt)]);

    public async Task PurgeAsync()
    {
        // Reminders are durable and keep firing on their own schedule, so they must be
        // unregistered explicitly — clearing state alone would leave them ticking.
        foreach (var reminder in await this.GetReminders())
        {
            await this.UnregisterReminder(reminder);
        }

        // Remove the conversation's indexed document chunks too, so deleting a conversation doesn't
        // leave its extracted document text behind (privacy — ADR 0007). The index is owner-scoped
        // by the same grain key.
        await _documentIndex.DeleteAsync(this.GetPrimaryKeyString());

        _state.State.ScheduledPrompts.Clear();
        _state.State.SerializedSession = null;
        _state.State.DeploymentName = null;
        await _state.ClearStateAsync();

        _logger.LogInformation("Agent grain {GrainKey} purged", this.GetPrimaryKeyString());

        // Start the next activation from clean state rather than the stale in-memory session.
        this.DeactivateOnIdle();
    }

    private static DateTime GetDate() => DateTime.Now;

    /// <summary>
    /// Agent tool: retrieves the passages most relevant to <paramref name="query"/> from the
    /// documents uploaded to THIS conversation (ADR 0007). Embeds the query with the same generator
    /// used at ingestion, then asks the owner-scoped <see cref="IDocumentIndex"/> for the top matches.
    /// Scoped by the grain key (<c>{userId}/{conversationId}</c>), so it can only ever see this
    /// conversation's documents. Returns each excerpt with its citation metadata (file, page/chunk).
    /// </summary>
    private async Task<IReadOnlyList<DocumentChunk>> SearchDocumentsAsync(
        [Description("The question or keywords to search the uploaded documents for.")] string query,
        [Description("Maximum number of excerpts to return. Defaults to 5.")] int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var generated = await _embeddings.GenerateAsync([query]);
        var results = await _documentIndex.SearchAsync(
            this.GetPrimaryKeyString(), generated[0].Vector, maxResults > 0 ? maxResults : 5);

        _logger.LogInformation(
            "Agent grain {GrainKey} searched documents for '{Query}' → {Count} excerpt(s)",
            this.GetPrimaryKeyString(), query, results.Count);

        return results;
    }

    /// <summary>
    /// Agent tool: schedules an Orleans reminder for this conversation. Exposed to the
    /// model as <c>schedule_prompt</c>; see <c>Skills/scheduledprompt/SKILL.md</c> for guidance.
    /// </summary>
    private async Task<string> SchedulePromptAsync(
        [Description("A short, stable identifier for the reminder, e.g. 'stand-up'. Reusing a name updates the existing reminder.")] string name,
        [Description("The prompt that is sent to the agent when the reminder fires, e.g. 'Remind the user to stand up and stretch.'")] string prompt,
        [Description("Minutes from now until the reminder first fires. Must be at least 1.")] double dueInMinutes,
        [Description("Minutes between repeats after the first firing. Must be at least 1.")] double repeatEveryMinutes)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "The reminder needs a short name.";
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "The reminder needs a prompt to send to the agent when it fires.";
        }

        if (dueInMinutes < 1 || repeatEveryMinutes < 1)
        {
            return "Orleans reminders fire at most once per minute, so both the initial " +
                "delay and the repeat interval must be at least 1 minute.";
        }

        await this.RegisterOrUpdateReminder(
            name,
            TimeSpan.FromMinutes(dueInMinutes),
            TimeSpan.FromMinutes(repeatEveryMinutes));

        _state.State.ScheduledPrompts[name] = new ReminderInfo(
            name, prompt, dueInMinutes, repeatEveryMinutes, DateTimeOffset.UtcNow, TimesFired: 0);
        await _state.WriteStateAsync();

        _logger.LogInformation(
            "Agent grain {GrainKey} scheduled reminder '{Reminder}' (first in {Due} min, every {Period} min)",
            this.GetPrimaryKeyString(), name, dueInMinutes, repeatEveryMinutes);

        return $"Reminder '{name}' scheduled: first firing in {dueInMinutes} minute(s), " +
            $"repeating every {repeatEveryMinutes} minute(s).";
    }

    /// <summary>
    /// Agent tool: cancels a single scheduled reminder by name. Exposed to the model as
    /// <c>cancel_scheduled_prompt</c>; see <c>Skills/scheduledprompt/SKILL.md</c> for guidance.
    /// </summary>
    private async Task<string> CancelScheduledPromptAsync(
        [Description("The name of the reminder to cancel — the same short identifier used when it was scheduled.")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Tell me which reminder to cancel by its name.";
        }

        if (!_state.State.ScheduledPrompts.ContainsKey(name))
        {
            return $"There is no scheduled reminder named '{name}' in this conversation.";
        }

        // The durable reminder keeps ticking unless it is explicitly unregistered —
        // clearing the state entry alone would leave it firing (see PurgeAsync).
        var reminder = await this.GetReminder(name);
        if (reminder is not null)
        {
            await this.UnregisterReminder(reminder);
        }

        _state.State.ScheduledPrompts.Remove(name);
        await _state.WriteStateAsync();

        _logger.LogInformation(
            "Agent grain {GrainKey} cancelled reminder '{Reminder}'",
            this.GetPrimaryKeyString(), name);

        return $"Reminder '{name}' cancelled.";
    }

    /// <summary>
    /// Agent tool: cancels every scheduled reminder for this conversation. Exposed to the
    /// model as <c>cancel_all_scheduled_prompts</c>; see <c>Skills/scheduledprompt/SKILL.md</c>.
    /// </summary>
    private async Task<string> CancelAllScheduledPromptsAsync()
    {
        if (_state.State.ScheduledPrompts.Count == 0)
        {
            return "There are no scheduled reminders to cancel in this conversation.";
        }

        var count = _state.State.ScheduledPrompts.Count;

        // Durable reminders must each be unregistered explicitly, or they keep firing
        // after the state is cleared (same reason PurgeAsync iterates them).
        foreach (var reminder in await this.GetReminders())
        {
            await this.UnregisterReminder(reminder);
        }

        _state.State.ScheduledPrompts.Clear();
        await _state.WriteStateAsync();

        _logger.LogInformation(
            "Agent grain {GrainKey} cancelled all {Count} scheduled reminder(s)",
            this.GetPrimaryKeyString(), count);

        return $"Cancelled {count} scheduled reminder(s).";
    }

    /// <summary>
    /// Called by the Orleans runtime when a scheduled reminder ticks. The reminder's
    /// prompt is sent to the agent as a user message; the resulting turn is persisted
    /// in the session, so the user sees both the prompt and the agent's response the
    /// next time they open the conversation.
    /// </summary>
    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!_state.State.ScheduledPrompts.TryGetValue(reminderName, out var info))
        {
            _logger.LogWarning(
                "Reminder '{Reminder}' fired for conversation {GrainKey} but no metadata was found.",
                reminderName, this.GetPrimaryKeyString());
            return;
        }

        // Refresh prior turns' image URLs before this model call too (a reminder can fire on a
        // long-warm grain whose SAS URLs have since expired) — same reason as ChatAsync.
        await RehydrateAttachmentUrlsAsync();

        // A reminder-fired turn is a user turn too, so it must push a (always empty) attachment
        // entry to keep TurnAttachments aligned 1:1 with user turns by index (constraint 11).
        AlignTurnAttachmentsToHistory();
        await _agent.RunAsync([new ChatMessage(ChatRole.User, info.Prompt)], _session);
        _state.State.TurnAttachments.Add([]);

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);

        _state.State.ScheduledPrompts[reminderName] = info with { TimesFired = info.TimesFired + 1 };

        await _state.WriteStateAsync();
    }

    // Pads TurnAttachments with empty entries so it has one entry per user turn already in the
    // session before the caller appends this turn's entry. This keeps the index join in
    // GetHistoryAsync correct even for conversations that predate TurnAttachments (old Cosmos
    // state deserializes it empty) — the first new turn back-fills the gap.
    private void AlignTurnAttachmentsToHistory()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return;
        }

        var priorUserTurns = messages.Count(m => m.Role == ChatRole.User);
        while (_state.State.TurnAttachments.Count < priorUserTurns)
        {
            _state.State.TurnAttachments.Add([]);
        }
    }

    // Refreshes the image URLs on the session's prior turns so the next model call always sends
    // currently-valid URLs. Azure OpenAI re-fetches a prior turn's image URL on every later turn
    // (Step 1 spike), and a blob SAS is short-lived, so stale URLs would 400. Called before every
    // model call (not just on activation), because a warm grain serves many turns per activation.
    // Each user turn's UriContent parts are matched, in order, to that turn's stored attachment
    // refs. Prod re-mints a fresh SAS; the in-memory store returns its stable placeholder (a no-op).
    private async Task RehydrateAttachmentUrlsAsync()
    {
        if (_state.State.TurnAttachments.Count == 0
            || !_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return;
        }

        var userTurnIndex = 0;
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.User)
            {
                continue;
            }

            var refs = userTurnIndex < _state.State.TurnAttachments.Count
                ? _state.State.TurnAttachments[userTurnIndex]
                : null;
            userTurnIndex++;

            if (refs is not { Count: > 0 })
            {
                continue;
            }

            var uriParts = message.Contents.OfType<UriContent>().ToList();
            for (var i = 0; i < uriParts.Count && i < refs.Count; i++)
            {
                uriParts[i].Uri = await _attachments.GetReadUrlAsync(refs[i]);
            }
        }
    }
}
