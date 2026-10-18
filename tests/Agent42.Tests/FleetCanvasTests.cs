using System.Text;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// The fleet canvas (ADR 0102): versioned pages behind one gate, with optimistic concurrency on <c>canvas_write</c>,
/// unique-snippet <c>canvas_edit</c>, the artifact caps, pruning at the version cap, write-through to the store
/// outside the workspace prefix, and a <c>canvas.updated</c> event only for a committed write.
/// </summary>
public sealed class FleetCanvasTests : IDisposable
{
    private const string Alice = "alice";
    private const string Workspace = "w1";

    private readonly ScriptedBlobs _blobs = new();
    private readonly FleetStore _store;
    private readonly RecordingFleetSink _events = new();
    private readonly FleetCanvas _canvas;

    public FleetCanvasTests()
    {
        _store = new FleetStore(_blobs);
        _canvas = new FleetCanvas(_store, Alice, Workspace, _events);
    }

    public void Dispose() => _canvas.Dispose();

    private static async Task<string> InvokeAsync(FleetCanvas canvas, string name, AIFunctionArguments arguments)
        => (await canvas.Tools.Single(t => t.Name == name).InvokeAsync(arguments))!.ToString()!;

    private Task<string> WriteAsync(string name, string content, int expectedVersion, string kind = ArtifactKind.Html, string? language = null, string? title = null)
        => InvokeAsync(_canvas, "canvas_write", new()
        {
            ["name"] = name,
            ["kind"] = kind,
            ["content"] = content,
            ["expectedVersion"] = expectedVersion,
            ["title"] = title,
            ["language"] = language,
        });

    private Task<string> EditAsync(string name, string oldText, string newText)
        => InvokeAsync(_canvas, "canvas_edit", new() { ["name"] = name, ["oldText"] = oldText, ["newText"] = newText });

    private Task<string> ReadAsync(string name) => InvokeAsync(_canvas, "canvas_read", new() { ["name"] = name });

    private async Task<string?> StoredAsync(string name, int version)
        => await _store.GetCanvasVersionAsync(Alice, Workspace, name, version) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    [Fact]
    public async Task Write_Create_ThenStaleVersionRefused_ReturnsCurrent()
    {
        Assert.Contains("version 1", await WriteAsync("report", "<p>one</p>", 0, title: "Report"));
        Assert.Contains("version 2", await WriteAsync("report", "<p>two</p>", 1));

        var stale = await WriteAsync("report", "<p>mine</p>", 1);

        Assert.StartsWith("Refused", stale);
        Assert.Contains("version 2", stale);
        Assert.Contains("<p>two</p>", stale);
        Assert.Contains("version 2:\n<p>two</p>", await ReadAsync("report"));
        Assert.Equal([1, 2], Assert.Single(_canvas.Pages).Versions);
        Assert.Equal("Report", _canvas.Pages[0].Title);
    }

    /// <summary>The lost update the canvas exists to prevent: both writers read v1, exactly one of them commits.</summary>
    [Fact]
    public async Task ConcurrentWrites_SameExpectedVersion_ExactlyOneWins()
    {
        await WriteAsync("report", "<p>base</p>", 0);
        _blobs.PutDelay = TimeSpan.FromMilliseconds(30);

        var results = await Task.WhenAll(
            Task.Run(() => WriteAsync("report", "<p>team a</p>", 1)),
            Task.Run(() => WriteAsync("report", "<p>team b</p>", 1)));

        Assert.Single(results, r => r.StartsWith("Stored", StringComparison.Ordinal));
        Assert.Single(results, r => r.StartsWith("Refused", StringComparison.Ordinal));
        Assert.Equal(2, _canvas.Pages[0].LatestVersion);
        Assert.Equal(2, _events.Of(FleetCanvas.UpdatedEvent).Count());
    }

    [Fact]
    public async Task ConcurrentEdits_DifferentSnippets_BothApply()
    {
        await WriteAsync("report", "<h2>Hotels</h2>TODO-hotels\n<h2>Food</h2>TODO-food", 0);
        _blobs.PutDelay = TimeSpan.FromMilliseconds(30);

        var results = await Task.WhenAll(
            Task.Run(() => EditAsync("report", "TODO-hotels", "Pestana Palace")),
            Task.Run(() => EditAsync("report", "TODO-food", "Pasteis de Belem")));

        Assert.All(results, r => Assert.StartsWith("Stored", r));
        Assert.Equal(3, _canvas.Pages[0].LatestVersion);
        var latest = await StoredAsync("report", 3);
        Assert.Contains("Pestana Palace", latest);
        Assert.Contains("Pasteis de Belem", latest);
    }

    [Theory]
    [InlineData("missing", "Refused: oldText does not occur")]
    [InlineData("same", "Refused: oldText occurs more than once")]
    [InlineData("", "oldText is empty")]
    public async Task Edit_AbsentOrAmbiguous_Refused(string oldText, string expected)
    {
        await WriteAsync("notes", "same and same", 0, ArtifactKind.Markdown);

        var result = await EditAsync("notes", oldText, "x");

        Assert.StartsWith(expected, result);
        Assert.Equal(1, _canvas.Pages[0].LatestVersion);
        Assert.Single(_events.Of(FleetCanvas.UpdatedEvent));
    }

