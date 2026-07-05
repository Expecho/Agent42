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
        var schedulePrompt = AIFunctionFactory.Create(
            SchedulePromptAsync,
            name: "schedule_prompt",
            description: "Schedule a durable reminder for this conversation. When it fires, " +
                "its 'prompt' is sent to you as a user message so you can act on it. It first " +
                "fires after 'dueInMinutes' and then repeats every 'repeatEveryMinutes'. Orleans " +
                "reminders fire at most once per minute, so both values must be at least 1.");

        // Compose the base persona with the embedded skills that document each tool.
        var skills = SkillLibrary.LoadAll();
        var instructions = string.IsNullOrEmpty(skills) ? Instructions : $"{Instructions}\n\n{skills}";

        return new ChatClientAgent(
            _chatClientFactory.GetClient(deployment),
            instructions: instructions,
            name: "Agent42",
            tools: [schedulePrompt]);
    }

    public Task<string> GetSelectedModelAsync() => Task.FromResult(_deployment);

    public async IAsyncEnumerable<string> ChatAsync(string message, string? model)
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

        await foreach (AgentResponseUpdate update in _agent.RunStreamingAsync(message, _session))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        await _state.WriteStateAsync();

        _logger.LogInformation("Agent grain {GrainKey} completed a turn and persisted its session",
            this.GetPrimaryKeyString());
    }

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

    /// <summary>
    /// Agent tool: schedules an Orleans reminder for this conversation. Exposed to the
    /// model as <c>schedule_prompt</c>; see <c>Skills/reminders.md</c> for guidance.
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
