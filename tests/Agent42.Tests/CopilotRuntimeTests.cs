using System.Runtime.CompilerServices;
using Agent42.Agents;
using Agent42.Backend;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable GHCP001 // The SDK's SessionFs and permission RPC types are [Experimental]; these tests pin them.

namespace Agent42.Tests;

/// <summary>
/// The Backend runtime host of the copilot agent kind, without a runtime process: the provider mapping (BYOK), the
/// permission policy, the client and session options that make up the phase-1 trust boundary, and the SessionFs
/// provider over the session store.
/// </summary>
public sealed class CopilotRuntimeTests
{
    private const string Key = "sk-secret-123";

    private static CopilotAgentRequest Request(IReadOnlyList<string>? skills = null, IReadOnlyList<AIFunction>? tools = null)
        => new("user-1", "conv-1", new SingleChatClientFactory(new FakeChatClient(), "fake-model"), "fake-model",
            "Pilot", "You fly.", tools ?? [AIFunctionFactory.Create(() => "ok", "get_word")], skills ?? []);

    // AC11: each provider kind maps onto the runtime's ProviderConfig the way the ordinary chat client reaches it.
    [Fact]
    public void CopilotProviderMapping_FoundryAndOpenAICompatible()
    {
        var responses = CopilotProviderSettings.Foundry(new Uri("https://agent42.services.ai.azure.com/"), Key, useResponses: true);
        var completions = CopilotProviderSettings.Foundry(new Uri("https://agent42.services.ai.azure.com"), Key, useResponses: false);
        var compatible = CopilotProviderSettings.OpenAICompatible(new Uri("https://models.github.ai/inference/"), Key);

        Assert.Equal(("openai", "https://agent42.services.ai.azure.com/openai/v1/", "responses"),
            (responses.Type, responses.BaseUrl, responses.WireApi));
        Assert.Equal("https://agent42.services.ai.azure.com/openai/v1/", completions.BaseUrl);
        Assert.Equal("completions", completions.WireApi);
        Assert.Equal(("openai", "https://models.github.ai/inference/", "completions"),
            (compatible.Type, compatible.BaseUrl, compatible.WireApi));
    }

    // AC11: the settings carry a decrypted key, and a record's generated ToString would print it into any log line.
    [Fact]
    public void CopilotProviderMapping_KeyNeverInToString()
    {
        var settings = CopilotProviderSettings.Foundry(new Uri("https://x.example"), Key, useResponses: true);

        Assert.DoesNotContain(Key, settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, $"{settings}", StringComparison.Ordinal);
    }

    // AC11: the session config carries the provider, the bare model and the persona appended to Copilot's own prompt.
    [Fact]
    public void CopilotProviderMapping_ReachesTheSessionConfig()
    {
        var settings = CopilotProviderSettings.Foundry(new Uri("https://x.example"), Key, useResponses: true);

        var config = CopilotAgentFactory.BuildSessionConfig(Request(), settings, "gpt-5-mini");

        Assert.Equal("gpt-5-mini", config.Model);
        Assert.Equal(("openai", "https://x.example/openai/v1/", Key, "responses"),
            (config.Provider!.Type, config.Provider.BaseUrl, config.Provider.ApiKey, config.Provider.WireApi));
        Assert.Equal(SystemMessageMode.Append, config.SystemMessage!.Mode);
        Assert.Equal("You fly.", config.SystemMessage.Content);
    }

    // A factory that cannot say how to reach the model's provider (any non-Backend factory) is refused cleanly.
    [Fact]
    public void CreateAgent_ProviderWithoutCopilotMapping_IsUnavailable()
    {
        var installed = Path.GetTempFileName();
        try
        {
            var factory = new CopilotAgentFactory(
                new InMemoryCopilotSessionStore(), new CopilotRuntimeOptions { RuntimePath = installed }, NullLoggerFactory.Instance);

            Assert.Throws<CopilotUnavailableException>(() => factory.CreateAgent(Request()));
        }
        finally
        {
            File.Delete(installed);
        }
    }

