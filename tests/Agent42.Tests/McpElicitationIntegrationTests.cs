using System.ComponentModel;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agent42.Tests;

/// <summary>
/// MCP elicitation through the REAL SDK (ADR 0095): <see cref="McpConnectionFactory"/> against an in-process
/// Kestrel MCP server on loopback. The server is stateful streamable HTTP, the shape the spike measured: protocol
/// 2025-11-25, elicitation arrives as a server-to-client request, and the caller's async local does not reach the
/// handler, so these calls route through the single-in-flight-scope fallback.
/// </summary>
public sealed class McpElicitationIntegrationTests : IAsyncLifetime
{
    private static readonly McpConnectionSecrets NoSecrets = new(null, null);

    private WebApplication? _app;

    private McpServerConnection? _connection;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = false).WithTools<ElicitingServerTools>();
        _app = builder.Build();
        _app.MapMcp();
        await _app.StartAsync();

        var server = new StoredMcpServer("elicit", "Elicit Server", _app.Urls.First(), nameof(McpAuthMode.None), Enabled: true);
        _connection = await new McpConnectionFactory(NullLoggerFactory.Instance)
            .ConnectAsync(server, NoSecrets, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.Connection.DisposeAsync();
        }

        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private AIFunction Tool(string name) =>
        Assert.IsAssignableFrom<AIFunction>(Assert.Single(_connection!.Tools, t => t.Name == name));

    private McpElicitationRouter Router => Assert.IsType<McpElicitationRouter>(_connection!.Elicitation);

    private sealed class AnsweringSink(McpElicitationAnswer answer) : IMcpElicitationSink
    {
        public List<McpElicitationRequest> Asked { get; } = [];

        public Task<McpElicitationAnswer> AskAsync(
            string serverName, string toolCallId, McpElicitationRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task FormElicitation_ReachesTheScopeSink_AndTheAnswerReturnsToTheServer()
    {
        var sink = new AnsweringSink(new McpElicitationAnswer(McpElicitationLimits.Accept, """{"name":"Bob"}"""));

        object? result;
        using (Router.Enter(sink, "call_1", "Elicit Server"))
        {
            result = await Tool("ask_form").InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
        }

        Assert.Contains("accept:name=Bob", result?.ToString(), StringComparison.Ordinal);
        var asked = Assert.Single(sink.Asked);
        Assert.Equal((McpElicitationLimits.FormMode, "Your name?"), (asked.Mode, asked.Message));
        Assert.Contains("\"name\"", asked.SchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoScope_AnswersCancel()
    {
        var result = await Tool("ask_form").InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.Contains("cancel:", result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UrlElicitation_WithJavascriptScheme_IsCancelledWithoutAsking()
    {
        var sink = new AnsweringSink(new McpElicitationAnswer(McpElicitationLimits.Accept));

        object? result;
        using (Router.Enter(sink, "call_1", "Elicit Server"))
        {
            result = await Tool("ask_bad_url").InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
        }

        Assert.Contains("cancel", result?.ToString(), StringComparison.Ordinal);
        Assert.Empty(sink.Asked);
    }

    [Fact]
    public async Task ClientDeclaresFormAndUrl()
    {
        var result = await Tool("caps").InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.Contains("form=True,url=True", result?.ToString(), StringComparison.Ordinal);
    }

    // AC1: one options helper for both factories, so the remote and local-bridge paths cannot drift apart.
    [Theory]
    [InlineData("src/Agent42.Backend/Mcp/McpConnectionFactory.cs")]
    [InlineData("src/Agent42.Backend/Mcp/LocalBridgeConnectionFactory.cs")]
    public void BothFactories_UseTheSharedOptionsHelper(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("McpElicitationClient.Options(router)", source, StringComparison.Ordinal);
        Assert.Equal(
            source.Split("McpClient.CreateAsync(").Length - 1,
            source.Split("McpElicitationClient.Options(").Length - 1);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Agent42.slnx not found above the test output.");
    }

    [McpServerToolType]
    public sealed class ElicitingServerTools
    {
        [McpServerTool(Name = "ask_form"), Description("Asks the user for their name.")]
        public static async Task<string> AskForm(McpServer server, CancellationToken cancellationToken)
        {
            var answer = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "Your name?",
                RequestedSchema = new()
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        ["name"] = new ElicitRequestParams.StringSchema(),
                    },
                    Required = ["name"],
                },
            }, cancellationToken);
            var content = answer.Content is null ? "" : string.Join(",", answer.Content.Select(kv => $"{kv.Key}={kv.Value}"));
            return $"{answer.Action}:{content}";
        }

        [McpServerTool(Name = "ask_bad_url"), Description("Asks the user to open a javascript: URL.")]
        public static async Task<string> AskBadUrl(McpServer server, CancellationToken cancellationToken)
            => (await server.ElicitAsync(new ElicitRequestParams
            {
                Mode = "url",
                Message = "Open this",
                Url = "javascript:alert(1)",
                ElicitationId = "bad",
            }, cancellationToken)).Action;

        [McpServerTool(Name = "caps"), Description("Reports the client's elicitation capability.")]
        public static string Caps(McpServer server)
            => server.ClientCapabilities?.Elicitation is { } e ? $"form={e.Form is not null},url={e.Url is not null}" : "none";
    }
}