    public static TheoryData<string, string, string?, int, string> Limits => new()
    {
        { "big", ArtifactKind.Markdown, null, 0, "-byte limit of a page" },
        { "kind", "pdf", null, 0, "kind must be one of" },
        { "report", ArtifactKind.Markdown, null, 1, "keeps its kind" },
        { "code", ArtifactKind.Code, "Python!", 0, "needs a language" },
        { "Bad/Name", ArtifactKind.Html, null, 0, "is not a valid page name" },
        { "trailing\n", ArtifactKind.Html, null, 0, "is not a valid page name" },
        { "new-page", ArtifactKind.Html, null, 3, "To create it, pass expectedVersion 0" },
        { "page-cap", ArtifactKind.Html, null, 0, "the limit" },
    };

    [Theory]
    [MemberData(nameof(Limits))]
    public async Task Write_Limits_Refused(string name, string kind, string? language, int expectedVersion, string expected)
    {
        await WriteAsync("report", "<p>base</p>", 0);
        if (name == "page-cap")
        {
            for (var i = 1; i < ArtifactLimits.MaxArtifactsPerConversation; i++)
            {
                await WriteAsync($"p{i}", "x", 0);
            }
        }

        var pages = _canvas.Pages.Count;
        var content = name == "big" ? new string('a', ArtifactLimits.MaxContentBytes + 1) : "content";
        var result = await WriteAsync(name, content, expectedVersion, kind, language);

        Assert.Contains(expected, result);
        Assert.Equal(pages, _canvas.Pages.Count);
        Assert.Equal(1, _canvas.Pages[0].LatestVersion);
        Assert.Empty(_events.Of(FleetCanvas.UpdatedEvent).Skip(pages));
    }

    [Fact]
    public async Task Write_PastVersionCap_PrunesOldest()
    {
        for (var v = 0; v < ArtifactLimits.MaxVersionsPerArtifact; v++)
        {
            await WriteAsync("report", $"v{v + 1}", v);
        }

        _blobs.Log.Clear();
        Assert.Contains("version 21", await WriteAsync("report", "v21", ArtifactLimits.MaxVersionsPerArtifact));

        var page = Assert.Single(_canvas.Pages);
        Assert.Equal(Enumerable.Range(2, ArtifactLimits.MaxVersionsPerArtifact), page.Versions);
        Assert.Equal(page.Versions, Assert.Single((await _store.GetCanvasManifestAsync(Alice, Workspace)).Pages).Versions);
        Assert.Null(await StoredAsync("report", 1));
        Assert.Equal("v21", await StoredAsync("report", 21));
        Assert.Equal(
            ["put alice/canvas/w1/report/v21", "put alice/canvas/w1/manifest.json", "delete alice/canvas/w1/report/v1"],
            _blobs.Log);
    }

