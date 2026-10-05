using System.Runtime.InteropServices;
using Agent42.Agents;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using PermissionDecision = GitHub.Copilot.Rpc.PermissionDecision;

namespace Agent42.Backend;

// GHCP001: the SDK marks its SessionFs and permission RPC types [Experimental]. The copilot agent kind accepts that
// exposure (it pins the SDK version and its ADR records the phase-1 scope); the suppression covers this file only.
#pragma warning disable GHCP001

/// <summary>Operator settings of the Copilot runtime (<c>Copilot:*</c> in configuration).</summary>
public sealed class CopilotRuntimeOptions
{
    /// <summary>
    /// The runtime binary. Default: the <c>copilot</c> executable the SDK's build targets copy to
    /// <c>runtimes/{rid}/native/</c> next to the app (measured in the image: <c>/app/runtimes/linux-x64/native/copilot</c>).
    /// Always an explicit stdio child process: the SDK's own bundled-runtime launch failed in a file-based build
    /// (<c>Copilot runtime wrapper not found</c>), and phase 2 swaps this seam for a remote sandbox anyway.
    /// </summary>
    public string? RuntimePath { get; init; }

    /// <summary>
    /// Server-wide idle timeout of a runtime session, a backstop only. Agent Framework already opens the session at
    /// the start of each run and disposes it at the end (measured), so nothing should idle this long.
    /// </summary>
    public int SessionIdleTimeoutSeconds { get; init; } = 1800;
}

/// <summary>
/// Builds a copilot-kind chat's agent on the GitHub Copilot SDK: a <see cref="GitHubCopilotAgent"/> over ONE
/// <see cref="CopilotClient"/> per silo (one runtime process, many sessions), with the user's own provider (BYOK, no
/// GitHub sign-in, measured), Agent42's tools as custom tools, the selected skills as native skill folders, and the
/// runtime's session files in <see cref="ICopilotSessionStore"/>.
///
/// <para><b>Phase-1 trust boundary.</b> The runtime is a third-party native binary inside the backend container, and
/// it can reach whatever that container can. What keeps it narrow: <c>CopilotClientMode.Empty</c>, so there is no
/// built-in tool it was not given (measured: it could not run <c>whoami</c>). The permission policy approves our
/// custom tools only (<see cref="DecidePermission"/>). The child process gets a minimal environment and none of the
/// app's (<see cref="MinimalEnvironment"/>), so it never sees the managed identity's or Cosmos's variables. And the
/// SDK's build checks the runtime's SHA256 against the release's <c>SHA256SUMS.txt</c>, with the version pinned by the
/// package version. A runtime compromise is still a backend compromise. Phase 2 moves it into a sandbox.</para>
/// </summary>
public sealed class CopilotAgentFactory : ICopilotAgentFactory, IAsyncDisposable
{
    private const string ProviderUnsupported =
        "_This copilot agent's model provider can't be reached by the Copilot runtime. Pick a model from another "
        + "provider in the agent's settings._\n\n";

    private const string RuntimeMissing = "_The Copilot runtime isn't installed on this server._\n\n";

    // Writers to one conversation's files are serialized (CopilotSessionFsProvider). Striped rather than one gate per
    // conversation, so the set never grows: two conversations sharing a stripe only wait on each other's writes.
    private readonly SemaphoreSlim[] _writeGates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    private readonly ICopilotSessionStore _store;
    private readonly CopilotRuntimeOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly Lazy<CopilotClient> _client;
    private readonly Lazy<bool> _runtimeInstalled;

    public CopilotAgentFactory(ICopilotSessionStore store, CopilotRuntimeOptions options, ILoggerFactory loggers)
    {
        _store = store;
        _options = options;
        _loggers = loggers;
        _runtimeInstalled = new(() => File.Exists(RuntimePath(options)));
        // Created on first use and started by Agent Framework on the first run (it calls StartAsync per run).
        // Creating one spawns nothing.
        _client = new(() => new CopilotClient(BuildClientOptions(options, loggers.CreateLogger("GitHub.Copilot"))));
    }

    public AIAgent CreateAgent(CopilotAgentRequest request)
    {
        if (!_runtimeInstalled.Value)
        {
            throw new CopilotUnavailableException(RuntimeMissing);
        }

        if (request.ChatClientFactory is not ICopilotProviderSource source
            || source.GetCopilotProvider(request.ModelId) is not { } provider)
        {
            throw new CopilotUnavailableException(ProviderUnsupported);
        }

        var prefix = CopilotSessionPaths.ConversationPrefix(request.UserId, request.ConversationId);
        var config = BuildSessionConfig(request, provider.Settings, provider.Model);
        config.CreateSessionFsProvider = _ => new CopilotSessionFsProvider(_store, prefix, GateFor(prefix));
        return new GitHubCopilotAgent(
            _client.Value, config, ownsClient: false, name: request.Name, loggerFactory: _loggers);
    }

