using System.ComponentModel;
using System.Text;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The shared canvas of a fleet task (ADR 0102): named, versioned pages (html, svg, markdown, code) that every agent
/// of the task can write, Copilot agents included, and that the user watches live in the artifact pane on
/// <c>/fleet/</c>. It lives beside <see cref="FleetWorkspace"/> and shares its <see cref="FleetWorkspace.WorkspaceId"/>,
/// so a follow-up sees the pages of the tasks before it.
///
/// <para><b>No update is ever lost.</b> Writes take turns behind one gate, and <c>canvas_write</c> carries the version
/// it was based on: a stale one is refused with the current content, so the agent merges and retries.
/// <c>canvas_edit</c> needs no version, because it replaces one exact snippet atomically, so sub-teams editing
/// different parts of one page don't conflict.</para>
///
/// <para><b>Write-through, outside the workspace prefix.</b> Every write puts the version blob, then the manifest, and
/// only then changes the in-memory state and publishes <c>canvas.updated</c>. A failed put leaves the previous state and
/// publishes nothing. The canvas isn't on disk (a Copilot shell would get around the gate) and isn't under the
/// workspace prefix (the workspace sync would delete it).</para>
/// </summary>
public sealed class FleetCanvas : IDisposable
{
    public const string UpdatedEvent = "canvas.updated";

    /// <summary>The four tool names, reserved in the fleet tool catalog so no MCP tool can take one.</summary>
    public static readonly IReadOnlyList<string> ToolNames = ["canvas_list", "canvas_read", "canvas_write", "canvas_edit"];

    private readonly IFleetStore _store;
    private readonly string _userId;
    private readonly IFleetEventSink _events;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Swapped whole under the gate, so a read without the gate always sees a manifest and the contents that match it.
    private volatile CanvasState _state = CanvasState.Empty;

    public FleetCanvas(IFleetStore store, string userId, string workspaceId, IFleetEventSink events)
    {
        _store = store;
        _userId = userId;
        WorkspaceId = workspaceId;
        _events = events;

        // The model obeys these descriptions, not the prompt (a user's prompt override replaces the prompt text), so
        // each one carries its own rules.
        Tools =
        [
            AIFunctionFactory.Create(List, "canvas_list",
                "List the pages on the task's shared canvas, with their kind, title and latest version. The user watches "
                + "the canvas live, so it is the place for output meant to be looked at: a report, a page, a diagram."),
            AIFunctionFactory.Create(Read, "canvas_read",
                "Read the latest content of a canvas page, with its version number. Read a page before you change it "
                + "with canvas_write, and pass that version as expectedVersion."),
            AIFunctionFactory.Create(WriteAsync, "canvas_write",
                "Create a canvas page, or replace its whole content with a new version. To CREATE a page pass "
                + "expectedVersion 0. To REPLACE one pass the version you last read; when another agent changed the page "
                + "since, the write is refused and returns the current version and content: merge your change into it "
                + "and call canvas_write again with that version. A page keeps its kind. Kinds: html (a full page; it "
                + "runs sandboxed with NO network, so put CSS, script and data inline; libraries only from "
                + "https://cdnjs.cloudflare.com; style with the CSS variables --bg, --ink, --line, --amber so it follows "
                + "the user's theme), svg, markdown, code. At most 64 KB, never truncated: larger content is refused. To "
                + "change only part of a page, prefer canvas_edit."),
            AIFunctionFactory.Create(EditAsync, "canvas_edit",
                "Change part of a canvas page: replace oldText with newText in its latest version. oldText must occur "
                + "EXACTLY ONCE in the page, so include enough surrounding text to make it unique; when it is absent or "
                + "occurs more than once the edit is refused with the reason and the current version. No version is "
                + "needed, so several agents can edit different parts of one page at the same time."),
        ];
    }

    public string WorkspaceId { get; }
    public IReadOnlyList<AIFunction> Tools { get; }
    public IReadOnlyList<FleetCanvasPage> Pages => _state.Manifest.Pages;