    [Fact]
    public async Task Write_PublishesCanvasUpdated_OnlyOnCommit()
    {
        await WriteAsync("report", "<p>one</p>", 0, title: "Trip report");
        await WriteAsync("report", "<p>stale</p>", 0);
        await EditAsync("report", "absent", "x");
        await EditAsync("report", "one", "two");

        var events = _events.Of(FleetCanvas.UpdatedEvent).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal("report", events[1].GetProperty("name").GetString());
        Assert.Equal(2, events[1].GetProperty("version").GetInt32());
        Assert.Equal("html", events[1].GetProperty("kind").GetString());
        Assert.Equal("Trip report", events[1].GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("manifest.json")]
    public async Task Write_StoreFailure_LeavesStateAndPublishesNothing(string failingKeySuffix)
    {
        await WriteAsync("report", "<p>one</p>", 0);
        _blobs.FailPutsEndingWith = failingKeySuffix;

        var result = await WriteAsync("report", "<p>two</p>", 1);

        Assert.StartsWith("Could not store 'report'", result);
        Assert.Contains("still at version 1", result);
        Assert.Equal(1, _canvas.Pages[0].LatestVersion);
        Assert.Contains("<p>one</p>", await ReadAsync("report"));
        Assert.Equal([1], Assert.Single((await _store.GetCanvasManifestAsync(Alice, Workspace)).Pages).Versions);
        Assert.Single(_events.Of(FleetCanvas.UpdatedEvent));
    }

    /// <summary>A failed prune delete only orphans a blob: the manifest no longer names it, so the write still commits.</summary>
    [Fact]
    public async Task Write_PruneDeleteFailure_StillCommits()
    {
        for (var v = 0; v < ArtifactLimits.MaxVersionsPerArtifact; v++)
        {
            await WriteAsync("report", $"v{v + 1}", v);
        }

        _blobs.FailDeletes = true;
        Assert.StartsWith("Stored 'report' version 21", await WriteAsync("report", "v21", ArtifactLimits.MaxVersionsPerArtifact));

        Assert.Equal(Enumerable.Range(2, ArtifactLimits.MaxVersionsPerArtifact), Assert.Single((await _store.GetCanvasManifestAsync(Alice, Workspace)).Pages).Versions);
        Assert.Equal(21, _events.Of(FleetCanvas.UpdatedEvent).Count());
    }

    [Fact]
    public async Task Load_RestoresPagesFromStore()
    {
        await WriteAsync("report", "<p>one</p>", 0);
        await WriteAsync("code", "print(1)", 0, ArtifactKind.Code, "python");
        using var followUp = new FleetCanvas(_store, Alice, Workspace, new RecordingFleetSink());

        await followUp.LoadAsync(default);

        Assert.Equal(["report", "code"], followUp.Pages.Select(p => p.Name));
        Assert.Equal("python", followUp.Pages[1].Language);
        Assert.Contains("version 1:\n<p>one</p>", await InvokeAsync(followUp, "canvas_read", new() { ["name"] = "report" }));
        Assert.Contains("version 2", await InvokeAsync(followUp, "canvas_edit", new() { ["name"] = "report", ["oldText"] = "one", ["newText"] = "two" }));
    }

    [Fact]
    public async Task Describe_LatestVersions_Capped()
    {
        Assert.Equal("The canvas has no pages.", _canvas.Describe());
        await WriteAsync("first", "old", 0, ArtifactKind.Markdown);
        await WriteAsync("first", new string('a', 50), 1, ArtifactKind.Markdown);
        await WriteAsync("second", new string('b', 50), 0, ArtifactKind.Markdown);
        await WriteAsync("third", new string('c', 50), 0, ArtifactKind.Markdown);

        var description = _canvas.Describe(maxCharsPerPage: 30, maxTotalChars: 40);

        Assert.StartsWith("Pages on the canvas (3):", description);
        Assert.DoesNotContain("old", description);
        Assert.Contains("--- first (markdown, \"first\", version 2, first 30 of 50 characters)\n" + new string('a', 30) + "\n", description);
        Assert.Contains("--- second (markdown, \"second\", version 1, first 10 of 50 characters)\n" + new string('b', 10) + "\n", description);
        Assert.Contains("--- third (markdown, \"third\", version 1, first 0 of 50 characters)", description);
    }

    /// <summary>The workspace sync deletes what isn't on disk under the workspace prefix; the canvas isn't under it.</summary>
    [Fact]
    public async Task Canvas_SurvivesWorkspaceSync()
    {
        using var workspace = new FleetWorkspace(Workspace, root: Path.Combine(Path.GetTempPath(), "agent42-canvas-" + Guid.NewGuid().ToString("N")));
        await WriteAsync("report", "<p>one</p>", 0);

        await workspace.SyncToStoreAsync(_store, Alice, 1_000_000, 100, default);

        Assert.Equal("<p>one</p>", await StoredAsync("report", 1));
        Assert.Single((await _store.GetCanvasManifestAsync(Alice, Workspace)).Pages);
    }

    [Fact]
    public void Tools_DescriptionsStateTheRules()
    {
        Assert.Equal(["canvas_list", "canvas_read", "canvas_write", "canvas_edit"], _canvas.Tools.Select(t => t.Name));
        var write = _canvas.Tools.Single(t => t.Name == "canvas_write").Description;
        Assert.Contains("expectedVersion 0", write);
        Assert.Contains("refused and returns the current version and content", write);
        Assert.Contains("NO network", write);
        var edit = _canvas.Tools.Single(t => t.Name == "canvas_edit").Description;
        Assert.Contains("EXACTLY ONCE", edit);
    }

    /// <summary><see cref="InMemoryFleetBlobs"/> with a put delay (to overlap concurrent writers), a put that fails
    /// for a key suffix, and an ordered log of puts and deletes.</summary>
    private sealed class ScriptedBlobs : IFleetBlobs
    {
        private readonly InMemoryFleetBlobs _inner = new();

        public TimeSpan PutDelay { get; set; }
        public string? FailPutsEndingWith { get; set; }
        public bool FailDeletes { get; set; }
        public List<string> Log { get; } = [];

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => _inner.GetAsync(key, cancellationToken);

        public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
        {
            if (FailPutsEndingWith is { } suffix && key.EndsWith(suffix, StringComparison.Ordinal))
            {
                throw new IOException("blob service unavailable");
            }

            await Task.Delay(PutDelay, cancellationToken);
            lock (Log)
            {
                Log.Add($"put {key}");
            }

            await _inner.PutAsync(key, content, cancellationToken);
        }

        public Task AppendAsync(string key, byte[] content, CancellationToken cancellationToken = default) => _inner.AppendAsync(key, content, cancellationToken);

        public Task<IReadOnlyList<FleetBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, cancellationToken);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (FailDeletes)
            {
                throw new IOException("blob service unavailable");
            }

            lock (Log)
            {
                Log.Add($"delete {key}");
            }

            return _inner.DeleteAsync(key, cancellationToken);
        }
    }
}
