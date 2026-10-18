using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>The API host with the agent fleet in memory, on the canned client. Plan approval holds a task open.</summary>
public sealed class FleetApiFixture : SharedApiFixture
{
    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog(FleetRunHarness.ModelA,
        [
            new ModelInfo(FleetRunHarness.ModelA, "fake", "Model A", "a", null, SupportsTools: true, SupportsVision: false),
        ]));
        builder.Services.AddSingleton<IUserChatClientFactoryProvider>(
            new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(new FakeChatClient(), FleetRunHarness.ModelA)));
        builder.Services.AddAgentFleetInMemory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fleet:ApprovalDepth"] = "0",
            ["Fleet:JudgeDepth"] = "-1",
            ["Fleet:MaxConcurrentTasks"] = "50",
            ["Fleet:SseKeepAlive"] = "00:00:00.200",
            ["Fleet:MaxInputFiles"] = "2",
            ["Fleet:MaxInputFileBytes"] = "1000",
        }).Build());
    }
}

/// <summary>
/// The fleet's endpoints (ADR 0097): isolation between users, the 409s, the SSE stream, the download headers that
/// keep model-written files from running in the app origin, and the per-user prompts.
/// </summary>
public sealed class FleetApiTests(FleetApiFixture fixture) : SharedClusterTests<FleetApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private HttpClient Client => Fixture.Client;

    private IFleetStore Store => Fixture.App.Services.GetRequiredService<IFleetStore>();

    private static HttpRequestMessage Request(HttpMethod method, string uri, string user, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, user);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string uri, string user, object? body = null)
        => Client.SendAsync(Request(method, uri, user, body));

    private static string Task() => FakeChatClient.FleetTask("Write a haiku about lighthouses", new([FleetRunHarness.Plan("Sequential", "Writer")]));

    private async Task<string> SubmitHeldAsync(string user)
    {
        var response = await Send(HttpMethod.Post, "/api/fleet/tasks", user, new { task = Task(), model = FleetRunHarness.ModelA });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var taskId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetString()!;
        await WaitForEventAsync(user, taskId, "\"type\":\"prompt\"");
        return taskId;
    }

    private async Task<string> SubmitAndStopAsync(string user)
    {
        var taskId = await SubmitHeldAsync(user);
        (await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/stop", user)).EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await (await Send(HttpMethod.Get, $"/api/fleet/tasks/{taskId}", user)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString() == FleetTaskStatus.Running)
        {
            await System.Threading.Tasks.Task.Delay(20, timeout.Token);
        }

        return taskId;
    }

    // Reads the SSE stream until a line contains the marker; returns everything read.
    private async Task<string> WaitForEventAsync(string user, string taskId, string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await Client.SendAsync(Request(HttpMethod.Get, $"/api/fleet/tasks/{taskId}/events", user),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var seen = new StringBuilder();
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            seen.AppendLine(line);
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                return seen.ToString();
            }
        }

        return seen.ToString();
    }

    [Fact]
    public async Task OtherUser_Gets404_Everywhere()
    {
        var taskId = await SubmitHeldAsync("fleet-owner-1");
        try
        {
            foreach (var (method, uri) in new[]
            {
                (HttpMethod.Get, $"/api/fleet/tasks/{taskId}"),
                (HttpMethod.Get, $"/api/fleet/tasks/{taskId}/events"),
                (HttpMethod.Get, $"/api/fleet/tasks/{taskId}/files"),
                (HttpMethod.Get, $"/api/fleet/tasks/{taskId}/files/report.md"),
                (HttpMethod.Delete, $"/api/fleet/tasks/{taskId}"),
                (HttpMethod.Post, $"/api/fleet/tasks/{taskId}/stop"),
            })
            {
                var response = await Send(method, uri, "fleet-intruder-1");
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {uri} gave {response.StatusCode}");
            }

            Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/answer", "fleet-intruder-1", new { answer = "y" })).StatusCode);
            var list = await (await Send(HttpMethod.Get, "/api/fleet/tasks", "fleet-intruder-1")).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, list.GetArrayLength());
        }
        finally
        {
            await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/stop", "fleet-owner-1");
        }
    }

    /// <summary>ADR 0099: the page's override reaches the record and comes back on the task, sanitized.</summary>
    [Fact]
    public async Task SubmitTask_EnabledTools_RoundTrips()
    {
        const string user = "fleet-tools-1";
        var response = await Send(HttpMethod.Post, "/api/fleet/tasks", user,
            new { task = Task(), model = FleetRunHarness.ModelA, enabledTools = new[] { "mcp:s1", "bogus" } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var taskId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetString()!;
        try
        {
            var task = await (await Send(HttpMethod.Get, $"/api/fleet/tasks/{taskId}", user)).Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal(["mcp:s1"], task.GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()));
        }
        finally
        {
            await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/stop", user);
        }
    }

    private static MultipartFormDataContent Form(object? request, params (string Name, byte[] Content)[] files)
    {
        var form = new MultipartFormDataContent();
        if (request is not null)
        {
            form.Add(new StringContent(JsonSerializer.Serialize(request)), "request");
        }

        foreach (var (name, content) in files)
        {
            form.Add(new ByteArrayContent(content), "files", name);
        }

        return form;
    }

    private Task<HttpResponseMessage> SendForm(string user, MultipartFormDataContent form)
    {
        var request = Request(HttpMethod.Post, "/api/fleet/tasks/with-files", user);
        request.Content = form;
        return Client.SendAsync(request);
    }

    /// <summary>ADR 0101: the multipart route stores the files under inputs/ and the record names them.</summary>
    [Fact]
    public async Task SubmitWithFiles_Multipart_StoresFiles()
    {
        const string user = "fleet-files-1";
        var response = await SendForm(user, Form(new { task = Task(), model = FleetRunHarness.ModelA }, ("notes.md", Encoding.UTF8.GetBytes("# notes"))));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var taskId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetString()!;
        try
        {
            var task = await (await Send(HttpMethod.Get, $"/api/fleet/tasks/{taskId}", user)).Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal(["notes.md"], task.GetProperty("inputs").EnumerateArray().Select(e => e.GetString()));
            Assert.Contains(await Store.ListWorkspaceAsync(user, task.GetProperty("workspaceId").GetString()!), f => f.Path == "inputs/notes.md");
        }
        finally
        {
            await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/stop", user);
        }
    }

    /// <summary>ADR 0101: a body over the route's limit is a 413, a form without the request JSON a 400, and nothing
    /// starts. The fixture's caps make the limit 2 × 1 000 bytes plus the 1 MB form allowance.</summary>
    [Fact]
    public async Task SubmitWithFiles_OverLimit_Refused()
    {
        const string user = "fleet-files-2";

        var tooLarge = await SendForm(user, Form(new { task = Task() }, ("big.md", new byte[(1024 * 1024) + 10_000])));
        var noRequest = await SendForm(user, Form(null, ("a.md", [1])));
        var badJson = await SendForm(user, Form("not an object", ("a.md", [1])));
        var notMultipart = await Send(HttpMethod.Post, "/api/fleet/tasks/with-files", user, new { task = Task() });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noRequest.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notMultipart.StatusCode);
        Assert.Empty(await Store.ListTasksAsync(user));
    }

    [Fact]
    public async Task Api_Conflicts()
    {
        const string user = "fleet-conflicts-1";
        var taskId = await SubmitHeldAsync(user);

        var second = await Send(HttpMethod.Post, "/api/fleet/tasks", user, new { task = Task() });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already running", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Delete, $"/api/fleet/tasks/{taskId}", user)).StatusCode);
        var empty = await Send(HttpMethod.Post, "/api/fleet/tasks", "fleet-conflicts-2", new { task = " " });
        Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);

        (await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/stop", user)).EnsureSuccessStatusCode();
        await WaitForEventAsync(user, taskId, "task.finished");

        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/stop", user)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/answer", user, new { answer = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/fleet/tasks/{taskId}", user)).StatusCode);
    }

    [Fact]
    public async Task Answer_RunsTheTask_AndTheStreamEnds()
    {
        const string user = "fleet-answer-1";
        var taskId = await SubmitHeldAsync(user);

        (await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/answer", user, new { answer = "y" })).EnsureSuccessStatusCode();
        var stream = await WaitForEventAsync(user, taskId, "task.finished");

        Assert.Contains("\"type\":\"task.started\"", stream);
        Assert.Contains("\"type\":\"plan.accepted\"", stream);
        Assert.Contains("\"status\":\"done\"", stream);
    }

    [Fact]
    public async Task Sse_SendsKeepAlive_WhileIdle()
    {
        const string user = "fleet-keepalive-1";
        var taskId = await SubmitHeldAsync(user);
        try
        {
            // The task waits on its question, so nothing is published: only the keep-alive comment arrives.
            var stream = await WaitForEventAsync(user, taskId, ": keep-alive");

            Assert.Contains(": keep-alive", stream);
            Assert.Contains("data: {\"seq\":1,\"type\":\"task.started\"", stream);
        }
        finally
        {
            await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/stop", user);
        }
    }

    [Fact]
    public async Task FileDownload_IsAttachment_NoSniff_NotHtml()
    {
        const string user = "fleet-files-1";
        var taskId = await SubmitAndStopAsync(user);
        var task = (await Store.GetTaskAsync(user, taskId))!;
        await Store.PutWorkspaceFileAsync(user, task.WorkspaceId, "site/index.html", Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
        await Store.PutWorkspaceFileAsync(user, task.WorkspaceId, "logo.svg", Encoding.UTF8.GetBytes("<svg onload=alert(1)/>"));
        await Store.PutWorkspaceFileAsync(user, task.WorkspaceId, "data.bin", [0, 1, 2, 255]);

        var files = await (await Send(HttpMethod.Get, $"/api/fleet/tasks/{taskId}/files", user)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, files.GetArrayLength());

        foreach (var (path, type) in new[] { ("site/index.html", "text/plain"), ("logo.svg", "text/plain"), ("data.bin", "application/octet-stream") })
        {
            var response = await Send(HttpMethod.Get, $"/api/fleet/tasks/{taskId}/files/{path}", user);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
    }

    [Theory]
    [InlineData("..%5Csecret.txt")]
    [InlineData("a/..%5C..%5Csecret.txt")]
    [InlineData("C:%5Cwindows%5Cwin.ini")]
    public async Task FileDownload_PathTraversal_Refused(string path)
    {
        const string user = "fleet-traversal-1";
        var taskId = (await Store.ListTasksAsync(user)).FirstOrDefault()?.Id ?? await SubmitAndStopAsync(user);

        var response = await Send(HttpMethod.Get, $"/api/fleet/tasks/{taskId}/files/{path}", user);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{path} gave {response.StatusCode}");
    }

    [Fact]
    public async Task Prompts_SetResetRoundTrip_PerUser()
    {
        var put = await Send(HttpMethod.Put, "/api/fleet/prompts/judge", "fleet-prompts-1", new { text = "Be strict." });
        put.EnsureSuccessStatusCode();
        Assert.True((await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("edited").GetBoolean());

        var mine = await (await Send(HttpMethod.Get, "/api/fleet/prompts", "fleet-prompts-1")).Content.ReadFromJsonAsync<JsonElement>();
        var theirs = await (await Send(HttpMethod.Get, "/api/fleet/prompts", "fleet-prompts-2")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Be strict.", Judge(mine).GetProperty("text").GetString());
        Assert.False(Judge(theirs).GetProperty("edited").GetBoolean());

        var reset = await Send(HttpMethod.Delete, "/api/fleet/prompts/judge", "fleet-prompts-1");
        Assert.False((await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("edited").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, "/api/fleet/prompts/nope", "fleet-prompts-1", new { text = "x" })).StatusCode);

        static JsonElement Judge(JsonElement prompts) => prompts.EnumerateArray().Single(p => p.GetProperty("key").GetString() == "judge");
    }

    [Fact]
    public async Task Config_ReportsLimitsAndCopilotOff()
    {
        var config = await (await Send(HttpMethod.Get, "/api/fleet/config", "fleet-config-1")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(config.GetProperty("copilotAgentsEnabled").GetBoolean());
        Assert.True(config.GetProperty("defaultMaxTokens").GetInt64() <= config.GetProperty("maxTokensLimit").GetInt64());
    }
}

/// <summary>A host without the fleet registered answers 503, never a 500 or a missing route (rule 7).</summary>
public sealed class FleetApiUnavailableTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    [Theory]
    [InlineData("/api/fleet/config")]
    [InlineData("/api/fleet/tasks")]
    [InlineData("/api/fleet/tasks/x/events")]
    [InlineData("/api/fleet/prompts")]
    public async Task MissingService_Is503(string uri)
    {
        var response = await Fixture.Client.GetAsync(uri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}
