using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.TestHost;

namespace Agent42.Tests;

/// <summary>
/// The <c>/api/skills</c> contract (ADR 0049), hosted in-memory (TestServer) against a real Orleans test
/// cluster, so upload → normalize → store → grain → picker is exercised end to end. Mirrors
/// <see cref="McpServersApiTests"/>.
///
/// <para>The two properties that only this level can prove: an upload reaches <c>GET /api/tools</c> as a
/// <c>skill:{name}</c> key (and SHADOWS a shipped skill of the same name), and the ADR 0049 auto-enable
/// appends to a materialised default selection while leaving a null one null — which is the difference
/// between the headline user story working and silently doing nothing.</para>
/// </summary>
public sealed class UserSkillsApiTests(UserSkillsApiFixture fixture) : SharedClusterTests<UserSkillsApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    // The host, the cluster and the client are all the fixture's, deployed once for the class.
    private HttpClient Client => Fixture.Client;

    private readonly List<string> _uploadedIds = [];

    protected override Task OnDisposeAsync()
    {
        foreach (var id in _uploadedIds)
        {
            try { Directory.Delete(Path.Combine(UserSkillProvider.MaterializationRoot, id), recursive: true); }
            catch { /* best effort */ }
        }

        return Task.CompletedTask;
    }

    // AC1: a standalone SKILL.md installs through the endpoint.
    [Fact]
    public async Task Upload_BareMarkdown_Installs()
    {
        var payload = await UploadAsync("user-md", "SKILL.md", Bytes(ZipTestData.Frontmatter("mynotes", "Takes notes.")));

        var skill = payload.GetProperty("skills").EnumerateArray().Single();
        Assert.Equal("mynotes", skill.GetProperty("name").GetString());
        Assert.Equal("Takes notes.", skill.GetProperty("description").GetString());
        Assert.Empty(payload.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Upload_MultiSkillZip_InstallsEach()
    {
        var payload = await UploadAsync("user-zip", "skills.zip", ZipTestData.Zip(
            ("alpha/SKILL.md", ZipTestData.Frontmatter("alpha", "First.")),
            ("alpha/references/notes.md", "NOTES"),
            ("beta/SKILL.md", ZipTestData.Frontmatter("beta", "Second."))));

        Assert.Equal(["alpha", "beta"], Names(payload).Order());
    }

    [Fact]
    public async Task Upload_GarbageZip_Is400()
        => Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PostAsync("user-garbage", "kit.zip", [0x00, 0x01, 0x02, 0x03])).StatusCode);

    // AC6 at the endpoint: a hostile frontmatter name is rejected with a 400 (and, proven a layer down in
    // SkillPackageTests, before anything is written).
    [Fact]
    public async Task Upload_TraversalName_Is400()
    {
        var response = await PostAsync(
            "user-traversal", "SKILL.md", Bytes(ZipTestData.Frontmatter("../../../../foo", "Hostile.")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Upload_Oversize_Is400()
    {
        var response = await PostAsync("user-oversize", "SKILL.md", new byte[(10 * 1024 * 1024) + 1]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("10 MB", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // AC4 at the endpoint: the skipped-entry report is what makes "silently drop, keep the rest" acceptable.
    // Without it a user whose scripts/ folder vanished has no way to learn that it was deliberate.
    [Fact]
    public async Task Upload_ReportsSkippedEntries()
    {
        var payload = await UploadAsync("user-skipped", "kit.zip", ZipTestData.Zip(
            ("kit/SKILL.md", ZipTestData.Frontmatter("kit", "Fine.")),
            ("kit/scripts/run.sh", "echo hi"),
            ("kit/logo.png", "PNG")));

        var skipped = payload.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(skipped, s => s.Contains("run.sh", StringComparison.Ordinal));
        Assert.Contains(skipped, s => s.Contains("logo.png", StringComparison.Ordinal));
    }

    // AC8: an uploaded skill appears in the picker as skill:{name}.
    [Fact]
    public async Task Tools_IncludesUploadedSkill()
    {
        await UploadAsync("user-tools", "SKILL.md", Bytes(ZipTestData.Frontmatter("invoices", "Reads an invoice.")));

        var keys = await ToolKeysAsync("user-tools");

        Assert.Contains(ToolCatalog.SkillKey("invoices"), keys);
        Assert.DoesNotContain(ToolCatalog.SkillKey("websearch"), keys);   // shipped skills are companions: no row
        Assert.DoesNotContain(ToolCatalog.Skills, keys);            // the bare wildcard is never a checkbox
    }

    // AC10 + the shadowing half of AC8: an upload named after a shipped skill replaces it — ONE key, not
    // two — and the response warns, because shadowing can remove a control rather than only adding one (the
    // shipped `websearch` skill is where the ADR 0011 prompt-injection mitigation lives).
    [Fact]
    public async Task Upload_ShadowingShippedSkill_Warns()
    {
        var payload = await UploadAsync(
            "user-shadow", "SKILL.md", Bytes(ZipTestData.Frontmatter("websearch", "My own web search rules.")));

        var warning = Assert.Single(payload.GetProperty("warnings").EnumerateArray()).GetString();
        Assert.Contains("websearch", warning!, StringComparison.Ordinal);

        // The shadowing upload is still the web search tool's companion, so it gets no picker row either.
        var keys = await ToolKeysAsync("user-shadow");
        Assert.DoesNotContain(ToolCatalog.SkillKey("websearch"), keys);
    }

    // AC11: the ADR 0049 auto-enable. A user who has ever pressed Save in the tools picker has a
    // MATERIALISED selection, and ADR 0048's "all is the full explicit list" rule would leave a
    // later-added skill silently OFF — which for an upload means the headline user story does nothing at
    // all. A null selection is left null: it already means all.
    [Fact]
    public async Task Upload_AutoEnables_OnMaterialisedSelection()
    {
        const string user = "user-autoenable";
        var profile = Cluster.GrainFactory.GetGrain<IUserProfileGrain>(user);
        await profile.SaveAsync(UserProfile.Empty with { EnabledTools = [ToolCatalog.WebSearch] });

        await UploadAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("invoices", "Reads an invoice.")));

        Assert.Equal(
            [ToolCatalog.WebSearch, ToolCatalog.SkillKey("invoices")],
            (await profile.GetAsync()).EnabledTools);
    }

    [Fact]
    public async Task Upload_LeavesANullSelectionNull()
    {
        const string user = "user-nullselection";
        await UploadAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("invoices", "Reads an invoice.")));

        Assert.Null((await Cluster.GrainFactory.GetGrain<IUserProfileGrain>(user).GetAsync()).EnabledTools);
    }

    // AC17 at the endpoint: a re-upload of the same name replaces rather than duplicating, and the picker
    // shows one key. The id changes, which is what makes the materialized directory immutable.
    [Fact]
    public async Task Upload_SameName_Replaces()
    {
        const string user = "user-replace";
        var first = await UploadAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("mynotes", "First version.")));
        var second = await UploadAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("mynotes", "Second version.")));

        Assert.NotEqual(Ids(first).Single(), Ids(second).Single());

        var listed = Assert.Single(await ListAsync(user));
        Assert.Equal("Second version.", listed.GetProperty("description").GetString());
        Assert.Single(await ToolKeysAsync(user), k => k == ToolCatalog.SkillKey("mynotes"));
    }

    // AC18: delete removes the grain row, the PACKAGE and the picker entry. The blob assertion is the
    // point — the store is probed directly, because "the row is gone" would pass just as well against an
    // endpoint that leaked every package it was asked to delete.
    [Fact]
    public async Task Delete_RemovesFromCatalog()
    {
        const string user = "user-delete";
        var payload = await UploadAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("invoices", "Reads an invoice.")));
        var id = Ids(payload).Single();
        var packages = new InMemorySkillPackageStore();   // static backing store — the same one the endpoint used
        Assert.NotNull(await packages.ReadAsync(user, id));

        var response = await Client.SendAsync(Request(HttpMethod.Delete, $"/api/skills/{id}", user));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ListAsync(user));
        Assert.DoesNotContain(ToolCatalog.SkillKey("invoices"), await ToolKeysAsync(user));
        Assert.Null(await packages.ReadAsync(user, id));
    }

    // The supersede cleanup on a REPLACE — the one place a leak accumulates silently, because replacing a
    // skill is the expected way to edit one. The grain hands back the row it replaced precisely so the
    // endpoint can delete the old package; nothing else can, since only the old id addresses it.
    [Fact]
    public async Task Upload_SameName_DeletesTheSupersededPackage()
    {
        const string user = "user-replace-cleanup";
        var first = await UploadAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("mynotes", "First version.")));
        var oldId = Ids(first).Single();
        var packages = new InMemorySkillPackageStore();
        Assert.NotNull(await packages.ReadAsync(user, oldId));

        var second = await UploadAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("mynotes", "Second version.")));
        var newId = Ids(second).Single();

        Assert.NotEqual(oldId, newId);
        Assert.Null(await packages.ReadAsync(user, oldId));      // the superseded package is gone
        Assert.NotNull(await packages.ReadAsync(user, newId));   // the replacement is there
    }

    [Fact]
    public async Task Delete_UnknownId_Is404()
        => Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.SendAsync(Request(HttpMethod.Delete, "/api/skills/nope", "user-delete-unknown"))).StatusCode);

    // The per-user install cap (SkillToolLimits). The 10 MB per-upload limit bounds a single request, not
    // what a user can accumulate on the container filesystem across many of them — and that filesystem is
    // the one thing in this feature with no other bound. The grain is seeded directly so the test is about
    // the cap rather than about 25 round-trips.
    [Fact]
    public async Task Upload_BeyondTheInstallCap_Is400()
    {
        const string user = "user-cap";
        var grain = Cluster.GrainFactory.GetGrain<IUserSkillsGrain>(user);
        for (var i = 0; i < SkillToolLimits.MaxInstalledSkillsPerUser; i++)
        {
            await grain.SaveAsync(new StoredUserSkill($"id-{i}", $"seeded{i}", "Seeded.", ["SKILL.md"], DateTimeOffset.UtcNow));
        }

        var response = await PostAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("onetoomany", "One too many.")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at most", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ...but REPLACING an existing skill is always allowed, even at the cap: it is counted against the
    // names that would REMAIN, not the ones being added. Otherwise a user at the cap could not fix a typo
    // in a skill they already have without deleting something first.
    [Fact]
    public async Task Upload_ReplacingAtTheCap_IsAllowed()
    {
        const string user = "user-cap-replace";
        var grain = Cluster.GrainFactory.GetGrain<IUserSkillsGrain>(user);
        for (var i = 0; i < SkillToolLimits.MaxInstalledSkillsPerUser; i++)
        {
            await grain.SaveAsync(new StoredUserSkill($"id-{i}", $"seeded{i}", "Seeded.", ["SKILL.md"], DateTimeOffset.UtcNow));
        }

        var payload = await UploadAsync(user, "SKILL.md", Bytes(ZipTestData.Frontmatter("seeded0", "Replaced.")));

        Assert.Equal("Replaced.", payload.GetProperty("skills").EnumerateArray().Single().GetProperty("description").GetString());
    }

    // AC16: skills are per-user. The overlay is keyed on the Easy-Auth principal, so one user's upload is
    // invisible to another — in the list AND in the picker.
    [Fact]
    public async Task Skills_AreScopedToTheUploader()
    {
        await UploadAsync("user-owner", "SKILL.md", Bytes(ZipTestData.Frontmatter("invoices", "Reads an invoice.")));

        Assert.Empty(await ListAsync("user-other"));
        Assert.DoesNotContain(ToolCatalog.SkillKey("invoices"), await ToolKeysAsync("user-other"));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static HttpRequestMessage Request(HttpMethod method, string uri, string principalId)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, principalId);
        return request;
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private async Task<HttpResponseMessage> PostAsync(string principalId, string fileName, byte[] content)
    {
        var request = Request(HttpMethod.Post, "/api/skills", principalId);
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        request.Content = form;
        return await Client.SendAsync(request);
    }

    private async Task<JsonElement> UploadAsync(string principalId, string fileName, byte[] content)
    {
        var response = await PostAsync(principalId, fileName, content);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var payload = doc.RootElement.Clone();
        _uploadedIds.AddRange(Ids(payload));
        return payload;
    }

    private static IEnumerable<string> Ids(JsonElement payload)
        => payload.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("id").GetString()!);

    private static IEnumerable<string> Names(JsonElement payload)
        => payload.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("name").GetString()!);

    private async Task<IReadOnlyList<JsonElement>> ListAsync(string principalId)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/skills", principalId));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. doc.RootElement.GetProperty("skills").EnumerateArray().Select(e => e.Clone())];
    }

    private async Task<IReadOnlyList<string>> ToolKeysAsync(string principalId)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/tools", principalId));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. doc.RootElement.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)];
    }
}
