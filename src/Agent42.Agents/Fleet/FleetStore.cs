using System.Text;
using System.Text.Json;

namespace Agent42.Agents;

/// <summary>
/// <see cref="IFleetStore"/> over the <see cref="IFleetBlobs"/> seam. ALL the logic lives here (the layout, escaping,
/// path refusal and delete semantics), so the contract tests cover it over <see cref="InMemoryFleetBlobs"/>. The
/// layout:
/// <code>
/// {userId}/tasks/{taskId}.json
/// {userId}/events/{taskId}.ndjson            (append blob)
/// {userId}/workspaces/{workspaceId}/{path}
/// {userId}/canvas/{workspaceId}/manifest.json
/// {userId}/canvas/{workspaceId}/{name}/v{version}
/// {userId}/prompts.json
/// {userId}/lessons.json                      (ADR 0124; outside every workspace prefix, so the sync never deletes it)
/// </code>
/// Every id and path segment is escaped with <see cref="Uri.EscapeDataString(string)"/>, so none can add a level or
/// step out of its own. A workspace path with <c>..</c>, a rooted path or an empty path is refused, never resolved:
/// these paths come from models and from the download URL. The canvas sits OUTSIDE the workspace prefix on purpose:
/// the workspace sync deletes every stored blob under that prefix that isn't on local disk (ADR 0102).
/// </summary>
public sealed class FleetStore(IFleetBlobs blobs) : IFleetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FleetTaskRecord>> ListTasksAsync(string userId, CancellationToken cancellationToken = default)
    {
        var tasks = new List<FleetTaskRecord>();
        foreach (var item in await blobs.ListAsync(TasksPrefix(userId), cancellationToken))
        {
            if (await ReadTaskAsync(item.Key, cancellationToken) is { } task)
            {
                tasks.Add(task);
            }
        }

        return [.. tasks.OrderByDescending(t => t.StartedAt)];
    }

    public Task<FleetTaskRecord?> GetTaskAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => ReadTaskAsync(TaskKey(userId, taskId), cancellationToken);

    public Task SaveTaskAsync(FleetTaskRecord task, CancellationToken cancellationToken = default)
        => blobs.PutAsync(TaskKey(task.UserId, task.Id), JsonSerializer.SerializeToUtf8Bytes(task, JsonOptions), cancellationToken);

    public async Task DeleteTaskAsync(string userId, string taskId, CancellationToken cancellationToken = default)
    {
        if (await GetTaskAsync(userId, taskId, cancellationToken) is not { } task)
        {
            return;
        }

        await blobs.DeleteAsync(TaskKey(userId, taskId), cancellationToken);
        await blobs.DeleteAsync(EventsKey(userId, taskId), cancellationToken);

        var others = await ListTasksAsync(userId, cancellationToken);
        if (!others.Any(t => t.WorkspaceId == task.WorkspaceId))
        {
            await DeleteWorkspaceAsync(userId, task.WorkspaceId, cancellationToken);
            await DeleteCanvasAsync(userId, task.WorkspaceId, cancellationToken);
        }
    }

    public Task AppendEventsAsync(string userId, string taskId, string lines, CancellationToken cancellationToken = default)
        => blobs.AppendAsync(EventsKey(userId, taskId), Encoding.UTF8.GetBytes(lines), cancellationToken);

    public async Task<IReadOnlyList<string>> ReadEventsAsync(string userId, string taskId, CancellationToken cancellationToken = default)
    {
        var content = await blobs.GetAsync(EventsKey(userId, taskId), cancellationToken);
        return content is null
            ? []
            : Encoding.UTF8.GetString(content).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Task PutWorkspaceFileAsync(string userId, string workspaceId, string path, byte[] content, CancellationToken cancellationToken = default)
        => blobs.PutAsync(WorkspaceFileKey(userId, workspaceId, path), content, cancellationToken);

    public Task<byte[]?> GetWorkspaceFileAsync(string userId, string workspaceId, string path, CancellationToken cancellationToken = default)
        => blobs.GetAsync(WorkspaceFileKey(userId, workspaceId, path), cancellationToken);

    public async Task<IReadOnlyList<FleetWorkspaceFile>> ListWorkspaceAsync(string userId, string workspaceId, CancellationToken cancellationToken = default)
    {
        var prefix = WorkspacePrefix(userId, workspaceId);
        return [.. (await blobs.ListAsync(prefix, cancellationToken))
            .Select(item => new FleetWorkspaceFile(
                string.Join('/', item.Key[prefix.Length..].Split('/').Select(Uri.UnescapeDataString)),
                item.Size))];
    }

    public Task DeleteWorkspaceFileAsync(string userId, string workspaceId, string path, CancellationToken cancellationToken = default)
        => blobs.DeleteAsync(WorkspaceFileKey(userId, workspaceId, path), cancellationToken);

    public async Task DeleteWorkspaceAsync(string userId, string workspaceId, CancellationToken cancellationToken = default)
    {
        foreach (var item in await blobs.ListAsync(WorkspacePrefix(userId, workspaceId), cancellationToken))
        {
            await blobs.DeleteAsync(item.Key, cancellationToken);
        }
    }

    public async Task<FleetCanvasManifest> GetCanvasManifestAsync(string userId, string workspaceId, CancellationToken cancellationToken = default)
    {
        var content = await blobs.GetAsync(CanvasManifestKey(userId, workspaceId), cancellationToken);
        return content is null
            ? new FleetCanvasManifest()
            : JsonSerializer.Deserialize<FleetCanvasManifest>(content, JsonOptions) ?? new FleetCanvasManifest();
    }

    public Task PutCanvasManifestAsync(string userId, string workspaceId, FleetCanvasManifest manifest, CancellationToken cancellationToken = default)
        => blobs.PutAsync(CanvasManifestKey(userId, workspaceId), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), cancellationToken);

    public Task PutCanvasVersionAsync(string userId, string workspaceId, string name, int version, byte[] content, CancellationToken cancellationToken = default)
        => blobs.PutAsync(CanvasVersionKey(userId, workspaceId, name, version), content, cancellationToken);

    public Task<byte[]?> GetCanvasVersionAsync(string userId, string workspaceId, string name, int version, CancellationToken cancellationToken = default)
        => blobs.GetAsync(CanvasVersionKey(userId, workspaceId, name, version), cancellationToken);

    public Task DeleteCanvasVersionAsync(string userId, string workspaceId, string name, int version, CancellationToken cancellationToken = default)
        => blobs.DeleteAsync(CanvasVersionKey(userId, workspaceId, name, version), cancellationToken);

    public async Task DeleteCanvasAsync(string userId, string workspaceId, CancellationToken cancellationToken = default)
    {
        foreach (var item in await blobs.ListAsync(CanvasPrefix(userId, workspaceId), cancellationToken))
        {
            await blobs.DeleteAsync(item.Key, cancellationToken);
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPromptsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var content = await blobs.GetAsync(PromptsKey(userId), cancellationToken);
        return content is null
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(content, JsonOptions) ?? [];
    }

    public Task SetPromptsAsync(string userId, IReadOnlyDictionary<string, string> prompts, CancellationToken cancellationToken = default)
        => blobs.PutAsync(PromptsKey(userId), JsonSerializer.SerializeToUtf8Bytes(prompts, JsonOptions), cancellationToken);

    public async Task<FleetLessonsDocument> GetLessonsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var content = await blobs.GetAsync(LessonsKey(userId), cancellationToken);
        return content is null
            ? new FleetLessonsDocument()
            : JsonSerializer.Deserialize<FleetLessonsDocument>(content, JsonOptions) ?? new FleetLessonsDocument();
    }

    public Task SetLessonsAsync(string userId, FleetLessonsDocument document, CancellationToken cancellationToken = default)
        => blobs.PutAsync(LessonsKey(userId), JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions), cancellationToken);

    /// <summary>
    /// The workspace-relative path as <c>/</c>-joined segments, or an exception when it is empty, rooted or
    /// contains <c>..</c>. Backslashes count as separators, and empty and <c>.</c> segments are dropped.
    /// </summary>
    public static IReadOnlyList<string> NormalizeWorkspacePath(string path)
    {
        var unified = (path ?? "").Replace('\\', '/');
        if (unified.StartsWith('/') || (unified.Length >= 2 && unified[1] == ':'))
        {
            throw new ArgumentException($"'{path}' is not a path relative to the workspace.", nameof(path));
        }

        var segments = unified.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        if (segments.Count == 0 || segments.Contains(".."))
        {
            throw new ArgumentException($"'{path}' is not a path inside the workspace.", nameof(path));
        }

        return segments;
    }

    private async Task<FleetTaskRecord?> ReadTaskAsync(string key, CancellationToken cancellationToken)
    {
        var content = await blobs.GetAsync(key, cancellationToken);
        return content is null ? null : JsonSerializer.Deserialize<FleetTaskRecord>(content, JsonOptions);
    }

    private static string UserPrefix(string userId) => $"{Escape(userId)}/";

    private static string TasksPrefix(string userId) => $"{UserPrefix(userId)}tasks/";

    private static string TaskKey(string userId, string taskId) => $"{TasksPrefix(userId)}{Escape(taskId)}.json";

    private static string EventsKey(string userId, string taskId) => $"{UserPrefix(userId)}events/{Escape(taskId)}.ndjson";

    private static string WorkspacePrefix(string userId, string workspaceId) => $"{UserPrefix(userId)}workspaces/{Escape(workspaceId)}/";

    private static string WorkspaceFileKey(string userId, string workspaceId, string path)
        => WorkspacePrefix(userId, workspaceId) + string.Join('/', NormalizeWorkspacePath(path).Select(Uri.EscapeDataString));

    private static string CanvasPrefix(string userId, string workspaceId) => $"{UserPrefix(userId)}canvas/{Escape(workspaceId)}/";

    private static string CanvasManifestKey(string userId, string workspaceId) => $"{CanvasPrefix(userId, workspaceId)}manifest.json";

    // "manifest.json" can't collide with a page, because a page name has no '.'.
    private static string CanvasVersionKey(string userId, string workspaceId, string name, int version)
    {
        if (!FleetCanvasPage.IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid canvas page name.", nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return $"{CanvasPrefix(userId, workspaceId)}{name}/v{version}";
    }

    private static string PromptsKey(string userId) => $"{UserPrefix(userId)}prompts.json";

    private static string LessonsKey(string userId) => $"{UserPrefix(userId)}lessons.json";

    // A '.' or '..' segment survives escaping, and an HTTP client may resolve it in the blob URL, so it is refused.
    private static string Escape(string segment)
        => string.IsNullOrEmpty(segment) || segment is "." or ".."
            ? throw new ArgumentException($"'{segment}' is not a valid id in a fleet key.", nameof(segment))
            : Uri.EscapeDataString(segment);
}
