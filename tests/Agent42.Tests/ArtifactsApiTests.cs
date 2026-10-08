using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The artifact routes, owner and share: what they serve, to whom, and above all with which headers. The
/// <c>/frame</c> route runs model-written script, and the CSP <c>sandbox</c> header is what keeps it off the app
/// origin even when someone opens it as a top-level page, where the iframe attribute does not exist.
/// </summary>
public sealed class ArtifactsApiTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private HttpClient Client => Fixture.Client;

    private static HttpRequestMessage Request(HttpMethod method, string uri, string principalId)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, principalId);
        return request;
    }

    private Task<HttpResponseMessage> Get(string uri, string user) => Client.SendAsync(Request(HttpMethod.Get, uri, user));

    // Creates an artifact the way the model does, through the scripted tool call, and returns its id.
    private async Task<string> CreateArtifact(string user, string conversation, string kind, string content)
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/{conversation}");
        await foreach (var _ in agent.ChatAsync(
            FakeChatClient.CallToolPrompt("create_artifact", new { kind, title = "T", content, language = kind == "code" ? "python" : null }),
            TestSiloConfigurator.DefaultModel))
        {
        }

        return (await agent.GetArtifactsAsync()).Last().Id;
    }

    private async Task<string> Share(string user, string conversation)
    {
        var created = await (await Client.SendAsync(Request(HttpMethod.Post, $"/api/conversations/{conversation}/share", user)))
            .Content.ReadFromJsonAsync<ShareLinkResponse>();
        return created!.Url!.Split('#', 2)[1];
    }

    private static void AssertSandboxHeaders(HttpResponseMessage response)
    {
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(AgentApi.ArtifactCsp, csp);
        Assert.StartsWith("sandbox allow-scripts;", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", csp, StringComparison.Ordinal);
        // Embeds of https pages are allowed (ADR 0084, 2026-10-08); http: and the wildcard are not.
        Assert.Contains("frame-src https:;", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("frame-src *", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("http:", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("no-store, private", string.Join(", ", response.Headers.CacheControl!.ToString().Split(", ").Order()));
    }

    // AC12: /content and /frame, owner and share, all carry the four headers. /content is text/plain even for an html
    // artifact, and /frame serves html as html.
    [Fact]
    public async Task Frame_And_Content_SendSandboxHeaders()
    {
        const string User = "art-api-headers";
        var id = await CreateArtifact(User, "conv", "html", "<p id=x>hi</p>");
        var token = await Share(User, "conv");

        foreach (var prefix in new[] { "/api/conversations/conv", $"/api/share/{token}" })
        {
            var content = await Get($"{prefix}/artifacts/{id}/versions/1/content", User);
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal("text/plain", content.Content.Headers.ContentType!.MediaType);
            Assert.Equal("<p id=x>hi</p>", await content.Content.ReadAsStringAsync());
            AssertSandboxHeaders(content);

            var frame = await Get($"{prefix}/artifacts/{id}/versions/1/frame", User);
            Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
            Assert.Equal("text/html", frame.Content.Headers.ContentType!.MediaType);
            AssertSandboxHeaders(frame);

            // A miss is sandboxed too: no path through these routes renders without the policy.
            AssertSandboxHeaders(await Get($"{prefix}/artifacts/{id}/versions/9/frame", User));
        }
    }

    // AC12: an svg is framed inside a minimal page; markdown and code have no frame at all.
    [Fact]
    public async Task SvgFrame_IsWrappedHtml()
    {
        const string User = "art-api-svg";
        var svg = await CreateArtifact(User, "conv", "svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><circle r=\"4\"/></svg>");
        var md = await CreateArtifact(User, "conv", "markdown", "# Title");
        var code = await CreateArtifact(User, "conv", "code", "print(1)");

        var frame = await (await Get($"/api/conversations/conv/artifacts/{svg}/versions/1/frame", User)).Content.ReadAsStringAsync();
        Assert.StartsWith("<!doctype html>", frame, StringComparison.Ordinal);
        Assert.Contains("<body><svg xmlns=\"http://www.w3.org/2000/svg\"><circle r=\"4\"/></svg></body>", frame, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/conversations/conv/artifacts/{md}/versions/1/frame", User)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/conversations/conv/artifacts/{code}/versions/1/frame", User)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get($"/api/conversations/conv/artifacts/{md}/versions/1/content", User)).StatusCode);
    }

    // The frame carries the app's look for the theme the pane asks for (dark by default), inside the artifact's own
    // head so its doctype stays first; owner and share alike.
    [Fact]
    public async Task Frame_InjectsTheAppTheme()
    {
        const string User = "art-api-theme";
        var id = await CreateArtifact(User, "conv", "html", "<!DOCTYPE html><html><head><title>t</title></head><body>hi</body></html>");
        var token = await Share(User, "conv");

        foreach (var prefix in new[] { "/api/conversations/conv", $"/api/share/{token}" })
        {
            var dark = await (await Get($"{prefix}/artifacts/{id}/versions/1/frame", User)).Content.ReadAsStringAsync();
            Assert.StartsWith("<!DOCTYPE html><html><head>" + ArtifactTheme.Block("dark") + "<title>", dark, StringComparison.Ordinal);
            Assert.Contains("--bg:#100f0d", dark, StringComparison.Ordinal);

            var light = await (await Get($"{prefix}/artifacts/{id}/versions/1/frame?theme=light", User)).Content.ReadAsStringAsync();
            Assert.Contains("--bg:#f4efe4", light, StringComparison.Ordinal);
        }

        // /content stays the raw source.
        Assert.DoesNotContain("data-agent42-theme", await (await Get($"/api/conversations/conv/artifacts/{id}/versions/1/content", User)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // AC13: both attachment proxies send nosniff. The artifact blob is octet-stream, so even read through the raw
    // proxy it downloads rather than renders.
    [Fact]
    public async Task AttachmentProxies_SendNoSniff()
    {
        const string User = "art-api-nosniff";
        var id = await CreateArtifact(User, "conv", "html", "<script>1</script>");
        var token = await Share(User, "conv");
        var record = Assert.Single(await Cluster.GrainFactory.GetGrain<IAgentGrain>($"{User}/conv").GetArtifactsAsync());
        var attachmentId = record.Versions[0].AttachmentId;

        foreach (var uri in new[] { $"/api/conversations/conv/attachments/{attachmentId}", $"/api/share/{token}/attachments/{attachmentId}" })
        {
            var response = await Get(uri, User);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        }

        Assert.Equal(id, record.Id);
    }

    // AC14: owner-scoped. Another user asking for the same conversation id reaches their OWN (empty) grain, and an
    // artifact id the conversation does not hold is a 404 whatever else exists. The list hides attachment ids.
    [Fact]
    public async Task Artifacts_AreOwnerScoped()
    {
        var id = await CreateArtifact("art-api-owner", "conv", "html", "<p>mine</p>");
        var otherId = await CreateArtifact("art-api-owner", "other-conv", "html", "<p>other</p>");

        var list = await (await Get("/api/conversations/conv/artifacts", "art-api-owner")).Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(list))
        {
            var only = Assert.Single(doc.RootElement.EnumerateArray());
            Assert.Equal(id, only.GetProperty("id").GetString());
            Assert.Equal(1, only.GetProperty("versions")[0].GetProperty("version").GetInt32());
        }

        Assert.DoesNotContain("attachmentId", list, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("[]", await (await Get("/api/conversations/conv/artifacts", "art-api-intruder")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/conversations/conv/artifacts/{id}/versions/1/content", "art-api-intruder")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/conversations/conv/artifacts/{otherId}/versions/1/content", "art-api-owner")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get("/api/conversations/conv/artifacts/art_nope/versions/1/frame", "art-api-owner")).StatusCode);
    }

    // AC14: the share routes fail closed exactly like the share attachment route: an unknown token, a revoked one,
    // and an artifact the shared conversation does not hold all give the same 404.
    [Fact]
    public async Task ShareArtifacts_FailClosed()
    {
        const string User = "art-api-share-fail";
        var id = await CreateArtifact(User, "conv", "html", "<p>x</p>");
        var otherId = await CreateArtifact(User, "other", "html", "<p>y</p>");
        var token = await Share(User, "conv");

        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/share/{token}/artifacts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/{token}/artifacts/{otherId}/versions/1/frame")).StatusCode);

        foreach (var bad in new[] { "shr_not-a-real-token", token })
        {
            if (bad == token)
            {
                (await Client.SendAsync(Request(HttpMethod.Delete, "/api/conversations/conv/share", User))).EnsureSuccessStatusCode();
            }

            Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/{bad}/artifacts")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/{bad}/artifacts/{id}/versions/1/content")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/{bad}/artifacts/{id}/versions/1/frame")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/share/{bad}/attachments/whatever")).StatusCode);
        }
    }

    // AC11: the public share response keeps the artifact cards and strips the tool calls (ADR 0080's strip).
    [Fact]
    public async Task Share_KeepsArtifacts_StripsToolCalls()
    {
        const string User = "art-api-share-cards";
        var id = await CreateArtifact(User, "conv", "html", "<p>x</p>");
        var token = await Share(User, "conv");

        var owned = await Cluster.GrainFactory.GetGrain<IAgentGrain>($"{User}/conv").GetHistoryAsync();
        Assert.Contains(owned, t => t.ToolCalls is { Count: > 0 });

        var shared = await Client.GetFromJsonAsync<SharedConversationResponse>($"/api/share/{token}");
        Assert.All(shared!.Turns, t => Assert.Null(t.ToolCalls));
        var card = Assert.Single(shared.Turns.Where(t => t.Artifacts is not null).SelectMany(t => t.Artifacts!));
        Assert.Equal(new ArtifactInfo(id, 1, ArtifactKind.Html, "T"), card);
    }
}
