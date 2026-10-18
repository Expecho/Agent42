using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>A file the page attached to a task (ADR 0101). <see cref="Name"/> is the browser's file name, unchecked.</summary>
public sealed record FleetInputFile(string Name, byte[] Content);

/// <summary>
/// The attached files of a fleet task (ADR 0101): validated and stored under <c>inputs/</c> in the workspace at
/// submit, before the task starts, and indexed when the run starts (<see cref="FleetDocumentIngest"/>).
/// </summary>
public sealed partial class FleetTaskService
{
    /// <summary>The workspace folder attached files are stored in.</summary>
    public const string InputsFolder = "inputs";

    /// <summary>The vector-store owner key of a workspace's documents. A chat's key is <c>{userId}/{conversationId}</c>
    /// and a conversation id has no <c>/</c>, so the two key spaces can't collide (ADR 0101).</summary>
    public static string DocumentOwnerKey(string userId, string workspaceId) => $"{userId}/fleet/{workspaceId}";

    /// <summary>The workspace path of an attached file.</summary>
    public static string InputPath(string name) => $"{InputsFolder}/{name}";

    // The task's final inputs: the copied ones of copyInputsOf, then the new files, which win on a name clash. Every
    // refusal happens here, before anything is written.
    private async Task<(List<FleetInputFile>? Inputs, string? Error)> PrepareInputsAsync(
        string userId, FleetSubmit request, FleetTaskRecord? previous, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.FollowUpOf) && !string.IsNullOrWhiteSpace(request.CopyInputsOf))
        {
            return (null, "A follow-up already shares the files of the task it follows up, so it can't copy files too.");
        }

        if (ValidateFiles(request.Files ?? []) is { } error)
        {
            return (null, error);
        }

        var inputs = new Dictionary<string, FleetInputFile>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.CopyInputsOf))
        {
            if (await GetAsync(userId, request.CopyInputsOf, cancellationToken) is not { } source)
            {
                return (null, "Only the files of a task of yours can be copied.");
            }

            foreach (var file in await CopiedInputsAsync(source, cancellationToken))
            {
                inputs[file.Name] = file;
            }
        }

        foreach (var file in request.Files ?? [])
        {
            var name = InputName(file.Name)!;
            inputs[name] = file with { Name = name };
        }

        var final = inputs.Values.ToList();
        return final.Count > options.MaxInputFiles
            ? (null, $"A task can have at most {options.MaxInputFiles} files.")
            : (final, await WorkspaceRoomErrorAsync(userId, previous, final, cancellationToken));
    }

    private string? ValidateFiles(IReadOnlyList<FleetInputFile> files)
    {
        if (files.Count > options.MaxInputFiles)
        {
            return $"A task can have at most {options.MaxInputFiles} files.";
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (InputName(file.Name) is not { } name)
            {
                return $"'{file.Name}' is not a usable file name.";
            }

            if (file.Content.LongLength > options.MaxInputFileBytes)
            {
                return $"'{name}' is larger than {options.MaxInputFileBytes / (1024 * 1024)} MB.";
            }

            if (!names.Add(name))
            {
                return $"Two files are named '{name}'.";
            }
        }

        return null;
    }

    /// <summary>The file name without any directory, or null when nothing usable is left (empty, a dot name, a control
    /// character). Backslashes count as separators, whatever the server's OS.</summary>
    public static string? InputName(string? fileName)
    {
        var name = (fileName ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].Trim();
        return name.Length is 0 or > 255 || name is "." or ".." || name.Any(char.IsControl) ? null : name;
    }

    // Every inputs/ file of the source task's workspace except its generated text copies.
    private async Task<List<FleetInputFile>> CopiedInputsAsync(FleetTaskRecord source, CancellationToken cancellationToken)
    {
        var copies = new List<FleetInputFile>();
        foreach (var file in await store.ListWorkspaceAsync(source.UserId, source.WorkspaceId, cancellationToken))
        {
            if (!file.Path.StartsWith(InputsFolder + "/", StringComparison.Ordinal)
                || source.TextCopies.Contains(file.Path, StringComparer.Ordinal)
                || InputName(file.Path[(InputsFolder.Length + 1)..]) is not { } name
                || await store.GetWorkspaceFileAsync(source.UserId, source.WorkspaceId, file.Path, cancellationToken) is not { } content)
            {
                continue;
            }

            copies.Add(new FleetInputFile(name, content));
        }

        return copies;
    }

    // Existing workspace files (a follow-up's) plus the new inputs must fit the workspace caps the sync enforces. A
    // new input replaces a stored file of the same path.
    private async Task<string?> WorkspaceRoomErrorAsync(
        string userId, FleetTaskRecord? previous, List<FleetInputFile> inputs, CancellationToken cancellationToken)
    {
        if (inputs.Count == 0)
        {
            return null;
        }

        var paths = inputs.Select(i => InputPath(i.Name)).ToHashSet(StringComparer.Ordinal);
        var existing = previous is null
            ? []
            : (await store.ListWorkspaceAsync(userId, previous.WorkspaceId, cancellationToken)).Where(f => !paths.Contains(f.Path)).ToList();
        var bytes = existing.Sum(f => f.Size) + inputs.Sum(i => i.Content.LongLength);
        if (bytes > options.MaxWorkspaceBytes)
        {
            return $"The files don't fit the workspace, which holds at most {options.MaxWorkspaceBytes / (1024 * 1024)} MB.";
        }

        return existing.Count + inputs.Count > options.MaxWorkspaceFiles
            ? $"The files don't fit the workspace, which holds at most {options.MaxWorkspaceFiles} files."
            : null;
    }

    private async Task StoreInputsAsync(FleetTaskRecord record, IReadOnlyList<FleetInputFile> inputs, CancellationToken cancellationToken)
    {
        foreach (var input in inputs)
        {
            await store.PutWorkspaceFileAsync(record.UserId, record.WorkspaceId, InputPath(input.Name), input.Content, cancellationToken);
        }
    }

    // A refused or failed start leaves nothing behind in a workspace of its own. A follow-up's shared workspace is left
    // alone: its earlier files are not this task's to delete.
    private async Task DiscardInputsQuietlyAsync(FleetTaskRecord record, bool ownWorkspace)
    {
        if (!ownWorkspace || record.Inputs.Count == 0)
        {
            return;
        }

        try
        {
            await store.DeleteWorkspaceAsync(record.UserId, record.WorkspaceId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fleet task {TaskId}: its inputs could not be removed after a failed start", record.Id);
        }
    }

    // When this is the workspace's last task, its document chunks go first: a failure here leaves the task and the
    // workspace intact, so the user can retry, and no chunks outlive the files (ADR 0101).
    private async Task DeleteDocumentsIfLastAsync(FleetTaskRecord record, CancellationToken cancellationToken)
    {
        if (documents is null)
        {
            return;
        }

        var tasks = await store.ListTasksAsync(record.UserId, cancellationToken);
        if (!tasks.Any(t => t.Id != record.Id && t.WorkspaceId == record.WorkspaceId))
        {
            await documents.DeleteAsync(DocumentOwnerKey(record.UserId, record.WorkspaceId), cancellationToken);
        }
    }
}
