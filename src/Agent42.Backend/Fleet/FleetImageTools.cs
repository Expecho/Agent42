using System.ComponentModel;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// <c>generate_image</c> for one fleet task (ADR 0144). In a chat, image generation is not a tool but a per-turn route
/// (ADR 0020); a fleet agent has no turn to route, so this tool generates with one of the user's image-generation models
/// and writes the bytes into the task workspace under <c>images/</c>, where the sync stores them and the task view lists
/// them. It returns the path, never the bytes (rule 5). Gated by capability, not a selectable key: the catalog builds it
/// only when the user has an image-generation model. Its tokens aren't counted by the task's budget (it is no chat call),
/// so <see cref="MaxImagesPerTask"/> is the bound.
/// </summary>
public sealed class FleetImageTools
{
    public const string Name = "generate_image";

    /// <summary>Images one fleet task may generate, all agents together.</summary>
    public const int MaxImagesPerTask = 5;

    /// <summary>The largest image kept, the chat's attachment upload limit (<c>AgentApi.MaxAttachmentBytes</c>).</summary>
    public const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly IChatClientFactory _models;
    private readonly IReadOnlyList<string> _modelIds;
    private readonly FleetWorkspace _workspace;
    private readonly ILogger _logger;
    private int _generated;

    /// <param name="modelIds">The user's image-generation models, in catalog order; the first is the default. Not empty.</param>
    public FleetImageTools(IChatClientFactory models, IReadOnlyList<string> modelIds, FleetWorkspace workspace, ILogger logger)
    {
        _models = models;
        _modelIds = modelIds;
        _workspace = workspace;
        _logger = logger;
        Tool = AIFunctionFactory.Create(GenerateAsync, Name,
            "Generate an image from a text description and save it in the shared workspace. Returns the file's path. "
            + $"Image models: {string.Join(", ", modelIds)} (default {modelIds[0]}). At most {MaxImagesPerTask} images per task.");
    }

    public AIFunction Tool { get; }

    private async Task<string> GenerateAsync(
        [Description("What the image shows, described in detail.")] string prompt,
        [Description("One of the image models in this tool's description, or empty for the default.")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "Describe the image to generate.";
        }

        var id = string.IsNullOrWhiteSpace(model) ? _modelIds[0] : model.Trim();
        if (!_modelIds.Contains(id, StringComparer.Ordinal))
        {
            return $"'{id}' is not one of the image models: {string.Join(", ", _modelIds)}.";
        }

        if (Interlocked.Increment(ref _generated) > MaxImagesPerTask)
        {
            return $"This task has generated {MaxImagesPerTask} images, the limit.";
        }

        try
        {
            return await GenerateCoreAsync(prompt, id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Fleet image generation failed on {Model}", id);
            return $"The image could not be generated: {ex.Message}";
        }
    }

    private async Task<string> GenerateCoreAsync(string prompt, string modelId, CancellationToken cancellationToken)
    {
        if (_models.GetImageGenerationService(modelId) is not { } generator)
        {
            return $"'{modelId}' can't generate images.";
        }

        var image = await generator.GenerateAsync(prompt, null, cancellationToken);
        if (image.Bytes.Length > MaxImageBytes)
        {
            return $"The generated image is larger than {MaxImageBytes / (1024 * 1024)} MB, so it was not saved.";
        }

        var path = $"images/{Guid.NewGuid():N}{ImageMediaTypes.ExtensionFor(image.MediaType)}";
        var full = _workspace.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, image.Bytes, cancellationToken);
        return $"Image saved in the workspace as {path} ({image.MediaType}).";
    }
}
