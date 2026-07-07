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
        """;

    private readonly IPersistentState<AgentGrainState> _state;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<AgentGrain> _logger;

    private AIAgent _agent = null!;
    private AgentSession _session = null!;
    private string _deployment = null!;

    public AgentGrain(
        [PersistentState("agent", StorageProviderName)] IPersistentState<AgentGrainState> state,
        IChatClientFactory chatClientFactory,
        ILogger<AgentGrain> logger)
    {
        _state = state;
        _chatClientFactory = chatClientFactory;
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
                    Tools = [schedulePrompt, cancelScheduledPrompt, cancelAllScheduledPrompts, getDateTime]
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

    public async IAsyncEnumerable<AgentStreamEvent> ChatAsync(string message, string? model)
    {
        _logger.LogInformation("Agent grain {GrainKey} received a message ({Length} chars)",
            this.GetPrimaryKeyString(), message.Length);

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

        // Token usage arrives as UsageContent updates, one per LLM turn (a tool-calling
        // run has several); accumulate them and report the totals on the Done event.
        long inputTokens = 0, outputTokens = 0, cachedTokens = 0;

        await foreach (AgentResponseUpdate update in _agent.RunStreamingAsync(message, _session))
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

        var turns = messages
            .Where(m => (m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
                && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new ChatTurn(m.Role.Value, m.Text))
            .ToList();

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

        await _agent.RunAsync([new ChatMessage(ChatRole.User, info.Prompt)], _session);
        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);

        _state.State.ScheduledPrompts[reminderName] = info with { TimesFired = info.TimesFired + 1 };

        await _state.WriteStateAsync();
    }
}
