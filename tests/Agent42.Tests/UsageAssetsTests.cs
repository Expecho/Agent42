namespace Agent42.Tests;

/// <summary>
/// Guards the token-usage dashboard wiring (ADR 0031) — a dedicated full-page overlay, not a
/// Preferences tab. Serves the real Backend <c>wwwroot</c> through the shared
/// <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same
/// source-text-heuristic shape as <see cref="LocationAssetsTests"/>/<see cref="ConfigHealthAssetsTests"/>
/// (the repo has no JS runner by design, so these prove the code <i>says</i> the right thing, not that a
/// browser <i>does</i> it).
/// </summary>
[Collection("StaticAssets")]
public sealed class UsageAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public UsageAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // AC10 (ADR 0104): the dashboard no longer claims cached and reasoning always read 0 — the emitter does tag them
    // (UsageProcessorTests.ChatSpan_CarriesCachedAndReasoningTokens).
    [Fact]
    public async Task UsageNote_DoesNotClaimCachedAlwaysZero()
    {
        var index = await _client.GetStringAsync("/");

        Assert.DoesNotContain("emits only input/output", index, StringComparison.Ordinal);
        Assert.Contains("Cached input and reasoning are what each provider reports", index, StringComparison.Ordinal);
    }

    // AC11 (ADR 0104): the per-turn usage line reads the Done event's cacheWriteTokens, and send.js hands it the chunk.
    [Fact]
    public async Task ShowUsage_RendersCacheWrites()
    {
        var thread = await _client.GetStringAsync("/js/thread.js");
        // The event dispatcher moved out of send() into chatstream.js (ADR 0153).
        var send = await _client.GetStringAsync("/js/chatstream.js");

        Assert.Contains(
            "export function showUsage(msg, { inputTokens, outputTokens, cachedTokens, cacheWriteTokens, costUsd, costStatus })",
            thread, StringComparison.Ordinal);
        Assert.Contains(
            "if (cacheWriteTokens != null) parts.push(`${cacheWriteTokens.toLocaleString()} cache write`);",
            thread, StringComparison.Ordinal);
        Assert.Contains("showUsage(agentMsg, chunk)", send, StringComparison.Ordinal);
    }

}
