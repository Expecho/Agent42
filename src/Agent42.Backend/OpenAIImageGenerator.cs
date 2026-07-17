using Agent42.Agents;
using OpenAI.Images;

namespace Agent42.Backend;

/// <summary>
/// Production <see cref="IImageGenerationService"/> (ADR 0020) over the OpenAI SDK <see cref="ImageClient"/>:
/// text-to-image via <c>GenerateImageAsync</c>, image-to-image via <c>GenerateImageEditAsync</c>, both asking
/// for raw bytes (<c>ResponseFormat = GeneratedImageFormat.Bytes</c>) so the grain can persist them to blob
/// storage and never inline them into the 2 MB session (ADR 0004).
///
/// <para>The <see cref="ImageClient"/> is built the SAME transport way as
/// <see cref="MultiProviderChatClientFactory.Provider.OpenAICompatible"/> — the SDK's own
/// <c>OpenAIClient</c>, NOT an <c>IHttpClientFactory</c> client — because a named <c>HttpClient</c> silently
/// inherits <c>AddServiceDefaults</c>' 3-retry resilience pipeline, and <c>AgentGrain</c> is non-reentrant, so
/// a retried image call would multiply the stall on the user's turn (see the "named HttpClient inherits the
/// retry pipeline" gotcha). The endpoint still comes only from the provider's config — SSRF invariant intact.</para>
///
/// <para>The Step-0 spike (pinned OpenAI 2.12.0) confirmed this surface; the LIVE <c>/v1/images</c> exchange
/// is not reachable offline (<see cref="InMemoryImageGenerator"/> stands in), so this class is exercised by
/// prod smoke, not a unit test — honestly out of unit scope, like the blob SAS path (ADR 0004).</para>
/// </summary>
public sealed class OpenAIImageGenerator(ImageClient client) : IImageGenerationService
{
    public async Task<Agent42.Agents.GeneratedImage> GenerateAsync(
        string prompt, ImageInput? input, CancellationToken cancellationToken = default)
    {
        if (input is null)
        {
            // Text-to-image. Bytes, not a URL: the grain owns storage, and a provider URL would be a second,
            // short-lived source we'd have to re-fetch (contrast the model-fetched chat image URL, ADR 0004).
            var result = await client.GenerateImageAsync(
                prompt,
                new ImageGenerationOptions { ResponseFormat = GeneratedImageFormat.Bytes },
                cancellationToken);
            var bytes = result.Value.ImageBytes.ToArray();
            return new Agent42.Agents.GeneratedImage(bytes, ImageMediaTypes.DetectMediaType(bytes));
        }

        // Image-to-image (edit): the SDK wants a stream + a filename (media type is inferred from it).
        using var stream = new MemoryStream(input.Bytes);
        var edited = await client.GenerateImageEditAsync(
            stream,
            "input" + ImageMediaTypes.ExtensionFor(input.MediaType),
            prompt,
            new ImageEditOptions { ResponseFormat = GeneratedImageFormat.Bytes },
            cancellationToken);
        var editedBytes = edited.Value.ImageBytes.ToArray();
        // Sniff the RETURNED format rather than assuming PNG: a non-OpenAI OpenAI-compatible endpoint may
        // return JPEG/WebP, and a wrong Content-Type would then be stored + served (critic Minor-1).
        return new Agent42.Agents.GeneratedImage(editedBytes, ImageMediaTypes.DetectMediaType(editedBytes));
    }
}
