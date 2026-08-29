using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.TestHost;

namespace Agent42.Tests;

/// <summary>
/// The selectable-tools HTTP contract (ADR 0021), hosted in-memory (TestServer) against a real Orleans
/// test cluster: the /api/tools catalog, the per-conversation tools GET/PUT round-trip, and the agent /
/// profile endpoints carrying EnabledTools. The load-bearing one is AC14 — a language-only profile save
/// must not wipe a previously-saved tool default (the read-modify-write / absent-means-keep contract),
/// which only an endpoint-level test can prove.
/// </summary>
public sealed class ToolSelectionApiTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    // The host, the cluster and the client are all the fixture's, deployed once for the class.
    private HttpClient Client => Fixture.Client;

    private static HttpRequestMessage Request(HttpMethod method, string uri, string principalId, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, principalId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static async Task<string[]> ReadStringArrayAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. doc.RootElement.EnumerateArray().Select(e => e.GetString()!)];
    }

    // AC6 (catalog): /api/tools returns the static selectable keys, with the hosted-tools flag. NEITHER bare
    // wildcard is a catalog key — not `mcp` (ADR 0026) and, since per-skill selectable tools, not `skills`
    // either: both are selection sentinels, not checkboxes. A user with no MCP servers gets the four static
    // keys plus one `skill:{name}` per shipped skill.
    [Fact]
    public async Task Tools_Catalog_ReturnsSelectableKeys()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/tools", "user-cat"));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keys = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();

        Assert.Equal(
            new[]
            {
                "web_search", "location", "scheduling", "code_interpreter", "calendar", "memory",
                "skill:scheduledprompt", "skill:websearch",
            }.Order(),
            keys.Order());

        Assert.DoesNotContain("skills", keys); // the wildcard is never rendered

        // code_interpreter is the only one flagged as needing a hosted-tools model.
        var codeInterpreter = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "code_interpreter");
        Assert.True(codeInterpreter.GetProperty("requiresHostedTools").GetBoolean());
    }

    // AC11 (ADR 0057): the memory descriptor reaches the picker over the wire, labelled "Long-term
    // memory". The label is asserted here and not only in ToolCatalogTests because it is what the user
    // actually reads: plain "Memory" would read as the conversation's own history (the ADR 0004 session),
    // which this is not, and that misreading makes the checkbox look like it wipes the current chat.
    //
    // It renders even with no Hindsight:ApiKey configured — today's production state, and the state this
    // fixture runs in. That is deliberate (the code_interpreter precedent: the catalog expresses INTENT
    // and an outer gate decides). Hiding it would make this per-user endpoint depend on operator config
    // for the first time; Preferences → Configuration reports memory's real state instead.
    [Fact]
    public async Task Tools_Catalog_IncludesMemory()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/tools", "user-mem-cat"));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var memory = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "memory");

        Assert.Equal("Long-term memory", memory.GetProperty("label").GetString());
        Assert.False(memory.GetProperty("requiresHostedTools").GetBoolean());
    }

    // AC6/AC14 (per-skill): every shipped skill appears as a `skill:{name}` descriptor whose LABEL is the
    // frontmatter name verbatim — the same token the key addresses and the model is shown, so the picker,
    // the stored key and the ADR all say the same thing. Deliberately not title-cased (a display-only
    // transform would drift from the key). Unlike the MCP portion this is per-deployment, not per-user.
    [Fact]
    public async Task Tools_Catalog_IncludesPerSkillKeys()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/tools", "user-skill-cat"));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var skills = doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("key").GetString()!.StartsWith("skill:", StringComparison.Ordinal))
            .Select(e => (Key: e.GetProperty("key").GetString(), Label: e.GetProperty("label").GetString()))
            .ToArray();

        Assert.Equal(
            [("skill:scheduledprompt", "scheduledprompt"), ("skill:websearch", "websearch")],
            skills);

        // A skill never needs a hosted-tools model — that flag belongs to code_interpreter alone.
        Assert.All(
            doc.RootElement.EnumerateArray()
                .Where(e => e.GetProperty("key").GetString()!.StartsWith("skill:", StringComparison.Ordinal)),
            e => Assert.False(e.GetProperty("requiresHostedTools").GetBoolean()));
    }

    // AC5 (per-server, ADR 0026): a management-ENABLED MCP server appears in the catalog as an `mcp:{id}`
    // descriptor labelled with the server name; a management-DISABLED server is excluded (no checkbox for a
    // server that contributes no tools).
    [Fact]
    public async Task Tools_Catalog_IncludesEnabledMcpServers_ExcludesDisabled()
    {
        var on = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-mcp-cat", new
        {
            name = "Weather",
            endpoint = "https://weather.example.com/mcp",
            auth = "None",
            enabled = true,
        }));
        on.EnsureSuccessStatusCode();
        var off = await Client.SendAsync(Request(HttpMethod.Post, "/api/mcp-servers", "user-mcp-cat", new
        {
            name = "Docs",
            endpoint = "https://docs.example.com/mcp",
            auth = "None",
            enabled = false,
        }));
        off.EnsureSuccessStatusCode();

        using var onDoc = JsonDocument.Parse(await on.Content.ReadAsStringAsync());
        var enabledId = onDoc.RootElement.GetProperty("id").GetString();
        using var offDoc = JsonDocument.Parse(await off.Content.ReadAsStringAsync());
        var disabledId = offDoc.RootElement.GetProperty("id").GetString();

        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/tools", "user-mcp-cat"));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var descriptors = doc.RootElement.EnumerateArray().ToArray();

        var enabled = descriptors.SingleOrDefault(e => e.GetProperty("key").GetString() == $"mcp:{enabledId}");
        Assert.Equal(JsonValueKind.Object, enabled.ValueKind); // present
        Assert.Equal("Weather", enabled.GetProperty("label").GetString());
        Assert.False(enabled.GetProperty("requiresHostedTools").GetBoolean());

        Assert.DoesNotContain(descriptors, e => e.GetProperty("key").GetString() == $"mcp:{disabledId}");
        Assert.DoesNotContain(descriptors, e => e.GetProperty("key").GetString() == "mcp"); // no bare wildcard
    }

    // AC12 (agent): POST /api/agents round-trips EnabledTools; a create that omits it stores null (= all).
    [Fact]
    public async Task Agent_RoundTripsEnabledTools()
    {
        var scoped = await Client.SendAsync(Request(HttpMethod.Post, "/api/agents", "user-agent", new
        {
            name = "Scheduler",
            description = "schedules",
            model = "fake-model",
            systemPrompt = "You schedule.",
            enabledTools = new[] { "web_search", "scheduling" },
        }));
        scoped.EnsureSuccessStatusCode();

        using (var doc = JsonDocument.Parse(await scoped.Content.ReadAsStringAsync()))
        {
            // The create body is now AgentSaveResponse { agent, apiKey } (ADR 0022) — the definition is nested.
            var tools = doc.RootElement.GetProperty("agent").GetProperty("enabledTools")
                .EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "web_search", "scheduling" }, tools);
        }

        // A create that omits enabledTools stores null — the "all tools" sentinel, no migration for old clients.
        var full = await Client.SendAsync(Request(HttpMethod.Post, "/api/agents", "user-agent", new
        {
            name = "Full",
            description = "everything",
            model = "fake-model",
            systemPrompt = "You do everything.",
        }));
        full.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await full.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("agent").GetProperty("enabledTools").ValueKind);
        }

        // GET /api/agents carries the round-tripped set for the scoped agent.
        var list = await Client.SendAsync(Request(HttpMethod.Get, "/api/agents", "user-agent"));
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var scheduler = listDoc.RootElement.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Scheduler");
        var schedulerId = scheduler.GetProperty("id").GetString();
        Assert.Equal(
            new[] { "web_search", "scheduling" },
            scheduler.GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()).ToArray());

        // AC12 also covers EDIT: PUT /api/agents/{id} threads a changed EnabledTools through the same path.
        var edit = await Client.SendAsync(Request(HttpMethod.Put, $"/api/agents/{schedulerId}", "user-agent", new
        {
            name = "Scheduler",
            description = "schedules",
            model = "fake-model",
            systemPrompt = "You schedule.",
            enabledTools = new[] { "location" },
        }));
        edit.EnsureSuccessStatusCode();
        using var editDoc = JsonDocument.Parse(await edit.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { "location" },
            editDoc.RootElement.GetProperty("agent").GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // AC13: PUT /api/conversations/{id}/tools then GET returns the same set. Unknown keys are dropped.
    [Fact]
    public async Task Conversation_ToolsPutThenGet_ReturnsSet()
    {
        var put = await Client.SendAsync(Request(HttpMethod.Put, "/api/conversations/conv-1/tools", "user-conv", new
        {
            tools = new[] { "web_search", "not-a-real-tool", "scheduling" },
        }));
        put.EnsureSuccessStatusCode();
        // The response is the sanitized effective set (garbage dropped, catalog order).
        Assert.Equal(new[] { "web_search", "scheduling" }, await ReadStringArrayAsync(put));

        var get = await Client.SendAsync(Request(HttpMethod.Get, "/api/conversations/conv-1/tools", "user-conv"));
        get.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "web_search", "scheduling" }, await ReadStringArrayAsync(get));
    }

    // AC14 (the load-bearing one): a language-only profile save does NOT wipe a previously-saved tool
    // default, and a tools-only save does NOT wipe the language — the read-modify-write / absent-means-keep
    // contract. Only reachable at the endpoint, so it is not covered by the grain-level round-trip test.
    [Fact]
    public async Task Profile_LanguageOnlySave_DoesNotWipeToolDefault()
    {
        // Save a tool default first (no language).
        (await Client.SendAsync(Request(HttpMethod.Put, "/api/profile", "user-prof", new
        {
            enabledTools = new[] { "web_search" },
        }))).EnsureSuccessStatusCode();

        // Then a language-only save (no enabledTools field at all).
        (await Client.SendAsync(Request(HttpMethod.Put, "/api/profile", "user-prof", new
        {
            preferredLanguage = "nl-NL",
        }))).EnsureSuccessStatusCode();

        var get = await Client.SendAsync(Request(HttpMethod.Get, "/api/profile", "user-prof"));
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("nl-NL", doc.RootElement.GetProperty("preferredLanguage").GetString());
        Assert.Equal(
            new[] { "web_search" },
            doc.RootElement.GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()).ToArray());

        // And the reverse: a tools-only save leaves the language intact.
        (await Client.SendAsync(Request(HttpMethod.Put, "/api/profile", "user-prof", new
        {
            enabledTools = new[] { "scheduling" },
        }))).EnsureSuccessStatusCode();

        var after = await Client.SendAsync(Request(HttpMethod.Get, "/api/profile", "user-prof"));
        using var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Equal("nl-NL", afterDoc.RootElement.GetProperty("preferredLanguage").GetString());
        Assert.Equal(
            new[] { "scheduling" },
            afterDoc.RootElement.GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()).ToArray());
    }
}
