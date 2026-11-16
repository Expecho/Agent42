using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>The API host with the fleet in memory, a chat model and a GitHub Copilot-only one.</summary>
public sealed class FleetSettingsApiFixture : SharedApiFixture
{
    public const string CopilotModel = "copilot:gpt";

    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog(FleetRunHarness.ModelA,
        [
            new ModelInfo(FleetRunHarness.ModelA, "fake", "Model A", "a", null, SupportsTools: true, SupportsVision: false),
            new ModelInfo(CopilotModel, "copilot", "Copilot GPT", "c", null, SupportsTools: true, SupportsVision: false, CopilotOnly: true),
        ]));
        builder.Services.AddSingleton<IUserChatClientFactoryProvider>(
            new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(new FakeChatClient(), FleetRunHarness.ModelA)));
        builder.Services.AddAgentFleetInMemory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fleet:MaxAttempts"] = "4",
        }).Build());
    }
}

/// <summary>The Advanced settings routes (ADR 0135): get with defaults and bounds, put, delete, the 400, and scoping to the caller.</summary>
public sealed class FleetSettingsApiTests(FleetSettingsApiFixture fixture) : SharedClusterTests<FleetSettingsApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private Task<HttpResponseMessage> Send(HttpMethod method, string user, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/fleet/settings");
        request.Headers.Add(IdHeader, user);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Fixture.Client.SendAsync(request);
    }

    private async Task<JsonElement> Get(string user) => await (await Send(HttpMethod.Get, user)).Content.ReadFromJsonAsync<JsonElement>();

    private static string NewUser() => $"settings-{Guid.NewGuid():N}";

    [Fact]
    public async Task Settings_Get_ReturnsSettingsDefaultsAndBounds()
    {
        var body = await Get(NewUser());

        Assert.Equal(JsonValueKind.Null, body.GetProperty("settings").GetProperty("maxAttempts").ValueKind);
        Assert.Equal(4, body.GetProperty("defaults").GetProperty("maxAttempts").GetInt32());
        var fields = body.GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(25, fields.Count);
        var attempts = fields.Single(f => f.GetProperty("key").GetString() == "maxAttempts");
        Assert.Equal((1, 6), (attempts.GetProperty("min").GetInt32(), attempts.GetProperty("max").GetInt32()));
        Assert.DoesNotContain(fields, f => f.GetProperty("key").GetString() == "maxTokensPerTaskLimit");
        Assert.Equal(FleetSettingsApiFixture.CopilotModel, body.GetProperty("copilotModels").EnumerateArray().Last().GetProperty("id").GetString());
    }

    [Fact]
    public async Task Settings_Put_Saves_AndDeleteClears()
    {
        var user = NewUser();

        var put = await Send(HttpMethod.Put, user, new { maxAttempts = 2, dreamAtUtc = "04:15", defaultCopilotModel = FleetSettingsApiFixture.CopilotModel });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await Get(user)).GetProperty("settings");
        Assert.Equal(2, saved.GetProperty("maxAttempts").GetInt32());
        Assert.Equal("04:15", saved.GetProperty("dreamAtUtc").GetString());

        // The body is the full set: a field left out is cleared.
        await Send(HttpMethod.Put, user, new { passingScore = 9 });
        var replaced = (await Get(user)).GetProperty("settings");
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("maxAttempts").ValueKind);
        Assert.Equal(9, replaced.GetProperty("passingScore").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, user)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Get(user)).GetProperty("settings").GetProperty("passingScore").ValueKind);
    }

    [Theory]
    [InlineData("{\"maxAttempts\":99}", "maxAttempts")]
    [InlineData("{\"dreamAtUtc\":\"25:00\"}", "dreamAtUtc")]
    [InlineData("{\"defaultCopilotModel\":\"nope\"}", "defaultCopilotModel")]
    public async Task Settings_Invalid_Is400_NamingTheField(string json, string field)
    {
        var user = NewUser();
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/fleet/settings") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(IdHeader, user);

        var response = await Fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal(JsonValueKind.Null, (await Get(user)).GetProperty("settings").GetProperty(field).ValueKind);
    }

    [Fact]
    public async Task Settings_UserScoped()
    {
        var alice = NewUser();
        await Send(HttpMethod.Put, alice, new { maxAttempts = 5 });

        Assert.Equal(JsonValueKind.Null, (await Get(NewUser())).GetProperty("settings").GetProperty("maxAttempts").ValueKind);
        Assert.Equal(5, (await Get(alice)).GetProperty("settings").GetProperty("maxAttempts").GetInt32());
    }
}
