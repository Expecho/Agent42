using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// The Anthropic SDK falls back to the <c>ANTHROPIC_API_KEY</c> / <c>ANTHROPIC_AUTH_TOKEN</c> /
/// <c>ANTHROPIC_BASE_URL</c> environment variables for anything not set explicitly (ADR 0103). A per-user provider
/// must use only the user's stored key and endpoint, never an ambient one (no operator model, ADR 0019/0030).
/// Process environment is global, so these run serialized in their own collection.
/// </summary>
[Collection("AnthropicEnvironment")]
public class AnthropicEnvironmentTests
{
    [Fact] // AC7
    public async Task AnthropicRequest_UsesStoredKey_NotEnvironment()
    {
        string[] names = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "env-key");
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "env-token");
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://env.invalid");

            var handler = AnthropicStub.Sequence(() => AnthropicStub.Sse(AnthropicStub.StreamedText));
            await foreach (var unused in AnthropicProviderTests.Factory(handler).GetClient("claude:claude-test")
                .GetStreamingResponseAsync("hi"))
            {
                // Drained for the request it produces.
            }

            var call = Assert.Single(handler.Recorded);
            Assert.Equal("stored-key", call.Headers!["x-api-key"]);
            Assert.Null(call.Authorization);
            Assert.StartsWith("https://anthropic.stub/", call.Url, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

[CollectionDefinition("AnthropicEnvironment", DisableParallelization = true)]
public sealed class AnthropicEnvironmentCollection;
