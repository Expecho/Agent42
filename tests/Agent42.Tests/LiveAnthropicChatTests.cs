using System.Text.Json;
using Agent42.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>Runs only when an Anthropic key is configured — xunit 2 has no runtime skip, so the attribute decides.</summary>
public sealed class LiveAnthropicFactAttribute : FactAttribute
{
    public LiveAnthropicFactAttribute()
    {
        if (string.IsNullOrEmpty(LiveAnthropicSettings.Key))
        {
            Skip = $"Put {{\"key\": \"...\"}} in tests/Agent42.Tests/{LiveAnthropicSettings.LocalFileName} (git-ignored), "
                + $"or set {LiveAnthropicSettings.KeyVariable}, to run against real Claude.";
        }
    }
}

/// <summary>
/// Where the live Anthropic test's key lives: the git-ignored <see cref="LocalFileName"/> (<c>key</c>, optional
/// <c>model</c>), or an environment variable, which wins. Deliberately NOT <c>ANTHROPIC_API_KEY</c>: the SDK reads that
/// one ambiently, which is exactly what the provider must never rely on (ADR 0102).
/// </summary>
public static class LiveAnthropicSettings
{
    public const string LocalFileName = "live-anthropic.local.json";
    public const string KeyVariable = "AGENT42_LIVE_ANTHROPIC_KEY";
    public const string ModelVariable = "AGENT42_LIVE_ANTHROPIC_MODEL";

    private static readonly Lazy<JsonElement?> LocalFile = new(ReadLocalFile);

    public static string Model => Setting(ModelVariable, "model") ?? "claude-sonnet-5-5";

    public static string Key => Setting(KeyVariable, "key") ?? "";

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
/// The real-wire twins of the stub-transport tests (ADR 0102 AC16), through the client the factory hands out:
/// <c>Provider.Anthropic</c> + the shim + <c>ChatClientFactory.Build</c>'s reasoning flag and OTel. Manual: skipped
/// without a key, and a skip is never a pass.
/// </summary>
public class LiveAnthropicChatTests
{
    private static readonly AIFunction GetTime = AIFunctionFactory.Create(() => "12:00", "get_time", "Gets the current time.");
    private static readonly AIFunction GetCity = AIFunctionFactory.Create(() => "Amsterdam", "get_city", "Gets the user's city.");

    // 1: a streamed multi-tool turn through ChatClientAgent + FICC with thinking shown. The thinking block before each
    // tool_use must go back with its signature, or the second request is a 400.
    [LiveAnthropicFact]
    public async Task LiveAnthropic_StreamedToolLoop_WithThinking()
    {
        var agent = new ChatClientAgent(Client(), new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Tools = [GetTime, GetCity] },
        });
        var reasoning = 0;
        var text = "";

        await foreach (var update in agent.RunStreamingAsync(
            "Call get_time and get_city, then tell me the time and city in one sentence."))
        {
            reasoning += update.Contents.OfType<TextReasoningContent>().Count();
            text += update.Text;
        }

        Assert.Contains("Amsterdam", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(reasoning > 0, "A reasoning-flagged Claude model should surface its (summarized) thinking.");
    }

    // 2: turn 2 replays turn 1 WITHOUT its thinking (ADR 0071 never stores it), the strip shape every later turn sends.
    [LiveAnthropicFact]
    public async Task LiveAnthropic_SecondTurn_WithoutThinking_Succeeds()
    {
        var client = Client();
        List<ChatMessage> history = [new(ChatRole.User, "Pick a number between 1 and 9. Reply with just the digit.")];
        var first = await client.GetResponseAsync(history);
        history.Add(new ChatMessage(ChatRole.Assistant, first.Text));
        history.Add(new ChatMessage(ChatRole.User, "Now double it. Reply with just the number."));

        var second = await client.GetResponseAsync(history);

        Assert.False(string.IsNullOrWhiteSpace(second.Text));
    }

    // 3: shapes current models reject on their own, which the shim rewrites.
    [LiveAnthropicFact]
    public async Task LiveAnthropic_ShimRewrites_ForcedToolAndTrailingAssistant()
    {
        var client = Client();

        var forced = await client.GetResponseAsync("What time is it?", new ChatOptions
        {
            Tools = [GetTime],
            ToolMode = ChatToolMode.RequireAny,
            Temperature = 0.2f,
        });
        Assert.NotNull(forced);

        var prefill = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Name a colour."), new ChatMessage(ChatRole.Assistant, "The colour is")]);
        Assert.False(string.IsNullOrWhiteSpace(prefill.Text));
    }

    // 4: an image by URL. A private-blob SAS URL is NOT exercised here (the test cannot mint one): post-deploy check.
    [LiveAnthropicFact]
    public async Task LiveAnthropic_ImageByUrl()
    {
        var response = await Client().GetResponseAsync(
        [
            new ChatMessage(ChatRole.User,
            [
                new TextContent("Describe this image in one short sentence."),
                new UriContent(
                    new Uri("https://upload.wikimedia.org/wikipedia/commons/4/47/PNG_transparency_demonstration_1.png"),
                    "image/png"),
            ]),
        ]);

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }

    // 5: a non-streaming request with the 32 000 default max_tokens completes (the fleet planner/judge path).
    [LiveAnthropicFact]
    public async Task LiveAnthropic_NonStreaming_MaxTokens32k()
    {
        var response = await Client().GetResponseAsync("Say hi.");

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }

    private static IChatClient Client()
    {
        var options = new ProviderOptions
        {
            Kind = ProviderKind.Anthropic,
            Endpoint = "https://api.anthropic.com",
            ApiKey = LiveAnthropicSettings.Key,
            Models =
            [
                new ConfiguredModel
                {
                    Id = LiveAnthropicSettings.Model, SupportsTools = true, SupportsVision = true, SupportsReasoning = true,
                },
            ],
        };
        var provider = MultiProviderChatClientFactory.Provider.Anthropic(options, new Uri(options.Endpoint), options.ApiKey);
        var factory = new MultiProviderChatClientFactory(
            new Dictionary<string, MultiProviderChatClientFactory.Provider> { ["claude"] = provider },
            $"claude:{LiveAnthropicSettings.Model}", "test", NullLogger<MultiProviderChatClientFactory>.Instance);
        return factory.GetClient(null);
    }
}
