using Agent42.Agents;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Agents.ObjectModel;

namespace Agent42.Backend;

// GHCP001: the SDK marks its permission RPC types [Experimental]; ADR 0097 accepts that, as 0085 did.
#pragma warning disable GHCP001

/// <summary>
/// The Copilot runtime the agent fleet's Copilot agents run on (ADR 0097): ONE lazily created client per silo, in
/// DEFAULT mode (<see cref="CopilotClientMode.CopilotCli"/>), so its agents keep Copilot's built-in shell, file and URL
/// tools. It is deliberately NOT ADR 0085's client: that one has a Blob <c>SessionFs</c> (transcripts that resume
/// across deploys), auto-approves, and follows its own kill switch (<c>Copilot:BuiltInToolsEnabled</c>, ADR 0107; off
/// it runs <c>Mode.Empty</c> at a virtual <c>/work</c>). Merging the two would give fleet agents a store-routed
/// filesystem, or tie one kill switch to the other. <b>Fleet Copilot agents therefore have NO kill switch</b>:
/// <c>Copilot:BuiltInToolsEnabled</c> governs chats only, and this client is hard-coded to <c>CopilotCli</c> mode, so with
/// sign-in open to everyone (ADR 0121) the only way to stop a fleet agent's shell is not to run the task. Fleet sessions are one-shot and never resumed, so there is no
/// <c>SessionFs</c>; each session works in its task's local workspace directory. Creating this spawns nothing.
/// </summary>
public sealed class FleetCopilotHost : IAsyncDisposable
{
    private readonly CopilotRuntimeOptions _runtime;
    private readonly Lazy<CopilotClient> _client;

    public FleetCopilotHost(CopilotRuntimeOptions runtime, FleetOptions options, ILoggerFactory loggers)
    {
        _runtime = runtime;
        Options = options;
        Loggers = loggers;
        _client = new(() => new CopilotClient(BuildClientOptions(runtime, loggers.CreateLogger("GitHub.Copilot.Fleet"))));
    }

    public FleetOptions Options { get; }
    public ILoggerFactory Loggers { get; }

    /// <summary>Whether the kill switch is on and the runtime binary is installed.</summary>
    public bool Available => Options.CopilotAgentsEnabled && File.Exists(CopilotAgentFactory.RuntimePath(_runtime));

    public CopilotClient Client => _client.Value;

    /// <summary>
    /// The runtime client's options: default mode (built-in tools), no GitHub identity, the minimal environment plus
    /// <c>PATH</c> (a shell needs it to find anything; it is not a secret), the idle backstop, and no <c>SessionFs</c>.
    /// </summary>
    public static CopilotClientOptions BuildClientOptions(CopilotRuntimeOptions runtime, ILogger? logger = null) => new()
    {
        Mode = CopilotClientMode.CopilotCli,
        Connection = RuntimeConnection.ForStdio(CopilotAgentFactory.RuntimePath(runtime), null),
        UseLoggedInUser = false,
        Environment = CopilotAgentFactory.ShellEnvironment(),
        SessionIdleTimeoutSeconds = runtime.SessionIdleTimeoutSeconds,
        Logger = logger,
    };

    public async ValueTask DisposeAsync()
    {
        if (_client.IsValueCreated)
        {
            await _client.Value.DisposeAsync();
        }
    }
}

