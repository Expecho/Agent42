using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>The API host with a catalog and a switchable Auto router port (ADR 0137).</summary>
public sealed class ModelRouterApiFixture : SharedApiFixture
{
    public FakeModelRouterProvider Routers { get; } = new();

    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IUserModelCatalogProvider>(new SingleUserModelCatalogProvider(new StaticModelCatalog("fake:a",
        [
            new ModelInfo("fake:a", "fake", "A", "a", null, SupportsTools: true, SupportsVision: false),
        ])));
        builder.Services.AddSingleton<IUserModelRouterProvider>(Routers);
    }
}

/// <summary>/api/models says whether the signed-in user gets an Auto entry (ADR 0137); the picker offers it from that flag alone.</summary>
public sealed class ModelRouterApiTests(ModelRouterApiFixture fixture) : SharedClusterTests<ModelRouterApiFixture>(fixture)
{
    private async Task<JsonElement> ModelsAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/models");
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "router-api-user");
        var response = await Fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    protected override Task OnDisposeAsync()
    {
        Fixture.Routers.Reset();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Models_NoRouter_AutoIsFalse_AndTheCatalogIsUntouched()
    {
        Fixture.Routers.Router = null;

        var body = await ModelsAsync();

        Assert.False(body.GetProperty("auto").GetBoolean());
        Assert.Equal("fake:a", Assert.Single(body.GetProperty("models").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Models_WithARouter_AutoIsTrue_WithoutAddingAModel()
    {
        Fixture.Routers.Router = new FakeModelRouter();

        var body = await ModelsAsync();

        Assert.True(body.GetProperty("auto").GetBoolean());
        // Auto is a picker entry, never a catalog model.
        Assert.Equal(1, body.GetProperty("models").GetArrayLength());
    }
}
