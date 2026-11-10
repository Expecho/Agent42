using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The fleet share link's endpoints (ADR 0120): the owner routes (behind Easy Auth in prod, scoped by the principal header
/// here) and the public ones, which are called with NO principal, as an unauthenticated recipient would. The load-bearing
/// properties are an exact public field set, a fixed failure reason, the same safe file and canvas serving as the owner
/// routes, and every miss being a 404.
/// </summary>
public sealed class FleetShareApiTests(FleetApiFixture fixture) : SharedClusterTests<FleetApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private HttpClient Client => Fixture.Client;

    private IFleetStore Store => Fixture.App.Services.GetRequiredService<IFleetStore>();

    private Task<HttpResponseMessage> AsUser(HttpMethod method, string uri, string user)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, user);
        return Client.SendAsync(request);
    }

    // A finished task saved straight into the store: no run is needed to test sharing.
    private async Task<FleetTaskRecord> SeedTaskAsync(string user, Func<FleetTaskRecord, FleetTaskRecord>? shape = null)
    {
        var record = new FleetTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user,
            Request = "Write a haiku about lighthouses",
            WorkspaceId = Guid.NewGuid().ToString("N"),
            PlannerModel = "fake:model-a",
            Status = FleetTaskStatus.Done,
            Result = "# Haiku\nA lamp on the rock",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            FinishedAt = DateTimeOffset.UtcNow,
        };
        record = shape?.Invoke(record) ?? record;
        await Store.SaveTaskAsync(record);
        return record;
    }

    private async Task<string> ShareAsync(string user, string taskId)
    {
        var response = await AsUser(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/share", user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        Assert.Contains("/shared/fleet.html#shr_", url);
        return url[(url.IndexOf('#') + 1)..];
    }

    private static void AssertSandboxed(HttpResponseMessage response, string what)
    {
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var csp), $"{what}: no CSP");
        Assert.Equal(AgentApi.ArtifactCsp, string.Join(";", csp!));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Share_Create_IsIdempotent_Get_Revoke()
    {
        const string user = "fleet-share-owner-1";
        var task = await SeedTaskAsync(user);

        var before = await (await AsUser(HttpMethod.Get, $"/api/fleet/tasks/{task.Id}/share", user)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("url").ValueKind);

        var token = await ShareAsync(user, task.Id);
        Assert.Equal(token, await ShareAsync(user, task.Id));

        var shown = await (await AsUser(HttpMethod.Get, $"/api/fleet/tasks/{task.Id}/share", user)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.EndsWith("#" + token, shown.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/share/fleet/{token}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await AsUser(HttpMethod.Delete, $"/api/fleet/tasks/{task.Id}/share", user)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/fleet/{token}")).StatusCode);
    }

    [Fact]
    public async Task Share_OtherUsersTask_PostNotFound_GetNull()
    {
        var task = await SeedTaskAsync("fleet-share-owner-2");

        Assert.Equal(HttpStatusCode.NotFound, (await AsUser(HttpMethod.Post, $"/api/fleet/tasks/{task.Id}/share", "fleet-share-intruder")).StatusCode);

        // The intruder's GET reads the intruder's own links grain, so nothing was minted for them and there is nothing to show.
        var shown = await (await AsUser(HttpMethod.Get, $"/api/fleet/tasks/{task.Id}/share", "fleet-share-intruder")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("url").ValueKind);
        Assert.Null(await Cluster.GrainFactory.GetGrain<IUserFleetShareLinksGrain>("fleet-share-owner-2").GetAsync(task.Id));
    }

    [Fact]
    public async Task Public_Task_ReturnsExactlyTheAllowlist()
    {
        const string user = "fleet-share-owner-3";
        var task = await SeedTaskAsync(user, t => t with
        {
            Notes = new Dictionary<string, string> { ["secret-note"] = "private notes text" },
            Inputs = ["private-upload.pdf"],
            Documents = ["private-doc.pdf"],
            EnabledTools = ["private-tool"],
            OriginConversationId = "private-origin-conversation",
            AppliedLessons = [new FleetAppliedLesson("private-lesson-id", "private lesson text")],
        });
        var token = await ShareAsync(user, task.Id);

        var response = await Client.GetAsync($"/api/share/fleet/{token}");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal(
            ["finishedAt", "reason", "request", "result", "startedAt", "status"],
            json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Write a haiku about lighthouses", json.GetProperty("request").GetString());
        Assert.Equal("done", json.GetProperty("status").GetString());
        Assert.Equal("# Haiku\nA lamp on the rock", json.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("reason").ValueKind);

        // The values behind the fields that must stay off the wire.
        foreach (var secret in new[] { user, task.WorkspaceId, "private notes text", "private-upload.pdf", "private-doc.pdf", "private-tool", "private-origin-conversation", "private-lesson-id", "private lesson text", "fake:model-a" })
        {
            Assert.DoesNotContain(secret, body);
        }
    }

    // The stored reason is a raw exception message: the public view shows fixed text per status instead.
    [Theory]
    [InlineData("failed", "The task failed.")]
    [InlineData("stopped", "The task was stopped before it finished.")]
    [InlineData("interrupted", "The task was interrupted by a restart.")]
    public async Task Public_EndedTask_ReasonIsFixedText(string status, string expected)
    {
        var user = $"fleet-share-reason-{status}";
        const string raw = "HttpRequestException: https://secret-resource.openai.azure.com/openai key=abc123 C:\\app\\secret.txt";
        var task = await SeedTaskAsync(user, t => t with { Status = status, Result = null, Reason = raw });
        var token = await ShareAsync(user, task.Id);

        var body = await (await Client.GetAsync($"/api/share/fleet/{token}")).Content.ReadAsStringAsync();

        Assert.Equal(expected, JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("secret-resource", body);
        Assert.DoesNotContain("abc123", body);
    }

    [Fact]
    public async Task Public_UnknownOrRevokedToken_404()
    {
        const string user = "fleet-share-owner-4";
        var task = await SeedTaskAsync(user);
        await Store.PutWorkspaceFileAsync(user, task.WorkspaceId, "report.md", Encoding.UTF8.GetBytes("hello"));
        var token = await ShareAsync(user, task.Id);
        await AsUser(HttpMethod.Delete, $"/api/fleet/tasks/{task.Id}/share", user);

        foreach (var shared in new[] { token, "shr_not-a-real-token" })
        {
            foreach (var tail in new[] { "", "/files", "/files/report.md", "/canvas", "/canvas/app/versions/1/content", "/canvas/app/versions/1/frame" })
            {
                var response = await Client.GetAsync($"/api/share/fleet/{shared}{tail}");
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{shared}{tail} answered {(int)response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task DeleteTask_RevokesLink()
    {
        const string user = "fleet-share-owner-5";
        var task = await SeedTaskAsync(user);
        var token = await ShareAsync(user, task.Id);

        Assert.Equal(HttpStatusCode.OK, (await AsUser(HttpMethod.Delete, $"/api/fleet/tasks/{task.Id}", user)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/fleet/{token}")).StatusCode);
        Assert.Null(await Cluster.GrainFactory.GetGrain<IFleetShareLinkDirectoryGrain>(ShareToken.Hash(token)).ResolveAsync());
        Assert.Null(await Cluster.GrainFactory.GetGrain<IUserFleetShareLinksGrain>(user).GetAsync(task.Id));
    }

    // The public routes reuse the owner's serving core: attachment, nosniff, and a type that never renders.
    [Fact]
    public async Task Public_Files_ListAndDownload_AreSafe()
    {
        const string user = "fleet-share-owner-6";
        var task = await SeedTaskAsync(user);
        await Store.PutWorkspaceFileAsync(user, task.WorkspaceId, "site/index.html", Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
        await Store.PutWorkspaceFileAsync(user, task.WorkspaceId, "logo.svg", Encoding.UTF8.GetBytes("<svg onload=alert(1)/>"));
        await Store.PutWorkspaceFileAsync(user, task.WorkspaceId, "data.bin", [0, 1, 2, 255]);
        var token = await ShareAsync(user, task.Id);

        var files = await (await Client.GetAsync($"/api/share/fleet/{token}/files")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, files.GetArrayLength());

        foreach (var (path, type) in new[] { ("site/index.html", "text/plain"), ("logo.svg", "text/plain"), ("data.bin", "application/octet-stream") })
        {
            var response = await Client.GetAsync($"/api/share/fleet/{token}/files/{path}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/fleet/{token}/files/missing.txt")).StatusCode);
    }

    [Theory]
    [InlineData("..%5Csecret.txt")]
    [InlineData("a/..%5C..%5Csecret.txt")]
    [InlineData("C:%5Cwindows%5Cwin.ini")]
    public async Task Public_File_PathTraversal_Refused(string path)
    {
        const string user = "fleet-share-owner-7";
        var task = (await Store.ListTasksAsync(user)).FirstOrDefault() ?? await SeedTaskAsync(user);
        var token = await ShareAsync(user, task.Id);

        var response = await Client.GetAsync($"/api/share/fleet/{token}/files/{path}");

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"answered {(int)response.StatusCode}");
    }

    private async Task<string> SeedSharedCanvasAsync(string user)
    {
        var task = await SeedTaskAsync(user);
        await Store.PutCanvasVersionAsync(user, task.WorkspaceId, "app", 2, Encoding.UTF8.GetBytes("<p>v2</p>"));
        await Store.PutCanvasVersionAsync(user, task.WorkspaceId, "app", 3, Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
        await Store.PutCanvasVersionAsync(user, task.WorkspaceId, "notes", 1, Encoding.UTF8.GetBytes("# Notes"));
        await Store.PutCanvasManifestAsync(user, task.WorkspaceId, new FleetCanvasManifest
        {
            Pages =
            [
                new FleetCanvasPage { Name = "app", Kind = ArtifactKind.Html, Title = "App", Versions = [2, 3] },
                new FleetCanvasPage { Name = "notes", Kind = ArtifactKind.Markdown, Title = "Notes", Versions = [1] },
            ],
        });
        return await ShareAsync(user, task.Id);
    }

    [Fact]
    public async Task Public_Canvas_ServesSandboxed_MissIs404WithHeaders()
    {
        var token = await SeedSharedCanvasAsync("fleet-share-owner-8");
        var root = $"/api/share/fleet/{token}/canvas";

        var pages = await (await Client.GetAsync(root)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, pages.GetArrayLength());
        Assert.Equal("app", pages[0].GetProperty("id").GetString());

        var content = await Client.GetAsync($"{root}/app/versions/3/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("text/plain", content.Content.Headers.ContentType?.MediaType);
        AssertSandboxed(content, "content");

        var frame = await Client.GetAsync($"{root}/app/versions/3/frame");
        Assert.Equal("text/html", frame.Content.Headers.ContentType?.MediaType);
        AssertSandboxed(frame, "frame");

        // A markdown page has no frame, a pruned or unknown version and an unknown page are not served: all 404 AND sandboxed.
        foreach (var miss in new[] { "notes/versions/1/frame", "app/versions/1/content", "app/versions/9/frame", "missing/versions/1/content" })
        {
            var response = await Client.GetAsync($"{root}/{miss}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            AssertSandboxed(response, miss);
        }

        // An unknown token's 404 is sandboxed too.
        AssertSandboxed(await Client.GetAsync("/api/share/fleet/shr_nope/canvas/app/versions/3/frame"), "unknown token");
    }

    // A credential namespace of its own: a chat token never opens a task, and a task token never opens a chat.
    [Fact]
    public async Task ChatToken_DoesNotOpenFleetRoute_AndViceVersa()
    {
        const string user = "fleet-share-owner-9";
        var task = await SeedTaskAsync(user);
        var fleetToken = await ShareAsync(user, task.Id);
        var chat = await Cluster.GrainFactory.GetGrain<IUserShareLinksGrain>(user).CreateOrGetAsync("conv-x");

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/fleet/{chat.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/{fleetToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/{fleetToken}/artifacts")).StatusCode);
    }

    // /api/share/fleet/artifacts matches both the fleet route (token "artifacts") and the chat artifacts route (token
    // "fleet"). The literal second segment wins, so it is a clean 404 and the chat routes keep resolving real tokens.
    [Fact]
    public async Task Public_RoutePrecedence_NoAmbiguity()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/share/fleet/artifacts")).StatusCode);

        var chat = await Cluster.GrainFactory.GetGrain<IUserShareLinksGrain>("fleet-share-owner-10").CreateOrGetAsync("conv-y");
        Assert.NotEqual(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/{chat.Token}")).StatusCode);
    }
}