    // ADR 0102 AC14: an Anthropic provider has no Copilot BYOK mapping, so a copilot chat on Claude is refused with
    // ProviderUnsupported (pick another provider), not failed mid-run.
    [Fact]
    public void CopilotKind_OnAnthropicModel_IsProviderUnsupported()
    {
        var installed = Path.GetTempFileName();
        try
        {
            var claude = AnthropicProviderTests.Factory(new RecordingHttpHandler());
            Assert.Null(claude.GetCopilotProvider("claude:claude-test"));

            var factory = new CopilotAgentFactory(
                new InMemoryCopilotSessionStore(), new CopilotRuntimeOptions { RuntimePath = installed }, NullLoggerFactory.Instance);
            var request = Request() with { ChatClientFactory = claude, ModelId = "claude:claude-test" };

            var ex = Assert.Throws<CopilotUnavailableException>(() => factory.CreateAgent(request));
            Assert.Contains("can't be reached by the Copilot runtime", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(installed);
        }
    }

    // A server whose image has no runtime binary refuses copilot turns instead of failing them mid-run.
    [Fact]
    public void CreateAgent_RuntimeMissing_IsUnavailable()
    {
        var factory = new CopilotAgentFactory(
            new InMemoryCopilotSessionStore(),
            new CopilotRuntimeOptions { RuntimePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "copilot") },
            NullLoggerFactory.Instance);

        var ex = Assert.Throws<CopilotUnavailableException>(() => factory.CreateAgent(Request()));
        Assert.Contains("isn't installed", ex.Message, StringComparison.Ordinal);
    }

    // AC12: our own custom tools are approved (the runtime asks even under Mode.Empty); a shell, file or URL request
    // is refused.
    [Fact]
    public async Task CopilotPermission_ApprovesCustomToolOnly()
    {
        var custom = await CopilotAgentFactory.DecidePermission(Fake<PermissionRequestCustomTool>(), null!);
        Assert.Equal(PermissionDecision.ApproveOnce().GetType(), custom.GetType());

        foreach (var request in new PermissionRequest[]
        {
            Fake<PermissionRequestShell>(), Fake<PermissionRequestRead>(), Fake<PermissionRequestWrite>(),
            Fake<PermissionRequestUrl>(), Fake<PermissionRequestMcp>(),
        })
        {
            var decision = await CopilotAgentFactory.DecidePermission(request, null!);
            Assert.Equal(PermissionDecision.UserNotAvailable().GetType(), decision.GetType());
        }
    }

