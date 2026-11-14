using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>The fleet's own tool catalog (ADR 0097): selection gating, MCP wrapping, web tool registration.</summary>
public class FleetToolCatalogTests
{
    private static readonly ScriptedFleetInteraction Interaction = new();

    private static FleetToolCatalog Catalog(IGrainFactory? grains = null)
        => new(grains!, NullLogger<FleetToolCatalog>.Instance, new InMemoryWebSearch());

    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => $"{name} ran", name);

    private static readonly McpServerTools ServerOne = new("s1", [Tool("alpha"), Tool("beta")], ServerName: "One", InteractiveOAuth: true);
    private static readonly McpServerTools ServerTwo = new("s2", [Tool("gamma")], ServerName: "Two");

    [Fact]
    public void McpTools_GatedBySelection()
    {
        var tools = Catalog().Build([ToolCatalog.McpToolKey("s1", "alpha")], "", [ServerOne, ServerTwo], Interaction);

        Assert.Equal(["alpha"], tools.Select(t => t.Name));
    }

    [Fact]
    public void McpTools_ServerKeyEnablesAllItsTools()
    {
        var tools = Catalog().Build([ToolCatalog.McpServerKey("s2"), ToolCatalog.WebSearch], "", [ServerOne, ServerTwo], Interaction);

        Assert.Equal(["web_search", "gamma"], tools.Select(t => t.Name));
    }

    /// <summary>ADR 0099: the task's override wins over a default that allows web search.</summary>
    [Fact]
    public async Task BuildAsync_Override_BeatsProfileDefault()
    {
        var profile = FakeProfileGrainFactory.Create([ToolCatalog.WebSearch]);

        var fromDefault = await Catalog(profile.Factory).BuildAsync("alice", "", null, Interaction, default);
        var overridden = await Catalog(profile.Factory).BuildAsync("alice", "", ["mcp:s1"], Interaction, default);

        Assert.Equal(["web_search"], fromDefault.Select(t => t.Name));
        Assert.Empty(overridden);
    }

    /// <summary>The override path never reads the profile: this catalog has no grain factory at all.</summary>
    [Fact]
    public async Task BuildAsync_Override_SkipsProfileRead()
    {
        var tools = await Catalog().BuildAsync("alice", "", [ToolCatalog.WebSearch], Interaction, default);

        Assert.Equal(["web_search"], tools.Select(t => t.Name));
    }

    /// <summary>An empty override means no selectable tools, not "the default" (which here allows everything).</summary>
    [Fact]
    public async Task BuildAsync_EmptyOverride_IsNone()
    {
        var profile = FakeProfileGrainFactory.Create(null);

        var tools = await Catalog(profile.Factory).BuildAsync("alice", "", [], Interaction, default);

        Assert.Empty(tools);
        Assert.Equal(0, profile.Reads);
    }

    [Fact]
    public void NullSelection_IsAll()
    {
        var tools = Catalog().Build(null, "", [ServerOne, ServerTwo], Interaction);

        Assert.Equal(["web_search", "alpha", "beta", "gamma"], tools.Select(t => t.Name));
    }

    [Fact]
    public void McpTools_ReauthWrapped_OnlyForInteractiveOAuth()
    {
        var tools = Catalog().Build(null, "", [ServerOne, ServerTwo], Interaction);

        Assert.IsType<ReauthNoticeMcpTool>(tools.Single(t => t.Name == "alpha"));
        Assert.IsNotType<ReauthNoticeMcpTool>(tools.Single(t => t.Name == "gamma"));
    }

    [Fact]
    public void InvalidOrTakenNames_Skipped()
    {
        var odd = new McpServerTools("s3", [Tool("has space"), Tool("web_search"), Tool("write_artifact"), Tool("canvas_write"), Tool("canvas_edit"), Tool("ok-name")]);

        var tools = Catalog().Build(null, "", [odd], Interaction);

        Assert.Equal(["web_search", "ok-name"], tools.Select(t => t.Name));
    }

    [Fact]
    public void McpTool_NamedLikeTodoTool_IsSkipped()
    {
        var clash = new McpServerTools("s3", [Tool("todo_add"), Tool("todo_list"), Tool("delta")]);

        var tools = Catalog().Build(null, "", [clash], Interaction);

        Assert.DoesNotContain(tools, t => t.Name.StartsWith("todo_", StringComparison.Ordinal));
        Assert.Contains(tools, t => t.Name == "delta");
    }

    /// <summary>ADR 0100: the document tools come first when the task has documents, whatever the selection (even
    /// an empty override); their names are reserved, so a same-named MCP tool is skipped with or without them.</summary>
    [Fact]
    public void Build_DocumentTools_OnlyWithDocuments_Reserved()
    {
        var clash = new McpServerTools("s3", [Tool("read_document"), Tool("search_documents"), Tool("delta")]);
        var documentTools = new[] { Tool(FleetDocumentTools.SearchName), Tool(FleetDocumentTools.ReadName) };

        var with = Catalog().Build([], "", [clash], Interaction, documentTools);
        var withAll = Catalog().Build(null, "", [clash], Interaction, documentTools);
        var without = Catalog().Build(null, "", [clash], Interaction);

        Assert.Equal(["search_documents", "read_document"], with.Select(t => t.Name));
        Assert.Equal(["search_documents", "read_document", "web_search", "delta"], withAll.Select(t => t.Name));
        Assert.Same(documentTools[1], withAll[1]);
        Assert.Equal(["web_search", "delta"], without.Select(t => t.Name));
    }

    /// <summary>ADR 0125: the fleet offers web_search only, with no page fetch.</summary>
    [Fact]
    public void Tools_DoNotIncludeFetchWebPage()
    {
        var tools = Catalog().Build(null, "Summarise https://docs.example.org/guide please", [], Interaction);

        Assert.Contains("web_search", tools.Select(t => t.Name));
        Assert.DoesNotContain("fetch_web_page", tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Elicitation_UrlAndForm_BecomeFleetQuestions()
    {
        var sink = new FleetElicitationSink(new ScriptedFleetInteraction("done", "{\"name\":\"Ada\"}", "no thanks"));

        var url = await sink.AskAsync("One", "c1", new(McpElicitationLimits.UrlMode, "Sign in", Url: "https://login.example.com/x"), default);
        var form = await sink.AskAsync("One", "c2", new(McpElicitationLimits.FormMode, "Your name?", "{\"type\":\"object\"}"), default);
        var declined = await sink.AskAsync("One", "c3", new(McpElicitationLimits.FormMode, "Your name?", "{}"), default);
        var unsafeUrl = await sink.AskAsync("One", "c4", new(McpElicitationLimits.UrlMode, "x", Url: "javascript:alert(1)"), default);

        Assert.Equal(McpElicitationLimits.Accept, url.Action);
        Assert.Equal((McpElicitationLimits.Accept, "{\"name\":\"Ada\"}"), (form.Action, form.ContentJson));
        Assert.Equal(McpElicitationLimits.Decline, declined.Action);
        Assert.Equal(McpElicitationLimits.Cancel, unsafeUrl.Action);
    }
}