    /// <summary>Reads the stored manifest and each page's latest content. Always called when a run starts, so a
    /// follow-up (which reuses the workspace id) sees the earlier pages.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await _store.GetCanvasManifestAsync(_userId, WorkspaceId, cancellationToken);
            var latest = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var page in manifest.Pages.Where(p => p.LatestVersion > 0))
            {
                var bytes = await _store.GetCanvasVersionAsync(_userId, WorkspaceId, page.Name, page.LatestVersion, cancellationToken);
                latest[page.Name] = bytes is null ? "" : Encoding.UTF8.GetString(bytes);
            }

            _state = new CanvasState(manifest, latest);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The latest version of each page, for the judge, in the shape of <see cref="FleetWorkspace.Describe"/>. Each page
    /// is cut off after <paramref name="maxCharsPerPage"/>, and contents stop after <paramref name="maxTotalChars"/>.
    /// </summary>
    public string Describe(int maxCharsPerPage = 8_000, int maxTotalChars = 20_000)
    {
        var state = _state;
        if (state.Manifest.Pages.Count == 0)
        {
            return "The canvas has no pages.";
        }

        var description = new StringBuilder($"Pages on the canvas ({state.Manifest.Pages.Count}):\n");
        var remaining = maxTotalChars;
        foreach (var page in state.Manifest.Pages)
        {
            var content = state.Content(page.Name);
            var shown = content[..Math.Min(content.Length, Math.Max(0, Math.Min(maxCharsPerPage, remaining)))];
            remaining -= shown.Length;
            var cut = shown.Length < content.Length ? $", first {shown.Length:N0} of {content.Length:N0} characters" : "";
            description.Append($"\n--- {page.Name} ({page.Kind}, \"{page.Title}\", version {page.LatestVersion}{cut})\n{shown}\n");
        }

        return description.ToString();
    }

    public void Dispose() => _gate.Dispose();

    private string List()
    {
        var pages = _state.Manifest.Pages;
        return pages.Count == 0
            ? "The canvas has no pages yet. Create one with canvas_write and expectedVersion 0."
            : string.Join("\n", pages.Select(p => $"{p.Name} ({p.Kind}, \"{p.Title}\", version {p.LatestVersion})"));
    }

    private string Read([Description("The page name, for example 'report'.")] string name)
    {
        var state = _state;
        return state.Find(name) is { } page
            ? $"Page '{page.Name}' ({page.Kind}, \"{page.Title}\"), version {page.LatestVersion}:\n{state.Content(page.Name)}"
            : UnknownPage(state, name);
    }

    private async Task<string> WriteAsync(
        [Description("The page name: lowercase letters, digits and '-', for example 'trip-report'.")] string name,
        [Description("One of: html, svg, markdown, code. An existing page keeps its kind.")] string kind,
        [Description("The complete content (not a diff). At most 64 KB.")] string content,
        [Description("0 to create the page; otherwise the version you last read.")] int expectedVersion,
        [Description("A short human title. Only needed when creating, or to change it.")] string? title = null,
        [Description("For kind=code, when creating: the language, e.g. python, typescript, csharp.")] string? language = null,
        CancellationToken cancellationToken = default)
    {
        if ((ValidateName(name) ?? ValidateContent(content)) is { } problem)
        {
            return problem;
        }

        return await UnderGateAsync(async () =>
        {
            var state = _state;
            var page = state.Find(name);
            var refusal = page is null
                ? ValidateCreate(state, name, kind, language, expectedVersion)
                : ValidateReplace(state, page, kind, expectedVersion);
            if (refusal is not null)
            {
                return refusal;
            }

            var target = page is null
                ? new FleetCanvasPage { Name = name, Kind = kind, Title = CleanTitle(title, name), Language = kind == ArtifactKind.Code ? language : null }
                : page with { Title = title is null ? page.Title : CleanTitle(title, name) };
            return await CommitAsync(state, target, content, cancellationToken);
        }, cancellationToken);
    }

    private async Task<string> EditAsync(
        [Description("The page name.")] string name,
        [Description("The exact text to replace. It must occur exactly once in the page.")] string oldText,
        [Description("The text to put in its place; empty deletes oldText.")] string newText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return "oldText is empty. Pass the exact text to replace, unique within the page.";
        }

        return await UnderGateAsync(async () =>
        {
            var state = _state;
            if (state.Find(name) is not { } page)
            {
                return UnknownPage(state, name);
            }

            var current = state.Content(page.Name);
            var at = current.IndexOf(oldText, StringComparison.Ordinal);
            if (at < 0)
            {
                return $"Refused: oldText does not occur in '{name}' (version {page.LatestVersion}). Call canvas_read to see the current content.";
            }

            if (current.IndexOf(oldText, at + 1, StringComparison.Ordinal) >= 0)
            {
                return $"Refused: oldText occurs more than once in '{name}' (version {page.LatestVersion}). Include more surrounding text so it is unique.";
            }

            var edited = string.Concat(current.AsSpan(0, at), newText ?? "", current.AsSpan(at + oldText.Length));
            return ValidateContent(edited) ?? await CommitAsync(state, page, edited, cancellationToken);
        }, cancellationToken);
    }

    // Under the gate: the version blob, then the manifest, then the in-memory state, then the event. Past the cap the
    // oldest version is pruned AFTER the manifest stops naming it, so the manifest never points at a missing blob.
    private async Task<string> CommitAsync(CanvasState state, FleetCanvasPage page, string content, CancellationToken cancellationToken)
    {
        var version = page.LatestVersion + 1;
        var versions = page.Versions.Append(version).ToList();
        var pruned = versions.Count > ArtifactLimits.MaxVersionsPerArtifact ? versions[0] : 0;
        var committed = page with { Versions = pruned > 0 ? versions[1..] : versions };
        var pages = state.Find(page.Name) is null
            ? [.. state.Manifest.Pages, committed]
            : state.Manifest.Pages.Select(p => p.Name == page.Name ? committed : p).ToList();
        var manifest = new FleetCanvasManifest { Pages = pages };

        try
        {
            await _store.PutCanvasVersionAsync(_userId, WorkspaceId, page.Name, version, Encoding.UTF8.GetBytes(content), cancellationToken);
            await _store.PutCanvasManifestAsync(_userId, WorkspaceId, manifest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Could not store '{page.Name}': {ex.Message}. Nothing was changed, the page is still at version {page.LatestVersion}.";
        }

        _state = state.With(manifest, page.Name, content);
        if (pruned > 0)
        {
            await PruneAsync(page.Name, pruned, cancellationToken);
        }

        // The literal, not UpdatedEvent: FleetJs_HandlesEveryPublishedEventType finds event types by Publish("…").
        _events.Publish("canvas.updated", new { name = page.Name, version, kind = page.Kind, title = committed.Title });
        return $"Stored '{page.Name}' version {version} ({content.Length:N0} characters). The user sees it on the canvas.";
    }

    private async Task PruneAsync(string name, int version, CancellationToken cancellationToken)
    {
        try
        {
            await _store.DeleteCanvasVersionAsync(_userId, WorkspaceId, name, version, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The manifest no longer names it, so a failed delete only orphans a blob; the task delete removes it.
        }
    }

    private async Task<string> UnderGateAsync(Func<Task<string>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? ValidateName(string? name) => FleetCanvasPage.IsValidName(name)
        ? null
        : $"'{name}' is not a valid page name. Use 1 to 64 lowercase letters, digits and '-', starting with a letter or digit.";

    private static string? ValidateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "The content is empty. Pass the complete page content.";
        }

        // Refused, never truncated: a truncated page is a broken page.
        var bytes = Encoding.UTF8.GetByteCount(content);
        return bytes > ArtifactLimits.MaxContentBytes
            ? $"The content is {bytes:N0} bytes, over the {ArtifactLimits.MaxContentBytes:N0}-byte limit of a page. Split it over several pages or shorten it."
            : null;
    }

    private static string? ValidateCreate(CanvasState state, string name, string? kind, string? language, int expectedVersion)
    {
        if (expectedVersion != 0)
        {
            return $"Refused: there is no page '{name}' yet. To create it, pass expectedVersion 0.";
        }

        if (kind is null || !ArtifactKind.All.Contains(kind))
        {
            return "kind must be one of: html, svg, markdown, code.";
        }

        if (kind == ArtifactKind.Code && !ArtifactLimits.IsValidLanguage(language))
        {
            return "A code page needs a language: a lowercase name such as python, typescript, csharp or bash.";
        }

        return state.Manifest.Pages.Count >= ArtifactLimits.MaxArtifactsPerConversation
            ? $"The canvas already has {ArtifactLimits.MaxArtifactsPerConversation} pages, the limit. Update an existing page instead."
            : null;
    }

    private static string? ValidateReplace(CanvasState state, FleetCanvasPage page, string? kind, int expectedVersion)
    {
        if (expectedVersion != page.LatestVersion)
        {
            return $"Refused: '{page.Name}' is at version {page.LatestVersion}, not {expectedVersion}; another agent changed it. "
                + $"Merge your change into the current content below and call canvas_write again with expectedVersion {page.LatestVersion}.\n"
                + $"--- {page.Name} version {page.LatestVersion}\n{state.Content(page.Name)}";
        }

        return kind == page.Kind
            ? null
            : $"Refused: '{page.Name}' is a {page.Kind} page and keeps its kind. Pass kind {page.Kind}, or write a new page.";
    }

    private static string UnknownPage(CanvasState state, string? name)
        => state.Manifest.Pages.Count == 0
            ? $"There is no page '{name}', the canvas has no pages yet."
            : $"There is no page '{name}', the canvas has: {string.Join(", ", state.Manifest.Pages.Select(p => p.Name))}";

    private static string CleanTitle(string? title, string name)
    {
        var clean = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length == 0 ? name : clean[..Math.Min(clean.Length, ArtifactLimits.MaxTitleLength)];
    }

    private sealed record CanvasState(FleetCanvasManifest Manifest, IReadOnlyDictionary<string, string> Latest)
    {
        public static readonly CanvasState Empty = new(new FleetCanvasManifest(), new Dictionary<string, string>());

        public FleetCanvasPage? Find(string? name) => Manifest.Pages.FirstOrDefault(p => p.Name == name);

        public string Content(string name) => Latest.TryGetValue(name, out var content) ? content : "";

        public CanvasState With(FleetCanvasManifest manifest, string name, string content)
            => new(manifest, new Dictionary<string, string>(Latest, StringComparer.Ordinal) { [name] = content });
    }
}