    // AC22: no built-in tools, no GitHub identity, an explicit stdio runtime, a minimal environment that carries none
    // of the app's variables, the idle backstop, and SessionFs.
    [Fact]
    public void CopilotClientOptions_MinimalEnvAndIdleTimeout()
    {
        Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "should-not-leak");
        try
        {
            var options = CopilotAgentFactory.BuildClientOptions(
                new CopilotRuntimeOptions { RuntimePath = "/app/copilot", SessionIdleTimeoutSeconds = 900 });

            Assert.Equal(CopilotClientMode.Empty, options.Mode);
            Assert.False(options.UseLoggedInUser);
            Assert.Null(options.GitHubToken);
            Assert.IsType<StdioRuntimeConnection>(options.Connection);
            Assert.Equal(900, options.SessionIdleTimeoutSeconds);
            Assert.Equal("/state", options.SessionFs!.SessionStatePath);
            Assert.NotNull(options.Environment);
            Assert.True(options.Environment!.Count <= 3);
            Assert.DoesNotContain(options.Environment, e => e.Key.StartsWith("AZURE_", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", null);
        }
    }

    // AC24 + AC23 (session half): the runtime's own skills are never included; selected folders turn skills and the
    // `skill` tool on, and no folders turn both off. Everything that would read the host's filesystem is off.
    [Fact]
    public void CopilotSkills_NoBuiltinSkills_AndSkillToolOnlyWithFolders()
    {
        var settings = CopilotProviderSettings.OpenAICompatible(new Uri("https://x.example"), Key);

        var with = CopilotAgentFactory.BuildSessionConfig(Request(["/skills/websearch"]), settings, "m");
        var without = CopilotAgentFactory.BuildSessionConfig(Request([]), settings, "m");

        Assert.Empty(with.IncludedBuiltinSkills!);
        Assert.Empty(without.IncludedBuiltinSkills!);
        Assert.True(with.EnableSkills);
        Assert.Equal(["/skills/websearch"], with.SkillDirectories!);
        Assert.Equal(["get_word", CopilotLimits.SkillToolName], with.AvailableTools!);
        Assert.False(without.EnableSkills);
        Assert.Null(without.SkillDirectories);
        Assert.Equal(["get_word"], without.AvailableTools!);
        Assert.False(with.EnableSessionStore);
        Assert.False(with.EnableConfigDiscovery);
        Assert.True(with.SkipCustomInstructions);
        Assert.False(with.LargeOutput!.Enabled);
    }

    // AC13: the runtime's files round-trip through the store as the runtime drives them, including the transcript's
    // appends and a rename, and they are found again by a NEW provider (a runtime restart).
    [Fact]
    public async Task CopilotSessionStore_RoundTrips()
    {
        var store = new InMemoryCopilotSessionStore();
        var prefix = CopilotSessionPaths.ConversationPrefix("user-1", "conv-1");
        ISessionFsHandler fs = new CopilotSessionFsProvider(store, prefix, new SemaphoreSlim(1, 1));

        await fs.MkdirAsync(new SessionFsMkdirRequest { Path = "/state/checkpoints", Recursive = true }, default);
        await fs.AppendFileAsync(new SessionFsAppendFileRequest { Path = "/state/events.jsonl", Content = "{\"a\":1}\n" }, default);
        await fs.AppendFileAsync(new SessionFsAppendFileRequest { Path = "/state/events.jsonl", Content = "{\"b\":2}\n" }, default);
        await fs.WriteFileAsync(new SessionFsWriteFileRequest { Path = "/state/workspace.yaml.tmp", Content = "id: 1" }, default);
        await fs.RenameAsync(new SessionFsRenameRequest { Src = "/state/workspace.yaml.tmp", Dest = "/state/workspace.yaml" }, default);

        ISessionFsHandler restarted = new CopilotSessionFsProvider(store, prefix, new SemaphoreSlim(1, 1));
        Assert.Equal("{\"a\":1}\n{\"b\":2}\n",
            (await restarted.ReadFileAsync(new SessionFsReadFileRequest { Path = "/state/events.jsonl" }, default)).Content);
        Assert.True((await restarted.ExistsAsync(new SessionFsExistsRequest { Path = "/state" }, default)).Exists);
        Assert.True((await restarted.StatAsync(new SessionFsStatRequest { Path = "/state" }, default)).IsDirectory);
        var entries = (await restarted.ReaddirWithTypesAsync(new SessionFsReaddirWithTypesRequest { Path = "/state" }, default)).Entries;
        Assert.Equal(["events.jsonl", "workspace.yaml"], entries.Select(e => e.Name).Order());
        Assert.False((await restarted.ExistsAsync(new SessionFsExistsRequest { Path = "/state/workspace.yaml.tmp" }, default)).Exists);
        Assert.All(await store.ListAsync(""), k => Assert.StartsWith(prefix, k, StringComparison.Ordinal));
    }

    // A missing file is a not-found to the runtime, not an exception that fails the turn.
    [Fact]
    public async Task CopilotSessionFs_MissingFile_IsReportedAsAnError()
    {
        ISessionFsHandler fs = new CopilotSessionFsProvider(
            new InMemoryCopilotSessionStore(), CopilotSessionPaths.ConversationPrefix("u", "c"), new SemaphoreSlim(1, 1));

        var result = await fs.ReadFileAsync(new SessionFsReadFileRequest { Path = "/state/none.jsonl" }, default);

        Assert.NotNull(result.Error);
    }

    // The runtime is third-party code: a path that steps out of the session is refused, never resolved, and an id
    // cannot add a level to the prefix.
    [Fact]
    public async Task CopilotSessionPaths_RefuseTraversal()
    {
        Assert.Throws<UnauthorizedAccessException>(() => CopilotSessionPaths.KeyFor("u/c/", "/state/../../other/c/x"));
        Assert.Equal("u/c/state/events.jsonl", CopilotSessionPaths.KeyFor("u/c/", "/./state//events.jsonl"));
        Assert.Equal("a%2F..%2Fb/c%2Fd/", CopilotSessionPaths.ConversationPrefix("a/../b", "c/d"));

        var store = new InMemoryCopilotSessionStore();
        ISessionFsHandler fs = new CopilotSessionFsProvider(store, "u/c/", new SemaphoreSlim(1, 1));
        var error = await fs.WriteFileAsync(new SessionFsWriteFileRequest { Path = "../../escape.txt", Content = "x" }, default);
        Assert.NotNull(error);
        Assert.Empty(await store.ListAsync(""));
    }

    // AC13 (store half): deleting a conversation removes every file under its prefix and nothing else.
    [Fact]
    public async Task DeleteConversation_RemovesOnlyThatConversation()
    {
        var store = new InMemoryCopilotSessionStore();
        await store.WriteAsync(CopilotSessionPaths.ConversationPrefix("u", "c1") + "state/events.jsonl", "x");
        await store.WriteAsync(CopilotSessionPaths.ConversationPrefix("u", "c1") + "state/workspace.yaml", "x");
        await store.WriteAsync(CopilotSessionPaths.ConversationPrefix("u", "c10") + "state/events.jsonl", "keep");
        var factory = new CopilotAgentFactory(store, new CopilotRuntimeOptions(), NullLoggerFactory.Instance);

        await factory.DeleteConversationAsync("u", "c1");

        Assert.Equal([CopilotSessionPaths.ConversationPrefix("u", "c10") + "state/events.jsonl"], await store.ListAsync(""));
    }

    // The SDK exposes no public constructor for its permission requests; the policy only looks at the type.
    private static T Fake<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