    public async Task DeleteConversationAsync(string userId, string conversationId, CancellationToken cancellationToken = default)
    {
        var prefix = CopilotSessionPaths.ConversationPrefix(userId, conversationId);
        var gate = GateFor(prefix);
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var key in await _store.ListAsync(prefix, cancellationToken))
            {
                await _store.DeleteAsync(key, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsValueCreated)
        {
            await _client.Value.DisposeAsync();
        }
    }

    /// <summary>
    /// The runtime client's options: no built-in tools, no GitHub identity, the minimal environment, the idle backstop
    /// and <c>SessionFs</c>. Public so a test can pin each one.
    /// </summary>
    public static CopilotClientOptions BuildClientOptions(CopilotRuntimeOptions options, ILogger? logger = null) => new()
    {
        Mode = CopilotClientMode.Empty,
        Connection = RuntimeConnection.ForStdio(RuntimePath(options), null),
        UseLoggedInUser = false,
        Environment = MinimalEnvironment(),
        SessionIdleTimeoutSeconds = options.SessionIdleTimeoutSeconds,
        // Paths inside each session's own SessionFs; CopilotSessionPaths maps them under the conversation's prefix.
        SessionFs = new SessionFsConfig
        {
            InitialWorkingDirectory = "/work",
            SessionStatePath = "/state",
            Conventions = GitHub.Copilot.Rpc.SessionFsSetProviderConventions.Posix,
        },
        Logger = logger,
    };

    /// <summary>
    /// One session's configuration. Copilot's own system prompt stays (it drives the loop the user chose Copilot
    /// for), and the persona is appended to it. Everything that would read the host's filesystem or a shared store is
    /// off: config and instruction discovery, file hooks, git context, the cross-session store, the large-output spill
    /// to a temp file (the model has no tool to read it back), and the runtime's own bundled skills. Public so a test
    /// can pin each one. <see cref="SessionConfigBase.CreateSessionFsProvider"/> is set by the caller.
    /// </summary>
    public static SessionConfig BuildSessionConfig(
        CopilotAgentRequest request, CopilotProviderSettings settings, string model)
    {
        var skills = request.SkillDirectories.Count > 0;
        return new SessionConfig
        {
            Model = model,
            Provider = new ProviderConfig
            {
                Type = settings.Type,
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                WireApi = settings.WireApi,
            },
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = request.Instructions },
            Tools = [.. request.Tools],
            // Mode.Empty exposes exactly these. The runtime's `skill` tool is only there when a skill is selected.
            AvailableTools = [.. request.Tools.Select(t => t.Name), .. skills ? [CopilotLimits.SkillToolName] : Array.Empty<string>()],
            EnableSkills = skills,
            SkillDirectories = skills ? [.. request.SkillDirectories] : null,
            IncludedBuiltinSkills = [],
            EnableSessionStore = false,
            EnableConfigDiscovery = false,
            SkipCustomInstructions = true,
            EnableOnDemandInstructionDiscovery = false,
            EnableFileHooks = false,
            EnableHostGitOperations = false,
            LargeOutput = new LargeToolOutputConfig { Enabled = false },
            OnPermissionRequest = DecidePermission,
        };
    }

    /// <summary>
    /// The permission policy: our own custom tools run (under <c>Mode.Empty</c> the runtime still asks for them,
    /// measured, and denying means they never run). Everything else is refused as if no user were there to ask.
    /// </summary>
    public static Task<PermissionDecision> DecidePermission(PermissionRequest request, PermissionInvocation invocation)
        => Task.FromResult(request is PermissionRequestCustomTool
            ? PermissionDecision.ApproveOnce()
            : PermissionDecision.UserNotAvailable());

    /// <summary>
    /// The runtime's whole environment. With <c>Environment</c> set the child does NOT inherit the app's, so the
    /// identity, Cosmos and storage variables never reach it. Only what it needs to run is set (measured: these alone
    /// complete a turn, in the image and on Windows).
    /// </summary>
    public static IReadOnlyDictionary<string, string> MinimalEnvironment()
    {
        var temp = Path.GetTempPath();
        return OperatingSystem.IsWindows()
            ? new Dictionary<string, string>
            {
                ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                ["TEMP"] = temp,
                ["TMP"] = temp,
            }
            : new Dictionary<string, string>
            {
                ["HOME"] = Path.Combine(temp, "copilot-home"),
                ["TMPDIR"] = temp,
            };
    }

    private static string RuntimePath(CopilotRuntimeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.RuntimePath))
        {
            return options.RuntimePath;
        }

        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return Path.Combine(
            AppContext.BaseDirectory, "runtimes", $"{os}-{arch}", "native", OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
    }

    private SemaphoreSlim GateFor(string prefix)
        => _writeGates[(int)((uint)StringComparer.Ordinal.GetHashCode(prefix) % (uint)_writeGates.Length)];
}
