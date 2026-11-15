using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> with a Hindsight client registered — the "operator set
/// <c>Hindsight:ApiKey</c>" state. A subclass rather than a shared registration for the reason
/// <see cref="SharedApiFixture"/> documents: only this class asserts on it.
/// </summary>
public sealed class MemoryConfiguredApiFixture : SharedApiFixture
{
    protected override void Configure(WebApplicationBuilder builder)
        => builder.Services.AddSingleton(new HindsightIntegration(new RecordingHindsightClient()));
}

/// <summary>
/// ADR 0131: <c>GET /api/tools</c> offers the <c>memory</c> row only when the operator configured Hindsight.
/// The unconfigured half lives in <see cref="ToolSelectionApiTests"/>.
/// </summary>
public sealed class MemoryToolCatalogApiTests(MemoryConfiguredApiFixture fixture)
    : SharedClusterTests<MemoryConfiguredApiFixture>(fixture)
{
    // The label is what the user reads: plain "Memory" would read as the conversation's own history (the
    // ADR 0004 session), which this is not (ADR 0057 AC11).
    [Fact]
    public async Task Tools_Catalog_IncludesMemory_WhenHindsightConfigured()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/tools");
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "user-mem-on");
        var response = await Fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var memory = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "memory");

        Assert.Equal("Long-term memory", memory.GetProperty("label").GetString());
        Assert.False(memory.GetProperty("requiresHostedTools").GetBoolean());
    }
}
