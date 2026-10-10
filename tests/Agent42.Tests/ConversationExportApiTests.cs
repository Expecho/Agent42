using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The owner's export routes (ADR 0091), end to end through the TestServer — which, like Kestrel, disallows
/// synchronous IO, so every ZIP test here also proves the archive is streamed with the async APIs only (AC10).
/// </summary>
public sealed class ConversationExportApiTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private static readonly byte[] ImageBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    private HttpClient Client => Fixture.Client;

    private async Task<HttpResponseMessage> Get(string uri, string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(IdHeader, user);
        return await Client.SendAsync(request);
    }

    // A turn through the real chat endpoint, so the conversation is indexed exactly as the browser leaves it.
    private async Task Chat(string user, string conversation, string message, IReadOnlyList<string>? attachmentIds = null, bool ephemeral = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/chat/{conversation}")
        {
            Content = JsonContent.Create(new ChatRequest(message, null, AttachmentIds: attachmentIds, Ephemeral: ephemeral)),
        };
        request.Headers.Add(IdHeader, user);
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync();
    }

    private async Task<string> CreateArtifactWithTwoVersions(string user, string conversation)
    {
        await Chat(user, conversation, FakeChatClient.CallToolPrompt("create_artifact", new { kind = "html", title = "Page", content = "<p>v1</p>" }));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/{conversation}");
        var id = (await agent.GetArtifactsAsync()).Single().Id;
        await Chat(user, conversation, FakeChatClient.CallToolPrompt("update_artifact", new { artifactId = id, content = "<p>v2</p>" }));
        return id;
    }

    private static Task<AttachmentRef> SaveImage(string user, string conversation, string fileName)
        => new InMemoryAttachmentStore().SaveAsync($"{user}/{conversation}", fileName, "image/png", ImageBytes);

    private static async Task<ZipArchive> ReadZip(HttpResponseMessage response)
    {
        var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadEntryBytes(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // AC1: Markdown, as an attachment, with the turns and the tool call folded.
    [Fact]
    public async Task Export_Markdown_ForOwner()
    {
        const string User = "export-md";
        await Chat(User, "conv", "Hello there");
        await CreateArtifactWithTwoVersions(User, "conv");

        using var response = await Get("/api/conversations/conv/export", User);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}-hello-there\.md$", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        var md = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("# Hello there", md, StringComparison.Ordinal);
        Assert.Contains("## You", md, StringComparison.Ordinal);
        Assert.Contains("## Agent 42", md, StringComparison.Ordinal);
        Assert.Contains("<summary>Tool call: create_artifact</summary>", md, StringComparison.Ordinal);
        Assert.Contains("- Artifact: **Page** (html, v1)", md, StringComparison.Ordinal);
    }

    // AC2: the turns are /messages exactly, tool calls with their results included; plus the conversation metadata.
    [Fact]
    public async Task Export_Json_MatchesMessagesProjection()
    {
        const string User = "export-json";
        await Chat(User, "conv", "Hello");
        await CreateArtifactWithTwoVersions(User, "conv");

        var messages = JsonDocument.Parse(await (await Get("/api/conversations/conv/messages", User)).Content.ReadAsStringAsync());
        using var response = await Get("/api/conversations/conv/export?format=json", User);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var export = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(ConversationExportDocument.SchemaName, export.GetProperty("schema").GetString());
        Assert.Equal("conv", export.GetProperty("conversation").GetProperty("id").GetString());
        Assert.Equal("perChat", export.GetProperty("historyMode").GetString());
        Assert.Equal(TestSiloConfigurator.DefaultModel, export.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, export.GetProperty("agent").ValueKind);
        Assert.True(export.TryGetProperty("sharedState", out _));
        Assert.False(export.TryGetProperty("files", out _));
        Assert.True(JsonElement.DeepEquals(messages.RootElement, export.GetProperty("turns")));

        var call = export.GetProperty("turns").EnumerateArray()
            .Where(t => t.TryGetProperty("toolCalls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            .SelectMany(t => t.GetProperty("toolCalls").EnumerateArray())
            .First(c => c.GetProperty("toolName").GetString() == "create_artifact");
        Assert.False(string.IsNullOrEmpty(call.GetProperty("toolResult").GetString()));

        var artifact = Assert.Single(export.GetProperty("artifacts").EnumerateArray());
        Assert.Equal(2, artifact.GetProperty("versions").GetArrayLength());
        Assert.False(artifact.GetProperty("versions")[0].TryGetProperty("attachmentId", out _));
    }

    // AC11: reasoning is stripped before storage (ADR 0071), so no export carries it.
    [Fact]
    public async Task Export_Json_HasNoReasoningField()
    {
        const string User = "export-reasoning";
        await Chat(User, "conv", "Hello");

        var json = await (await Get("/api/conversations/conv/export?format=json", User)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("reasoning", json, StringComparison.OrdinalIgnoreCase);
    }

    // AC3: the archive holds both documents, the image's bytes, every artifact version's source and a manifest.
    [Fact]
    public async Task Export_Zip_BundlesAttachmentsAndArtifactVersions()
    {
        const string User = "export-zip";
        var image = await SaveImage(User, "conv", "cat.png");
        await Chat(User, "conv", "Look at this", [image.Id]);
        var artifactId = await CreateArtifactWithTwoVersions(User, "conv");

        using var response = await Get("/api/conversations/conv/export?format=zip", User);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith("-look-at-this.zip", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'), StringComparison.Ordinal);
        using var zip = await ReadZip(response);

        var imagePath = $"attachments/{image.Id}-cat.png";
        Assert.Equal(ImageBytes, ReadEntryBytes(zip, imagePath));
        Assert.Equal("<p>v1</p>", ReadEntry(zip, $"artifacts/{artifactId}/v1.html"));
        Assert.Equal("<p>v2</p>", ReadEntry(zip, $"artifacts/{artifactId}/v2.html"));
        Assert.Contains($"![cat.png]({imagePath})", ReadEntry(zip, "conversation.md"), StringComparison.Ordinal);

        var json = JsonDocument.Parse(ReadEntry(zip, "conversation.json")).RootElement;
        Assert.Equal(imagePath, json.GetProperty("files").GetProperty("attachments").GetProperty(image.Id).GetString());
        Assert.Equal($"artifacts/{artifactId}/v2.html", json.GetProperty("files").GetProperty("artifacts").GetProperty($"{artifactId}/v2").GetString());

        var manifest = JsonDocument.Parse(ReadEntry(zip, "manifest.json")).RootElement;
        Assert.Equal("conv", Assert.Single(manifest.GetProperty("conversations").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(0, manifest.GetProperty("missing").GetArrayLength());
    }

    // AC4: a blob that is gone is listed, not fatal, and nothing links to it.
    [Fact]
    public async Task Export_Zip_MissingBlob_IsListedNotFatal()
    {
        const string User = "export-missing";
        var ghost = new AttachmentRef("ghost", $"{User}/conv/ghost", "image/png", "ghost.png");
        await Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(User).TouchAsync("conv", "Ghost");
        await foreach (var _ in Cluster.GrainFactory.GetGrain<IAgentGrain>($"{User}/conv")
            .ChatAsync("see", TestSiloConfigurator.DefaultModel, [ghost]))
        {
        }

        using var response = await Get("/api/conversations/conv/export?format=zip", User);
        using var zip = await ReadZip(response);

        var manifest = JsonDocument.Parse(ReadEntry(zip, "manifest.json")).RootElement;
        Assert.Equal("attachments/ghost-ghost.png", Assert.Single(manifest.GetProperty("missing").EnumerateArray()).GetString());
        Assert.Null(zip.GetEntry("attachments/ghost-ghost.png"));
        Assert.Contains("_[image: ghost.png]_", ReadEntry(zip, "conversation.md"), StringComparison.Ordinal);
    }

    // AC7: no title or file name can put a separator or `..` into an entry name or the download name.
    [Fact]
    public async Task Export_Zip_EntryNamesAreSafe()
    {
        const string User = "export-names";
        var image = await SaveImage(User, "conv", "../../evil\\x.png");
        await Chat(User, "conv", "../../etc/passwd", [image.Id]);

        using var response = await Get("/api/conversations/conv/export?format=zip", User);
        var fileName = response.Content.Headers.ContentDisposition!.FileName!.Trim('"');
        Assert.EndsWith("-etc-passwd.zip", fileName, StringComparison.Ordinal);
        using var zip = await ReadZip(response);

        Assert.All(zip.Entries, e =>
        {
            Assert.DoesNotContain("..", e.FullName, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", e.FullName, StringComparison.Ordinal);
            Assert.False(e.FullName.StartsWith('/'));
        });
        Assert.NotNull(zip.GetEntry($"attachments/{image.Id}-evil_x.png"));
    }

    // AC5: another user's chat, an incognito chat and an id never used are all a 404 — the index is the gate.
    [Theory]
    [InlineData("other")]
    [InlineData("ephemeral")]
    [InlineData("unknown")]
    public async Task Export_Unlisted_Is404(string kind)
    {
        var user = $"export-404-{kind}";
        switch (kind)
        {
            case "other":
                await Chat("export-404-owner", "conv", "mine");
                break;
            case "ephemeral":
                await Chat(user, "conv", "secret", ephemeral: true);
                break;
        }

        foreach (var format in new[] { "markdown", "json", "zip" })
        {
            using var response = await Get($"/api/conversations/conv/export?format={format}", user);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    // AC6.
    [Fact]
    public async Task Export_UnknownFormat_Is400()
    {
        using var response = await Get("/api/conversations/conv/export?format=pdf", "export-400");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
    }

    // ————— bulk export (AC8, AC9) —————

    private sealed record Part(IReadOnlyList<string> Ids, int Number, int Parts, int Total, string? Next, JsonElement Manifest, ZipArchive Zip);

    private async Task<Part> GetPart(string user, string? after)
    {
        var response = await Get("/api/export" + (after is null ? "" : $"?after={Uri.EscapeDataString(after)}"), user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        int Header(string name) => int.Parse(response.Headers.GetValues(name).Single(), System.Globalization.CultureInfo.InvariantCulture);
        var next = response.Headers.TryGetValues("X-Export-Next", out var values) ? values.Single() : null;
        var zip = await ReadZip(response);
        var manifest = JsonDocument.Parse(ReadEntry(zip, "manifest.json")).RootElement;
        var ids = manifest.GetProperty("conversations").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();
        return new Part(ids, Header("X-Export-Part"), Header("X-Export-Parts"), Header("X-Export-Total"), next, manifest, zip);
    }

    private async Task<List<string>> Seed(string user, int count)
    {
        var index = Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(user);
        var ids = Enumerable.Range(1, count).Select(i => $"c-{i:D3}").ToList();
        foreach (var id in ids)
        {
            await index.TouchAsync(id, $"Chat {id}");
        }

        return ids;
    }

    [Fact]
    public async Task BulkExport_WalksEveryConversationOnce()
    {
        const string User = "bulk-walk";
        var ids = await Seed(User, ExportLimits.BulkBatchSize + 1);

        var first = await GetPart(User, null);
        Assert.Equal((1, 2, ids.Count), (first.Number, first.Parts, first.Total));
        Assert.Equal(ExportLimits.BulkBatchSize, first.Ids.Count);
        Assert.Equal(first.Ids[^1], first.Next);
        Assert.Contains(first.Zip.Entries, e => System.Text.RegularExpressions.Regex.IsMatch(
            e.FullName, @"^conversations/\d{4}-\d{2}-\d{2}-chat-c-001-c-001/conversation\.md$"));

        var second = await GetPart(User, first.Next);
        Assert.Equal((2, 2), (second.Number, second.Parts));
        Assert.Null(second.Next);

        Assert.Equal(ids, first.Ids.Concat(second.Ids).ToList());
    }

    // A conversation added BEFORE the cursor (it sorts below the last exported id) does not shift the walk: no
    // duplicate, no skip — the failure offset paging would have.
    [Fact]
    public async Task BulkExport_KeysetSurvivesInsertMidWalk()
    {
        const string User = "bulk-insert-before";
        var ids = await Seed(User, ExportLimits.BulkBatchSize + 1);

        var first = await GetPart(User, null);
        await Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(User).TouchAsync("a-new", "Inserted");
        var second = await GetPart(User, first.Next);

        var walked = first.Ids.Concat(second.Ids).ToList();
        Assert.Equal(walked.Count, walked.Distinct().Count());
        Assert.Equal(ids, walked);
    }

    [Fact]
    public async Task BulkExport_InsertAfterCursorAppears()
    {
        const string User = "bulk-insert-after";
        await Seed(User, ExportLimits.BulkBatchSize + 1);

        var first = await GetPart(User, null);
        await Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(User).TouchAsync("z-new", "Inserted");
        var second = await GetPart(User, first.Next);

        Assert.Equal(["c-026", "z-new"], second.Ids);
    }

    [Fact]
    public async Task BulkExport_DeleteMidWalkIsSkippedQuietly()
    {
        const string User = "bulk-delete";
        await Seed(User, ExportLimits.BulkBatchSize + 2);

        var first = await GetPart(User, null);
        await Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(User).RemoveAsync("c-026");
        var second = await GetPart(User, first.Next);

        Assert.Equal(["c-027"], second.Ids);
        Assert.Equal(0, second.Manifest.GetProperty("errors").GetArrayLength());
        Assert.Null(second.Next);
    }

    // AC9: only the caller's listed conversations — never an incognito chat, never another user's.
    [Fact]
    public async Task BulkExport_IsOwnerScopedAndSkipsEphemeral()
    {
        const string User = "bulk-scope";
        await Chat(User, "kept", "Kept chat");
        await Chat(User, "secret", "Incognito", ephemeral: true);
        await Chat("bulk-scope-other", "theirs", "Not yours");

        var part = await GetPart(User, null);

        Assert.Equal(["kept"], part.Ids);
        Assert.Equal(1, part.Total);
        Assert.Contains(part.Zip.Entries, e => e.FullName.EndsWith("-kept/conversation.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulkExport_Empty()
    {
        var part = await GetPart("bulk-empty", null);

        Assert.Equal("manifest.json", Assert.Single(part.Zip.Entries).FullName);
        Assert.Equal((1, 1, 0), (part.Number, part.Parts, part.Total));
        Assert.Null(part.Next);
    }
}
