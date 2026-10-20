using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>Runs only when a GitHub token is configured — xunit 2 has no runtime skip, so the attribute decides.</summary>
public sealed class LiveGitHubFactAttribute : FactAttribute
{
    public LiveGitHubFactAttribute()
    {
        if (string.IsNullOrEmpty(LiveGitHubSettings.Token))
        {
            Skip = $"Put {{\"token\": \"...\"}} in tests/Agent42.Tests/{LiveGitHubSettings.LocalFileName} (git-ignored), "
                + $"or set {LiveGitHubSettings.TokenVariable}, to run copilot agents on a real GitHub Copilot plan.";
        }
    }
}

/// <summary>
/// Where the live GitHub token lives: the git-ignored <see cref="LocalFileName"/> (<c>token</c>, optional <c>model</c>),
/// or an environment variable, which wins. Deliberately NOT <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c>: the runtime must never
/// pick a token up ambiently (ADR 0108), and those are set on CI runners.
/// </summary>
public static class LiveGitHubSettings
{
    public const string LocalFileName = "live-github.local.json";
    public const string TokenVariable = "AGENT42_LIVE_GITHUB_TOKEN";
    public const string ModelVariable = "AGENT42_LIVE_GITHUB_MODEL";

    private static readonly Lazy<JsonElement?> LocalFile = new(ReadLocalFile);

    public static string Model => Setting(ModelVariable, "model") ?? "gpt-5-mini";

    public static string Token => Setting(TokenVariable, "token") ?? "";

    private static string? Setting(string variable, string property) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnv
            ? fromEnv
            : LocalFile.Value is { } file && file.TryGetProperty(property, out var value) ? value.GetString() : null;

    private static JsonElement? ReadLocalFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, LocalFileName);
            if (File.Exists(path))
            {
                return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
            }
        }

        return null;
    }
}

/// <summary>
/// ADR 0108 AC1 against a REAL Copilot runtime and a real GitHub Copilot plan, through the production factory: the plan's
/// models are listed, a tool turn runs on the token, and after a runtime restart the session resumes from SessionFs on
/// the token alone. Opt-in: it spends the account's Copilot quota.
/// </summary>
public sealed class LiveCopilotGitHubTests
{
    [LiveGitHubFact]
    public async Task LiveCopilotGitHub_ToolTurn_ResumesFromSessionFs()
    {
        var store = new InMemoryCopilotSessionStore();
        var users = UserFactory();
        var model = ModelId.Qualify(GitHubCopilotProvider.Key, LiveGitHubSettings.Model);
        var codeWord = $"zebra-{Random.Shared.Next(1000, 9999)}";
        var request = new CopilotAgentRequest("live-user", $"live-{Guid.NewGuid():N}", users, model, "Pilot", "Be brief.",
            [AIFunctionFactory.Create(() => codeWord, "get_code_word", "Returns the secret code word.")], []);

        JsonElement saved;
        await using (var first = new CopilotAgentFactory(store, new CopilotRuntimeOptions(), NullLoggerFactory.Instance))
        {
            Assert.NotEmpty(await first.ListAsync(LiveGitHubSettings.Token, CancellationToken.None));

            var agent = first.CreateAgent(request);
            var session = await agent.CreateSessionAsync();
            var reply = await agent.RunAsync("Call get_code_word and reply with only the word.", session);
            Assert.Contains(codeWord, reply.Text, StringComparison.Ordinal);
            saved = await agent.SerializeSessionAsync(session);
        }

        // A new runtime process over the same store: only the token and the stored transcript carry the conversation.
        await using var second = new CopilotAgentFactory(store, new CopilotRuntimeOptions(), NullLoggerFactory.Instance);
        var resumed = second.CreateAgent(request);
        var again = await resumed.RunAsync(
            "What was the code word you returned earlier? Answer with only the word, don't call tools.",
            await resumed.DeserializeSessionAsync(saved));
        Assert.Contains(codeWord, again.Text, StringComparison.Ordinal);

        // The token never reached the stored transcript.
        foreach (var key in await store.ListAsync(""))
        {
            Assert.DoesNotContain(LiveGitHubSettings.Token, await store.ReadAsync(key) ?? "", StringComparison.Ordinal);
        }
    }

    private static MultiProviderChatClientFactory UserFactory()
    {
        var setup = ModelProviderBuilder.Build(new ModelProvidersOptions
        {
            Providers =
            {
                [GitHubCopilotProvider.Key] = new ProviderOptions { Kind = ProviderKind.GitHubCopilot, ApiKey = LiveGitHubSettings.Token },
            },
        }, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        return new MultiProviderChatClientFactory(setup.Providers, "", "test", NullLogger<MultiProviderChatClientFactory>.Instance);
    }
}