/// <summary>
/// Creates a fleet task's Copilot agents from YAML whose model is <c>copilot</c> or <c>copilot/&lt;model id&gt;</c>, where
/// the model is one of the user's catalog ids (ADR 0097). Any other model returns null, so the aggregator falls
/// through to the chat-client factories. When Copilot agents are switched off or can't reach the model's provider,
/// it throws, and the planner gets the error as a load error.
/// </summary>
public sealed class FleetCopilotAgentFactory(
    FleetCopilotHost host,
    IChatClientFactory userFactory,
    IReadOnlyCollection<string> catalogModels,
    string defaultModel,
    FleetWorkspace workspace,
    FleetCanvas canvas,
    FleetPromptSet prompts,
    FleetCopilotPermissionPolicy policy) : PromptAgentFactory
{
    public const string Unavailable = "Copilot agents are not available, use a normal agent.";

    public override Task<AIAgent?> TryCreateAsync(GptComponentMetadata promptAgent, CancellationToken cancellationToken = default)
        => TryCreateAsync(promptAgent, null);

    /// <summary>
    /// A factory for ONE team attempt (ADR 0117): its agents get that attempt's todo tools beside the canvas tools. This
    /// factory is built once per task and can't see a per-attempt list, so the runner binds one per attempt.
    /// </summary>
    public AttemptFactory ForAttempt(FleetTodos todos) => new(this, todos);

    /// <summary>The Copilot factory of one team attempt: <see cref="FleetCopilotAgentFactory"/> plus that attempt's todo list.</summary>
    public sealed class AttemptFactory(FleetCopilotAgentFactory inner, FleetTodos todos) : PromptAgentFactory
    {
        public FleetTodos Todos { get; } = todos;

        public override Task<AIAgent?> TryCreateAsync(GptComponentMetadata promptAgent, CancellationToken cancellationToken = default)
            => inner.TryCreateAsync(promptAgent, Todos);
    }

    private Task<AIAgent?> TryCreateAsync(GptComponentMetadata promptAgent, FleetTodos? todos)
    {
        var hint = promptAgent.Model?.ModelNameHint ?? "";
        if (!IsCopilotModel(hint))
        {
            return Task.FromResult<AIAgent?>(null);
        }

        if (!host.Available)
        {
            throw new InvalidOperationException(Unavailable);
        }

        var model = ModelFor(hint);
        if (!catalogModels.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown model '{model}' for a Copilot agent, use 'copilot' or 'copilot/<one of: {string.Join(", ", catalogModels)}>'.");
        }

        if (userFactory is not ICopilotProviderSource source || source.GetCopilotProvider(model) is not { } provider)
        {
            throw new InvalidOperationException(GitHubCopilotProvider.IsGitHubModel(model)
                ? $"{Unavailable} '{model}' needs the user's GitHub account, which isn't connected."
                : $"{Unavailable} The provider of '{model}' can't be reached by the Copilot runtime.");
        }

        var config = BuildSessionConfig(provider.Settings, provider.Model, $"{promptAgent.Instructions?.ToTemplateString()}\n\n{prompts.Get("copilot-report")}", todos);
        return Task.FromResult<AIAgent?>(host.Client.AsAIAgent(config, ownsClient: false, name: promptAgent.Name, description: promptAgent.Description));
    }

    /// <summary>
    /// The catalog models the Copilot runtime can reach: the same <see cref="ICopilotProviderSource.GetCopilotProvider"/>
    /// check <see cref="TryCreateAsync"/> makes, so filtering with it cannot disagree with the factory. Pass only ids
    /// from the catalog (an id whose provider is gone resolves to the default provider). A factory that is not an
    /// <see cref="ICopilotProviderSource"/> reaches none.
    /// </summary>
    public static IReadOnlyList<ModelInfo> ReachableModels(IChatClientFactory factory, IEnumerable<ModelInfo> models)
        => factory is ICopilotProviderSource source
            ? [.. models.Where(m => source.GetCopilotProvider(m.Id) is not null)]
            : [];

    /// <summary>Whether the planner is told Copilot agents exist: the runtime is usable and some model can run on it.</summary>
    public static bool CopilotOffered(FleetCopilotHost? host, IReadOnlyCollection<ModelInfo> reachable)
        => host?.Available == true && reachable.Count > 0;

    public static bool IsCopilotModel(string modelId)
        => modelId.Equals("copilot", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("copilot/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The catalog id a <c>copilot</c> hint runs on: the part after <c>copilot/</c>, else
    /// <c>Fleet:DefaultCopilotModel</c>, else the task's model.</summary>
    public string ModelFor(string hint)
        => hint.Contains('/') ? hint[(hint.IndexOf('/') + 1)..]
            : string.IsNullOrWhiteSpace(host.Options.DefaultCopilotModel) ? defaultModel : host.Options.DefaultCopilotModel;

    /// <summary>
    /// One fleet session: the user's provider (BYOK), the agent's instructions appended to Copilot's own prompt, the
    /// task's workspace as working directory, and this task's permission policy. Host discovery (config, custom
    /// instructions, file hooks, the session store) and the large-output spill to a temp file outside the workspace are
    /// off. The canvas tools, and the attempt's todo tools when <paramref name="todos"/> is given (ADR 0117), are custom
    /// tools (ADR 0102), and <c>AvailableTools</c> stays unset on purpose: the chat
    /// kind sets it (with built-in tools off) to restrict a <c>Mode.Empty</c> session to its custom tools, and here it
    /// would hide the shell and file built-ins the fleet's default mode keeps.
    /// </summary>
    public SessionConfig BuildSessionConfig(CopilotProviderSettings settings, string model, string instructions, FleetTodos? todos = null) => new()
    {
        Model = model,
        // BYOK, or the user's own GitHub account (ADR 0108): the same rule as a copilot chat's session.
        Provider = CopilotAgentFactory.ProviderFor(settings),
        GitHubToken = CopilotAgentFactory.GitHubTokenFor(settings),
        EnableSessionTelemetry = CopilotAgentFactory.TelemetryFor(settings),
        SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = instructions },
        WorkingDirectory = workspace.Root,
        Tools = [.. canvas.Tools, .. todos?.Tools ?? []],
        IncludedBuiltinSkills = [],
        EnableSessionStore = false,
        EnableConfigDiscovery = false,
        SkipCustomInstructions = true,
        EnableOnDemandInstructionDiscovery = false,
        EnableFileHooks = false,
        LargeOutput = new LargeToolOutputConfig { Enabled = false },
        OnPermissionRequest = policy.HandleAsync,
    };
}
